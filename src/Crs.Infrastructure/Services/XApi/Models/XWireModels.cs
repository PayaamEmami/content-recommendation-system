using System.Text.Json.Serialization;

namespace Crs.Infrastructure.Services.XApi.Models;

/// <summary>
/// Wire-format DTOs that mirror the JSON returned by the X (Twitter) API. These types
/// are deserialized directly from HTTP responses and then mapped to the domain models in
/// <see cref="Crs.Core.Models"/> by <see cref="XApiResponseMapper"/>.
/// </summary>
internal sealed class XTokenApiResponse
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = string.Empty;
    [JsonPropertyName("refresh_token")]
    public string RefreshToken { get; set; } = string.Empty;
    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }
    [JsonPropertyName("scope")]
    public string Scope { get; set; } = string.Empty;
    [JsonPropertyName("token_type")]
    public string TokenType { get; set; } = string.Empty;
}

internal sealed class XUserResponse
{
    public XUser? Data { get; set; }
}

internal sealed class XFollowResponse
{
    public List<XUser>? Data { get; set; }
    public XMeta? Meta { get; set; }
}

internal sealed class XPostsResponse
{
    public List<XPost>? Data { get; set; }
    public XPostIncludes? Includes { get; set; }
    public XMeta? Meta { get; set; }
}

internal sealed class XPostIncludes
{
    public List<XMedia>? Media { get; set; }
    public List<XUser>? Users { get; set; }
}

internal sealed class XMeta
{
    [JsonPropertyName("next_token")]
    public string? NextToken { get; set; }
}

internal sealed class XUser
{
    public string Id { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string? Name { get; set; }
    [JsonPropertyName("profile_image_url")]
    public string? ProfileImageUrl { get; set; }
}

internal sealed class XPost
{
    public string Id { get; set; } = string.Empty;
    public string? Text { get; set; }
    [JsonPropertyName("created_at")]
    public DateTime? CreatedAt { get; set; }
    [JsonPropertyName("author_id")]
    public string? AuthorId { get; set; }
    [JsonPropertyName("public_metrics")]
    public XPublicMetrics? PublicMetrics { get; set; }
    public XAttachments? Attachments { get; set; }
}

internal sealed class XPublicMetrics
{
    [JsonPropertyName("like_count")]
    public int LikeCount { get; set; }
    [JsonPropertyName("reply_count")]
    public int ReplyCount { get; set; }
    [JsonPropertyName("retweet_count")]
    public int RepostCount { get; set; }
    [JsonPropertyName("quote_count")]
    public int QuoteCount { get; set; }
}

internal sealed class XAttachments
{
    [JsonPropertyName("media_keys")]
    public List<string>? MediaKeys { get; set; }
}

internal sealed class XMedia
{
    [JsonPropertyName("media_key")]
    public string MediaKey { get; set; } = string.Empty;
    public string? Type { get; set; }
    public string? Url { get; set; }
    [JsonPropertyName("preview_image_url")]
    public string? PreviewImageUrl { get; set; }
}
