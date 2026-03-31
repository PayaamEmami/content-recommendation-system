using Crs.Core.Entities;
using Crs.Recommendation.Models;

namespace Crs.Recommendation.Scorers;

/// <summary>
/// Scores content based on recency/freshness.
/// Newer content gets higher scores with exponential decay.
/// </summary>
public class RecencyScorer : IContentScorer
{
    public double Weight => 0.8; // Dominant heuristic signal so fresher content rises first

    // Newer content should rank much higher, but older items should never drop to zero.
    private const double DecayWindowDays = 14.0;
    private const double MinimumScore = 0.15;

    public Task<double> ScoreAsync(
        Content content,
        RecommendationContext context,
        CancellationToken cancellationToken = default)
    {
        var publishedDate = content.CreatedAt;
        var today = context.Date.ToDateTime(TimeOnly.MinValue);
        var ageInDays = Math.Max(0.0, (today - publishedDate).TotalDays);

        // Exponential decay with a floor:
        // Recent content (0 days) = 1.0
        // 14 days old ~= 0.46
        // 30 days old ~= 0.25
        // Very old content bottoms out around 0.15 instead of disappearing entirely
        var freshness = Math.Exp(-ageInDays / DecayWindowDays);
        var score = MinimumScore + ((1.0 - MinimumScore) * freshness);

        return Task.FromResult(Math.Clamp(score, 0.0, 1.0));
    }
}
