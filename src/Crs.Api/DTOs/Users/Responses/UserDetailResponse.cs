using Crs.Api.DTOs.Sources.Responses;
using Crs.Core.Entities;

namespace Crs.Api.DTOs.Users.Responses;

/// <summary>
/// Detailed response model for user information including their sources.
/// </summary>
public class UserDetailResponse
{
    /// <summary>
    /// The user's unique identifier.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// The user's email address.
    /// </summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>
    /// The user's display name.
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// When the user account was created.
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// When the user last logged in.
    /// </summary>
    public DateTime? LastLoginAt { get; set; }

    /// <summary>
    /// URL-based sources configured by the user.
    /// </summary>
    public List<SourceResponse> Sources { get; set; } = new();

    /// <summary>
    /// Maps a <see cref="User"/> entity (with its sources loaded) to a
    /// <see cref="UserDetailResponse"/> DTO.
    /// </summary>
    public static UserDetailResponse FromEntity(User user)
    {
        return new UserDetailResponse
        {
            Id = user.Id,
            Email = user.Email,
            DisplayName = user.DisplayName,
            CreatedAt = user.CreatedAt,
            LastLoginAt = user.LastLoginAt,
            Sources = user.Sources.Select(SourceResponse.FromEntity).ToList()
        };
    }
}

