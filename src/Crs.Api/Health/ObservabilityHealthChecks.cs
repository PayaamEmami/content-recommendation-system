using System.Text.Json;
using Crs.Infrastructure.Configuration;
using Crs.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Crs.Api.Health;

internal static class ObservabilityHealthChecks
{
    public static IHealthChecksBuilder AddObservabilityChecks(this IHealthChecksBuilder builder)
    {
        return builder
            .AddCheck<OpenSearchHealthCheck>("opensearch", tags: ["ready"])
            .AddCheck<OpenAiConfigurationHealthCheck>("openai-config", tags: ["ready"])
            .AddCheck<XConfigurationHealthCheck>("x-config", tags: ["ready"]);
    }

    public static Task WriteResponseAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";

        var payload = new
        {
            status = report.Status.ToString(),
            totalDuration = report.TotalDuration.TotalMilliseconds,
            results = report.Entries.ToDictionary(
                entry => entry.Key,
                entry => new
                {
                    status = entry.Value.Status.ToString(),
                    description = entry.Value.Description,
                    duration = entry.Value.Duration.TotalMilliseconds,
                    exception = entry.Value.Exception?.Message,
                    data = entry.Value.Data
                })
        };

        return context.Response.WriteAsync(JsonSerializer.Serialize(payload));
    }
}

internal sealed class OpenSearchHealthCheck : IHealthCheck
{
    private readonly OpenSearchSettings _settings;
    private readonly IServiceProvider _serviceProvider;

    public OpenSearchHealthCheck(IOptions<OpenSearchSettings> settings, IServiceProvider serviceProvider)
    {
        _settings = settings.Value;
        _serviceProvider = serviceProvider;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_settings.Endpoint))
        {
            return HealthCheckResult.Healthy("OpenSearch is disabled for this environment", new Dictionary<string, object>
            {
                ["mode"] = _settings.Mode.ToString(),
                ["configured"] = false
            });
        }

        try
        {
            var vectorStore = _serviceProvider.GetRequiredService<IVectorStore>();
            var count = await vectorStore.GetDocumentCountAsync(cancellationToken);
            return HealthCheckResult.Healthy("OpenSearch is reachable", new Dictionary<string, object>
            {
                ["documentCount"] = count,
                ["mode"] = _settings.Mode.ToString()
            });
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("OpenSearch check failed", ex);
        }
    }
}

internal sealed class OpenAiConfigurationHealthCheck : IHealthCheck
{
    private readonly IConfiguration _configuration;

    public OpenAiConfigurationHealthCheck(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(string.IsNullOrWhiteSpace(_configuration["OpenAI:ApiKey"])
            ? HealthCheckResult.Unhealthy("OpenAI API key is missing")
            : HealthCheckResult.Healthy("OpenAI configuration is present"));
    }
}

internal sealed class XConfigurationHealthCheck : IHealthCheck
{
    private readonly XApiSettings _settings;

    public XConfigurationHealthCheck(IOptions<XApiSettings> settings)
    {
        _settings = settings.Value;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var isCompletelyUnset = string.IsNullOrWhiteSpace(_settings.ClientId)
            && string.IsNullOrWhiteSpace(_settings.RedirectUri)
            && string.IsNullOrWhiteSpace(_settings.ClientSecret);

        if (isCompletelyUnset)
        {
            return Task.FromResult(HealthCheckResult.Healthy("X integration is not configured"));
        }

        if (string.IsNullOrWhiteSpace(_settings.ClientId) || string.IsNullOrWhiteSpace(_settings.RedirectUri))
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("X integration configuration is incomplete"));
        }

        return Task.FromResult(HealthCheckResult.Healthy("X configuration is present"));
    }
}
