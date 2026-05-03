using Crs.Core.Entities;
using Crs.Llm.Models;

namespace Crs.Llm.Services;

/// <summary>
/// Ingests content for a single source: extracts via the LLM agent, validates URLs,
/// dedupes against the database, persists new items, and indexes embeddings in the vector store.
/// Used by both the background ingestion job and the synchronous API endpoint to ensure
/// identical behavior in both paths.
/// </summary>
public interface ISourceIngestionService
{
    /// <summary>
    /// Run the full ingestion pipeline for a single source.
    /// </summary>
    Task<SourceIngestionSummary> IngestSourceAsync(Source source, CancellationToken cancellationToken = default);
}
