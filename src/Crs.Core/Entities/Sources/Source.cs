using Crs.Core.Enums;

namespace Crs.Core.Entities;

/// <summary>
/// Represents a URL-based source from which content is ingested.
/// Examples: RSS feeds, YouTube channels, blogs, newsletters.
/// </summary>
public class Source
{
    public Guid Id { get; set; }

    /// <summary>
    /// Foreign key of the user who owns/configured this source.
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// Navigation property to the owning user.
    /// </summary>
    public User User { get; set; } = null!;

    /// <summary>
    /// The name of the source (e.g., "ArXiv AI Papers", "3Blue1Brown").
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The URL of the source (RSS feed, channel URL, etc.).
    /// </summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// Optional description of what this source provides.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// The category/type of content this source provides.
    /// </summary>
    public ContentType Category { get; set; }

    /// <summary>
    /// Whether this source is currently active for ingestion.
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// When this source was added to the system.
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// When this source was last updated.
    /// </summary>
    public DateTime UpdatedAt { get; set; }

    // Navigation properties

    /// <summary>
    /// Content that has been ingested from this source.
    /// </summary>
    public List<Content> Content { get; set; } = new();
}
