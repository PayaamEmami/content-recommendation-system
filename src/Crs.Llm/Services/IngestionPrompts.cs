namespace Crs.Llm.Services;

/// <summary>
/// Prompt templates used by <see cref="IngestionAgent"/> when asking the LLM to extract
/// learning content from fetched HTML/RSS/XML.
/// </summary>
internal static class IngestionPrompts
{
    /// <summary>Maximum number of characters of source content sent to the LLM (~12.5k tokens).</summary>
    private const int MaxHtmlLength = 50000;

    public const string SystemPrompt = @"You are a learning content extraction assistant. You MUST respond with ONLY valid JSON - no other text.

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

    public static string BuildUserMessage(string sourceUrl, string htmlContent)
    {
        // Truncate content if it's too long to avoid token limits
        var truncatedHtml = htmlContent.Length > MaxHtmlLength
            ? htmlContent.Substring(0, MaxHtmlLength) + "\n\n[...Content truncated for length...]"
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
