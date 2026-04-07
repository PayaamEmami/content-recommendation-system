using System.Net;
using Crs.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Crs.Tests.Integration;

[TestClass]
public sealed class HealthIntegrationTests
{
    private static ApiWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;

    [ClassInitialize]
    public static void ClassInitialize(TestContext context)
    {
        _factory = new ApiWebApplicationFactory();
    }

    [ClassCleanup]
    public static void ClassCleanup()
    {
        _factory.Dispose();
    }

    [TestInitialize]
    public void TestInitialize()
    {
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
    }

    [TestCleanup]
    public void TestCleanup()
    {
        _client.Dispose();
    }

    [TestMethod]
    public async Task Health_ReturnsOk()
    {
        var response = await _client.GetAsync("/health");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [TestMethod]
    public async Task HealthLive_ReturnsOk()
    {
        var response = await _client.GetAsync("/health/live");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [TestMethod]
    public async Task HealthReady_ReturnsOk()
    {
        var response = await _client.GetAsync("/health/ready");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [TestMethod]
    public async Task Health_WithTrustedForwardedHttpsHeader_DoesNotRedirect()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", "127.0.0.1");
        request.Headers.TryAddWithoutValidation("X-Forwarded-Host", "api.example.com");
        request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");

        var response = await _client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }
}
