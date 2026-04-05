using Microsoft.AspNetCore.Mvc;
using System.Net;
using System.Text.Json;
using System.Diagnostics;
using Crs.Api.Observability;
using Crs.Core.Observability;
using Crs.Infrastructure.Configuration;

namespace Crs.Api.Middleware;

/// <summary>
/// Global exception handling middleware that converts exceptions to problem details responses.
/// </summary>
public class ExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;
    private readonly IHostEnvironment _environment;
    private readonly ObservabilitySettings _settings;

    public ExceptionHandlingMiddleware(
        RequestDelegate next,
        ILogger<ExceptionHandlingMiddleware> logger,
        IHostEnvironment environment,
        ObservabilitySettings settings)
    {
        _next = next;
        _logger = logger;
        _environment = environment;
        _settings = settings;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            await HandleExceptionAsync(context, ex);
        }
    }

    private async Task HandleExceptionAsync(HttpContext context, Exception exception)
    {
        var correlationId = context.Items.TryGetValue(HttpContextTelemetryKeys.CorrelationId, out var existingCorrelationId) &&
                            existingCorrelationId is string correlationIdValue &&
                            !string.IsNullOrWhiteSpace(correlationIdValue)
            ? correlationIdValue
            : context.TraceIdentifier;
        var traceId = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;
        using (_logger.BeginScope(new Dictionary<string, object?>
        {
            ["correlation_id"] = correlationId,
            ["execution_environment"] = _settings.ExecutionEnvironment,
            ["trace_id"] = traceId,
            ["span_id"] = Activity.Current?.SpanId.ToString()
        }))
        {
            _logger.LogError(
                exception,
                "Unhandled exception for {Method} {Path} trace {TraceId}",
                context.Request.Method,
                context.Request.Path,
                traceId);
        }

        var statusCode = exception switch
        {
            ArgumentException => HttpStatusCode.BadRequest,
            KeyNotFoundException => HttpStatusCode.NotFound,
            UnauthorizedAccessException => HttpStatusCode.Unauthorized,
            _ => HttpStatusCode.InternalServerError
        };

        var problemDetails = new ProblemDetails
        {
            Type = $"https://httpstatuses.com/{(int)statusCode}",
            Title = GetTitle(statusCode),
            Status = (int)statusCode,
            Detail = _environment.IsDevelopment() ? exception.Message : "An error occurred processing your request.",
            Instance = context.Request.Path
        };

        // Add stack trace in development
        if (_environment.IsDevelopment())
        {
            problemDetails.Extensions["stackTrace"] = exception.StackTrace;
        }

        problemDetails.Extensions["correlationId"] = correlationId;
        problemDetails.Extensions["traceId"] = traceId;

        context.Response.ContentType = "application/problem+json";
        context.Response.StatusCode = (int)statusCode;
        context.Response.Headers[CrsTelemetry.CorrelationIdHeaderName] = correlationId;

        var json = JsonSerializer.Serialize(problemDetails, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        await context.Response.WriteAsync(json);
    }

    private static string GetTitle(HttpStatusCode statusCode)
    {
        return statusCode switch
        {
            HttpStatusCode.BadRequest => "Bad Request",
            HttpStatusCode.NotFound => "Not Found",
            HttpStatusCode.Unauthorized => "Unauthorized",
            HttpStatusCode.InternalServerError => "Internal Server Error",
            _ => "An error occurred"
        };
    }
}
