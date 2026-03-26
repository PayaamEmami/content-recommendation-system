using Amazon;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenSearch.Client;
using OpenSearch.Net;
using OpenSearch.Net.Auth.AwsSigV4;
using Crs.Core.Interfaces;
using Crs.Core.Models;
using Crs.Core.Observability;
using Crs.Infrastructure.Configuration;

namespace Crs.Infrastructure.VectorStore;

/// <summary>
/// AWS OpenSearch Serverless implementation of vector store.
/// </summary>
public class OpenSearchVectorStore : IVectorStore
{
    private readonly OpenSearchClient _client;
    private readonly OpenSearchSettings _settings;
    private readonly ILogger<OpenSearchVectorStore> _logger;
    private readonly IObservabilityMetrics _metrics;

    public OpenSearchVectorStore(
        IOptions<OpenSearchSettings> settings,
        ILogger<OpenSearchVectorStore> logger,
        IObservabilityMetrics metrics)
    {
        _settings = settings.Value;
        _logger = logger;
        _metrics = metrics;

        if (_settings.Mode == OpenSearchMode.Aws && string.IsNullOrWhiteSpace(_settings.Region))
        {
            throw new InvalidOperationException("OpenSearch region is required when Mode is Aws.");
        }

        var endpoint = new Uri(_settings.Endpoint);
        var pool = new SingleNodeConnectionPool(endpoint);

        ConnectionSettings connectionSettings;
        if (_settings.Mode == OpenSearchMode.Aws)
        {
            // Configure OpenSearch client with AWS SigV4 for Serverless
            var region = RegionEndpoint.GetBySystemName(_settings.Region);
            var awsConnection = new AwsSigV4HttpConnection(region, service: "aoss");
            connectionSettings = new ConnectionSettings(pool, awsConnection);
        }
        else
        {
            // Local OpenSearch uses no auth by default (Docker).
            connectionSettings = new ConnectionSettings(pool);
        }

        connectionSettings = connectionSettings
            .DefaultIndex(_settings.IndexName)
            .DefaultDisableIdInference()
            .RequestTimeout(TimeSpan.FromMinutes(2))
            .DisableDirectStreaming(); // Helps with debugging

        _client = new OpenSearchClient(connectionSettings);

        _logger.LogInformation(
            "Using OpenSearch mode {Mode} at {Endpoint}",
            _settings.Mode,
            _settings.Endpoint);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var activity = CrsTelemetry.ActivitySource.StartActivity("opensearch.initialize");
            activity?.SetTag(CrsTelemetry.Tags.Dependency, "opensearch");
            var stopwatch = Stopwatch.StartNew();
            _logger.LogInformation("Initializing OpenSearch index: {IndexName}", _settings.IndexName);

            var indexExists = await _client.Indices.ExistsAsync(_settings.IndexName, ct: cancellationToken);

            if (!indexExists.Exists)
            {
                var createIndexResponse = await _client.Indices.CreateAsync(_settings.IndexName, c => c
                    .Settings(s => s
                        .Setting("index.knn", true)
                        .NumberOfShards(1)
                        .NumberOfReplicas(0)
                    )
                    .Map<ContentSearchDocument>(m => m
                        .Properties(p => p
                            .Keyword(k => k.Name(n => n.Id))
                            .Text(t => t.Name(n => n.Title))
                            .Text(t => t.Name(n => n.Description))
                            .Keyword(k => k.Name(n => n.Url))
                            .Keyword(k => k.Name(n => n.Type))
                            .Keyword(k => k.Name(n => n.SourceId))
                            .Date(d => d.Name(n => n.PublishedDate))
                            .Date(d => d.Name(n => n.CreatedAt))
                            .Date(d => d.Name(n => n.UpdatedAt))
                            .KnnVector(knn => knn
                                .Name(n => n.Embedding)
                                .Dimension(_settings.EmbeddingDimensions)
                                .Method(m => m
                                    .Name("hnsw")
                                    .SpaceType("cosinesimil")
                                    .Engine("nmslib")
                                    .Parameters(p => p
                                        .Parameter("ef_construction", 512)
                                        .Parameter("m", 16)
                                    )
                                )
                            )
                        )
                    ),
                    cancellationToken
                );

                if (!createIndexResponse.IsValid)
                {
                    if (createIndexResponse.ServerError?.Error?.Type == "content_already_exists_exception")
                    {
                        _logger.LogInformation("Index already exists: {IndexName}", _settings.IndexName);
                        return;
                    }

                    _logger.LogError("Failed to create index: {Error}", createIndexResponse.DebugInformation);
                    throw new Exception($"Failed to create OpenSearch index: {createIndexResponse.DebugInformation}");
                }

                _logger.LogInformation("Successfully created index: {IndexName}", _settings.IndexName);
            }
            else
            {
                _logger.LogInformation("Index already exists: {IndexName}", _settings.IndexName);
            }

            stopwatch.Stop();
            RecordMetric("initialize", "success", stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error initializing OpenSearch index");
            RecordMetric("initialize", "failed", TimeSpan.Zero);
            throw;
        }
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
            .GroupBy(d => d.Id)
            .Select(g => g.Last())
            .ToList();
        if (!documentsList.Any())
        {
            return;
        }

        try
        {
            using var activity = CrsTelemetry.ActivitySource.StartActivity("opensearch.upsert");
            activity?.SetTag(CrsTelemetry.Tags.Dependency, "opensearch");
            activity?.SetTag(CrsTelemetry.Tags.ResultCount, documentsList.Count);
            var stopwatch = Stopwatch.StartNew();
            var searchDocuments = documentsList.Select(ToSearchDocument).ToList();
            var contentIds = documentsList.Select(d => d.Id.ToString()).ToList();

            // OpenSearch Serverless vector collections don't allow custom _id values.
            // Remove any existing docs with matching IDs before indexing.
            var deleteResponse = await _client.DeleteByQueryAsync<ContentSearchDocument>(d => d
                    .Index(_settings.IndexName)
                    .Query(q => q
                        .Terms(t => t
                            .Field(f => f.Id)
                            .Terms(contentIds)
                        )
                    ),
                cancellationToken);

            if (!deleteResponse.IsValid)
            {
                _logger.LogWarning("Pre-delete by query failed: {Error}", deleteResponse.DebugInformation);
            }

            var bulkResponse = await _client.BulkAsync(b => b
                .Index(_settings.IndexName)
                .IndexMany(searchDocuments),
                cancellationToken
            );

            if (!bulkResponse.IsValid)
            {
                _logger.LogError("Bulk upsert failed: {Error}", bulkResponse.DebugInformation);
            }

            var successCount = bulkResponse.Items.Count(i => i.IsValid);
            var failureCount = bulkResponse.Items.Count(i => !i.IsValid);

            _logger.LogInformation(
                "Upserted {Count} documents to index, {Succeeded} succeeded, {Failed} failed",
                documentsList.Count,
                successCount,
                failureCount);

            foreach (var item in bulkResponse.Items.Where(i => !i.IsValid))
            {
                _logger.LogWarning(
                    "Failed to index document {Id}: {Error}",
                    item.Id,
                    item.Error?.Reason);
            }

            stopwatch.Stop();
            RecordMetric("upsert", failureCount == 0 ? "success" : "partial_failure", stopwatch.Elapsed, documentsList.Count);
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
            using var activity = CrsTelemetry.ActivitySource.StartActivity("opensearch.delete");
            activity?.SetTag(CrsTelemetry.Tags.Dependency, "opensearch");
            var stopwatch = Stopwatch.StartNew();
            var response = await _client.DeleteByQueryAsync<ContentSearchDocument>(d => d
                    .Index(_settings.IndexName)
                    .Query(q => q
                        .Term(t => t
                            .Field(f => f.Id)
                            .Value(contentId.ToString())
                        )
                    ),
                cancellationToken);

            if (response.IsValid)
            {
                _logger.LogInformation("Deleted document {ContentId} from index", contentId);
            }
            else
            {
                _logger.LogWarning("Failed to delete document {ContentId}: {Error}",
                    contentId, response.DebugInformation);
            }

            stopwatch.Stop();
            RecordMetric("delete", response.IsValid ? "success" : "failed", stopwatch.Elapsed);
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
            using var activity = CrsTelemetry.ActivitySource.StartActivity("opensearch.search");
            activity?.SetTag(CrsTelemetry.Tags.Dependency, "opensearch");
            activity?.SetTag(CrsTelemetry.Tags.FeedType, request.ContentType?.ToString());
            var stopwatch = Stopwatch.StartNew();
            // Build filter queries
            var mustQueries = new List<Func<QueryContainerDescriptor<ContentSearchDocument>, QueryContainer>>();
            var mustNotQueries = new List<Func<QueryContainerDescriptor<ContentSearchDocument>, QueryContainer>>();

            if (request.ContentType.HasValue)
            {
                mustQueries.Add(q => q.Term(t => t.Field(f => f.Type).Value(request.ContentType.Value.ToString())));
            }

            if (request.SourceIds != null && request.SourceIds.Any())
            {
                mustQueries.Add(q => q.Terms(t => t.Field(f => f.SourceId).Terms(request.SourceIds.Select(id => id.ToString()))));
            }

            if (request.PublishedAfter.HasValue)
            {
                mustQueries.Add(q => q.DateRange(dr => dr.Field(f => f.PublishedDate).GreaterThanOrEquals(request.PublishedAfter.Value)));
            }

            if (request.PublishedBefore.HasValue)
            {
                mustQueries.Add(q => q.DateRange(dr => dr.Field(f => f.PublishedDate).LessThanOrEquals(request.PublishedBefore.Value)));
            }

            if (request.ExcludeContentIds != null && request.ExcludeContentIds.Any())
            {
                mustNotQueries.Add(q => q.Terms(t => t
                    .Field(f => f.Id)
                    .Terms(request.ExcludeContentIds.Select(id => id.ToString()))));
            }

            // Build the k-NN query with filters
            var searchResponse = await _client.SearchAsync<ContentSearchDocument>(s => s
                .Index(_settings.IndexName)
                .Size(request.TopK)
                .Query(q => q
                    .Bool(b =>
                    {
                        var boolQuery = b
                            .Must(mustQueries.ToArray())
                            .MustNot(mustNotQueries.ToArray());

                        // Add k-NN query
                        boolQuery.Filter(f => f
                            .Knn(knn => knn
                                .Field(field => field.Embedding)
                                .Vector(request.QueryVector)
                                .K(request.TopK)
                            )
                        );

                        return boolQuery;
                    })
                ),
                cancellationToken
            );

            if (!searchResponse.IsValid)
            {
                _logger.LogError("Vector search failed: {Error}", searchResponse.DebugInformation);
                return new List<VectorSearchResult>();
            }

            var results = searchResponse.Hits
                .Where(hit => !request.MinimumScore.HasValue || hit.Score >= request.MinimumScore.Value)
                .Select(hit => new VectorSearchResult
                {
                    ContentId = Guid.Parse(hit.Source.Id),
                    SimilarityScore = hit.Score ?? 0.0
                })
                .ToList();

            _logger.LogInformation(
                "Vector search returned {Count} results (requested {TopK})",
                results.Count,
                request.TopK);

            stopwatch.Stop();
            RecordMetric("search", "success", stopwatch.Elapsed, results.Count);

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
            var stopwatch = Stopwatch.StartNew();
            var response = await _client.CountAsync<ContentSearchDocument>(c => c
                .Index(_settings.IndexName),
                cancellationToken
            );

            stopwatch.Stop();
            RecordMetric("count", "success", stopwatch.Elapsed);

            return response.Count;
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
        const int pageSize = 1000;
        var documentIds = new HashSet<Guid>();
        object[]? searchAfter = null;

        try
        {
            var stopwatch = Stopwatch.StartNew();
            while (true)
            {
                var response = await _client.SearchAsync<ContentSearchDocument>(s =>
                {
                    var descriptor = s
                        .Index(_settings.IndexName)
                        .Size(pageSize)
                        .Sort(sort => sort
                            .Ascending(f => f.CreatedAt)
                            .Ascending(f => f.Id));

                    if (searchAfter != null)
                    {
                        descriptor = descriptor.SearchAfter(searchAfter);
                    }

                    return descriptor;
                }, cancellationToken);

                if (!response.IsValid)
                {
                    _logger.LogError("Failed to list indexed document IDs: {Error}", response.DebugInformation);
                    throw new Exception($"Failed to list indexed document IDs: {response.DebugInformation}");
                }

                if (!response.Hits.Any())
                {
                    break;
                }

                foreach (var hit in response.Hits)
                {
                    if (Guid.TryParse(hit.Source.Id, out var contentId))
                    {
                        documentIds.Add(contentId);
                    }
                }

                var lastSort = response.Hits.Last().Sorts;
                if (lastSort == null || lastSort.Count == 0)
                {
                    break;
                }

                searchAfter = lastSort.ToArray();
            }

            _logger.LogInformation("Loaded {Count} indexed document IDs from OpenSearch", documentIds.Count);
            stopwatch.Stop();
            RecordMetric("list_ids", "success", stopwatch.Elapsed, documentIds.Count);
            return documentIds;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing indexed document IDs");
            RecordMetric("list_ids", "failed", TimeSpan.Zero);
            throw;
        }
    }

    private void RecordMetric(string operation, string outcome, TimeSpan duration, int? count = null)
    {
        var context = new MetricContext(
            Dimensions: new Dictionary<string, string>
            {
                ["Dependency"] = "OpenSearch",
                ["Operation"] = operation,
                ["Outcome"] = outcome
            },
            Properties: count.HasValue ? new Dictionary<string, object?> { ["Count"] = count.Value } : null);

        _metrics.Increment("dependency.call.count", context: context);
        _metrics.RecordDuration("dependency.call.duration", duration, context);
        if (count.HasValue)
        {
            _metrics.RecordValue("opensearch.result.count", count.Value, "Count", context);
        }

        if (outcome is "failed" or "partial_failure")
        {
            _metrics.Increment("dependency.failure.count", context: context);
        }
    }

    private static ContentSearchDocument ToSearchDocument(ContentDocument document)
    {
        return new ContentSearchDocument
        {
            Id = document.Id.ToString(),
            Title = document.Title,
            Description = document.Description ?? string.Empty,
            Url = document.Url,
            Type = document.Type.ToString(),
            SourceId = document.SourceId?.ToString() ?? string.Empty,
            PublishedDate = document.PublishedDate ?? document.CreatedAt,
            CreatedAt = document.CreatedAt,
            UpdatedAt = document.UpdatedAt,
            Embedding = document.Embedding
        };
    }
}

/// <summary>
/// Internal document type for OpenSearch indexing.
/// </summary>
internal class ContentSearchDocument
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string SourceId { get; set; } = string.Empty;
    public DateTime PublishedDate { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public float[] Embedding { get; set; } = Array.Empty<float>();
}
