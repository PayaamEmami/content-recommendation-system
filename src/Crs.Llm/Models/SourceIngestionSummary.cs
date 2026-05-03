namespace Crs.Llm.Models;

/// <summary>
/// Aggregated outcome of ingesting a single source: extraction, persistence, and indexing counts.
/// </summary>
public sealed class SourceIngestionSummary
{
    /// <summary>
    /// Whether the upstream extraction call succeeded. Per-item save errors do not flip this to false.
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// Error message describing why the ingestion did not run, when <see cref="Success"/> is false.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Number of content items the LLM agent extracted from the source.
    /// </summary>
    public int Extracted { get; init; }

    /// <summary>
    /// Number of new content items persisted to the database.
    /// </summary>
    public int Saved { get; init; }

    /// <summary>
    /// Number of items skipped because their URL already exists.
    /// </summary>
    public int Duplicates { get; init; }

    /// <summary>
    /// Number of items that failed validation or persistence.
    /// </summary>
    public int Errors { get; init; }

    /// <summary>
    /// Number of items embedded and pushed to the vector store.
    /// </summary>
    public int Embedded { get; init; }
}
