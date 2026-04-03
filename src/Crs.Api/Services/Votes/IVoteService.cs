using Crs.Api.DTOs.Votes.Requests;
using Crs.Api.DTOs.Votes.Responses;

namespace Crs.Api.Services;

/// <summary>
/// Service interface for voting operations.
/// </summary>
public interface IVoteService
{
    /// <summary>
    /// Casts or updates a vote on a content item.
    /// </summary>
    Task<VoteResponse> VoteOnContentAsync(Guid userId, Guid contentId, VoteRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a vote from a content item.
    /// </summary>
    Task RemoveVoteAsync(Guid userId, Guid contentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all votes by a user.
    /// </summary>
    Task<List<VoteResponse>> GetUserVotesAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the user's current vote history joined with content details.
    /// </summary>
    Task<List<VoteHistoryItemResponse>> GetUserVoteHistoryAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a user's vote on a specific content item.
    /// </summary>
    Task<VoteResponse?> GetUserVoteOnContentAsync(Guid userId, Guid contentId, CancellationToken cancellationToken = default);
}
