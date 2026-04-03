using Crs.Api.DTOs.Sources.Responses;
using Crs.Core.Enums;

namespace Crs.Api.DTOs.Content.Responses;

/// <summary>
/// Response model for content information.
/// </summary>
public class ContentResponse
{
    /// <summary>
    /// The content's unique identifier.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// The title of the content.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// A description of the content.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// The URL where the content can be accessed.
    /// </summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// The type of content.
    /// </summary>
    public ContentType Type { get; set; }

    /// <summary>
    /// When the content was originally published, if available.
    /// </summary>
    public DateTime? PublishedDate { get; set; }

    /// <summary>
    /// When the content was added to the system.
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// When the content was last updated.
    /// </summary>
    public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// The source this content was ingested from (if any).
    /// </summary>
    public SourceResponse? SourceInfo { get; set; }
}
