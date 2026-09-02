using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.AuthenticatedEncryption.ConfigurationModel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Crs.Core.Interfaces;
using Crs.Infrastructure.Configuration;
using Crs.Infrastructure.Data;
using Crs.Infrastructure.Repositories;
using Crs.Infrastructure.Services;
using Crs.Infrastructure.VectorStore;

namespace Crs.Infrastructure;

/// <summary>
/// Extension methods for registering Infrastructure services with dependency injection.
/// </summary>
public static class DependencyInjection
{
  /// <summary>
  /// Registers all Infrastructure layer services (DbContext, repositories, vector store, embeddings) into the DI container.
  /// </summary>
  /// <param name="services">The service collection to add services to.</param>
  /// <param name="configuration">The application configuration.</param>
  /// <returns>The service collection for chaining.</returns>
  public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
  {
    // Register DbContext with PostgreSQL
    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    services.AddDbContext<CrsDbContext>(options => options.UseCrsNpgsql(connectionString));

    // Register repositories
    services.AddScoped<IUserRepository, UserRepository>();
    services.AddScoped<ISourceRepository, SourceRepository>();
    services.AddScoped<IContentRepository, ContentRepository>();
    services.AddScoped<IContentVoteRepository, ContentVoteRepository>();
    services.AddScoped<IManualContentFeedbackRepository, ManualContentFeedbackRepository>();
    services.AddScoped<IRecommendationRepository, RecommendationRepository>();
    services.AddScoped<IXConnectionRepository, XConnectionRepository>();
    services.AddScoped<IXAuthStateRepository, XAuthStateRepository>();
    services.AddScoped<IRefreshTokenRepository, RefreshTokenRepository>();
    services.AddScoped<IXFollowedAccountRepository, XFollowedAccountRepository>();
    services.AddScoped<IXSelectedAccountRepository, XSelectedAccountRepository>();
    services.AddScoped<IXPostRepository, XPostRepository>();

    // Register configuration settings
    services.Configure<EmbeddingSettings>(configuration.GetSection(EmbeddingSettings.SectionName));
    services.Configure<XApiSettings>(configuration.GetSection(XApiSettings.SectionName));

    // Register embedding service (OpenAI)
    services.AddHttpClient();
    services.AddSingleton<IEmbeddingService, OpenAIEmbeddingService>();
    services.AddHttpClient<IXApiClient, XApiClient>();

    services.AddScoped<IVectorStore, PostgresVectorStore>();

    // Register content fetcher service (handles HTML and RSS/XML feeds)
    services.AddHttpClient<IContentFetcherService, ContentFetcherService>();

    // Persist Data Protection keys to the shared database so API and jobs can decrypt tokens.
    // Force managed algorithms so payloads are cross-platform compatible (Linux API + Windows Jobs).
    services.AddDataProtection()
        .PersistKeysToDbContext<CrsDbContext>()
        .SetApplicationName("Crs")
        .UseCustomCryptographicAlgorithms(new ManagedAuthenticatedEncryptorConfiguration
        {
            EncryptionAlgorithmType = typeof(Aes),
            EncryptionAlgorithmKeySize = 256,
            ValidationAlgorithmType = typeof(HMACSHA256)
        });

    return services;
  }
}
