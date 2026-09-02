using Pgvector;

namespace Crs.Infrastructure.Data.Entities;

/// <summary>
/// Persisted embedding for a content row. Kept in Infrastructure so Core stays free of pgvector types.
/// </summary>
public class ContentEmbedding
{
    public Guid ContentId { get; set; }

    public Vector Embedding { get; set; } = null!;

    public DateTime UpdatedAt { get; set; }
}
