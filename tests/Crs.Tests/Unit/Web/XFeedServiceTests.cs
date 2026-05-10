using System.Net;
using System.Text.Json;
using Blazored.LocalStorage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Crs.Web.Services;

namespace Crs.Tests.Unit.Web;

[TestClass]
public sealed class XFeedServiceTests
{
    [TestMethod]
    public async Task GetConnectUrlAsync_WhenSuccess_ReturnsUrl()
    {
        var localStorage = new Mock<ILocalStorageService>(MockBehavior.Strict);
        localStorage.Setup(store => store.GetItemAsync<AuthState>(It.IsAny<string>()))
            .ReturnsAsync(new AuthState
            {
                IsAuthenticated = true,
                AccessToken = "access",
                ExpiresAt = DateTime.UtcNow.AddMinutes(5)
            });

        var authService = CreateAuthService(localStorage);
        await authService.InitializeAsync();

        var payload = new XConnectUrlResponse { AuthorizationUrl = "https://x.com/oauth" };
        var handler = new HttpTestHandler(_ =>
        {
            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json)
            };
        });

        var service = new XFeedService(new HttpClient(handler) { BaseAddress = new Uri("https://example.com") }, authService, NullLogger<XFeedService>.Instance);

        var result = await service.GetConnectUrlAsync();

        Assert.AreEqual("https://x.com/oauth", result);
    }

    [TestMethod]
    public async Task HandleCallbackAsync_WhenProblemDetailsReturned_UsesDetailAsError()
    {
        var localStorage = new Mock<ILocalStorageService>(MockBehavior.Strict);
        localStorage.Setup(store => store.GetItemAsync<AuthState>(It.IsAny<string>()))
            .ReturnsAsync(new AuthState
            {
                IsAuthenticated = true,
                AccessToken = "access",
                ExpiresAt = DateTime.UtcNow.AddMinutes(5)
            });

        var authService = CreateAuthService(localStorage);
        await authService.InitializeAsync();

        var payload = """
        {
          "title": "X connection failed",
          "detail": "X authorized the app, but rejected a follow-up API request."
        }
        """;
        var handler = new HttpTestHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent(payload)
        });

        var service = new XFeedService(new HttpClient(handler) { BaseAddress = new Uri("https://example.com") }, authService, NullLogger<XFeedService>.Instance);

        var result = await service.HandleCallbackAsync("code", "state");

        Assert.IsFalse(result.Success);
        Assert.AreEqual("X authorized the app, but rejected a follow-up API request.", result.ErrorMessage);
    }

    private static AuthService CreateAuthService(Mock<ILocalStorageService> localStorage)
    {
        var configuration = new ConfigurationBuilder().Build();
        var authHandler = new HttpTestHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var authHttpClient = new HttpClient(authHandler) { BaseAddress = new Uri("https://example.com") };

        return new AuthService(
            authHttpClient,
            localStorage.Object,
            configuration,
            new TestNavigationManager(),
            NullLogger<AuthService>.Instance);
    }
}
