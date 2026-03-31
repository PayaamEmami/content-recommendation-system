using Crs.Core.Entities;
using Crs.Core.Enums;
using Crs.Core.Interfaces;
using Crs.Recommendation.Models;

namespace Crs.Recommendation.Scorers;

/// <summary>
/// Scores content based on the user's historical sentiment toward the content's source.
/// Uses Laplace smoothing so a tiny amount of history does not overcorrect the ranking.
/// </summary>
public class SourceAffinityScorer : IContentScorer
{
    private readonly IContentVoteRepository _voteRepository;
    private Guid? _cachedUserId;
    private IEnumerable<ContentVote>? _cachedVotes;

    public SourceAffinityScorer(IContentVoteRepository voteRepository)
    {
        _voteRepository = voteRepository;
    }

    public double Weight => 0.2; // Supporting signal behind freshness

    public async Task<double> ScoreAsync(
        Content content,
        RecommendationContext context,
        CancellationToken cancellationToken = default)
    {
        if (!content.SourceId.HasValue)
        {
            return 0.5;
        }

        var userVotes = await GetUserVotesCachedAsync(context.UserId, cancellationToken);
        var sourceVotes = userVotes
            .Where(v => v.Content.SourceId == content.SourceId.Value)
            .ToList();

        if (!sourceVotes.Any())
        {
            return 0.5;
        }

        var upvotes = sourceVotes.Count(v => v.VoteType == VoteType.Upvote);
        var totalVotes = sourceVotes.Count(v => v.VoteType == VoteType.Upvote || v.VoteType == VoteType.Downvote);

        if (totalVotes == 0)
        {
            return 0.5;
        }

        // Beta(1,1) prior keeps the score neutral when evidence is sparse.
        var smoothedScore = (upvotes + 1.0) / (totalVotes + 2.0);
        return Math.Clamp(smoothedScore, 0.0, 1.0);
    }

    private async Task<IEnumerable<ContentVote>> GetUserVotesCachedAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (_cachedUserId == userId && _cachedVotes != null)
        {
            return _cachedVotes;
        }

        var votes = await _voteRepository.GetByUserAsync(userId, cancellationToken);
        var voteList = votes.ToList();
        _cachedUserId = userId;
        _cachedVotes = voteList;
        return voteList;
    }
}
