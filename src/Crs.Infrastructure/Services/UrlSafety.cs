using System.Net;
using System.Net.Sockets;

namespace Crs.Infrastructure.Services;

/// <summary>
/// Blocks non-public fetch targets so ingestion cannot be used for SSRF
/// (localhost, private RFC1918 ranges, link-local / cloud metadata, etc.).
/// </summary>
public static class UrlSafety
{
    public static bool IsSafePublicHttpUrl(string? url, out string? rejectionReason)
    {
        rejectionReason = null;

        if (string.IsNullOrWhiteSpace(url) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            rejectionReason = "URL must be an absolute http(s) address";
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            rejectionReason = "Only http and https URLs are allowed";
            return false;
        }

        if (string.IsNullOrWhiteSpace(uri.Host) ||
            uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.Equals("metadata.google.internal", StringComparison.OrdinalIgnoreCase))
        {
            rejectionReason = "Host is not allowed";
            return false;
        }

        if (IPAddress.TryParse(uri.Host, out var literalAddress))
        {
            if (!IsPublicIpAddress(literalAddress))
            {
                rejectionReason = "IP address is not publicly routable";
                return false;
            }

            return true;
        }

        try
        {
            var addresses = Dns.GetHostAddresses(uri.Host);
            if (addresses.Length == 0)
            {
                rejectionReason = "Host could not be resolved";
                return false;
            }

            if (addresses.Any(address => !IsPublicIpAddress(address)))
            {
                rejectionReason = "Host resolves to a non-public address";
                return false;
            }
        }
        catch (SocketException)
        {
            rejectionReason = "Host could not be resolved";
            return false;
        }

        return true;
    }

    public static bool IsPublicIpAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return false;
        }

        if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast)
        {
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv4MappedToIPv6)
            {
                return IsPublicIpAddress(address.MapToIPv4());
            }

            // Unique local addresses (fc00::/7) and unspecified.
            var bytes = address.GetAddressBytes();
            if (bytes[0] == 0x00 || (bytes[0] & 0xfe) == 0xfc)
            {
                return false;
            }

            return true;
        }

        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var v4 = address.GetAddressBytes();
        // 0.0.0.0/8, 10/8, 127/8
        if (v4[0] is 0 or 10 or 127)
        {
            return false;
        }

        // 169.254.0.0/16 (link-local / cloud metadata)
        if (v4[0] == 169 && v4[1] == 254)
        {
            return false;
        }

        // 172.16.0.0/12
        if (v4[0] == 172 && v4[1] is >= 16 and <= 31)
        {
            return false;
        }

        // 192.168.0.0/16
        if (v4[0] == 192 && v4[1] == 168)
        {
            return false;
        }

        // 100.64.0.0/10 (CGNAT)
        if (v4[0] == 100 && v4[1] is >= 64 and <= 127)
        {
            return false;
        }

        return true;
    }
}
