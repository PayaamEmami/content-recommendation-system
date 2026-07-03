using Crs.Core.Interfaces;
using Crs.Core.Models;
using Crs.Llm.Models;
using Crs.Llm.Services;
using Microsoft.Extensions.Hosting;

namespace Crs.Tests.Infrastructure;

public sealed class FakeEmbeddingService : IEmbeddingService
{
    public int Dimensions => 3;

    public Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new[] { 0.1f, 0.2f, 0.3f });
    }

    public Task<IList<float[]>> GenerateEmbeddingsAsync(IEnumerable<string> texts, CancellationToken cancellationToken = default)
    {
        IList<float[]> results = texts.Select(_ => new[] { 0.1f, 0.2f, 0.3f }).ToList();
        return Task.FromResult(results);
    }
}

public sealed class InMemoryVectorStore : IVectorStore
{
    private readonly Dictionary<Guid, ContentDocument> _documents = new();

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public Task UpsertDocumentAsync(ContentDocument document, CancellationToken cancellationToken = default)
    {
        _documents[document.Id] = document;
        return Task.CompletedTask;
    }

    public Task UpsertDocumentsAsync(IEnumerable<ContentDocument> documents, CancellationToken cancellationToken = default)
    {
        foreach (var document in documents)
        {
            _documents[document.Id] = document;
        }

        return Task.CompletedTask;
    }

    public Task DeleteDocumentAsync(Guid contentId, CancellationToken cancellationToken = default)
    {
        _documents.Remove(contentId);
        return Task.CompletedTask;
    }

    public Task<List<VectorSearchResult>> SearchAsync(VectorSearchRequest request, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new List<VectorSearchResult>());
    }

    public Task<long> GetDocumentCountAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult((long)_documents.Count);
    }

    public Task<HashSet<Guid>> GetAllDocumentIdsAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_documents.Keys.ToHashSet());
    }
}

public sealed class FakeLlmClient : ILlmClient
{
    public Task<LlmResponse> SendMessageAsync(
        string systemPrompt,
        string userMessage,
        List<object>? tools = null,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new LlmResponse { Content = string.Empty, IsComplete = true });
    }
}

public sealed class FakeIngestionAgent : IIngestionAgent
{
    public Task<IngestionResult> IngestFromUrlAsync(
        string sourceUrl,
        Guid? sourceId = null,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new IngestionResult
        {
            Success = true,
            SourceUrl = sourceUrl,
            Content = new List<ExtractedContent>(),
            TotalFound = 0,
            NewContent = 0,
            DuplicatesSkipped = 0
        });
    }
}

public sealed class FakeXApiClient : IXApiClient
{
    public Task<XTokenResponse> ExchangeCodeAsync(
        string code,
        string codeVerifier,
        string redirectUri,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new XTokenResponse
        {
            AccessToken = "access-token",
            RefreshToken = "refresh-token",
            ExpiresIn = 3600,
            Scope = "users.read",
            TokenType = "bearer"
        });
    }

    public Task<XTokenResponse> RefreshTokenAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new XTokenResponse
        {
            AccessToken = "access-token",
            RefreshToken = "refresh-token",
            ExpiresIn = 3600,
            Scope = "users.read",
            TokenType = "bearer"
        });
    }

    public Task<XUserProfile> GetCurrentUserAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new XUserProfile
        {
            XUserId = "x-user",
            Handle = "testuser",
            DisplayName = "Test User"
        });
    }

    public Task<List<XFollowedAccountInfo>> GetFollowedAccountsAsync(
        string accessToken,
        string userId,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new List<XFollowedAccountInfo>());
    }

    public Task<List<XPostInfo>> GetRecentPostsAsync(
        string accessToken,
        string userId,
        DateTime? since,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new List<XPostInfo>());
    }
}

public sealed class FakeContentFetcherService : IContentFetcherService
{
    public Task<ContentFetchResult> FetchContentAsync(string url, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new ContentFetchResult
        {
            Success = true,
            Content = string.Empty,
            StatusCode = 200
        });
    }
}

public sealed class TestHostEnvironment : IHostEnvironment
{
    public string EnvironmentName { get; set; } = "Testing";
    public string ApplicationName { get; set; } = "Crs.Tests";
    public string ContentRootPath { get; set; } = string.Empty;
    public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; }
        = new Microsoft.Extensions.FileProviders.NullFileProvider();
}
