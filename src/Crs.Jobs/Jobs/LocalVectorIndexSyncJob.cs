using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using Crs.Core.Entities;
using Crs.Core.Interfaces;
using Crs.Core.Models;
using Crs.Core.Observability;
using Crs.Infrastructure.Configuration;

namespace Crs.Jobs.Jobs;

/// <summary>
/// Reconciles the local vector index with the database.
/// Treats local OpenSearch as a rebuildable cache that should converge to the DB state.
/// </summary>
public class LocalVectorIndexSyncJob
{
    private const int BatchSize = 50;

    private readonly IServiceProvider _serviceProvider;
    private readonly IOptions<OpenSearchSettings> _openSearchSettings;
    private readonly ILogger<LocalVectorIndexSyncJob> _logger;
    private readonly IObservabilityMetrics _metrics;

    public LocalVectorIndexSyncJob(
        IServiceProvider serviceProvider,
        IOptions<OpenSearchSettings> openSearchSettings,
        ILogger<LocalVectorIndexSyncJob> logger,
        IObservabilityMetrics metrics)
    {
        _serviceProvider = serviceProvider;
        _openSearchSettings = openSearchSettings;
        _logger = logger;
        _metrics = metrics;
    }

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        using var activity = CrsTelemetry.ActivitySource.StartActivity("job.local_vector_index_sync");
        activity?.SetTag(CrsTelemetry.Tags.JobName, "sync-index");
        var stopwatch = Stopwatch.StartNew();
        try
        {
            if (_openSearchSettings.Value.Mode != OpenSearchMode.Local)
            {
                _logger.LogInformation("Skipping local vector index sync because OpenSearch mode is {Mode}", _openSearchSettings.Value.Mode);
                stopwatch.Stop();
                _metrics.RecordDuration("job.duration", stopwatch.Elapsed, BuildMetricContext("skipped"));
                return;
            }

            _logger.LogInformation("Starting local vector index sync");

            using var scope = _serviceProvider.CreateScope();
            var contentRepository = scope.ServiceProvider.GetRequiredService<IContentRepository>();
            var embeddingService = scope.ServiceProvider.GetRequiredService<IEmbeddingService>();
            var vectorStore = scope.ServiceProvider.GetRequiredService<IVectorStore>();

            var allContent = (await contentRepository.GetAllAsync(cancellationToken)).ToList();
            var dbContentMap = allContent.ToDictionary(content => content.Id);
            var indexedIds = await vectorStore.GetAllDocumentIdsAsync(cancellationToken);

            var missingIds = dbContentMap.Keys.Except(indexedIds).ToList();
            var orphanedIds = indexedIds.Except(dbContentMap.Keys).ToList();

            _logger.LogInformation(
                "Local vector index sync diff: {DatabaseCount} DB content, {IndexedCount} indexed, {MissingCount} missing, {OrphanedCount} orphaned",
                dbContentMap.Count,
                indexedIds.Count,
                missingIds.Count,
                orphanedIds.Count);

            if (!missingIds.Any() && !orphanedIds.Any())
            {
                _logger.LogInformation("Local vector index is already synchronized");
                stopwatch.Stop();
                _metrics.RecordDuration("job.duration", stopwatch.Elapsed, BuildMetricContext("success"));
                return;
            }

            if (missingIds.Any())
            {
                foreach (var batch in missingIds.Chunk(BatchSize))
                {
                    var batchContent = batch.Select(id => dbContentMap[id]).ToList();
                    var documents = await BuildDocumentsAsync(batchContent, embeddingService, cancellationToken);
                    await vectorStore.UpsertDocumentsAsync(documents, cancellationToken);

                    _logger.LogInformation("Backfilled {Count} missing documents into local vector index", documents.Count);
                }
            }

            if (orphanedIds.Any())
            {
                foreach (var orphanedId in orphanedIds)
                {
                    await vectorStore.DeleteDocumentAsync(orphanedId, cancellationToken);
                }

                _logger.LogInformation("Removed {Count} orphaned documents from local vector index", orphanedIds.Count);
            }

            _logger.LogInformation("Completed local vector index sync");
            stopwatch.Stop();
            _metrics.Increment("job.success.count", context: BuildMetricContext("success"));
            _metrics.RecordDuration("job.duration", stopwatch.Elapsed, BuildMetricContext("success"));
        }
        catch
        {
            stopwatch.Stop();
            _metrics.Increment("job.failure.count", context: BuildMetricContext("failed"));
            _metrics.RecordDuration("job.duration", stopwatch.Elapsed, BuildMetricContext("failed"));
            throw;
        }
    }

    private static async Task<List<ContentDocument>> BuildDocumentsAsync(
        List<Content> content,
        IEmbeddingService embeddingService,
        CancellationToken cancellationToken)
    {
        var texts = content
            .Select(item => $"{item.Title} {item.Description}".Trim())
            .ToList();

        var embeddings = await embeddingService.GenerateEmbeddingsAsync(texts, cancellationToken);

        return content.Zip(embeddings, (item, embedding) => new ContentDocument
        {
            Id = item.Id,
            Title = item.Title,
            Description = item.Description,
            Url = item.Url,
            Type = item.Type,
            SourceId = item.SourceId,
            PublishedDate = item.CreatedAt,
            CreatedAt = item.CreatedAt,
            UpdatedAt = item.UpdatedAt,
            Embedding = embedding
        }).ToList();
    }

    private static MetricContext BuildMetricContext(string outcome)
    {
        return new MetricContext(
            Dimensions: new Dictionary<string, string>
            {
                ["JobName"] = "sync-index",
                ["Operation"] = "job.run",
                ["Outcome"] = outcome
            });
    }
}
