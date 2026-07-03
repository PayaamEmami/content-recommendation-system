using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Crs.Llm.Configuration;
using Crs.Llm.Services;

namespace Crs.Llm;

/// <summary>
/// Extension methods for registering LLM services in the dependency injection container.
/// </summary>
public static class DependencyInjection
{
    /// <summary>
    /// Registers all LLM-related services with the service collection.
    /// </summary>
    public static IServiceCollection AddLlmServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Register configuration
        services.Configure<OpenAISettings>(
            configuration.GetSection("OpenAI"));

        // Register HttpClient for OpenAI with extended timeout for large feeds
        services.AddHttpClient<ILlmClient, OpenAIClient>()
            .ConfigureHttpClient(client =>
            {
                client.Timeout = TimeSpan.FromMinutes(5); // 300 seconds for processing large RSS feeds
            });

        // Register agent services
        services.AddScoped<IIngestionAgent, IngestionAgent>();
        services.AddScoped<ISourceIngestionService, SourceIngestionService>();

        return services;
    }
}

