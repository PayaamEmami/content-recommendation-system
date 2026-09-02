namespace Crs.Infrastructure.Configuration;

/// <summary>
/// Configuration settings for embedding generation using OpenAI.
/// </summary>
public class EmbeddingSettings
{
    public const string SectionName = "Embedding";

    /// <summary>
    /// Model name for OpenAI embeddings (e.g., "text-embedding-3-small").
    /// </summary>
    public string ModelName { get; set; } = "text-embedding-3-small";

    /// <summary>
    /// Default dimensions for <c>text-embedding-3-small</c>. Must match the pgvector column.
    /// </summary>
    public const int DefaultDimensions = 1536;

    /// <summary>
    /// Embedding dimensions (must match the model).
    /// </summary>
    public int Dimensions { get; set; } = DefaultDimensions;

    /// <summary>
    /// Maximum number of texts to embed in a single batch.
    /// </summary>
    public int MaxBatchSize { get; set; } = 100;
}
