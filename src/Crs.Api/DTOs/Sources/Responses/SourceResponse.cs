using Crs.Core.Entities;
using Crs.Core.Enums;

namespace Crs.Api.DTOs.Sources.Responses;

/// <summary>
/// Response DTO for source data.
/// </summary>
public class SourceResponse
{
    /// <summary>
    /// The unique identifier of the source.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// The user ID who owns this source.
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// The name of the source.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The URL of the source.
    /// </summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// Optional description of the source.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// The category/type of content this source provides.
    /// </summary>
    public ContentType Category { get; set; }

    /// <summary>
    /// Whether this source is active.
    /// </summary>
    public bool IsActive { get; set; }

    /// <summary>
    /// When the source was created.
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// When the source was last updated.
    /// </summary>
    public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// Number of content ingested from this source.
    /// </summary>
    public int ContentCount { get; set; }

    /// <summary>
    /// Maps a <see cref="Source"/> entity to a <see cref="SourceResponse"/> DTO.
    /// </summary>
    public static SourceResponse FromEntity(Source source)
    {
        return new SourceResponse
        {
            Id = source.Id,
            UserId = source.UserId,
            Name = source.Name,
            Url = source.Url,
            Description = source.Description,
            Category = source.Category,
            IsActive = source.IsActive,
            CreatedAt = source.CreatedAt,
            UpdatedAt = source.UpdatedAt,
            ContentCount = source.Content?.Count ?? 0
        };
    }
}

