using Blazored.LocalStorage;
using Crs.Core.Enums;
using Moq;
using Crs.Web.Services;

namespace Crs.Tests.Unit.Web;

[TestClass]
public sealed class DevelopmentDataStoreTests
{
    [TestMethod]
    public async Task WriteAsync_PersistsVotesForTheNextStore()
    {
        var saved = new SavedSnapshot();
        var store = new DevelopmentDataStore(saved.Storage);
        var video = await store.ReadAsync(() => store.GetFeed().Single(item => item.Type == ContentType.Video));
        await store.WriteAsync(() => store.Vote(video.Id, VoteType.Upvote));

        var reloaded = new DevelopmentDataStore(saved.Storage);
        var votes = await reloaded.ReadAsync(() => reloaded.GetVotes());

        Assert.IsTrue(votes.Any(vote => vote.ContentId == video.Id && vote.VoteType == VoteType.Upvote));
    }

    [TestMethod]
    public async Task ResetAsync_RestoresTheSeededVotes()
    {
        var saved = new SavedSnapshot();
        var store = new DevelopmentDataStore(saved.Storage);
        var video = await store.ReadAsync(() => store.GetFeed().Single(item => item.Type == ContentType.Video));
        await store.WriteAsync(() => store.Vote(video.Id, VoteType.Upvote));
        await store.ResetAsync();

        var reloaded = new DevelopmentDataStore(saved.Storage);
        var votes = await reloaded.ReadAsync(() => reloaded.GetVotes());

        Assert.IsFalse(votes.Any(vote => vote.ContentId == video.Id));
        Assert.IsGreaterThanOrEqualTo(votes.Count, 2);
    }

    private sealed class SavedSnapshot
    {
        private DevelopmentDataSnapshot? _saved;

        public SavedSnapshot()
        {
            var localStorage = new Mock<ILocalStorageService>(MockBehavior.Strict);
            localStorage.Setup(store => store.GetItemAsync<DevelopmentDataSnapshot>(DevelopmentDataStore.StorageKey))
                .Returns(() => ValueTask.FromResult<DevelopmentDataSnapshot?>(_saved));
            localStorage.Setup(store => store.SetItemAsync(
                    DevelopmentDataStore.StorageKey,
                    It.IsAny<DevelopmentDataSnapshot>()))
                .Returns((string _, DevelopmentDataSnapshot snapshot, CancellationToken _) =>
                {
                    _saved = snapshot;
                    return ValueTask.CompletedTask;
                });
            Storage = localStorage.Object;
        }

        public ILocalStorageService Storage { get; }
    }
}
