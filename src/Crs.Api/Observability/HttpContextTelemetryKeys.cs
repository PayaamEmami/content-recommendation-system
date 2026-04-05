namespace Crs.Api.Observability;

internal static class HttpContextTelemetryKeys
{
    public const string CorrelationId = "__CrsCorrelationId";
    public const string RateLimitRejected = "__CrsRateLimitRejected";
}
