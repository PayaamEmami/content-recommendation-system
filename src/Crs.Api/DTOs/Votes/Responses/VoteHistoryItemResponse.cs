using Crs.Core.Enums;

namespace Crs.Api.DTOs.Votes.Responses;

/// <summary>
/// Response model for a user's vote history joined with content details.
/// </summary>
public class VoteHistoryItemResponse
{
    /// <summary>
    /// The vote's unique identifier.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// The user's ID who cast the vote.
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// The content's unique identifier.
    /// </summary>
    public Guid ContentId { get; set; }

    /// <summary>
    /// The current vote type.
    /// </summary>
    public VoteType VoteType { get; set; }

    /// <summary>
    /// When the vote was first created.
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// When the vote was most recently updated.
    /// </summary>
    public DateTime? UpdatedAt { get; set; }

    /// <summary>
    /// The content title.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// The content description.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// The content URL.
    /// </summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// The content type.
    /// </summary>
    public ContentType Type { get; set; }

    /// <summary>
    /// The best available date to display for the content.
    /// </summary>
    public DateTime ContentDate { get; set; }
}
