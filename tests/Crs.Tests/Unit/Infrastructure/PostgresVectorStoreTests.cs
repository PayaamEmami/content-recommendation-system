using Crs.Core.Entities;
using Crs.Core.Enums;
using Crs.Core.Models;
using Crs.Core.Observability;
using Crs.Infrastructure.Configuration;
using Crs.Infrastructure.Data;
using Crs.Infrastructure.VectorStore;
using Crs.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Crs.Tests.Unit.Infrastructure;

[TestClass]
public sealed class PostgresVectorStoreTests
{
    [TestMethod]
    [DataRow(0.0, 1.0)]
    [DataRow(1.0, 0.0)]
    [DataRow(2.0, 0.0)]
    [DataRow(-0.25, 1.0)]
    public void ToSimilarity_ClampsCosineDistance(double distance, double expected)
    {
        Assert.AreEqual(expected, PostgresVectorStore.ToSimilarity(distance), 0.0001);
    }

    [TestMethod]
    public async Task Upsert_ThenCountListAndDelete()
    {
        await using var db = CreateDb();
        var store = CreateStore(db);
        var paper = await SeedPaperAsync(db, createdAt: DateTime.UtcNow);

        await store.UpsertDocumentsAsync(new[] { DocumentFor(paper, OneHot(0)) }, CancellationToken.None);

        CollectionAssert.Contains((await store.GetAllDocumentIdsAsync()).ToList(), paper.Id);

        await store.DeleteDocumentAsync(paper.Id, CancellationToken.None);

        CollectionAssert.DoesNotContain((await store.GetAllDocumentIdsAsync()).ToList(), paper.Id);
    }

    [TestMethod]
    public async Task Search_RanksNearestNeighborsFirst()
    {
        await using var db = CreateDb();
        var store = CreateStore(db);
        var now = DateTime.UtcNow;
        var source = await SeedSourceAsync(db);
        var nearest = await SeedPaperAsync(db, createdAt: now, title: "nearest", sourceId: source.Id);
        var middle = await SeedPaperAsync(db, createdAt: now, title: "middle", sourceId: source.Id);
        var farthest = await SeedPaperAsync(db, createdAt: now, title: "farthest", sourceId: source.Id);

        await store.UpsertDocumentsAsync(
            new[]
            {
                DocumentFor(nearest, OneHot(0)),
                DocumentFor(middle, Mix(primary: 0, secondary: 1, primaryWeight: 0.9f)),
                DocumentFor(farthest, OneHot(1))
            },
            CancellationToken.None);

        var results = await store.SearchAsync(
            new VectorSearchRequest
            {
                QueryVector = OneHot(0),
                TopK = 3,
                ContentType = ContentType.Paper,
                SourceIds = new HashSet<Guid> { source.Id }
            },
            CancellationToken.None);

        CollectionAssert.AreEqual(
            new[] { nearest.Id, middle.Id, farthest.Id },
            results.Select(result => result.ContentId).ToArray());
        Assert.AreEqual(1.0, results[0].SimilarityScore, 0.0001);
        Assert.IsGreaterThan(results[2].SimilarityScore, results[1].SimilarityScore);
        Assert.AreEqual(0.0, results[2].SimilarityScore, 0.05);
    }

    [TestMethod]
    public async Task Search_AppliesTypeDateExcludeAndMinimumScoreFilters()
    {
        await using var db = CreateDb();
        var store = CreateStore(db);
        var recent = DateTime.UtcNow;
        var old = recent.AddDays(-120);
        var source = await SeedSourceAsync(db);
        var matchingPaper = await SeedPaperAsync(db, createdAt: recent, title: "keep", sourceId: source.Id);
        var excludedPaper = await SeedPaperAsync(db, createdAt: recent, title: "exclude", sourceId: source.Id);
        var stalePaper = await SeedPaperAsync(db, createdAt: old, title: "stale", sourceId: source.Id);
        var video = await SeedVideoAsync(db, createdAt: recent, sourceId: source.Id);

        await store.UpsertDocumentsAsync(
            new[]
            {
                DocumentFor(matchingPaper, OneHot(0)),
                DocumentFor(excludedPaper, OneHot(0)),
                DocumentFor(stalePaper, OneHot(0)),
                DocumentFor(video, OneHot(0))
            },
            CancellationToken.None);

        var results = await store.SearchAsync(
            new VectorSearchRequest
            {
                QueryVector = OneHot(0),
                TopK = 10,
                ContentType = ContentType.Paper,
                SourceIds = new HashSet<Guid> { source.Id },
                PublishedAfter = recent.AddDays(-1),
                ExcludeContentIds = new HashSet<Guid> { excludedPaper.Id },
                MinimumScore = 0.5
            },
            CancellationToken.None);

        CollectionAssert.AreEqual(
            new[] { matchingPaper.Id },
            results.Select(result => result.ContentId).ToArray());
    }

