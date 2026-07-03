using Microsoft.Extensions.Logging;
using OpenSearch.Client;
using Crs.Infrastructure.Configuration;

namespace Crs.Infrastructure.VectorStore;

/// <summary>
/// Owns the OpenSearch index bootstrap concern: checking whether the content index exists
/// and, if not, creating it with the k-NN vector mapping. Kept separate from the CRUD/query
/// paths in <see cref="OpenSearchVectorStore"/>.
/// </summary>
internal static class OpenSearchIndexBootstrapper
{
    /// <summary>
    /// Ensures the configured index exists, creating it with the vector mapping when missing.
    /// </summary>
    public static async Task EnsureIndexAsync(
        IOpenSearchClient client,
        OpenSearchSettings settings,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var indexExists = await client.Indices.ExistsAsync(settings.IndexName, ct: cancellationToken);
        if (indexExists.Exists)
        {
            logger.LogInformation("Index already exists: {IndexName}", settings.IndexName);
            return;
        }

        var createIndexResponse = await client.Indices.CreateAsync(settings.IndexName, c => c
            .Settings(s => s
                .Setting("index.knn", true)
                .NumberOfShards(1)
                .NumberOfReplicas(0)
            )
            .Map<ContentSearchDocument>(m => m
                .Properties(p => p
                    .Keyword(k => k.Name(n => n.Id))
                    .Text(t => t.Name(n => n.Title))
                    .Text(t => t.Name(n => n.Description))
                    .Keyword(k => k.Name(n => n.Url))
                    .Keyword(k => k.Name(n => n.Type))
                    .Keyword(k => k.Name(n => n.SourceId))
                    .Date(d => d.Name(n => n.PublishedDate))
                    .Date(d => d.Name(n => n.CreatedAt))
                    .Date(d => d.Name(n => n.UpdatedAt))
                    .KnnVector(knn => knn
                        .Name(n => n.Embedding)
                        .Dimension(settings.EmbeddingDimensions)
                        .Method(m => m
                            .Name("hnsw")
                            .SpaceType("cosinesimil")
                            .Engine("nmslib")
                            .Parameters(p => p
                                .Parameter("ef_construction", 512)
                                .Parameter("m", 16)
                            )
                        )
                    )
                )
            ),
            cancellationToken
        );

        if (!createIndexResponse.IsValid)
        {
            if (createIndexResponse.ServerError?.Error?.Type == "content_already_exists_exception")
            {
                logger.LogInformation("Index already exists: {IndexName}", settings.IndexName);
                return;
            }

            logger.LogError("Failed to create index: {Error}", createIndexResponse.DebugInformation);
            throw new Exception($"Failed to create OpenSearch index: {createIndexResponse.DebugInformation}");
        }

        logger.LogInformation("Successfully created index: {IndexName}", settings.IndexName);
    }
}
