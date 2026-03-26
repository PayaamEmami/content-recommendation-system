using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Crs.Core.Entities;
using Crs.Core.Enums;
using Crs.Core.Interfaces;
using Crs.Core.Observability;
using Crs.Recommendation.Engine;
using Crs.Recommendation.Models;
using Crs.Recommendation.Services;
using RecommendationEntity = Crs.Core.Entities.Recommendation;

namespace Crs.Tests.Unit.Recommendation;

[TestClass]
public sealed class FeedGeneratorTests
{
    [TestMethod]
    public async Task GenerateFeedAsync_WhenExistingFeedIsComplete_ReturnsExistingFeed()
    {
        var generator = CreateGenerator(
            out var engine,
            out var profileService,
            out var recommendationRepository,
            out var voteRepository);

        var userId = Guid.NewGuid();
        var date = new DateOnly(2026, 3, 18);
        var existing = Enumerable.Range(1, 5)
            .Select(position => new RecommendationEntity
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                ContentId = Guid.NewGuid(),
                FeedType = ContentType.BlogPost,
                Date = date,
                Position = position,
                GeneratedAt = DateTime.UtcNow
            })
            .ToList();

        recommendationRepository.Setup(repo => repo.GetByUserDateAndTypeAsync(
                userId,
                date,
                ContentType.BlogPost,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);

        var result = await generator.GenerateFeedAsync(userId, ContentType.BlogPost, date, 5, CancellationToken.None);

        Assert.HasCount(5, result);
        CollectionAssert.AreEqual(existing, result);
        profileService.VerifyNoOtherCalls();
        voteRepository.VerifyNoOtherCalls();
        engine.VerifyNoOtherCalls();
        recommendationRepository.Verify(repo => repo.ReplaceFeedAsync(
            It.IsAny<Guid>(),
            It.IsAny<DateOnly>(),
            It.IsAny<ContentType>(),
            It.IsAny<IEnumerable<RecommendationEntity>>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task GenerateFeedAsync_WhenExistingFeedIsIncomplete_RegeneratesAndReplacesFeed()
    {
        var generator = CreateGenerator(
            out var engine,
            out var profileService,
            out var recommendationRepository,
            out var voteRepository);

        var userId = Guid.NewGuid();
        var date = new DateOnly(2026, 3, 18);
        var profile = new UserInterestProfile { UserId = userId };
        var existing = Enumerable.Range(1, 4)
            .Select(position => new RecommendationEntity
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                ContentId = Guid.NewGuid(),
                FeedType = ContentType.BlogPost,
                Date = date,
                Position = position,
                GeneratedAt = DateTime.UtcNow
            })
            .ToList();

        var scored = Enumerable.Range(1, 5)
            .Select(position => new ScoredContent
            {
                Content = new BlogPost
                {
                    Id = Guid.NewGuid(),
                    Title = $"Blog {position}",
                    Url = $"https://example.com/{position}",
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                },
                FinalScore = 1.0 - (position * 0.1)
            })
            .ToList();

        recommendationRepository.Setup(repo => repo.GetByUserDateAndTypeAsync(
                userId,
                date,
                ContentType.BlogPost,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);
        profileService.Setup(service => service.BuildProfileAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(profile);
        voteRepository.Setup(repo => repo.GetByUserAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ContentVote>());
        recommendationRepository.Setup(repo => repo.GetRecentByUserAsync(
                userId,
                date.AddDays(-7),
                date.AddDays(-1),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<RecommendationEntity>());
        engine.Setup(service => service.GenerateRecommendationsAsync(
                It.Is<RecommendationContext>(context =>
                    context.UserId == userId &&
                    context.FeedType == ContentType.BlogPost &&
                    context.Date == date &&
                    context.Count == 5 &&
                    context.UserProfile == profile),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(scored);
        recommendationRepository.Setup(repo => repo.ReplaceFeedAsync(
                userId,
                date,
                ContentType.BlogPost,
                It.Is<IEnumerable<RecommendationEntity>>(recommendations =>
                    recommendations.Count() == 5 &&
                    recommendations.Select(r => r.Position).SequenceEqual(new[] { 1, 2, 3, 4, 5 }) &&
                    recommendations.Select(r => r.ContentId).SequenceEqual(scored.Select(s => s.Content.Id))),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var result = await generator.GenerateFeedAsync(userId, ContentType.BlogPost, date, 5, CancellationToken.None);

        Assert.HasCount(5, result);
        CollectionAssert.AreEqual(new[] { 1, 2, 3, 4, 5 }, result.Select(r => r.Position).ToArray());
        recommendationRepository.VerifyAll();
        profileService.VerifyAll();
        voteRepository.VerifyAll();
        engine.VerifyAll();
    }

    private static FeedGenerator CreateGenerator(
        out Mock<IRecommendationEngine> engine,
        out Mock<IUserProfileService> profileService,
        out Mock<IRecommendationRepository> recommendationRepository,
        out Mock<IContentVoteRepository> voteRepository)
    {
        engine = new Mock<IRecommendationEngine>(MockBehavior.Strict);
        profileService = new Mock<IUserProfileService>(MockBehavior.Strict);
        recommendationRepository = new Mock<IRecommendationRepository>(MockBehavior.Strict);
        voteRepository = new Mock<IContentVoteRepository>(MockBehavior.Strict);

        return new FeedGenerator(
            engine.Object,
            profileService.Object,
            recommendationRepository.Object,
            voteRepository.Object,
            NullLogger<FeedGenerator>.Instance,
            NullObservabilityMetrics.Instance);
    }
}
