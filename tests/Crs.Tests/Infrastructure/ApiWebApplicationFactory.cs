using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Crs.Core.Interfaces;
using Crs.Infrastructure.Data;
using Crs.Llm.Services;

namespace Crs.Tests.Infrastructure;

public sealed class ApiWebApplicationFactory : WebApplicationFactory<Program>
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
                ["ReverseProxy:KnownNetworks:0"] = "127.0.0.1/32",
                ["ReverseProxy:KnownNetworks:1"] = "::1/128",
                ["Embedding:ApiKey"] = "test-key",
                ["Embedding:ModelName"] = "test",
                ["Embedding:Dimensions"] = "3",
                ["Embedding:MaxBatchSize"] = "100",
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
                options.UseCrsNpgsql(PostgresTestContainerFixture.ConnectionString));

            services.RemoveAll<IEmbeddingService>();
            services.RemoveAll<IVectorStore>();
            services.RemoveAll<ILlmClient>();
            services.RemoveAll<IIngestionAgent>();
            services.RemoveAll<IXApiClient>();
            services.RemoveAll<IContentFetcherService>();

            services.AddSingleton<IEmbeddingService, FakeEmbeddingService>();
            services.AddSingleton<IVectorStore, InMemoryVectorStore>();
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
