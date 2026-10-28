using System.Net;
using Blazored.LocalStorage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Crs.Web.Services;

namespace Crs.Tests.Unit.Web;

[TestClass]
public sealed class AuthorizedApiClientTests
{
    [TestMethod]
    public async Task SendAsync_WhenDevelopmentLogin_DoesNotSendHttp()
    {
        var localStorage = new Mock<ILocalStorageService>(MockBehavior.Strict);
        localStorage.Setup(store => store.GetItemAsync<AuthState>(It.IsAny<string>()))
            .ReturnsAsync(new AuthState
            {
                IsAuthenticated = true,
                IsDevelopmentLogin = true,
                AccessToken = "development-login-token",
                ExpiresAt = DateTime.UtcNow.AddYears(1)
            });

        var handler = new HttpTestHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.example") };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DevelopmentLogin:Enabled"] = "true"
            })
            .Build();
        var authService = new AuthService(
            httpClient,
            localStorage.Object,
            configuration,
            NullLogger<AuthService>.Instance);
        var client = new AuthorizedApiClient(httpClient, authService);

        var response = await client.SendAsync(() => httpClient.GetAsync("/api/v1/x/callback"));

        Assert.IsNull(response);
        Assert.HasCount(0, handler.Requests);
    }
}
