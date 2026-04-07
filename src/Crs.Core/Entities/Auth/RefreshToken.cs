namespace Crs.Core.Entities;

/// <summary>
/// Stored refresh token for JWT auth. Persisted in DB so multi-instance deployments can validate tokens.
/// </summary>
public class RefreshToken
{
    /// <summary>
    /// The refresh token value (PK).
    /// </summary>
    public string Token { get; set; } = string.Empty;

    public Guid UserId { get; set; }

    /// <summary>
    /// When this token expires.
    /// </summary>
    public DateTime ExpiresAt { get; set; }
}
