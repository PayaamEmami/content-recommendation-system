using System.Diagnostics;
using System.Security.Claims;
using Crs.Api.Observability;
using Crs.Core.Observability;

namespace Crs.Api.Middleware;

/// <summary>
/// Logs one structured event per completed HTTP request and emits API metrics.
/// </summary>
public sealed class RequestLoggingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<RequestLoggingMiddleware> _logger;
    private readonly IObservabilityMetrics _metrics;

    public RequestLoggingMiddleware(
        RequestDelegate next,
        ILogger<RequestLoggingMiddleware> logger,
        IObservabilityMetrics metrics)
    {
        _next = next;
        _logger = logger;
        _metrics = metrics;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var stopwatch = Stopwatch.StartNew();
        await _next(context);
        stopwatch.Stop();

        var route = GetRoute(context);
        var statusCode = context.Response.StatusCode;
        var statusClass = $"{statusCode / 100}xx";
        var rateLimited = context.Items.ContainsKey(HttpContextTelemetryKeys.RateLimitRejected) || statusCode == StatusCodes.Status429TooManyRequests;
        var outcome = rateLimited
            ? "rate_limited"
            : statusCode >= 500
                ? "server_error"
                : statusCode >= 400
                    ? "client_error"
                    : "success";
        var traceId = Activity.Current?.TraceId.ToString();
        var spanId = Activity.Current?.SpanId.ToString();
        var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);

        using (_logger.BeginScope(new Dictionary<string, object?>
        {
            ["trace_id"] = traceId,
            ["span_id"] = spanId,
            ["user_id"] = userId,
            ["request_outcome"] = outcome,
            ["rate_limited"] = rateLimited
        }))
        {
            _logger.LogInformation(
                "request.completed {Method} {Route} {StatusCode} {Outcome} in {ElapsedMs}ms",
                context.Request.Method,
                route,
                statusCode,
                outcome,
                stopwatch.Elapsed.TotalMilliseconds);
        }

        var metricContext = new MetricContext(
            Dimensions: new Dictionary<string, string>
            {
                ["Operation"] = route,
                ["Outcome"] = outcome,
                ["StatusClass"] = statusClass
            },
            Properties: new Dictionary<string, object?>
            {
                ["Method"] = context.Request.Method,
                ["Route"] = route,
                ["StatusCode"] = statusCode,
                ["TraceId"] = traceId,
                ["RateLimited"] = rateLimited
            });

        _metrics.Increment("api.request.count", context: metricContext);
        _metrics.RecordDuration("api.request.duration", stopwatch.Elapsed, metricContext);

        if (statusCode is >= 400 and < 500)
        {
            _metrics.Increment("api.request.4xx.count", context: metricContext);
        }

        if (statusCode >= 500)
        {
            _metrics.Increment("api.request.5xx.count", context: metricContext);
        }
    }

    private static string GetRoute(HttpContext context)
    {
        return context.GetEndpoint() is RouteEndpoint routeEndpoint
            ? routeEndpoint.RoutePattern.RawText ?? context.Request.Path.ToString()
            : context.Request.Path.ToString();
    }
}
