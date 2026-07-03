namespace Crs.Infrastructure.VectorStore;

/// <summary>
/// Internal document type for OpenSearch indexing.
/// </summary>
internal class ContentSearchDocument
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string SourceId { get; set; } = string.Empty;
    public DateTime PublishedDate { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public float[] Embedding { get; set; } = Array.Empty<float>();
}
