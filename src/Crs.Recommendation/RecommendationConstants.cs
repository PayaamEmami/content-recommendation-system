namespace Crs.Recommendation;

/// <summary>
/// Centralized tuning constants for the hybrid recommendation pipeline. Previously these
/// values were scattered as inline literals across the engine and its helpers.
/// </summary>
public static class RecommendationConstants
{
    /// <summary>Weight applied to vector similarity in the hybrid blend.</summary>
    public const double VectorSimilarityWeight = 0.70;

    /// <summary>Weight applied to the heuristic score in the hybrid blend.</summary>
    public const double HeuristicWeight = 0.30;

    /// <summary>Neutral score assigned when no signal is available.</summary>
    public const double NeutralScore = 0.5;

    /// <summary>Recency window (in days) used to separate "recent" from "older" content.</summary>
    public const int RecencyWindowDays = 90;

    /// <summary>Minimum number of candidates to request from the vector store.</summary>
    public const int MinVectorCandidates = 100;

    /// <summary>Multiplier applied to the requested count when sizing the vector candidate pool.</summary>
    public const int VectorCandidateMultiplier = 25;
}
