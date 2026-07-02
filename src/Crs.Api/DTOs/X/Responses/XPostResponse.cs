using Crs.Core.Entities;

namespace Crs.Api.DTOs.X.Responses;

/// <summary>
/// Response payload for X posts.
/// </summary>
public class XPostResponse
{
    public Guid Id { get; set; }
    public string PostId { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public DateTime PostCreatedAt { get; set; }
    public string AuthorHandle { get; set; } = string.Empty;
    public string? AuthorName { get; set; }
    public string? AuthorProfileImageUrl { get; set; }
    public string? MediaJson { get; set; }
    public int LikeCount { get; set; }
    public int ReplyCount { get; set; }
    public int RepostCount { get; set; }
    public int QuoteCount { get; set; }

    /// <summary>
    /// Maps an <see cref="XPost"/> entity to a response DTO.
    /// </summary>
    public static XPostResponse FromEntity(XPost post)
    {
        return new XPostResponse
        {
            Id = post.Id,
            PostId = post.PostId,
            Text = post.Text,
            Url = post.Url,
            PostCreatedAt = post.PostCreatedAt,
            AuthorHandle = post.AuthorHandle,
            AuthorName = post.AuthorName,
            AuthorProfileImageUrl = post.AuthorProfileImageUrl,
            MediaJson = post.MediaJson,
            LikeCount = post.LikeCount,
            ReplyCount = post.ReplyCount,
            RepostCount = post.RepostCount,
            QuoteCount = post.QuoteCount
        };
    }
}

