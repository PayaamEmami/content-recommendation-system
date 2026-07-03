using Crs.Core.Enums;

namespace Crs.Web.Services;

/// <summary>
/// Shared display formatting helpers for the Web UI. Centralizes the content-type
/// labels and relative-date formats that were previously duplicated across pages.
/// </summary>
/// <remarks>
/// The feed/sources pages use plural content-type labels ("Papers", "Videos", "Blogs")
/// while the history/preferences pages use the singular "Blog Post" convention; both are
/// kept here so each page's existing output is preserved.
/// </remarks>
public static class DisplayFormatters
{
    /// <summary>
    /// Plural content-type label used by the feed and sources pages.
    /// </summary>
    public static string ContentTypeLabel(ContentType type) => type switch
    {
        ContentType.Paper => "Papers",
        ContentType.Video => "Videos",
        ContentType.BlogPost => "Blogs",
        _ => type.ToString()
    };

    /// <summary>
    /// Relative date used by the feed page: hours/days/weeks ago, then a full date.
    /// </summary>
    public static string RelativeDate(DateTime date)
    {
        var span = DateTime.UtcNow - date;

        if (span.TotalDays < 1)
            return $"{(int)span.TotalHours}h ago";
        if (span.TotalDays < 7)
            return $"{(int)span.TotalDays}d ago";
        if (span.TotalDays < 30)
            return $"{(int)(span.TotalDays / 7)}w ago";

        return date.ToString("MMM d, yyyy");
    }

    /// <summary>
    /// Compact relative date used by the sources list: "just now", hours/days ago,
    /// then a short month/day.
    /// </summary>
    public static string CompactRelativeDate(DateTime date)
    {
        var span = DateTime.UtcNow - date;
        if (span.TotalHours < 1) return "just now";
        if (span.TotalDays < 1) return $"{(int)span.TotalHours}h ago";
        if (span.TotalDays < 7) return $"{(int)span.TotalDays}d ago";
        return date.ToString("MMM d");
    }
}
