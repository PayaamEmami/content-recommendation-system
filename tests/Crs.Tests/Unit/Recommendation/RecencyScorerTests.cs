using Crs.Core.Entities;
using Crs.Core.Enums;
using Crs.Recommendation.Models;
using Crs.Recommendation.Scorers;

namespace Crs.Tests.Unit.Recommendation;

[TestClass]
public sealed class RecencyScorerTests
{
    [TestMethod]
    public async Task ScoreAsync_WhenContentIsToday_ReturnsOne()
    {
        var scorer = new RecencyScorer();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var content = new BlogPost
        {
            Id = Guid.NewGuid(),
            Title = "New",
            Url = "https://example.com/new",
            CreatedAt = today.ToDateTime(TimeOnly.MinValue),
            UpdatedAt = today.ToDateTime(TimeOnly.MinValue)
        };

        var context = new RecommendationContext
        {
            UserId = Guid.NewGuid(),
            FeedType = ContentType.BlogPost,
            Date = today
        };

        var score = await scorer.ScoreAsync(content, context);

        Assert.AreEqual(1.0, score, 0.0001);
    }

    [TestMethod]
    public async Task ScoreAsync_WhenContentIsThirtyDaysOld_Decays()
    {
        var scorer = new RecencyScorer();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var createdAt = today.AddDays(-30).ToDateTime(TimeOnly.MinValue);
        var content = new BlogPost
        {
            Id = Guid.NewGuid(),
            Title = "Older",
            Url = "https://example.com/old",
            CreatedAt = createdAt,
            UpdatedAt = createdAt
        };

        var context = new RecommendationContext
        {
            UserId = Guid.NewGuid(),
            FeedType = ContentType.BlogPost,
            Date = today
        };

        var score = await scorer.ScoreAsync(content, context);

        var expected = 0.15 + (0.85 * Math.Exp(-30.0 / 14.0));
        Assert.AreEqual(expected, score, 0.0001);
    }

    [TestMethod]
    public async Task ScoreAsync_WhenContentIsVeryOld_RetainsFloorScore()
    {
        var scorer = new RecencyScorer();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var createdAt = today.AddDays(-365).ToDateTime(TimeOnly.MinValue);
        var content = new BlogPost
        {
            Id = Guid.NewGuid(),
            Title = "Very old",
            Url = "https://example.com/very-old",
            CreatedAt = createdAt,
            UpdatedAt = createdAt
        };

        var context = new RecommendationContext
        {
            UserId = Guid.NewGuid(),
            FeedType = ContentType.BlogPost,
            Date = today
        };

        var score = await scorer.ScoreAsync(content, context);

        Assert.IsGreaterThanOrEqualTo(score, 0.15);
        Assert.IsLessThan(score, 0.16);
    }
}
