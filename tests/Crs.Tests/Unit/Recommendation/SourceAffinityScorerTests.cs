using Crs.Core.Entities;
using Crs.Core.Enums;
using Crs.Recommendation.Models;
using Crs.Recommendation.Scorers;
using Crs.Tests.Unit.Infrastructure;

namespace Crs.Tests.Unit.Recommendation;

[TestClass]
public sealed class SourceAffinityScorerTests
{
    [TestMethod]
    public async Task ScoreAsync_WhenNoVotes_ReturnsNeutral()
    {
        var voteRepository = new InMemoryContentVoteRepository();
        var scorer = new SourceAffinityScorer(voteRepository);
        var content = new BlogPost
        {
            Id = Guid.NewGuid(),
            Title = "Content",
            Url = "https://example.com",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            SourceId = Guid.NewGuid()
        };

        var context = new RecommendationContext
        {
            UserId = Guid.NewGuid(),
            FeedType = ContentType.BlogPost,
            Date = DateOnly.FromDateTime(DateTime.UtcNow)
        };

        var score = await scorer.ScoreAsync(content, context);

        Assert.AreEqual(0.5, score, 0.0001);
    }

    [TestMethod]
    public async Task ScoreAsync_WhenOnlyOneUpvote_UsesSmoothedScore()
    {
        var voteRepository = new InMemoryContentVoteRepository();
        var scorer = new SourceAffinityScorer(voteRepository);
        var sourceId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var votedContent = new BlogPost
        {
            Id = Guid.NewGuid(),
            Title = "Voted",
            Url = "https://example.com/voted",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            SourceId = sourceId
        };

        await voteRepository.CreateAsync(new ContentVote
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ContentId = votedContent.Id,
            Content = votedContent,
            VoteType = VoteType.Upvote,
            CreatedAt = DateTime.UtcNow
        });

        var targetContent = new BlogPost
        {
            Id = Guid.NewGuid(),
            Title = "Target",
            Url = "https://example.com/target",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            SourceId = sourceId
        };

        var context = new RecommendationContext
        {
            UserId = userId,
            FeedType = ContentType.BlogPost,
            Date = DateOnly.FromDateTime(DateTime.UtcNow)
        };

        var score = await scorer.ScoreAsync(targetContent, context);

        Assert.AreEqual(2.0 / 3.0, score, 0.0001);
    }

    [TestMethod]
    public async Task ScoreAsync_WhenMixedVotes_UsesSmoothedSourceSentiment()
    {
        var voteRepository = new InMemoryContentVoteRepository();
        var scorer = new SourceAffinityScorer(voteRepository);
        var sourceId = Guid.NewGuid();
        var otherSourceId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var votedContent = new BlogPost
        {
            Id = Guid.NewGuid(),
            Title = "Voted",
            Url = "https://example.com/voted",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            SourceId = sourceId
        };

        var otherSourceContent = new BlogPost
        {
            Id = Guid.NewGuid(),
            Title = "Other",
            Url = "https://example.com/other",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            SourceId = otherSourceId
        };

        await voteRepository.CreateAsync(new ContentVote
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ContentId = votedContent.Id,
            Content = votedContent,
            VoteType = VoteType.Upvote,
            CreatedAt = DateTime.UtcNow
        });

        await voteRepository.CreateAsync(new ContentVote
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ContentId = votedContent.Id,
            Content = votedContent,
            VoteType = VoteType.Upvote,
            CreatedAt = DateTime.UtcNow
        });

        await voteRepository.CreateAsync(new ContentVote
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ContentId = votedContent.Id,
            Content = votedContent,
            VoteType = VoteType.Downvote,
            CreatedAt = DateTime.UtcNow
        });

        await voteRepository.CreateAsync(new ContentVote
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ContentId = otherSourceContent.Id,
            Content = otherSourceContent,
            VoteType = VoteType.Downvote,
            CreatedAt = DateTime.UtcNow
        });

        var targetContent = new BlogPost
        {
            Id = Guid.NewGuid(),
            Title = "Target",
            Url = "https://example.com/target",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            SourceId = sourceId
        };

        var context = new RecommendationContext
        {
            UserId = userId,
            FeedType = ContentType.BlogPost,
            Date = DateOnly.FromDateTime(DateTime.UtcNow)
        };

        var score = await scorer.ScoreAsync(targetContent, context);

        Assert.AreEqual(3.0 / 5.0, score, 0.0001);
    }
}
