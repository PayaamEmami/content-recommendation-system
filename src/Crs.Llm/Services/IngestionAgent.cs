using Microsoft.Extensions.Logging;
using Crs.Core.Interfaces;
using Crs.Llm.Models;

namespace Crs.Llm.Services;

/// <summary>
/// LLM-based ingestion agent that extracts learning content from URLs.
/// Fetches HTML content and uses ChatGPT to extract structured content data.
/// </summary>
public class IngestionAgent : IIngestionAgent
{
    private readonly ILlmClient _llmClient;
    private readonly IContentFetcherService _contentFetcher;
    private readonly ILogger<IngestionAgent> _logger;

    public IngestionAgent(
        ILlmClient llmClient,
        IContentFetcherService contentFetcher,
        ILogger<IngestionAgent> logger)
    {
        _llmClient = llmClient;
        _contentFetcher = contentFetcher;
        _logger = logger;
    }

    public async Task<IngestionResult> IngestFromUrlAsync(
        string sourceUrl,
        Guid? sourceId = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Starting ingestion from URL: {SourceUrl}", sourceUrl);

            // Step 1: Fetch content (HTML or RSS/XML)
            var contentResult = await _contentFetcher.FetchContentAsync(sourceUrl, cancellationToken);

            if (!contentResult.Success || string.IsNullOrWhiteSpace(contentResult.Content))
            {
                _logger.LogWarning("Failed to fetch content from {SourceUrl}: {Error}",
                    sourceUrl, contentResult.ErrorMessage);

                return new IngestionResult
                {
                    Success = false,
                    SourceUrl = sourceUrl,
                    Content = new List<ExtractedContent>(),
                    TotalFound = 0,
                    NewContent = 0,
                    DuplicatesSkipped = 0,
                    ErrorMessage = contentResult.ErrorMessage ?? "Failed to fetch content"
                };
            }

            // Step 2: Send content to ChatGPT for content extraction
            var response = await _llmClient.SendMessageAsync(
                IngestionPrompts.SystemPrompt,
                IngestionPrompts.BuildUserMessage(sourceUrl, contentResult.Content),
                tools: null,
                cancellationToken);

            // Log token usage and finish reason
            _logger.LogInformation(
                "OpenAI response: {CompletionTokens} completion tokens, finish_reason: {FinishReason}",
                response.CompletionTokens, response.FinishReason);

            // Step 3: Parse the response
            var result = IngestionResponseParser.Parse(response, sourceUrl, _logger);

            _logger.LogInformation(
                "Ingestion completed from {SourceUrl}: {TotalFound} content found",
                sourceUrl, result.TotalFound);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during ingestion from {SourceUrl}", sourceUrl);
            return new IngestionResult
            {
                Success = false,
                SourceUrl = sourceUrl,
                Content = new List<ExtractedContent>(),
                TotalFound = 0,
                NewContent = 0,
                DuplicatesSkipped = 0,
                ErrorMessage = $"Ingestion error: {ex.Message}"
            };
        }
    }
}
