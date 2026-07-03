using Crs.Core.Enums;

namespace Crs.Web.Services;

/// <summary>
/// Encapsulates the optimistic voting state machine used by the feed page: it tracks the
/// displayed vote for each content item, debounces rapid toggles, and reconciles the
/// desired state with the server in the background, rolling back on failure.
/// </summary>
/// <remarks>
/// Persistence is delegated so the coordinator stays free of HTTP concerns and can be unit
/// tested. <paramref name="voteAsync"/> returns the persisted vote (or <c>null</c> on
/// failure) and <paramref name="removeVoteAsync"/> returns whether the removal succeeded.
/// </remarks>
public sealed class VoteSyncCoordinator
{
    private static readonly TimeSpan VoteActionDebounceWindow = TimeSpan.FromMilliseconds(400);

    private readonly Func<Guid, VoteType, Task<VoteType?>> _voteAsync;
    private readonly Func<Guid, Task<bool>> _removeVoteAsync;
    private readonly Action? _onStateChanged;

    private Dictionary<Guid, VoteType> _userVotes = new();
    private readonly Dictionary<Guid, VoteType> _serverVotes = new();
    private readonly HashSet<Guid> _syncingVoteContentIds = new();
    private readonly Dictionary<Guid, VoteType?> _desiredVoteStates = new();
    private readonly Dictionary<Guid, DateTime> _lastVoteActionAt = new();

    public VoteSyncCoordinator(
        Func<Guid, VoteType, Task<VoteType?>> voteAsync,
        Func<Guid, Task<bool>> removeVoteAsync,
        Action? onStateChanged = null)
    {
        _voteAsync = voteAsync;
        _removeVoteAsync = removeVoteAsync;
        _onStateChanged = onStateChanged;
    }

    /// <summary>The most recently started background sync; primarily useful for tests.</summary>
    public Task LastSyncTask { get; private set; } = Task.CompletedTask;

    /// <summary>Seeds the coordinator with the authoritative votes loaded from the server.</summary>
    public void SetServerVotes(IEnumerable<VoteItem> votes)
    {
        var voteList = votes.ToList();
        _userVotes = voteList.ToDictionary(v => v.ContentId, v => v.VoteType);
        _serverVotes.Clear();
        foreach (var vote in voteList)
        {
            _serverVotes[vote.ContentId] = vote.VoteType;
        }
    }

    /// <summary>The currently displayed vote for the content item, if any.</summary>
    public VoteType? GetVoteState(Guid contentId)
    {
        return _userVotes.TryGetValue(contentId, out var voteType) ? voteType : null;
    }

    /// <summary>Whether a background reconciliation is in flight for the content item.</summary>
    public bool IsSyncing(Guid contentId) => _syncingVoteContentIds.Contains(contentId);

    /// <summary>
    /// Applies an optimistic vote toggle and, unless a sync is already running for the item,
    /// starts reconciling it with the server. Rapid repeat actions are debounced.
    /// </summary>
    public void HandleVote(Guid contentId, VoteType voteType)
    {
        if (_lastVoteActionAt.TryGetValue(contentId, out var lastActionAt) &&
            DateTime.UtcNow - lastActionAt < VoteActionDebounceWindow)
        {
            return;
        }

        _lastVoteActionAt[contentId] = DateTime.UtcNow;

        var currentVote = GetVoteState(contentId);
        VoteType? nextVote = currentVote == voteType ? null : voteType;

        if (nextVote is { } newVote)
        {
            _userVotes[contentId] = newVote;
        }
        else
        {
            _userVotes.Remove(contentId);
        }

        _desiredVoteStates[contentId] = nextVote;

        if (!_syncingVoteContentIds.Contains(contentId))
        {
            LastSyncTask = SyncVoteStateAsync(contentId);
        }

        _onStateChanged?.Invoke();
    }

    private async Task SyncVoteStateAsync(Guid contentId)
    {
        _syncingVoteContentIds.Add(contentId);

        try
        {
            while (_desiredVoteStates.TryGetValue(contentId, out var desiredVoteState))
            {
                var previousServerVoteState = _serverVotes.TryGetValue(contentId, out var serverVote)
                    ? serverVote
                    : (VoteType?)null;

                var succeeded = await PersistVoteStateAsync(contentId, desiredVoteState);
                if (!succeeded)
                {
                    ApplyServerVoteState(contentId, previousServerVoteState);
                    _desiredVoteStates[contentId] = previousServerVoteState;
                    break;
                }

                if (desiredVoteState is { } persistedVote)
                {
                    _serverVotes[contentId] = persistedVote;
                }
                else
                {
                    _serverVotes.Remove(contentId);
                }

                if (_desiredVoteStates.TryGetValue(contentId, out var latestDesiredState) &&
                    latestDesiredState == desiredVoteState)
                {
                    _desiredVoteStates.Remove(contentId);
                }
            }
        }
        finally
        {
            _syncingVoteContentIds.Remove(contentId);
            _onStateChanged?.Invoke();
        }
    }

    private async Task<bool> PersistVoteStateAsync(Guid contentId, VoteType? desiredVoteState)
    {
        if (desiredVoteState is null)
        {
            return await _removeVoteAsync(contentId);
        }

        var result = await _voteAsync(contentId, desiredVoteState.Value);
        if (result is null)
        {
            return false;
        }

        _serverVotes[contentId] = result.Value;
        if (_desiredVoteStates.TryGetValue(contentId, out var latestDesiredState) && latestDesiredState == desiredVoteState)
        {
            _userVotes[contentId] = result.Value;
        }

        return true;
    }

    private void ApplyServerVoteState(Guid contentId, VoteType? voteState)
    {
        if (voteState is { } persistedVote)
        {
            _userVotes[contentId] = persistedVote;
            _serverVotes[contentId] = persistedVote;
            return;
        }

        _userVotes.Remove(contentId);
        _serverVotes.Remove(contentId);
    }
}
