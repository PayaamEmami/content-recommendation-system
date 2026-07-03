using Crs.Core.Enums;
using Crs.Web.Services;

namespace Crs.Tests.Unit.Web;

[TestClass]
public sealed class VoteSyncCoordinatorTests
{
    [TestMethod]
    public void SetServerVotes_SeedsDisplayedState()
    {
        var contentId = Guid.NewGuid();
        var coordinator = new VoteSyncCoordinator(
            (_, vt) => Task.FromResult<VoteType?>(vt),
            _ => Task.FromResult(true));

        coordinator.SetServerVotes(new[]
        {
            new VoteItem { ContentId = contentId, VoteType = VoteType.Upvote }
        });

        Assert.AreEqual(VoteType.Upvote, coordinator.GetVoteState(contentId));
    }

    [TestMethod]
    public async Task HandleVote_AppliesOptimisticStateAndPersists()
    {
        var contentId = Guid.NewGuid();
        var persistedVotes = new List<VoteType>();
        var coordinator = new VoteSyncCoordinator(
            (_, vt) =>
            {
                persistedVotes.Add(vt);
                return Task.FromResult<VoteType?>(vt);
            },
            _ => Task.FromResult(true));

        coordinator.HandleVote(contentId, VoteType.Upvote);

        // Optimistic state is applied synchronously.
        Assert.AreEqual(VoteType.Upvote, coordinator.GetVoteState(contentId));

        await coordinator.LastSyncTask;

        Assert.AreEqual(VoteType.Upvote, coordinator.GetVoteState(contentId));
        Assert.HasCount(1, persistedVotes);
        Assert.IsFalse(coordinator.IsSyncing(contentId));
    }

    [TestMethod]
    public async Task HandleVote_TogglingSameVote_ClearsAndRemovesServerVote()
    {
        var contentId = Guid.NewGuid();
        var removeCalls = 0;
        var coordinator = new VoteSyncCoordinator(
            (_, vt) => Task.FromResult<VoteType?>(vt),
            _ =>
            {
                removeCalls++;
                return Task.FromResult(true);
            });

        coordinator.SetServerVotes(new[]
        {
            new VoteItem { ContentId = contentId, VoteType = VoteType.Upvote }
        });

        // Re-voting the same direction clears the vote.
        coordinator.HandleVote(contentId, VoteType.Upvote);

        Assert.IsNull(coordinator.GetVoteState(contentId));

        await coordinator.LastSyncTask;

        Assert.IsNull(coordinator.GetVoteState(contentId));
        Assert.AreEqual(1, removeCalls);
    }

    [TestMethod]
    public async Task HandleVote_WhenPersistFails_RollsBackToServerState()
    {
        var contentId = Guid.NewGuid();
        var coordinator = new VoteSyncCoordinator(
            (_, _) => Task.FromResult<VoteType?>(null),
            _ => Task.FromResult(false));

        coordinator.HandleVote(contentId, VoteType.Downvote);

        await coordinator.LastSyncTask;

        // Rolled back to the (empty) server state after the failed persist.
        Assert.IsNull(coordinator.GetVoteState(contentId));
    }

    [TestMethod]
    public async Task HandleVote_RapidRepeat_IsDebounced()
    {
        var contentId = Guid.NewGuid();
        var voteCalls = 0;
        var coordinator = new VoteSyncCoordinator(
            (_, vt) =>
            {
                voteCalls++;
                return Task.FromResult<VoteType?>(vt);
            },
            _ => Task.FromResult(true));

        coordinator.HandleVote(contentId, VoteType.Upvote);
        // Immediate second action within the debounce window is ignored.
        coordinator.HandleVote(contentId, VoteType.Downvote);

        await coordinator.LastSyncTask;

        Assert.AreEqual(VoteType.Upvote, coordinator.GetVoteState(contentId));
        Assert.AreEqual(1, voteCalls);
    }
}
