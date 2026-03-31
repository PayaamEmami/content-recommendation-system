using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Crs.Core.Entities;
using Crs.Core.Enums;
using Crs.Core.Interfaces;
using Crs.Core.Observability;
using Crs.Recommendation.Engine;
using Crs.Recommendation.Filters;
using Crs.Recommendation.Models;
using Crs.Recommendation.Scorers;

namespace Crs.Tests.Unit.Recommendation;

[TestClass]
public sealed class RecommendationEngineTests
{
    [TestMethod]
    public async Task GenerateRecommendationsAsync_WhenRecentPoolIsTooSmall_UsesOlderContentFallback()
    {
        var engine = CreateEngine(
            out var vectorStore,
            out var contentRepository);

        var context = BuildContext();
        var allContent = new[]
        {
            BuildContent(daysOld: 5),
            BuildContent(daysOld: 10),
            BuildContent(daysOld: 110),
            BuildContent(daysOld: 120),
            BuildContent(daysOld: 130)
        };

        contentRepository.Setup(repo => repo.GetByTypeAsync(ContentType.BlogPost, It.IsAny<CancellationToken>()))
            .ReturnsAsync(allContent);

        var results = await engine.GenerateRecommendationsAsync(context, CancellationToken.None);

        Assert.HasCount(5, results);
        CollectionAssert.AreEquivalent(
            allContent.Select(content => content.Id).ToArray(),
            results.Select(result => result.Content.Id).ToArray());
        vectorStore.VerifyNoOtherCalls();
        contentRepository.Verify(repo => repo.GetByTypeAsync(ContentType.BlogPost, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [TestMethod]
    public async Task GenerateRecommendationsAsync_WhenOnlyRecentRepeatsRemain_UsesRecentRepeatFallback()
    {
        var engine = CreateEngine(
            out var vectorStore,
            out var contentRepository);

        var repeatedA = BuildContent(daysOld: 3);
        var repeatedB = BuildContent(daysOld: 4);
        var repeatedC = BuildContent(daysOld: 5);
        var freshA = BuildContent(daysOld: 1);
        var freshB = BuildContent(daysOld: 2);
        var allContent = new[] { freshA, freshB, repeatedA, repeatedB, repeatedC };

        var context = BuildContext();
        context.RecentlyRecommendedIds = new HashSet<Guid>
        {
            repeatedA.Id,
            repeatedB.Id,
            repeatedC.Id
        };

        contentRepository.Setup(repo => repo.GetByTypeAsync(ContentType.BlogPost, It.IsAny<CancellationToken>()))
            .ReturnsAsync(allContent);

        var results = await engine.GenerateRecommendationsAsync(context, CancellationToken.None);

        Assert.HasCount(5, results);
        CollectionAssert.AreEquivalent(
            allContent.Select(content => content.Id).ToArray(),
            results.Select(result => result.Content.Id).ToArray());
        vectorStore.VerifyNoOtherCalls();
        contentRepository.Verify(repo => repo.GetByTypeAsync(ContentType.BlogPost, It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    [TestMethod]
    public async Task GenerateRecommendationsAsync_WhenCandidatesHaveSimilarRelevance_RanksNewerContentFirst()
    {
        var engine = CreateEngine(
            out var vectorStore,
            out var contentRepository,
            new IContentScorer[] { new RecencyScorer() });

        var newest = BuildContent(daysOld: 1);
        var older = BuildContent(daysOld: 45);
        var context = BuildContext();
        context.Count = 2;

        contentRepository.Setup(repo => repo.GetByTypeAsync(ContentType.BlogPost, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { older, newest });

        var results = await engine.GenerateRecommendationsAsync(context, CancellationToken.None);

        Assert.HasCount(2, results);
        Assert.AreEqual(newest.Id, results[0].Content.Id);
        Assert.AreEqual(older.Id, results[1].Content.Id);
        vectorStore.VerifyNoOtherCalls();
    }

    private static RecommendationEngine CreateEngine(
        out Mock<IVectorStore> vectorStore,
        out Mock<IContentRepository> contentRepository,
        IEnumerable<IContentScorer>? scorers = null)
    {
        vectorStore = new Mock<IVectorStore>(MockBehavior.Strict);
        contentRepository = new Mock<IContentRepository>(MockBehavior.Strict);

        return new RecommendationEngine(
            vectorStore.Object,
            contentRepository.Object,
            new CompositeScorer(scorers ?? Array.Empty<IContentScorer>()),
            new IRecommendationFilter[]
            {
                new SeenContentFilter(),
                new DiversityFilter()
            },
            NullLogger<RecommendationEngine>.Instance,
            NullObservabilityMetrics.Instance);
    }

    private static RecommendationContext BuildContext()
    {
        return new RecommendationContext
        {
            UserId = Guid.NewGuid(),
            FeedType = ContentType.BlogPost,
            Date = new DateOnly(2026, 3, 22),
            Count = 5
        };
    }

    private static BlogPost BuildContent(int daysOld)
    {
        var createdAt = new DateTime(2026, 3, 22, 12, 0, 0, DateTimeKind.Utc).AddDays(-daysOld);

        return new BlogPost
        {
            Id = Guid.NewGuid(),
            Title = $"Blog {daysOld}",
            Url = $"https://example.com/{Guid.NewGuid():N}",
            CreatedAt = createdAt,
            UpdatedAt = createdAt
        };
    }
}
