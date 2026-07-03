using Crs.Recommendation.Models;

namespace Crs.Recommendation.Filters;

/// <summary>
/// Ensures source diversity in recommendations.
/// Prevents all recommendations from being from the same source.
/// </summary>
public class DiversityFilter : IRecommendationFilter
{
    // Maximum number of content from the same source
    private const int MaxPerSource = 3;

    public Task<List<ScoredContent>> FilterAsync(
        List<ScoredContent> candidates,
        RecommendationContext context,
        CancellationToken cancellationToken = default)
    {
        var sourceCounts = new Dictionary<Guid, int>();
        var diversified = new List<ScoredContent>();
        var overflowCandidates = new List<ScoredContent>();

        // Sort by score descending (best first)
        var sortedCandidates = candidates.OrderByDescending(sr => sr.FinalScore).ToList();

        foreach (var candidate in sortedCandidates)
        {
            // Check if this content has a source
            if (candidate.Content.SourceId.HasValue)
            {
                var sourceId = candidate.Content.SourceId.Value;
                var currentCount = sourceCounts.GetValueOrDefault(sourceId, 0);

                // Check if source is at max count
                if (currentCount >= MaxPerSource)
                {
                    overflowCandidates.Add(candidate);
                    continue;
                }

                diversified.Add(ApplyDiversityPenalty(candidate, sourceCounts));
            }
            else
            {
                // No source - always include (manual entries)
                diversified.Add(candidate);
            }
        }

        // If the diversity pass underfills the feed, backfill with the best
        // remaining candidates rather than returning a short feed.
        foreach (var candidate in overflowCandidates)
        {
            if (diversified.Count >= context.Count)
            {
                break;
            }

            diversified.Add(ApplyDiversityPenalty(candidate, sourceCounts));
        }

        return Task.FromResult(diversified);
    }

    /// <summary>
    /// Returns a copy of the candidate with the source-diversity penalty applied to its final
    /// score, and increments the per-source count. Producing a copy avoids mutating the shared
    /// input <see cref="ScoredContent"/> instances during filtering.
    /// </summary>
    private static ScoredContent ApplyDiversityPenalty(
        ScoredContent candidate,
        IDictionary<Guid, int> sourceCounts)
    {
        if (!candidate.Content.SourceId.HasValue)
        {
            return candidate;
        }

        var sourceId = candidate.Content.SourceId.Value;
        var currentCount = sourceCounts.TryGetValue(sourceId, out var existingCount)
            ? existingCount
            : 0;
        var diversityPenalty = CalculateDiversityPenalty(currentCount);
        sourceCounts[sourceId] = currentCount + 1;

        return new ScoredContent
        {
            Content = candidate.Content,
            Scores = new Dictionary<string, double>(candidate.Scores)
            {
                ["diversity_penalty"] = diversityPenalty
            },
            FinalScore = candidate.FinalScore - diversityPenalty
        };
    }

    private static double CalculateDiversityPenalty(int currentCount)
    {
        // Penalize sources that are becoming overrepresented
        return currentCount switch
        {
            0 => 0.0,      // First occurrence - no penalty
            1 => 0.02,     // Second occurrence - small penalty
            2 => 0.04,     // Third occurrence - larger penalty
            _ => 0.05      // Beyond - largest penalty
        };
    }
}
