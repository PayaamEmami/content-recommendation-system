using Crs.Recommendation.Models;

namespace Crs.Recommendation;

/// <summary>
/// Shared rules for deciding which content is excluded from recommendations. Centralizes
/// the "already seen or recently recommended" logic that the engine (when building vector
/// search exclusions) and <see cref="Filters.SeenContentFilter"/> both rely on.
/// </summary>
public static class RecommendationExclusions
{
    /// <summary>
    /// Whether the content has already been seen by the user or was recently recommended.
    /// </summary>
    public static bool IsSeenOrRecentlyRecommended(RecommendationContext context, Guid contentId) =>
        context.SeenContentIds.Contains(contentId) ||
        context.RecentlyRecommendedIds.Contains(contentId);

    /// <summary>
    /// Builds the exclusion set for candidate retrieval: always excludes seen content, and
    /// excludes recently recommended content unless <paramref name="allowRecentRecommendations"/>
    /// is set. Any <paramref name="additionalExcludedIds"/> are unioned in.
    /// </summary>
    public static HashSet<Guid> BuildExcludedIds(
        RecommendationContext context,
        bool allowRecentRecommendations,
        IEnumerable<Guid>? additionalExcludedIds = null) =>
        context.SeenContentIds
            .Union(allowRecentRecommendations ? Enumerable.Empty<Guid>() : context.RecentlyRecommendedIds)
            .Union(additionalExcludedIds ?? Enumerable.Empty<Guid>())
            .ToHashSet();
}
