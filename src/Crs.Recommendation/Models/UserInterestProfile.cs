namespace Crs.Recommendation.Models;

/// <summary>
/// Represents a user's interest profile based on their interaction history.
/// Used primarily for semantic similarity matching.
/// </summary>
public class UserInterestProfile
{
    /// <summary>
    /// User ID this profile belongs to.
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// User preference embedding vector built from aggregating embeddings of liked content.
    /// This is the primary representation for semantic similarity matching.
    /// </summary>
    public float[]? UserEmbedding { get; set; }

    /// <summary>
    /// When this profile was last calculated.
    /// </summary>
    public DateTime LastUpdated { get; set; }

    /// <summary>
    /// Total number of interactions (votes) used to build this profile.
    /// </summary>
    public int TotalInteractions { get; set; }

}

