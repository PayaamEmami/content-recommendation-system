using System.Text.Json;
using Microsoft.Extensions.Logging;
using Crs.Core.Enums;
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
            var systemPrompt = GetSystemPrompt();
            var userMessage = GetUserMessage(sourceUrl, contentResult.Content);

            var response = await _llmClient.SendMessageAsync(
                systemPrompt,
                userMessage,
                tools: null,
                cancellationToken);

            // Log token usage and finish reason
            _logger.LogInformation(
                "OpenAI response: {CompletionTokens} completion tokens, finish_reason: {FinishReason}",
                response.CompletionTokens, response.FinishReason);

            // Step 3: Parse the response
            var result = ParseIngestionResult(response, sourceUrl);

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


    // Note: parse-level issues (missing/malformed JSON, parse errors) intentionally return
    // Success = true with an empty Content list. Some sources legitimately contain no
    // extractable items, and we don't want callers to treat that as a pipeline failure.
    // Only infrastructure-level failures (fetch error, unexpected exception) return Success = false.
    private IngestionResult ParseIngestionResult(LlmResponse response, string sourceUrl)
    {
        var llmResponse = response.Content;

        try
        {
            // Log last 200 chars for debugging truncation
            if (llmResponse.Length > 200)
            {
                var lastChars = llmResponse.Substring(llmResponse.Length - 200);
                _logger.LogDebug("Last 200 chars of response: {LastChars}", lastChars);
            }

            // Try to extract JSON from the response
            var jsonStart = llmResponse.IndexOf('{');
            var jsonEnd = llmResponse.LastIndexOf('}');

            if (jsonStart == -1 || jsonEnd == -1)
            {
                _logger.LogWarning(
                    "No JSON found in LLM response for {SourceUrl}. FinishReason: {FinishReason}, Response preview: {Response}",
                    sourceUrl, response.FinishReason, llmResponse?.Substring(0, Math.Min(200, llmResponse?.Length ?? 0)));

                // Return success with 0 content rather than failing - this is expected for some sources
                return new IngestionResult
                {
                    Success = true,
                    SourceUrl = sourceUrl,
                    Content = new List<ExtractedContent>(),
                    TotalFound = 0,
                    NewContent = 0,
                    DuplicatesSkipped = 0,
                    ErrorMessage = $"No JSON found (finish_reason: {response.FinishReason})"
                };
            }

            var jsonString = llmResponse.Substring(jsonStart, jsonEnd - jsonStart + 1);

            // Validate JSON is complete (basic check)
            var openBraces = jsonString.Count(c => c == '{');
            var closeBraces = jsonString.Count(c => c == '}');
            var openBrackets = jsonString.Count(c => c == '[');
            var closeBrackets = jsonString.Count(c => c == ']');

            if (openBraces != closeBraces || openBrackets != closeBrackets)
            {
                _logger.LogWarning(
                    "Malformed JSON detected for {SourceUrl}: braces {OpenBrace}/{CloseBrace}, brackets {OpenBracket}/{CloseBracket}",
                    sourceUrl, openBraces, closeBraces, openBrackets, closeBrackets);

                return new IngestionResult
                {
                    Success = true,
                    SourceUrl = sourceUrl,
                    Content = new List<ExtractedContent>(),
                    TotalFound = 0,
                    NewContent = 0,
                    DuplicatesSkipped = 0,
                    ErrorMessage = "Malformed JSON (likely truncated)"
                };
            }

            var parsedData = JsonSerializer.Deserialize<JsonElement>(jsonString);

            var extractedContent = new List<ExtractedContent>();

            if (parsedData.TryGetProperty("content", out var contentArray))
            {
                int arrayIndex = 0;
                foreach (var item in contentArray.EnumerateArray())
                {
                    arrayIndex++;
                    try
                    {
                        var extractedItem = ParseExtractedContent(item);
                        if (extractedItem != null)
                        {
                            extractedContent.Add(extractedItem);
                            _logger.LogInformation("Parsed content #{Index}: {Title} (Type: {Type}, URL: {Url})",
                                arrayIndex, extractedItem.Title, extractedItem.Type, extractedItem.Url);
                        }
                        else
                        {
                            _logger.LogWarning("Failed to parse content #{Index}: missing title or url. JSON: {Json}",
                                arrayIndex, item.GetRawText());
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Exception parsing content #{Index} from JSON: {Json}",
                            arrayIndex, item.GetRawText());
                    }
                }
            }

            _logger.LogInformation(
                "Successfully parsed {ContentCount} content from {SourceUrl} (JSON had {ArrayLength} items)",
                extractedContent.Count, sourceUrl, contentArray.GetArrayLength());

            return new IngestionResult
            {
                Success = true,
                SourceUrl = sourceUrl,
                Content = extractedContent,
                TotalFound = extractedContent.Count,
                NewContent = extractedContent.Count,
                DuplicatesSkipped = 0
            };
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(
                ex,
                "JSON parse error for {SourceUrl}. FinishReason: {FinishReason}, Last 100 chars: {LastChars}",
                sourceUrl,
                response.FinishReason,
                llmResponse.Length > 100 ? llmResponse.Substring(llmResponse.Length - 100) : llmResponse);

            return new IngestionResult
            {
                Success = true,
                SourceUrl = sourceUrl,
                Content = new List<ExtractedContent>(),
                TotalFound = 0,
                NewContent = 0,
                DuplicatesSkipped = 0,
                ErrorMessage = $"JSON parse error: {ex.Message}"
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error parsing ingestion result from {SourceUrl}", sourceUrl);
            // Return success with 0 content rather than failing
            return new IngestionResult
            {
                Success = true,
                SourceUrl = sourceUrl,
                Content = new List<ExtractedContent>(),
                TotalFound = 0,
                NewContent = 0,
                DuplicatesSkipped = 0,
                ErrorMessage = $"Could not parse response: {ex.Message}"
            };
        }
    }

    private ExtractedContent? ParseExtractedContent(JsonElement json)
    {
        if (!json.TryGetProperty("title", out var title) ||
            !json.TryGetProperty("url", out var url))
        {
            return null;
        }

        var content = new ExtractedContent
        {
            Title = title.GetString() ?? string.Empty,
            Url = url.GetString() ?? string.Empty,
            Description = json.TryGetProperty("description", out var desc)
                ? desc.GetString() ?? string.Empty : string.Empty,
            Type = ContentType.Paper // Default if not specified
        };

        // Parse content type (override default if present)
        if (json.TryGetProperty("type", out var type))
        {
            var typeString = type.GetString();
            if (!string.IsNullOrEmpty(typeString))
            {
                if (Enum.TryParse<ContentType>(typeString, true, out var contentType))
                {
                    content.Type = contentType;
                    _logger.LogDebug("Parsed type '{TypeString}' as {ContentType}", typeString, contentType);
                }
                else
                {
                    _logger.LogWarning("Failed to parse type string '{TypeString}' - using default", typeString);
                }
            }
        }

        return content;
    }

    private string GetSystemPrompt()
    {
        return @"You are a learning content extraction assistant. You MUST respond with ONLY valid JSON - no other text.

Extract learning content from the provided HTML/RSS/XML content. If nothing can be extracted, return { ""content"": [] }.

OUTPUT SCHEMA (strict):
{
  ""content"": [
    {
      ""title"": string,
      ""url"": string (absolute URL),
      ""description"": string,
      ""type"": ""Paper"" | ""Video"" | ""BlogPost""
    }
  ]
}

CRITICAL CONSTRAINTS:
1. Extract up to 20 most important/recent items
2. DESCRIPTIONS: Write a clear, concise description (max 200 characters) that explains what this content is about. Use information from the title, abstract, or summary if available. Do not use promotional language or random text from the page.
3. URLS: Must be absolute (not relative)
4. DEDUPLICATE: Same URL = skip duplicate
5. VALID JSON: Response must be parseable JSON, properly closed braces/brackets

EXTRACTION RULES:
- Only extract explicitly present content (no invention)
- Each item MUST have: non-empty title, absolute URL, description
- Description: A factual summary of what the content teaches or discusses. Prioritize abstracts, summaries, or descriptions from the content. If none exist, create a brief description based on the title and context.
- De-duplicate by URL (keep first occurrence)
- Select the most valuable/recent items

TYPE GUIDANCE:
- Paper: academic/research papers, preprints (arXiv, DOI pages)
- Video: individual video watch pages
- BlogPost: articles, posts, tutorials

EXCLUDE:
- Source/feed/channel itself (only extract individual items)
- Navigation, ads, indexes, search pages, login pages
- URLs matching the source URL provided
- RSS/XML: extract <item>/<entry> only, NOT feed metadata
- YouTube: extract watch URLs only, NOT channel URLs

FAILURE MODE: If extraction fails or no valid items found, return { ""content"": [] }";
    }

    private string GetUserMessage(string sourceUrl, string htmlContent)
    {
        // Truncate content if it's too long to avoid token limits
        const int maxHtmlLength = 50000; // ~12.5k tokens
        var truncatedHtml = htmlContent.Length > maxHtmlLength
            ? htmlContent.Substring(0, maxHtmlLength) + "\n\n[...Content truncated for length...]"
            : htmlContent;

        return $@"Extract learning content from this content.

Source URL: {sourceUrl}

Content:
{truncatedHtml}

REQUIREMENTS:
- Maximum 20 content
- Each description: Write a clear, factual summary (max 200 chars) of what the content teaches or discusses. Use abstracts/summaries if available, otherwise derive from title and context.
- URLs must be absolute
- De-duplicate by URL
- Return ONLY valid JSON

Respond with JSON only (no markdown, no explanation):";
    }
}
