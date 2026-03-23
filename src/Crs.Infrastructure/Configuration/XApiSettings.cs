namespace Crs.Infrastructure.Configuration;

/// <summary>
/// Configuration settings for X API integration.
/// </summary>
public class XApiSettings
{
    public const string SectionName = "X";

    public string ClientId { get; set; } = string.Empty;
    public string? ClientSecret { get; set; }
    public string RedirectUri { get; set; } = string.Empty;
    public string Scopes { get; set; } = "tweet.read users.read follows.read offline.access";
    public string BaseUrl { get; set; } = "https://api.x.com";
    public string AuthorizationUrl { get; set; } = "https://x.com/i/oauth2/authorize";
    public string TokenUrl { get; set; } = "https://api.x.com/2/oauth2/token";
}
