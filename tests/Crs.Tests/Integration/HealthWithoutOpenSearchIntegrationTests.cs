using System.Net;
using Crs.Api.Health;
using Crs.Core.Interfaces;
using Crs.Infrastructure.Data;
using Crs.Llm.Services;
using Crs.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Crs.Tests.Integration;

[TestClass]
public sealed class HealthWithoutOpenSearchIntegrationTests
{
    private static WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    [ClassInitialize]
    public static void ClassInitialize(TestContext context)
    {
        _factory = new OpenSearchDisabledWebApplicationFactory();
    }

    [ClassCleanup]
    public static void ClassCleanup()
    {
        _factory.Dispose();
    }

    [TestInitialize]
    public void TestInitialize()
    {
        _client = _factory.CreateClient();
    }

    [TestCleanup]
    public void TestCleanup()
    {
        _client.Dispose();
    }

    [TestMethod]
    public async Task HealthReady_ReturnsOk_WhenOpenSearchIsDisabled()
    {
        var response = await _client.GetAsync("/health/ready");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        StringAssert.Contains(body, "\"configured\":false");
    }

    private sealed class OpenSearchDisabledWebApplicationFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");

            builder.ConfigureAppConfiguration((context, config) =>
            {
                var settings = new Dictionary<string, string?>
                {
                    ["ConnectionStrings:DefaultConnection"] = PostgresTestContainerFixture.ConnectionString,
                    ["JwtSettings:SecretKey"] = "test-secret-key-for-integration-tests-only",
                    ["JwtSettings:Issuer"] = "Crs.Api.Tests",
                    ["JwtSettings:Audience"] = "Crs.Web.Tests",
                    ["JwtSettings:ExpirationMinutes"] = "60",
                    ["JwtSettings:RefreshTokenExpirationDays"] = "7",
                    ["Registration:Enabled"] = "true",
                    ["Registration:DisabledMessage"] = "Registrations disabled",
                    ["Embedding:ApiKey"] = "test-key",
                    ["Embedding:ModelName"] = "test",
                    ["Embedding:Dimensions"] = "3",
                    ["Embedding:MaxBatchSize"] = "100",
                    ["OpenSearch:Endpoint"] = string.Empty,
                    ["OpenSearch:IndexName"] = "crs-content-test",
                    ["OpenSearch:EmbeddingDimensions"] = "3",
                    ["OpenSearch:Region"] = "us-west-2",
                    ["OpenAI:ApiKey"] = "test-key",
                    ["OpenAI:Model"] = "gpt-5-nano",
                    ["OpenAI:MaxTokens"] = "2048",
                    ["OpenAI:Temperature"] = "0",
                    ["X:ClientId"] = "test-client",
                    ["X:ClientSecret"] = "test-secret",
                    ["X:RedirectUri"] = "http://localhost/callback",
                    ["X:Scopes"] = "users.read",
                    ["X:BaseUrl"] = "https://api.x.com",
                    ["X:AuthorizationUrl"] = "https://x.com/i/oauth2/authorize",
                    ["X:TokenUrl"] = "https://api.x.com/2/oauth2/token"
                };

                config.AddInMemoryCollection(settings);
            });

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<CrsDbContext>>();
                services.RemoveAll<CrsDbContext>();
                services.AddDbContext<CrsDbContext>(options =>
                    options.UseNpgsql(PostgresTestContainerFixture.ConnectionString));

                services.RemoveAll<IEmbeddingService>();
                services.RemoveAll<ILlmClient>();
                services.RemoveAll<IIngestionAgent>();
                services.RemoveAll<IXApiClient>();
                services.RemoveAll<IContentFetcherService>();

                services.AddSingleton<IEmbeddingService, FakeEmbeddingService>();
                services.AddSingleton<ILlmClient, FakeLlmClient>();
                services.AddSingleton<IIngestionAgent, FakeIngestionAgent>();
                services.AddSingleton<IXApiClient, FakeXApiClient>();
                services.AddSingleton<IContentFetcherService, FakeContentFetcherService>();

                using var provider = services.BuildServiceProvider();
                using var scope = provider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<CrsDbContext>();
                db.Database.Migrate();
            });
        }
    }
}
