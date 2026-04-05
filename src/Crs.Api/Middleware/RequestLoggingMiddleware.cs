using System.Diagnostics;
using Crs.Api.Observability;
using Crs.Core.Observability;
using Crs.Infrastructure.Configuration;

namespace Crs.Api.Middleware;

/// <summary>
/// Logs one structured event per completed HTTP request and emits API metrics.
/// </summary>
public sealed class RequestLoggingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<RequestLoggingMiddleware> _logger;
    private readonly IObservabilityMetrics _metrics;
    private readonly ObservabilitySettings _settings;

    public RequestLoggingMiddleware(
        RequestDelegate next,
        ILogger<RequestLoggingMiddleware> logger,
        IObservabilityMetrics metrics,
        ObservabilitySettings settings)
    {
        _next = next;
        _logger = logger;
        _metrics = metrics;
        _settings = settings;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = GetOrCreateCorrelationId(context);
        context.Response.Headers[CrsTelemetry.CorrelationIdHeaderName] = correlationId;
        Activity.Current?.SetTag(CrsTelemetry.Tags.CorrelationId, correlationId);
        Activity.Current?.SetTag(CrsTelemetry.Tags.ExecutionEnvironment, _settings.ExecutionEnvironment);

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
        var authenticated = context.User.Identity?.IsAuthenticated ?? false;

        using (_logger.BeginScope(new Dictionary<string, object?>
        {
            ["authenticated"] = authenticated,
            ["correlation_id"] = correlationId,
            ["execution_environment"] = _settings.ExecutionEnvironment,
            ["trace_id"] = traceId,
            ["span_id"] = spanId,
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
                ["CorrelationId"] = correlationId,
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

    private static string GetOrCreateCorrelationId(HttpContext context)
    {
        if (context.Items.TryGetValue(HttpContextTelemetryKeys.CorrelationId, out var existing) &&
            existing is string existingCorrelationId &&
            !string.IsNullOrWhiteSpace(existingCorrelationId))
        {
            return existingCorrelationId;
        }

        var correlationId = context.Request.Headers.TryGetValue(CrsTelemetry.CorrelationIdHeaderName, out var incoming) &&
                            !string.IsNullOrWhiteSpace(incoming)
            ? incoming.ToString()
            : context.TraceIdentifier;

        context.Items[HttpContextTelemetryKeys.CorrelationId] = correlationId;
        return correlationId;
    }
}
