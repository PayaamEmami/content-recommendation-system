using Crs.Recommendation.Models;

namespace Crs.Recommendation.Filters;

/// <summary>
/// Filters out content the user has already seen or interacted with.
/// </summary>
public class SeenContentFilter : IRecommendationFilter
{
    public Task<List<ScoredContent>> FilterAsync(
        List<ScoredContent> candidates,
        RecommendationContext context,
        CancellationToken cancellationToken = default)
    {
        // Remove content the user has already seen (voted on) or that was recently recommended.
        var filtered = candidates
            .Where(sr => !RecommendationExclusions.IsSeenOrRecentlyRecommended(context, sr.Content.Id))
            .ToList();

        return Task.FromResult(filtered);
    }
}

