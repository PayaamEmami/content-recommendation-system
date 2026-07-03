using System.Text.Json;
using Microsoft.Extensions.Logging;
using Crs.Core.Enums;
using Crs.Llm.Models;

namespace Crs.Llm.Services;

/// <summary>
/// Parses the LLM's JSON response into an <see cref="IngestionResult"/>.
/// </summary>
/// <remarks>
/// Parse-level issues (missing/malformed JSON, parse errors) intentionally return
/// <see cref="IngestionResult.Success"/> = <c>true</c> with an empty content list. Some
/// sources legitimately contain no extractable items, and callers should not treat that as a
/// pipeline failure. Only infrastructure-level failures (fetch error, unexpected exception)
/// are surfaced as failures by <see cref="IngestionAgent"/>.
/// </remarks>
internal static class IngestionResponseParser
{
    public static IngestionResult Parse(LlmResponse response, string sourceUrl, ILogger logger)
    {
        var llmResponse = response.Content;

        try
        {
            // Log last 200 chars for debugging truncation
            if (llmResponse.Length > 200)
            {
                var lastChars = llmResponse.Substring(llmResponse.Length - 200);
                logger.LogDebug("Last 200 chars of response: {LastChars}", lastChars);
            }

            // Extract the JSON object from the response. The LLM is called with
            // response_format=json_object, so the payload is already valid JSON; this
            // trims any stray wrapping text defensively.
            var jsonStart = llmResponse.IndexOf('{');
            var jsonEnd = llmResponse.LastIndexOf('}');

            if (jsonStart == -1 || jsonEnd == -1)
            {
                logger.LogWarning(
                    "No JSON found in LLM response for {SourceUrl}. FinishReason: {FinishReason}, Response preview: {Response}",
                    sourceUrl, response.FinishReason, llmResponse?.Substring(0, Math.Min(200, llmResponse?.Length ?? 0)));

                return EmptyResult(sourceUrl, $"No JSON found (finish_reason: {response.FinishReason})");
            }

            var jsonString = llmResponse.Substring(jsonStart, jsonEnd - jsonStart + 1);
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
                        var extractedItem = ParseExtractedContent(item, logger);
                        if (extractedItem != null)
                        {
                            extractedContent.Add(extractedItem);
                            logger.LogInformation("Parsed content #{Index}: {Title} (Type: {Type}, URL: {Url})",
                                arrayIndex, extractedItem.Title, extractedItem.Type, extractedItem.Url);
                        }
                        else
                        {
                            logger.LogWarning("Failed to parse content #{Index}: missing title or url. JSON: {Json}",
                                arrayIndex, item.GetRawText());
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Exception parsing content #{Index} from JSON: {Json}",
                            arrayIndex, item.GetRawText());
                    }
                }

                logger.LogInformation(
                    "Successfully parsed {ContentCount} content from {SourceUrl} (JSON had {ArrayLength} items)",
                    extractedContent.Count, sourceUrl, contentArray.GetArrayLength());
            }

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
            logger.LogWarning(
                ex,
                "JSON parse error for {SourceUrl}. FinishReason: {FinishReason}, Last 100 chars: {LastChars}",
                sourceUrl,
                response.FinishReason,
                llmResponse.Length > 100 ? llmResponse.Substring(llmResponse.Length - 100) : llmResponse);

            return EmptyResult(sourceUrl, $"JSON parse error: {ex.Message}");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error parsing ingestion result from {SourceUrl}", sourceUrl);
            return EmptyResult(sourceUrl, $"Could not parse response: {ex.Message}");
        }
    }

    private static IngestionResult EmptyResult(string sourceUrl, string errorMessage) => new()
    {
        Success = true,
        SourceUrl = sourceUrl,
        Content = new List<ExtractedContent>(),
        TotalFound = 0,
        NewContent = 0,
        DuplicatesSkipped = 0,
        ErrorMessage = errorMessage
    };

    private static ExtractedContent? ParseExtractedContent(JsonElement json, ILogger logger)
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
                    logger.LogDebug("Parsed type '{TypeString}' as {ContentType}", typeString, contentType);
                }
                else
                {
                    logger.LogWarning("Failed to parse type string '{TypeString}' - using default", typeString);
                }
            }
        }

        return content;
    }
}
