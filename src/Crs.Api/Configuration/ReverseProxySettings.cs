namespace Crs.Api.Configuration;

/// <summary>
/// Configuration for trusted reverse proxies and load balancers.
/// </summary>
public sealed class ReverseProxySettings
{
    /// <summary>
    /// Gets or sets the trusted CIDR blocks that are allowed to supply X-Forwarded-* headers.
    /// </summary>
    public string[] KnownNetworks { get; set; } = [];
}
