using Crs.Recommendation.Models;

namespace Crs.Recommendation.Scorers;

/// <summary>
/// Blends vector-similarity scores with heuristic scores into the final ranking score,
/// applying the 70/30 hybrid weighting. Semantic relevance stays primary while the
/// heuristic portion (dominated by recency) nudges freshness.
/// </summary>
public sealed class HybridScorer
{
    /// <summary>
    /// Merges heuristic component scores into each candidate and recomputes its
    /// <see cref="ScoredContent.FinalScore"/> using the hybrid weighting. Candidates without a
    /// matching heuristic score are left unchanged.
    /// </summary>
    public List<ScoredContent> Merge(
        List<ScoredContent> candidates,
        IReadOnlyList<ScoredContent> heuristicScored)
    {
        var heuristicScoreMap = heuristicScored.ToDictionary(sr => sr.Content.Id);

        foreach (var candidate in candidates)
        {
            if (!heuristicScoreMap.TryGetValue(candidate.Content.Id, out var heuristicScore))
            {
                continue;
            }

            foreach (var kvp in heuristicScore.Scores)
            {
                candidate.Scores[kvp.Key] = kvp.Value;
            }

            var vectorScore = candidate.Scores.TryGetValue("vector_similarity", out var vs)
                ? vs
                : RecommendationConstants.NeutralScore;

            candidate.FinalScore =
                (vectorScore * RecommendationConstants.VectorSimilarityWeight) +
                (heuristicScore.FinalScore * RecommendationConstants.HeuristicWeight);
        }

        return candidates;
    }
}
