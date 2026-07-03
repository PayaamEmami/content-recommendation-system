using Crs.Core.Entities;

namespace Crs.Api.DTOs.X.Responses;

/// <summary>
/// Response payload for followed X accounts.
/// </summary>
public class XFollowedAccountResponse
{
    public Guid Id { get; set; }
    public string XUserId { get; set; } = string.Empty;
    public string Handle { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string? ProfileImageUrl { get; set; }
    public bool IsSelected { get; set; }

    /// <summary>
    /// Maps an <see cref="XFollowedAccount"/> entity to a response DTO, marking whether
    /// the account is part of the user's selected feed accounts.
    /// </summary>
    public static XFollowedAccountResponse FromEntity(XFollowedAccount account, bool isSelected)
    {
        return new XFollowedAccountResponse
        {
            Id = account.Id,
            XUserId = account.XUserId,
            Handle = account.Handle,
            DisplayName = account.DisplayName,
            ProfileImageUrl = account.ProfileImageUrl,
            IsSelected = isSelected
        };
    }
}

