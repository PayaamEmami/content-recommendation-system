using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Crs.Api.Services;
using Crs.Core.Entities;
using Crs.Core.Interfaces;
using Crs.Core.Models;
using Crs.Infrastructure.Configuration;

namespace Crs.Tests.Unit.Api;

[TestClass]
public sealed class XAccountServiceTests
{
    private static XAccountService CreateService(
        XApiSettings settings,
        out Mock<IXConnectionRepository> connectionRepository,
        out Mock<IXFollowedAccountRepository> followedAccountRepository,
        out Mock<IXSelectedAccountRepository> selectedAccountRepository,
        out Mock<IXPostRepository> postRepository,
        out Mock<IXApiClient> xApiClient,
        Mock<IXAuthStateRepository>? authStateRepository = null)
    {
        connectionRepository = new Mock<IXConnectionRepository>(MockBehavior.Strict);
        var authState = authStateRepository ?? new Mock<IXAuthStateRepository>(MockBehavior.Strict);
        followedAccountRepository = new Mock<IXFollowedAccountRepository>(MockBehavior.Strict);
        selectedAccountRepository = new Mock<IXSelectedAccountRepository>(MockBehavior.Strict);
        postRepository = new Mock<IXPostRepository>(MockBehavior.Strict);
        xApiClient = new Mock<IXApiClient>(MockBehavior.Strict);

        var protector = DataProtectionProvider.Create("crs-tests");
        return new XAccountService(
            connectionRepository.Object,
            authState.Object,
            followedAccountRepository.Object,
            selectedAccountRepository.Object,
            postRepository.Object,
            xApiClient.Object,
            Options.Create(settings),
            protector,
            NullLogger<XAccountService>.Instance);
    }

    private static Mock<IXAuthStateRepository> CreateAuthStateRepo(
        XAuthState? stateToReturn = null,
        Action<XAuthState>? captureAdd = null)
    {
        var mock = new Mock<IXAuthStateRepository>(MockBehavior.Strict);
        mock.Setup(x => x.RemoveExpiredAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mock.Setup(x => x.AddAsync(It.IsAny<XAuthState>(), It.IsAny<CancellationToken>()))
            .Callback<XAuthState, CancellationToken>((s, _) => captureAdd?.Invoke(s))
            .Returns(Task.CompletedTask);
        mock.Setup(x => x.GetAndRemoveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string state, CancellationToken _) =>
                stateToReturn != null && stateToReturn.State == state ? stateToReturn : null);
        return mock;
    }

