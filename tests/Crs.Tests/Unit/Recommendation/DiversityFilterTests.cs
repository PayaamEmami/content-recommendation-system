using Crs.Core.Entities;
using Crs.Core.Enums;
using Crs.Recommendation.Filters;
using Crs.Recommendation.Models;

namespace Crs.Tests.Unit.Recommendation;

[TestClass]
public sealed class DiversityFilterTests
{
    [TestMethod]
    public async Task FilterAsync_LimitsContentPerSource()
    {
        var filter = new DiversityFilter();
        var sourceId = Guid.NewGuid();
        var context = new RecommendationContext
        {
            UserId = Guid.NewGuid(),
            FeedType = ContentType.BlogPost,
            Date = DateOnly.FromDateTime(DateTime.UtcNow),
            Count = 4
        };

        var candidates = new List<ScoredContent>
        {
            BuildCandidate(sourceId, 0.9),
            BuildCandidate(sourceId, 0.8),
            BuildCandidate(sourceId, 0.7),
            BuildCandidate(sourceId, 0.6),
            BuildCandidate(null, 0.5)
        };

        var filtered = await filter.FilterAsync(candidates, context);

        var sameSourceCount = filtered.Count(sr => sr.Content.SourceId == sourceId);
        Assert.AreEqual(3, sameSourceCount);
        Assert.HasCount(4, filtered);
    }

    [TestMethod]
    public async Task FilterAsync_BackfillsWhenDiversityWouldUnderfillFeed()
    {
        var filter = new DiversityFilter();
        var sourceId = Guid.NewGuid();
        var context = new RecommendationContext
        {
            UserId = Guid.NewGuid(),
            FeedType = ContentType.BlogPost,
            Date = DateOnly.FromDateTime(DateTime.UtcNow),
            Count = 5
        };

        var candidates = new List<ScoredContent>
        {
            BuildCandidate(sourceId, 0.9),
            BuildCandidate(sourceId, 0.8),
            BuildCandidate(sourceId, 0.7),
            BuildCandidate(sourceId, 0.6),
            BuildCandidate(sourceId, 0.5)
        };

        var filtered = await filter.FilterAsync(candidates, context);

        Assert.HasCount(5, filtered);
        Assert.AreEqual(5, filtered.Count(sr => sr.Content.SourceId == sourceId));
    }

    private static ScoredContent BuildCandidate(Guid? sourceId, double score)
    {
        var content = new BlogPost
        {
            Id = Guid.NewGuid(),
            Title = "Content",
            Url = "https://example.com",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            SourceId = sourceId
        };

        return new ScoredContent
        {
            Content = content,
            FinalScore = score
        };
    }
}