    [TestMethod]
    public async Task Upsert_OverwritesExistingEmbedding()
    {
        await using var db = CreateDb();
        var store = CreateStore(db);
        var source = await SeedSourceAsync(db);
        var paper = await SeedPaperAsync(db, createdAt: DateTime.UtcNow, sourceId: source.Id);

        await store.UpsertDocumentAsync(DocumentFor(paper, OneHot(1)), CancellationToken.None);
        await store.UpsertDocumentAsync(DocumentFor(paper, OneHot(0)), CancellationToken.None);

        var results = await store.SearchAsync(
            new VectorSearchRequest
            {
                QueryVector = OneHot(0),
                TopK = 1,
                SourceIds = new HashSet<Guid> { source.Id }
            },
            CancellationToken.None);

        Assert.AreEqual(paper.Id, results[0].ContentId);
        Assert.AreEqual(1.0, results[0].SimilarityScore, 0.0001);
    }

    private static CrsDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<CrsDbContext>()
            .UseCrsNpgsql(PostgresTestContainerFixture.ConnectionString)
            .Options;
        var db = new CrsDbContext(options);
        db.Database.Migrate();
        return db;
    }

    private static PostgresVectorStore CreateStore(CrsDbContext db)
    {
        return new PostgresVectorStore(
            db,
            Options.Create(new EmbeddingSettings { Dimensions = EmbeddingSettings.DefaultDimensions }),
            NullLogger<PostgresVectorStore>.Instance,
            NullObservabilityMetrics.Instance);
    }

    private static async Task<Source> SeedSourceAsync(CrsDbContext db)
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = $"pgvector-{Guid.NewGuid():N}@example.com",
            PasswordHash = "hash",
            CreatedAt = DateTime.UtcNow
        };
        var source = new Source
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            Name = "pgvector-test",
            Url = $"https://example.com/sources/{Guid.NewGuid():N}",
            Category = ContentType.Paper,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Users.Add(user);
        db.Sources.Add(source);
        await db.SaveChangesAsync();
        return source;
    }

    private static async Task<Paper> SeedPaperAsync(
        CrsDbContext db,
        DateTime createdAt,
        string? title = null,
        Guid? sourceId = null)
    {
        var paper = new Paper
        {
            Id = Guid.NewGuid(),
            Title = title ?? "paper",
            Url = $"https://example.com/papers/{Guid.NewGuid():N}",
            SourceId = sourceId,
            CreatedAt = DateTime.SpecifyKind(createdAt, DateTimeKind.Utc),
            UpdatedAt = DateTime.UtcNow
        };
        db.Papers.Add(paper);
        await db.SaveChangesAsync();
        return paper;
    }

    private static async Task<Video> SeedVideoAsync(CrsDbContext db, DateTime createdAt, Guid? sourceId = null)
    {
        var video = new Video
        {
            Id = Guid.NewGuid(),
            Title = "video",
            Url = $"https://example.com/videos/{Guid.NewGuid():N}",
            SourceId = sourceId,
            CreatedAt = DateTime.SpecifyKind(createdAt, DateTimeKind.Utc),
            UpdatedAt = DateTime.UtcNow
        };
        db.Videos.Add(video);
        await db.SaveChangesAsync();
        return video;
    }

    private static ContentDocument DocumentFor(Content content, float[] embedding)
    {
        return new ContentDocument
        {
            Id = content.Id,
            Title = content.Title,
            Description = content.Description,
            Url = content.Url,
            Type = content.Type,
            SourceId = content.SourceId,
            CreatedAt = content.CreatedAt,
            UpdatedAt = content.UpdatedAt,
            PublishedDate = content.CreatedAt,
            Embedding = embedding
        };
    }

    private static float[] OneHot(int index)
    {
        var values = new float[EmbeddingSettings.DefaultDimensions];
        values[index] = 1f;
        return values;
    }

    private static float[] Mix(int primary, int secondary, float primaryWeight)
    {
        var values = new float[EmbeddingSettings.DefaultDimensions];
        values[primary] = primaryWeight;
        values[secondary] = 1f - primaryWeight;
        var magnitude = MathF.Sqrt(values.Sum(value => value * value));
        for (var i = 0; i < values.Length; i++)
        {
            values[i] /= magnitude;
        }

        return values;
    }
}
