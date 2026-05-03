using System.Collections.Specialized;
using System.Web;
using Crs.Core.Enums;

namespace Crs.Llm.Validation;

/// <summary>
/// Heuristics for excluding non-content pages (tag indexes, archives, categories, etc.)
/// from ingestion results.
/// </summary>
public static class ContentUrlPolicy
{
    private static readonly HashSet<string> NonContentPathSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "tag", "tags", "category", "categories", "archive", "archives", "author", "authors",
        "topic", "topics", "label", "labels", "search", "page", "pages"
    };

    private static readonly HashSet<string> NonContentQueryKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "tag", "tags", "category", "categories", "archive", "author", "topic", "label", "page", "q", "search"
    };

    public static bool IsLikelyContentUrl(string? candidateUrl, ContentType contentType, string? sourceUrl = null)
    {
        if (string.IsNullOrWhiteSpace(candidateUrl) || !Uri.TryCreate(candidateUrl, UriKind.Absolute, out var candidate))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(sourceUrl) &&
            Uri.TryCreate(sourceUrl, UriKind.Absolute, out var source) &&
            Uri.Compare(candidate, source, UriComponents.SchemeAndServer | UriComponents.PathAndQuery, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0)
        {
            return false;
        }

        var query = HttpUtility.ParseQueryString(candidate.Query);
        if (ContainsNonContentQuery(query))
        {
            return false;
        }

        if (contentType != ContentType.BlogPost)
        {
            return true;
        }

        var segments = candidate.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (segments.Length == 0)
        {
            return false;
        }

        if (segments.Any(NonContentPathSegments.Contains))
        {
            return false;
        }

        // Reject date/archive folders like /2026/01/30/ with no slug.
        if (IsDateOnlyPath(segments))
        {
            return false;
        }

        return true;
    }

    private static bool ContainsNonContentQuery(NameValueCollection query)
    {
        foreach (var key in query.AllKeys)
        {
            if (key is null)
            {
                continue;
            }

            if (NonContentQueryKeys.Contains(key))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsDateOnlyPath(string[] segments)
    {
        if (segments.Length is < 1 or > 3)
        {
            return false;
        }

        var numericSegments = segments.Where(s => s.All(char.IsDigit)).ToArray();
        if (numericSegments.Length != segments.Length)
        {
            return false;
        }

        return segments.Length switch
        {
            1 => segments[0].Length == 4,
            2 => segments[0].Length == 4 && segments[1].Length <= 2,
            3 => segments[0].Length == 4 && segments[1].Length <= 2 && segments[2].Length <= 2,
            _ => false
        };
    }
}
