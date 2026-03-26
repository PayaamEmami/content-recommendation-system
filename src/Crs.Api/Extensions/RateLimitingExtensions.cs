using System.Threading.RateLimiting;
using Crs.Api.Observability;
using Crs.Core.Observability;

namespace Crs.Api.Extensions;

/// <summary>
/// Extension methods for configuring rate limiting.
/// </summary>
public static class RateLimitingExtensions
{
    /// <summary>
    /// Configures rate limiting policies.
    /// </summary>
    public static IServiceCollection AddRateLimitingConfiguration(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            // Global rate limit: 100 requests per minute per IP
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 100,
                        Window = TimeSpan.FromMinutes(1),
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = 0
                    }));

            // Stricter limit for authentication endpoints (10 login attempts per minute per IP)
            options.AddPolicy("auth", context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 10,
                        Window = TimeSpan.FromMinutes(1),
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = 0
                    }));

            // Standard limit for API endpoints (60 requests per minute per IP)
            options.AddPolicy("api", context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 60,
                        Window = TimeSpan.FromMinutes(1),
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = 0
                    }));

            // Return 429 Too Many Requests with Retry-After header
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = (context, _) =>
            {
                context.HttpContext.Items[HttpContextTelemetryKeys.RateLimitRejected] = true;

                var metrics = context.HttpContext.RequestServices.GetRequiredService<IObservabilityMetrics>();
                var route = context.HttpContext.GetEndpoint() is RouteEndpoint endpoint
                    ? endpoint.RoutePattern.RawText ?? context.HttpContext.Request.Path.ToString()
                    : context.HttpContext.Request.Path.ToString();

                metrics.Increment(
                    "api.rate_limit.rejections",
                    context: new MetricContext(
                        Dimensions: new Dictionary<string, string>
                        {
                            ["Operation"] = route,
                            ["Outcome"] = "rate_limited",
                            ["StatusClass"] = "4xx"
                        },
                        Properties: new Dictionary<string, object?>
                        {
                            ["Method"] = context.HttpContext.Request.Method,
                            ["Route"] = route
                }));

                return ValueTask.CompletedTask;
            };
        });

        return services;
    }
}
