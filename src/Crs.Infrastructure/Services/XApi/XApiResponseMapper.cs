using Crs.Core.Models;
using Crs.Infrastructure.Services.XApi.Models;

namespace Crs.Infrastructure.Services.XApi;

/// <summary>
/// Maps X API wire-format DTOs (<see cref="Models"/>) to the domain models consumed by the
/// rest of the application. Centralizes the projection logic that was previously duplicated
/// across <see cref="XApiClient"/> operations.
/// </summary>
internal static class XApiResponseMapper
{
    public static XTokenResponse MapToken(XTokenApiResponse? token) =>
        token == null
            ? new XTokenResponse()
            : new XTokenResponse
            {
                AccessToken = token.AccessToken,
                RefreshToken = token.RefreshToken,
                ExpiresIn = token.ExpiresIn,
                Scope = token.Scope,
                TokenType = token.TokenType
            };

    public static XUserProfile MapUserProfile(XUser user) => new()
    {
        XUserId = user.Id,
        Handle = user.Username,
        DisplayName = user.Name,
        ProfileImageUrl = user.ProfileImageUrl
    };

    public static XFollowedAccountInfo MapFollowedAccount(XUser user) => new()
    {
        XUserId = user.Id,
        Handle = user.Username,
        DisplayName = user.Name,
        ProfileImageUrl = user.ProfileImageUrl
    };

    public static XPostInfo MapPost(
        XPost item,
        IReadOnlyDictionary<string, XUser> usersById,
        IReadOnlyDictionary<string, XMedia> mediaByKey)
    {
        var author = usersById.TryGetValue(item.AuthorId ?? string.Empty, out var user)
            ? MapUserProfile(user)
            : new XUserProfile { XUserId = item.AuthorId ?? string.Empty };

        var media = new List<XMediaInfo>();
        if (item.Attachments?.MediaKeys != null)
        {
            foreach (var key in item.Attachments.MediaKeys)
            {
                if (mediaByKey.TryGetValue(key, out var mediaItem))
                {
                    media.Add(new XMediaInfo
                    {
                        Type = mediaItem.Type ?? string.Empty,
                        Url = mediaItem.Url,
                        PreviewImageUrl = mediaItem.PreviewImageUrl
                    });
                }
            }
        }

        return new XPostInfo
        {
            PostId = item.Id,
            Text = item.Text ?? string.Empty,
            Url = $"https://x.com/{author.Handle}/status/{item.Id}",
            CreatedAt = item.CreatedAt ?? DateTime.UtcNow,
            Author = author,
            LikeCount = item.PublicMetrics?.LikeCount ?? 0,
            ReplyCount = item.PublicMetrics?.ReplyCount ?? 0,
            RepostCount = item.PublicMetrics?.RepostCount ?? 0,
            QuoteCount = item.PublicMetrics?.QuoteCount ?? 0,
            Media = media
        };
    }
}
