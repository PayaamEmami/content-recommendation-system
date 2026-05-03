using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Crs.Core.Entities;
using Crs.Core.Enums;
using Crs.Core.Exceptions;
using Crs.Core.Interfaces;
using Crs.Core.Models;
using Crs.Core.Observability;
using Crs.Llm.Models;
using Crs.Llm.Validation;

namespace Crs.Llm.Services;

/// <summary>
/// Default implementation of <see cref="ISourceIngestionService"/>.
/// Owns the per-source ingestion pipeline previously inlined in SourceIngestionJob.
/// </summary>
public class SourceIngestionService : ISourceIngestionService
{
    private readonly IIngestionAgent _ingestionAgent;
    private readonly IContentRepository _contentRepository;
    private readonly IEmbeddingService _embeddingService;
    private readonly IVectorStore _vectorStore;
    private readonly ILogger<SourceIngestionService> _logger;
    private readonly IObservabilityMetrics _metrics;

    public SourceIngestionService(
        IIngestionAgent ingestionAgent,
        IContentRepository contentRepository,
        IEmbeddingService embeddingService,
        IVectorStore vectorStore,
        ILogger<SourceIngestionService> logger,
        IObservabilityMetrics metrics)
    {
        _ingestionAgent = ingestionAgent;
        _contentRepository = contentRepository;
        _embeddingService = embeddingService;
        _vectorStore = vectorStore;
        _logger = logger;
        _metrics = metrics;
    }

    public async Task<SourceIngestionSummary> IngestSourceAsync(
        Source source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        _logger.LogInformation("Ingesting from source: {SourceName} ({SourceUrl})", source.Name, source.Url);
        var stopwatch = Stopwatch.StartNew();

        var ingestionResult = await _ingestionAgent.IngestFromUrlAsync(
            source.Url,
            source.Id,
            cancellationToken);

        if (!ingestionResult.Success)
        {
            _logger.LogWarning(
                "Failed to ingest from source {SourceId}: {Error}",
                source.Id,
                ingestionResult.ErrorMessage);

            return new SourceIngestionSummary
            {
                Success = false,
                ErrorMessage = ingestionResult.ErrorMessage,
                Extracted = ingestionResult.Content.Count
            };
        }

        var newContent = new List<Content>();
        var duplicateCount = 0;
        var errorCount = 0;

        _logger.LogInformation(
            "Attempting to save {Count} extracted content from {SourceName}",
            ingestionResult.Content.Count,
            source.Name);

        foreach (var extractedContent in ingestionResult.Content)
        {
            try
            {
                _logger.LogInformation(
                    "Processing: {Title} (Type: {Type}, URL: {Url})",
                    extractedContent.Title,
                    extractedContent.Type,
                    extractedContent.Url);

                if (string.IsNullOrWhiteSpace(extractedContent.Url))
                {
                    errorCount++;
                    _logger.LogWarning("Skipping content with empty URL: {Title}", extractedContent.Title);
                    continue;
                }

                if (!ContentUrlPolicy.IsLikelyContentUrl(extractedContent.Url, extractedContent.Type, source.Url))
                {
                    _logger.LogInformation(
                        "Skipping non-content URL: {Title} (URL: {Url})",
                        extractedContent.Title,
                        extractedContent.Url);
                    continue;
                }

                if (await _contentRepository.ExistsByUrlAsync(extractedContent.Url, cancellationToken))
                {
                    duplicateCount++;
                    _logger.LogInformation(
                        "Duplicate URL found: {Title} (URL: {Url})",
                        extractedContent.Title,
                        extractedContent.Url);
                    continue;
                }

                var content = CreateContentEntity(extractedContent, source.Id, source.Category);

                _logger.LogInformation(
                    "Attempting to save: {Title} (Type: {Type}, URL: {Url})",
                    content.Title,
                    content.Type,
                    content.Url);

                await _contentRepository.AddAsync(content, cancellationToken);
                newContent.Add(content);

                _logger.LogInformation(
                    "Successfully saved: {Title} (Type: {Type})",
                    content.Title,
                    content.Type);
            }
            catch (DuplicateContentException)
            {
                // Race condition: another caller wrote the same URL between the dedup check and the insert.
                duplicateCount++;
                _logger.LogInformation(
                    "Duplicate (race condition): {Title} (URL: {Url})",
                    extractedContent.Title,
                    extractedContent.Url);
            }
            catch (Exception ex)
            {
                errorCount++;
                _logger.LogError(
                    ex,
                    "Error saving: {Title} (Type: {Type}, URL: {Url}) - {Error}",
                    extractedContent.Title,
                    extractedContent.Type,
                    extractedContent.Url,
                    ex.Message);
            }
        }

        var embeddedCount = 0;
        if (newContent.Count > 0)
        {
            embeddedCount = await EmbedAndIndexContentAsync(newContent, cancellationToken);
        }

        stopwatch.Stop();
        _logger.LogInformation(
            "Completed {SourceName}: {Extracted} extracted, {New} saved, {Duplicates} duplicates, {Errors} errors in {ElapsedMs} ms",
            source.Name,
            ingestionResult.Content.Count,
            newContent.Count,
            duplicateCount,
            errorCount,
            stopwatch.ElapsedMilliseconds);

        return new SourceIngestionSummary
        {
            Success = true,
            Extracted = ingestionResult.Content.Count,
            Saved = newContent.Count,
            Duplicates = duplicateCount,
            Errors = errorCount,
            Embedded = embeddedCount
        };
    }

