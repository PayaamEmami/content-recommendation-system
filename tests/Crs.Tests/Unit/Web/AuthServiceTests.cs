using System.Net;
using System.Text.Json;
using Blazored.LocalStorage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Crs.Web.Services;

namespace Crs.Tests.Unit.Web;

[TestClass]
public sealed class AuthServiceTests
{
    [TestMethod]
    public async Task InitializeAsync_RestoresStateFromStorage()
    {
        var storedState = new AuthState
        {
            IsAuthenticated = true,
            Email = "user@example.com",
            AccessToken = "access",
            ExpiresAt = DateTime.UtcNow.AddMinutes(5)
        };
        var localStorage = new Mock<ILocalStorageService>(MockBehavior.Strict);
        localStorage.Setup(store => store.GetItemAsync<AuthState>(It.IsAny<string>()))
            .ReturnsAsync(storedState);

        var authService = CreateAuthService(localStorage, new HttpTestHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));

        await authService.InitializeAsync();

        Assert.IsTrue(authService.CurrentState.IsAuthenticated);
        Assert.AreEqual("user@example.com", authService.CurrentState.Email);
    }

    [TestMethod]
    public async Task InitializeAsync_DoesNotLogStoredEmailAddress()
    {
        var storedState = new AuthState
        {
            IsAuthenticated = true,
            Email = "user@example.com",
            AccessToken = "access",
            ExpiresAt = DateTime.UtcNow.AddMinutes(5)
        };
        var localStorage = new Mock<ILocalStorageService>(MockBehavior.Strict);
        localStorage.Setup(store => store.GetItemAsync<AuthState>(It.IsAny<string>()))
            .ReturnsAsync(storedState);
        var logger = new RecordingLogger<AuthService>();

        var authService = CreateAuthService(localStorage, new HttpTestHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)), logger);

        await authService.InitializeAsync();

        Assert.IsTrue(logger.Messages.Any(message => message.Contains("Restored auth state from storage", StringComparison.Ordinal)));
        Assert.IsFalse(logger.Messages.Any(message => message.Contains("user@example.com", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task EnsureAuthenticatedAsync_WhenNoState_ReturnsFalse()
    {
        var localStorage = new Mock<ILocalStorageService>(MockBehavior.Strict);
        localStorage.Setup(store => store.GetItemAsync<AuthState>(It.IsAny<string>()))
            .ReturnsAsync((AuthState?)null);

        var authService = CreateAuthService(localStorage, new HttpTestHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));

        var result = await authService.EnsureAuthenticatedAsync();

        Assert.IsFalse(result);
    }

    [TestMethod]
    public async Task TryRefreshAsync_WhenMissingRefreshToken_ReturnsFalse()
    {
        var localStorage = new Mock<ILocalStorageService>(MockBehavior.Strict);
        localStorage.Setup(store => store.GetItemAsync<AuthState>(It.IsAny<string>()))
            .ReturnsAsync(new AuthState { IsAuthenticated = true, AccessToken = "access", ExpiresAt = DateTime.UtcNow.AddMinutes(-5) });

        var authService = CreateAuthService(localStorage, new HttpTestHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        await authService.InitializeAsync();

        var result = await authService.TryRefreshAsync();

        Assert.IsFalse(result);
    }

    [TestMethod]
    public async Task TryRefreshAsync_WhenSuccess_UpdatesState()
    {
        var storedState = new AuthState
        {
            IsAuthenticated = true,
            AccessToken = "expired",
            RefreshToken = "refresh",
            ExpiresAt = DateTime.UtcNow.AddMinutes(-5)
        };

        var localStorage = new Mock<ILocalStorageService>(MockBehavior.Strict);
        localStorage.Setup(store => store.GetItemAsync<AuthState>(It.IsAny<string>()))
            .ReturnsAsync(storedState);
        localStorage.Setup(store => store.SetItemAsync(It.IsAny<string>(), It.IsAny<AuthState>()))
            .Returns(ValueTask.CompletedTask);

        var refreshPayload = new RefreshTokenResponse
        {
            AccessToken = "new-access",
            RefreshToken = "new-refresh",
            ExpiresAt = DateTime.UtcNow.AddMinutes(10)
        };

        var handler = new HttpTestHandler(_ =>
        {
            var json = JsonSerializer.Serialize(refreshPayload, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json)
            };
        });

        var authService = CreateAuthService(localStorage, handler);
        await authService.InitializeAsync();

        var result = await authService.TryRefreshAsync();

        Assert.IsTrue(result);
        Assert.AreEqual("new-access", authService.CurrentState.AccessToken);
    }

    [TestMethod]
    public async Task UseDevelopmentLoginAsync_WhenEnabled_SetsDevelopmentAuthState()
    {
        var localStorage = new Mock<ILocalStorageService>(MockBehavior.Strict);
        localStorage.Setup(store => store.SetItemAsync(It.IsAny<string>(), It.IsAny<AuthState>()))
            .Returns(ValueTask.CompletedTask);

        var authService = CreateAuthService(
            localStorage,
            new HttpTestHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)),
            configurationValues: new Dictionary<string, string?>
            {
                ["DevelopmentLogin:Enabled"] = "true"
            });

        var result = await authService.UseDevelopmentLoginAsync();

        Assert.IsTrue(result.Success);
        Assert.IsTrue(authService.CurrentState.IsAuthenticated);
        Assert.IsTrue(authService.CurrentState.IsDevelopmentLogin);
        Assert.AreEqual("dev-user@localhost", authService.CurrentState.Email);
    }

    [TestMethod]
    public async Task UseDevelopmentLoginAsync_WhenDisabled_ReturnsFailure()
    {
        var localStorage = new Mock<ILocalStorageService>(MockBehavior.Strict);
        var authService = CreateAuthService(localStorage, new HttpTestHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));

        var result = await authService.UseDevelopmentLoginAsync();

        Assert.IsFalse(result.Success);
        Assert.IsFalse(authService.CurrentState.IsAuthenticated);
    }

    [TestMethod]
    public void IsDevelopmentLoginEnabled_WhenRunningOnLocalhost_ReturnsTrue()
    {
        var localStorage = new Mock<ILocalStorageService>(MockBehavior.Strict);
        var authService = CreateAuthService(
            localStorage,
            new HttpTestHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)),
            navigationManager: new TestNavigationManager("http://localhost:5250/"));

        Assert.IsTrue(authService.IsDevelopmentLoginEnabled);
    }

    private static AuthService CreateAuthService(
        Mock<ILocalStorageService> localStorage,
        HttpTestHandler handler,
        ILogger<AuthService>? logger = null,
        Dictionary<string, string?>? configurationValues = null,
        TestNavigationManager? navigationManager = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["Registration:Enabled"] = "true",
            ["Registration:DisabledMessage"] = "off"
        };

        if (configurationValues != null)
        {
            foreach (var pair in configurationValues)
            {
                values[pair.Key] = pair.Value;
            }
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://example.com")
        };

        return new AuthService(
            httpClient,
            localStorage.Object,
            configuration,
            navigationManager ?? new TestNavigationManager(),
            logger ?? NullLogger<AuthService>.Instance);
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }
}
