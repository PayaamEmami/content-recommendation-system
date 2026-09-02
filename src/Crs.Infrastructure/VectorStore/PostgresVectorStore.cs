using System.Diagnostics;
using Crs.Core.Interfaces;
using Crs.Core.Models;
using Crs.Core.Observability;
using Crs.Infrastructure.Configuration;
using Crs.Infrastructure.Data;
using Crs.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pgvector;
using Pgvector.EntityFrameworkCore;

namespace Crs.Infrastructure.VectorStore;

/// <summary>
/// Postgres/pgvector implementation of the content vector store.
/// </summary>
public class PostgresVectorStore : IVectorStore
{
    private readonly CrsDbContext _db;
    private readonly EmbeddingSettings _embeddingSettings;
    private readonly ILogger<PostgresVectorStore> _logger;
    private readonly IObservabilityMetrics _metrics;

    public PostgresVectorStore(
        CrsDbContext db,
        IOptions<EmbeddingSettings> embeddingSettings,
        ILogger<PostgresVectorStore> logger,
        IObservabilityMetrics metrics)
    {
        _db = db;
        _embeddingSettings = embeddingSettings.Value;
        _logger = logger;
        _metrics = metrics;
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public async Task UpsertDocumentAsync(ContentDocument document, CancellationToken cancellationToken = default)
    {
        await UpsertDocumentsAsync(new[] { document }, cancellationToken);
    }

    public async Task UpsertDocumentsAsync(
        IEnumerable<ContentDocument> documents,
        CancellationToken cancellationToken = default)
    {
        var documentsList = documents
            .GroupBy(document => document.Id)
            .Select(group => group.Last())
            .ToList();
        if (documentsList.Count == 0)
        {
            return;
        }

        try
        {
            using var activity = CrsTelemetry.ActivitySource.StartActivity("pgvector.upsert");
            activity?.SetTag(CrsTelemetry.Tags.Dependency, "postgres");
            activity?.SetTag(CrsTelemetry.Tags.ResultCount, documentsList.Count);
            var startedAt = Stopwatch.StartNew();

            foreach (var document in documentsList)
            {
                ValidateEmbedding(document.Embedding);
            }

            var ids = documentsList.Select(document => document.Id).ToList();
            var existing = await _db.ContentEmbeddings
                .Where(row => ids.Contains(row.ContentId))
                .ToDictionaryAsync(row => row.ContentId, cancellationToken);

            var now = DateTime.UtcNow;
            foreach (var document in documentsList)
            {
                var vector = new Vector(document.Embedding);
                if (existing.TryGetValue(document.Id, out var row))
                {
                    row.Embedding = vector;
                    row.UpdatedAt = now;
                }
                else
                {
                    _db.ContentEmbeddings.Add(new ContentEmbedding
                    {
                        ContentId = document.Id,
                        Embedding = vector,
                        UpdatedAt = now
                    });
                }
            }

            await _db.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("Upserted {Count} embeddings into Postgres", documentsList.Count);
            startedAt.Stop();
            RecordMetric("upsert", "success", startedAt.Elapsed, documentsList.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error upserting documents to vector store");
            RecordMetric("upsert", "failed", TimeSpan.Zero, documentsList.Count);
            throw;
        }
    }

    public async Task DeleteDocumentAsync(Guid contentId, CancellationToken cancellationToken = default)
    {
        try
        {
            using var activity = CrsTelemetry.ActivitySource.StartActivity("pgvector.delete");
            activity?.SetTag(CrsTelemetry.Tags.Dependency, "postgres");
            var startedAt = Stopwatch.StartNew();

            var row = await _db.ContentEmbeddings.FindAsync([contentId], cancellationToken);
            if (row != null)
            {
                _db.ContentEmbeddings.Remove(row);
                await _db.SaveChangesAsync(cancellationToken);
            }

            startedAt.Stop();
            RecordMetric("delete", "success", startedAt.Elapsed);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting document {ContentId} from vector store", contentId);
            RecordMetric("delete", "failed", TimeSpan.Zero);
            throw;
        }
    }

    public async Task<List<VectorSearchResult>> SearchAsync(
        VectorSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var activity = CrsTelemetry.ActivitySource.StartActivity("pgvector.search");
            activity?.SetTag(CrsTelemetry.Tags.Dependency, "postgres");
            activity?.SetTag(CrsTelemetry.Tags.FeedType, request.ContentType?.ToString());
            var startedAt = Stopwatch.StartNew();

            ValidateEmbedding(request.QueryVector);
            var queryVector = new Vector(request.QueryVector);

            var query =
                from embedding in _db.ContentEmbeddings.AsNoTracking()
                join content in _db.Content.AsNoTracking() on embedding.ContentId equals content.Id
                select new { embedding, content };

            if (request.ContentType.HasValue)
            {
                query = query.Where(row => row.content.Type == request.ContentType.Value);
            }

            if (request.SourceIds is { Count: > 0 })
            {
                var sourceIds = request.SourceIds.ToList();
                query = query.Where(row =>
                    row.content.SourceId.HasValue && sourceIds.Contains(row.content.SourceId.Value));
            }

            if (request.PublishedAfter.HasValue)
            {
                var publishedAfter = AsUtc(request.PublishedAfter.Value);
                query = query.Where(row => row.content.CreatedAt >= publishedAfter);
            }

            if (request.PublishedBefore.HasValue)
            {
                var publishedBefore = AsUtc(request.PublishedBefore.Value);
                query = query.Where(row => row.content.CreatedAt <= publishedBefore);
            }

            if (request.ExcludeContentIds is { Count: > 0 })
            {
                var excludedIds = request.ExcludeContentIds.ToList();
                query = query.Where(row => !excludedIds.Contains(row.content.Id));
            }

            var rows = await query
                .OrderBy(row => row.embedding.Embedding.CosineDistance(queryVector))
                .Take(request.TopK)
                .Select(row => new
                {
                    row.content.Id,
                    Distance = row.embedding.Embedding.CosineDistance(queryVector)
                })
                .ToListAsync(cancellationToken);

            var results = rows
                .Select(row => new VectorSearchResult
                {
                    ContentId = row.Id,
                    SimilarityScore = ToSimilarity(row.Distance)
                })
                .Where(result => !request.MinimumScore.HasValue || result.SimilarityScore >= request.MinimumScore.Value)
                .ToList();

            _logger.LogInformation(
                "Vector search returned {Count} results (requested {TopK})",
                results.Count,
                request.TopK);

            startedAt.Stop();
            RecordMetric("search", "success", startedAt.Elapsed, results.Count);
            return results;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error performing vector search");
            RecordMetric("search", "failed", TimeSpan.Zero);
            throw;
        }
    }

    public async Task<long> GetDocumentCountAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var startedAt = Stopwatch.StartNew();
            var count = await _db.ContentEmbeddings.LongCountAsync(cancellationToken);
            startedAt.Stop();
            RecordMetric("count", "success", startedAt.Elapsed);
            return count;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting document count");
            RecordMetric("count", "failed", TimeSpan.Zero);
            throw;
        }
    }

    public async Task<HashSet<Guid>> GetAllDocumentIdsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var startedAt = Stopwatch.StartNew();
            var ids = await _db.ContentEmbeddings
                .AsNoTracking()
                .Select(row => row.ContentId)
                .ToListAsync(cancellationToken);
            startedAt.Stop();
            RecordMetric("list_ids", "success", startedAt.Elapsed, ids.Count);
            return ids.ToHashSet();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing indexed document IDs");
            RecordMetric("list_ids", "failed", TimeSpan.Zero);
            throw;
        }
    }

    internal static double ToSimilarity(double cosineDistance)
    {
        return Math.Clamp(1.0 - cosineDistance, 0.0, 1.0);
    }

    private void ValidateEmbedding(float[] embedding)
    {
        if (embedding.Length != _embeddingSettings.Dimensions)
        {
            throw new ArgumentException(
                $"Embedding has {embedding.Length} dimensions; expected {_embeddingSettings.Dimensions}.",
                nameof(embedding));
        }
    }

    private static DateTime AsUtc(DateTime value)
    {
        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
    }

    private void RecordMetric(string operation, string outcome, TimeSpan duration, int? count = null)
    {
        var context = new MetricContext(
            Dimensions: new Dictionary<string, string>
            {
                ["Dependency"] = "Postgres",
                ["Operation"] = operation,
                ["Outcome"] = outcome
            },
            Properties: count.HasValue ? new Dictionary<string, object?> { ["Count"] = count.Value } : null);

        _metrics.Increment("dependency.call.count", context: context);
        _metrics.RecordDuration("dependency.call.duration", duration, context);
        if (count.HasValue)
        {
            _metrics.RecordValue("pgvector.result.count", count.Value, "Count", context);
        }

        if (outcome is "failed" or "partial_failure")
        {
            _metrics.Increment("dependency.failure.count", context: context);
        }
    }
}
