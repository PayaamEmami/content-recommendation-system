using Crs.Api.DTOs.Votes.Requests;
using Crs.Api.DTOs.Votes.Responses;
using Crs.Core.Entities;
using Crs.Core.Interfaces;

namespace Crs.Api.Services;

/// <summary>
/// Service for handling voting operations.
/// </summary>
public class VoteService : IVoteService
{
    private readonly IContentVoteRepository _voteRepository;
    private readonly IUserRepository _userRepository;
    private readonly IContentRepository _contentRepository;
    private readonly ILogger<VoteService> _logger;

    public VoteService(
        IContentVoteRepository voteRepository,
        IUserRepository userRepository,
        IContentRepository contentRepository,
        ILogger<VoteService> logger)
    {
        _voteRepository = voteRepository;
        _userRepository = userRepository;
        _contentRepository = contentRepository;
        _logger = logger;
    }

    public async Task<VoteResponse> VoteOnContentAsync(
        Guid userId,
        Guid contentId,
        VoteRequest request,
        CancellationToken cancellationToken = default)
    {
        // Verify user exists
        var user = await _userRepository.GetByIdAsync(userId, cancellationToken);
        if (user == null)
        {
            throw new KeyNotFoundException($"User with ID {userId} not found");
        }

        // Verify content exists
        var content = await _contentRepository.GetByIdAsync(contentId, cancellationToken);
        if (content == null)
        {
            throw new KeyNotFoundException($"Content with ID {contentId} not found");
        }

        // Check if user already voted on this content
        var existingVote = await _voteRepository.GetByUserAndContentAsync(userId, contentId, cancellationToken);

        ContentVote vote;

        if (existingVote != null)
        {
            // Update existing vote
            existingVote.VoteType = request.VoteType;
            existingVote.UpdatedAt = DateTime.UtcNow;
            vote = await _voteRepository.UpdateAsync(existingVote, cancellationToken);

            _logger.LogInformation("User {UserId} updated vote on content {ContentId} to {VoteType}",
                userId, contentId, request.VoteType);
        }
        else
        {
            // Create new vote
            vote = new ContentVote
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                ContentId = contentId,
                VoteType = request.VoteType,
                CreatedAt = DateTime.UtcNow
            };

            vote = await _voteRepository.CreateAsync(vote, cancellationToken);

            _logger.LogInformation("User {UserId} voted {VoteType} on content {ContentId}",
                userId, request.VoteType, contentId);
        }

        return MapToVoteResponse(vote);
    }

    public async Task RemoveVoteAsync(Guid userId, Guid contentId, CancellationToken cancellationToken = default)
    {
        var vote = await _voteRepository.GetByUserAndContentAsync(userId, contentId, cancellationToken);

        if (vote == null)
        {
            throw new KeyNotFoundException($"No vote found for user {userId} on content {contentId}");
        }

        await _voteRepository.DeleteAsync(vote.Id, cancellationToken);

        _logger.LogInformation("User {UserId} removed vote from content {ContentId}", userId, contentId);
    }

    public async Task<List<VoteResponse>> GetUserVotesAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var votes = await _voteRepository.GetByUserAsync(userId, cancellationToken);

        return votes.Select(MapToVoteResponse).ToList();
    }

    public async Task<List<VoteHistoryItemResponse>> GetUserVoteHistoryAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var votes = await _voteRepository.GetByUserAsync(userId, cancellationToken);

        return votes
            .Where(vote => vote.Content != null)
            .OrderByDescending(vote => vote.UpdatedAt ?? vote.CreatedAt)
            .Select(MapToVoteHistoryItemResponse)
            .ToList();
    }

    public async Task<VoteResponse?> GetUserVoteOnContentAsync(
        Guid userId,
        Guid contentId,
        CancellationToken cancellationToken = default)
    {
        var vote = await _voteRepository.GetByUserAndContentAsync(userId, contentId, cancellationToken);

        return vote != null ? MapToVoteResponse(vote) : null;
    }

    private static VoteResponse MapToVoteResponse(ContentVote vote)
    {
        return new VoteResponse
        {
            Id = vote.Id,
            UserId = vote.UserId,
            ContentId = vote.ContentId,
            VoteType = vote.VoteType,
            CreatedAt = vote.CreatedAt,
            UpdatedAt = vote.UpdatedAt
        };
    }

    private static VoteHistoryItemResponse MapToVoteHistoryItemResponse(ContentVote vote)
    {
        var content = vote.Content ?? throw new InvalidOperationException("Vote history content was not loaded.");

        return new VoteHistoryItemResponse
        {
            Id = vote.Id,
            UserId = vote.UserId,
            ContentId = vote.ContentId,
            VoteType = vote.VoteType,
            CreatedAt = vote.CreatedAt,
            UpdatedAt = vote.UpdatedAt,
            Title = content.Title,
            Description = content.Description,
            Url = content.Url,
            Type = content.Type,
            ContentDate = content.CreatedAt
        };
    }
}