    [TestMethod]
    public async Task CreateConnectUrlAsync_WhenRedirectMissing_Throws()
    {
        var authState = CreateAuthStateRepo();
        var service = CreateService(new XApiSettings { ClientId = "client", RedirectUri = string.Empty }, out _, out _, out _, out _, out _, authState);

        await TestAssert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateConnectUrlAsync(Guid.NewGuid(), null, CancellationToken.None));
    }

    [TestMethod]
    public async Task CreateConnectUrlAsync_WhenConfigured_ReturnsAuthorizationUrl()
    {
        var authState = CreateAuthStateRepo();
        var service = CreateService(new XApiSettings
        {
            ClientId = "client",
            RedirectUri = "https://app.example.com/callback",
            AuthorizationUrl = "https://x.com/oauth2"
        }, out _, out _, out _, out _, out _, authState);

        var url = await service.CreateConnectUrlAsync(Guid.NewGuid(), null, CancellationToken.None);

        Assert.IsTrue(url.StartsWith("https://x.com/oauth2?", StringComparison.Ordinal));
        Assert.IsTrue(url.Contains("client_id=client", StringComparison.Ordinal));
        Assert.IsTrue(url.Contains("redirect_uri=https%3A%2F%2Fapp.example.com%2Fcallback", StringComparison.Ordinal));
        Assert.IsTrue(url.Contains("scope=tweet.read%20users.read%20follows.read%20offline.access", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task HandleCallbackAsync_WhenStateInvalid_Throws()
    {
        var authState = CreateAuthStateRepo();
        var service = CreateService(new XApiSettings { ClientId = "client", RedirectUri = "https://app.example.com/callback" },
            out _, out _, out _, out _, out _, authState);

        await TestAssert.ThrowsAsync<InvalidOperationException>(() =>
            service.HandleCallbackAsync(Guid.NewGuid(), "code", "missing", CancellationToken.None));
    }

    [TestMethod]
    public async Task HandleCallbackAsync_WhenValid_StoresConnectionAndRefreshes()
    {
        var settings = new XApiSettings
        {
            ClientId = "client",
            RedirectUri = "https://app.example.com/callback",
            AuthorizationUrl = "https://x.com/oauth2"
        };

        XAuthState? capturedState = null;
        var authStateRepo = new Mock<IXAuthStateRepository>(MockBehavior.Strict);
        authStateRepo.Setup(x => x.RemoveExpiredAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        authStateRepo.Setup(x => x.AddAsync(It.IsAny<XAuthState>(), It.IsAny<CancellationToken>()))
            .Callback<XAuthState, CancellationToken>((s, _) => capturedState = s)
            .Returns(Task.CompletedTask);
        authStateRepo.Setup(x => x.GetAndRemoveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string state, CancellationToken _) =>
                capturedState != null && capturedState.State == state ? capturedState : null);

        var service = CreateService(settings, out var connectionRepository, out var followedAccountRepository, out _, out _, out var xApiClient, authStateRepo);
        var userId = Guid.NewGuid();

        var url = await service.CreateConnectUrlAsync(userId, null, CancellationToken.None);
        var state = ExtractQueryValue(url, "state");

        var storedConnection = (XConnection?)null;
        connectionRepository.Setup(repo => repo.UpsertAsync(It.IsAny<XConnection>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((XConnection connection, CancellationToken _) =>
            {
                storedConnection = connection;
                return connection;
            });
        connectionRepository.Setup(repo => repo.GetByUserIdAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => storedConnection);

        xApiClient.Setup(client => client.ExchangeCodeAsync("code", It.IsAny<string>(), settings.RedirectUri, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new XTokenResponse
            {
                AccessToken = "access",
                RefreshToken = "refresh",
                ExpiresIn = 3600,
                Scope = "tweet.read users.read follows.read offline.access"
            });
        xApiClient.Setup(client => client.GetCurrentUserAsync("access", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new XUserProfile { XUserId = "x-user", Handle = "handle", DisplayName = "Name" });
        xApiClient.Setup(client => client.GetFollowedAccountsAsync(It.IsAny<string>(), "x-user", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<XFollowedAccountInfo>());

        followedAccountRepository.Setup(repo => repo.ReplaceForUserAsync(userId, It.IsAny<List<XFollowedAccount>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        followedAccountRepository.Setup(repo => repo.GetByUserIdAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<XFollowedAccount>());

        await service.HandleCallbackAsync(userId, "code", state, CancellationToken.None);

        Assert.IsNotNull(storedConnection);
        Assert.AreEqual(userId, storedConnection!.UserId);
    }

    [TestMethod]
    public async Task HandleCallbackAsync_WhenTokenMissingTweetRead_ThrowsHelpfulError()
    {
        var settings = new XApiSettings
        {
            ClientId = "client",
            RedirectUri = "https://app.example.com/callback",
            AuthorizationUrl = "https://x.com/oauth2"
        };

        XAuthState? capturedState = null;
        var authStateRepo = new Mock<IXAuthStateRepository>(MockBehavior.Strict);
        authStateRepo.Setup(x => x.RemoveExpiredAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        authStateRepo.Setup(x => x.AddAsync(It.IsAny<XAuthState>(), It.IsAny<CancellationToken>()))
            .Callback<XAuthState, CancellationToken>((s, _) => capturedState = s)
            .Returns(Task.CompletedTask);
        authStateRepo.Setup(x => x.GetAndRemoveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string state, CancellationToken _) =>
                capturedState != null && capturedState.State == state ? capturedState : null);

        var service = CreateService(settings, out _, out _, out _, out _, out var xApiClient, authStateRepo);
        var userId = Guid.NewGuid();

        var url = await service.CreateConnectUrlAsync(userId, null, CancellationToken.None);
        var state = ExtractQueryValue(url, "state");

        xApiClient.Setup(client => client.ExchangeCodeAsync("code", It.IsAny<string>(), settings.RedirectUri, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new XTokenResponse
            {
                AccessToken = "access",
                RefreshToken = "refresh",
                ExpiresIn = 3600,
                Scope = "users.read follows.read offline.access"
            });

        var ex = await TestAssert.ThrowsAsync<InvalidOperationException>(() =>
            service.HandleCallbackAsync(userId, "code", state, CancellationToken.None));

        StringAssert.Contains(ex.Message, "tweet.read");
    }

    [TestMethod]
    public async Task RefreshFollowedAccountsAsync_WhenNoConnection_Throws()
    {
        var service = CreateService(new XApiSettings { ClientId = "client", RedirectUri = "https://app.example.com/callback" },
            out var connectionRepository, out _, out _, out _, out _);

        connectionRepository.Setup(repo => repo.GetByUserIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((XConnection?)null);

        await TestAssert.ThrowsAsync<InvalidOperationException>(() =>
            service.RefreshFollowedAccountsAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [TestMethod]
    public async Task UpdateSelectedAccountsAsync_ReturnsSelection()
    {
        var service = CreateService(new XApiSettings { ClientId = "client", RedirectUri = "https://app.example.com/callback" },
            out _, out _, out var selectedAccountRepository, out _, out _);

        var userId = Guid.NewGuid();
        var accountId = Guid.NewGuid();

        selectedAccountRepository.Setup(repo => repo.ReplaceForUserAsync(userId, It.IsAny<List<XSelectedAccount>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        selectedAccountRepository.Setup(repo => repo.GetByUserIdAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<XSelectedAccount> { new() { XFollowedAccountId = accountId } });

        var result = await service.UpdateSelectedAccountsAsync(userId, new List<Guid> { accountId }, CancellationToken.None);

        Assert.HasCount(1, result);
        Assert.AreEqual(accountId, result[0].XFollowedAccountId);
    }

    [TestMethod]
    public async Task GetPostsAsync_ReturnsRepositoryPosts()
    {
        var service = CreateService(new XApiSettings { ClientId = "client", RedirectUri = "https://app.example.com/callback" },
            out _, out _, out _, out var postRepository, out _);

        var userId = Guid.NewGuid();
        postRepository.Setup(repo => repo.GetLatestByUserIdAsync(userId, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<XPost> { new() { Id = Guid.NewGuid(), UserId = userId } });

        var result = await service.GetPostsAsync(userId, 5, CancellationToken.None);

        Assert.HasCount(1, result);
    }

    private static string ExtractQueryValue(string url, string key)
    {
        var query = new Uri(url).Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
        foreach (var pair in query)
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2 && string.Equals(parts[0], key, StringComparison.Ordinal))
            {
                return Uri.UnescapeDataString(parts[1]);
            }
        }

        return string.Empty;
    }
}
