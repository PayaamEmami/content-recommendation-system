using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Crs.Core.Interfaces;

namespace Crs.Infrastructure.Services;

/// <summary>
/// Implementation of IContentFetcherService that fetches and minimally cleans HTML content and RSS/XML feeds.
/// </summary>
public class ContentFetcherService : IContentFetcherService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<ContentFetcherService> _logger;

    private const int TimeoutSeconds = 30;
    private const int MaxRedirects = 5;

    public ContentFetcherService(
        HttpClient httpClient,
        ILogger<ContentFetcherService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;

        // Configure HttpClient
        _httpClient.Timeout = TimeSpan.FromSeconds(TimeoutSeconds);
        _httpClient.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (compatible; CRS-Bot/1.0; +https://github.com/payaam/content-recommendation-system)");
        _httpClient.DefaultRequestHeaders.Add("Accept",
            "text/html,application/xhtml+xml,application/xml,application/rss+xml,application/atom+xml;q=0.9,*/*;q=0.8");
    }

    public async Task<ContentFetchResult> FetchContentAsync(string url, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!UrlSafety.IsSafePublicHttpUrl(url, out var rejectionReason))
            {
                _logger.LogWarning("Blocked unsafe fetch URL {Url}: {Reason}", url, rejectionReason);
                return new ContentFetchResult
                {
                    Success = false,
                    StatusCode = 400,
                    ErrorMessage = rejectionReason ?? "URL is not allowed"
                };
            }

            _logger.LogInformation("Fetching content from URL: {Url}", url);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            // Re-check the final URI after redirects so a public host cannot bounce
            // into link-local / private ranges (classic SSRF bypass).
            if (response.RequestMessage?.RequestUri is { } finalUri &&
                !UrlSafety.IsSafePublicHttpUrl(finalUri.AbsoluteUri, out var redirectReason))
            {
                _logger.LogWarning(
                    "Blocked unsafe redirect target {Url} from {OriginalUrl}: {Reason}",
                    finalUri,
                    url,
                    redirectReason);
                return new ContentFetchResult
                {
                    Success = false,
                    StatusCode = 400,
                    ErrorMessage = redirectReason ?? "Redirect target is not allowed"
                };
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to fetch content from {Url}: {StatusCode}",
                    url, response.StatusCode);

                return new ContentFetchResult
                {
                    Success = false,
                    StatusCode = (int)response.StatusCode,
                    ErrorMessage = $"HTTP {response.StatusCode}: {response.ReasonPhrase}"
                };
            }

            var html = await response.Content.ReadAsStringAsync(cancellationToken);

            // Minimal cleaning: Remove script and style tags to reduce token usage
            var cleanedHtml = CleanHtml(html);

            _logger.LogInformation("Successfully fetched content from {Url} ({Length} characters after cleaning)",
                url, cleanedHtml.Length);

            return new ContentFetchResult
            {
                Success = true,
                Content = cleanedHtml,
                StatusCode = (int)response.StatusCode
            };
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error fetching content from {Url}", url);
            return new ContentFetchResult
            {
                Success = false,
                ErrorMessage = $"HTTP error: {ex.Message}"
            };
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogError(ex, "Timeout fetching content from {Url}", url);
            return new ContentFetchResult
            {
                Success = false,
                ErrorMessage = "Request timed out"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error fetching content from {Url}", url);
            return new ContentFetchResult
            {
                Success = false,
                ErrorMessage = $"Unexpected error: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Performs HTML cleaning by removing non-essential elements.
    /// This significantly reduces token usage while preserving content for ChatGPT.
    /// </summary>
    private string CleanHtml(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return string.Empty;

        // Remove head section entirely (meta tags, links, etc. not useful for content extraction)
        html = Regex.Replace(html, @"<head\b[^>]*>.*?<\/head>",
            "", RegexOptions.IgnoreCase | RegexOptions.Singleline);

        // Remove script tags and their content
        html = Regex.Replace(html, @"<script\b[^<]*(?:(?!<\/script>)<[^<]*)*<\/script>",
            "", RegexOptions.IgnoreCase);

        // Remove style tags and their content
        html = Regex.Replace(html, @"<style\b[^<]*(?:(?!<\/style>)<[^<]*)*<\/style>",
            "", RegexOptions.IgnoreCase);

        // Remove SVG elements (often large and not useful for text extraction)
        html = Regex.Replace(html, @"<svg\b[^>]*>.*?<\/svg>",
            "", RegexOptions.IgnoreCase | RegexOptions.Singleline);

        // Remove nav elements (navigation menus)
        html = Regex.Replace(html, @"<nav\b[^>]*>.*?<\/nav>",
            "", RegexOptions.IgnoreCase | RegexOptions.Singleline);

        // Remove footer elements
        html = Regex.Replace(html, @"<footer\b[^>]*>.*?<\/footer>",
            "", RegexOptions.IgnoreCase | RegexOptions.Singleline);

        // Remove HTML comments
        html = Regex.Replace(html, @"<!--.*?-->", "", RegexOptions.Singleline);

        // Collapse multiple whitespace/newlines into single spaces
        html = Regex.Replace(html, @"\s+", " ");

        return html.Trim();
    }
}

