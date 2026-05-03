namespace Crs.Core.Exceptions;

/// <summary>
/// Thrown by the content repository when an insert violates the URL uniqueness constraint.
/// Lets ingestion callers handle dedup-race conditions without depending on a specific persistence stack.
/// </summary>
public sealed class DuplicateContentException : Exception
{
    public DuplicateContentException(string url)
        : base($"Content with URL '{url}' already exists.")
    {
        Url = url;
    }

    public DuplicateContentException(string url, Exception innerException)
        : base($"Content with URL '{url}' already exists.", innerException)
    {
        Url = url;
    }

    public string Url { get; }
}
