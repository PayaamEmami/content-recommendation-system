namespace Crs.Core.Observability;

/// <summary>
/// Formats HTTP response bodies for diagnostic logging, truncating or suppressing them
/// so potentially sensitive upstream payloads are only logged in full when explicitly
/// allowed (e.g. in development or when sensitive body logging is enabled).
/// </summary>
public static class ResponseBodyFormatter
{
    /// <summary>
    /// Returns a log-safe representation of <paramref name="body"/>. When
    /// <paramref name="allowFullBody"/> is <c>true</c> the body is returned truncated to
    /// <paramref name="maxLength"/>; otherwise only its length is disclosed.
    /// </summary>
    public static string Format(string? body, bool allowFullBody, int maxLength = 512)
    {
        body ??= string.Empty;

        if (allowFullBody)
        {
            return body.Length <= maxLength ? body : $"{body[..maxLength]}...";
        }

        return $"<suppressed length={body.Length}>";
    }
}