    private async Task<int> EmbedAndIndexContentAsync(
        List<Content> content,
        CancellationToken cancellationToken)
    {
        try
        {
            var texts = content
                .Select(c => $"{c.Title} {c.Description}".Trim())
                .ToList();

            var embeddings = await _embeddingService.GenerateEmbeddingsAsync(texts, cancellationToken);

            var documents = content.Zip(embeddings, (c, embedding) => new ContentDocument
            {
                Id = c.Id,
                Title = c.Title,
                Description = c.Description,
                Url = c.Url,
                Type = c.Type,
                SourceId = c.SourceId,
                PublishedDate = c.CreatedAt, // Use CreatedAt as the published date for filtering
                CreatedAt = c.CreatedAt,
                UpdatedAt = c.UpdatedAt,
                Embedding = embedding
            }).ToList();

            await _vectorStore.UpsertDocumentsAsync(documents, cancellationToken);

            _logger.LogInformation("Embedded and indexed {Count} content", documents.Count);
            _metrics.RecordValue(
                "ingestion.items.indexed",
                documents.Count,
                "Count",
                BuildEmbeddingContext("success"));

            return documents.Count;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error embedding and indexing content");
            _metrics.Increment("ingestion.failure.count", context: BuildEmbeddingContext("failed"));
            throw;
        }
    }

    private static Content CreateContentEntity(ExtractedContent extracted, Guid sourceId, ContentType sourceCategory)
    {
        // Prefer extracted type; if missing/default, fall back to source category
        var contentType = extracted.Type != default ? extracted.Type : sourceCategory;

        return contentType switch
        {
            ContentType.Paper => new Paper
            {
                Id = Guid.NewGuid(),
                Title = extracted.Title,
                Description = extracted.Description,
                Url = extracted.Url,
                SourceId = sourceId,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            },
            ContentType.Video => new Video
            {
                Id = Guid.NewGuid(),
                Title = extracted.Title,
                Description = extracted.Description,
                Url = extracted.Url,
                SourceId = sourceId,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            },
            ContentType.BlogPost => new BlogPost
            {
                Id = Guid.NewGuid(),
                Title = extracted.Title,
                Description = extracted.Description,
                Url = extracted.Url,
                SourceId = sourceId,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            },
            _ => throw new ArgumentException($"Unknown content type: {extracted.Type}")
        };
    }

    private static MetricContext BuildEmbeddingContext(string outcome)
    {
        return new MetricContext(
            Dimensions: new Dictionary<string, string>
            {
                ["JobName"] = "ingestion",
                ["Operation"] = "embedding",
                ["Outcome"] = outcome
            });
    }
}
