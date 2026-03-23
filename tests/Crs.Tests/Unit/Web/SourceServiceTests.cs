using System.Net;
using System.Text.Json;
using Blazored.LocalStorage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Crs.Core.Enums;
using Crs.Tests.Unit.Api;
using Crs.Web.Services;

namespace Crs.Tests.Unit.Web;

[TestClass]
public sealed class SourceServiceTests
{
    [TestMethod]
    public async Task AddSourceAsync_WhenSuccess_ReturnsTrue()
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

        var handler = new HttpTestHandler(_ => new HttpResponseMessage(HttpStatusCode.Created));
        var service = new SourceService(new HttpClient(handler) { BaseAddress = new Uri("https://example.com") }, authService, NullLogger<SourceService>.Instance);

        var result = await service.AddSourceAsync("Test", "https://example.com", ContentType.Video, null);

        Assert.IsTrue(result);
        Assert.HasCount(1, handler.Requests);
    }

    [TestMethod]
    public async Task BulkImportSourcesAsync_WhenUnauthenticated_Throws()
    {
        var localStorage = new Mock<ILocalStorageService>(MockBehavior.Strict);
        localStorage.Setup(store => store.GetItemAsync<AuthState>(It.IsAny<string>()))
            .ReturnsAsync((AuthState?)null);

        var authService = CreateAuthService(localStorage);

        var handler = new HttpTestHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var service = new SourceService(new HttpClient(handler) { BaseAddress = new Uri("https://example.com") }, authService, NullLogger<SourceService>.Instance);

        await TestAssert.ThrowsAsync<InvalidOperationException>(() =>
            service.BulkImportSourcesAsync(JsonSerializer.Serialize(new { sources = Array.Empty<object>() })));
    }

    [TestMethod]
    public async Task GetUserSourcesResultAsync_WhenApiReturnsError_ReturnsFailureResult()
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

        var handler = new HttpTestHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var service = new SourceService(new HttpClient(handler) { BaseAddress = new Uri("https://example.com") }, authService, NullLogger<SourceService>.Instance);

        var result = await service.GetUserSourcesResultAsync();

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("Couldn't load your sources right now. Please try again.", result.ErrorMessage);
        Assert.HasCount(0, result.Sources);
    }

    [TestMethod]
    public async Task GetUserSourcesResultAsync_WhenSuccess_ReturnsSources()
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
[
  {
    "id": "11111111-1111-1111-1111-111111111111",
    "userId": "22222222-2222-2222-2222-222222222222",
    "name": "Example Source",
    "url": "https://example.com/feed",
    "category": "BlogPost",
    "description": "Example",
    "isActive": true,
    "createdAt": "2026-03-22T00:00:00Z",
    "contentCount": 3
  }
]
""";

        var handler = new HttpTestHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload)
        });
        var service = new SourceService(new HttpClient(handler) { BaseAddress = new Uri("https://example.com") }, authService, NullLogger<SourceService>.Instance);

        var result = await service.GetUserSourcesResultAsync();

        Assert.IsTrue(result.IsSuccess);
        Assert.IsNull(result.ErrorMessage);
        Assert.HasCount(1, result.Sources);
        Assert.AreEqual("Example Source", result.Sources[0].Name);
    }

    private static AuthService CreateAuthService(Mock<ILocalStorageService> localStorage)
    {
        var configuration = new ConfigurationBuilder().Build();
        var authHandler = new HttpTestHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var authHttpClient = new HttpClient(authHandler) { BaseAddress = new Uri("https://example.com") };

        return new AuthService(authHttpClient, localStorage.Object, configuration, NullLogger<AuthService>.Instance);
    }
}
