using Microsoft.Extensions.DependencyInjection;
using Crs.Recommendation.Engine;
using Crs.Recommendation.Filters;
using Crs.Recommendation.Scorers;
using Crs.Recommendation.Services;

namespace Crs.Recommendation;

/// <summary>
/// Extension methods for configuring recommendation services.
/// </summary>
public static class DependencyInjection
{
    /// <summary>
    /// Register all recommendation engine services.
    /// </summary>
    public static IServiceCollection AddRecommendationEngine(this IServiceCollection services)
    {
        // Scorers
        services.AddScoped<IContentScorer, RecencyScorer>();
        services.AddScoped<IContentScorer, SourceAffinityScorer>();
        services.AddScoped<CompositeScorer>();

        // Filters
        services.AddScoped<IRecommendationFilter, SeenContentFilter>();
        services.AddScoped<IRecommendationFilter, DiversityFilter>();

        // Services
        services.AddScoped<IUserProfileService, UserProfileService>();
        services.AddScoped<IRecommendationEngine, RecommendationEngine>();
        services.AddScoped<IFeedGenerator, FeedGenerator>();

        return services;
    }
}
