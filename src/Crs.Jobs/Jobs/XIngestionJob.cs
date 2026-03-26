using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.DataProtection;
using Crs.Core.Entities;
using Crs.Core.Interfaces;
using Crs.Core.Observability;

namespace Crs.Jobs.Jobs;

/// <summary>
/// Background job that ingests posts for selected X accounts.
/// </summary>
public class XIngestionJob
{
    private const int KeepPerAccount = 200;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<XIngestionJob> _logger;
    private readonly IObservabilityMetrics _metrics;

    public XIngestionJob(
        IServiceProvider serviceProvider,
        ILogger<XIngestionJob> logger,
        IObservabilityMetrics metrics)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _metrics = metrics;
    }

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        using var activity = CrsTelemetry.ActivitySource.StartActivity("job.x_ingestion");
        activity?.SetTag(CrsTelemetry.Tags.JobName, "x-ingestion");
        var stopwatch = Stopwatch.StartNew();
        var runId = Guid.NewGuid().ToString("n");
        using var logScope = _logger.BeginScope(new Dictionary<string, object?>
        {
            ["job.name"] = "x-ingestion",
            ["job.run_id"] = runId,
            ["job.trigger"] = "manual"
        });

        _logger.LogInformation("Starting X ingestion job");

        using var scope = _serviceProvider.CreateScope();
        var connectionRepository = scope.ServiceProvider.GetRequiredService<IXConnectionRepository>();
        var selectedAccountRepository = scope.ServiceProvider.GetRequiredService<IXSelectedAccountRepository>();
        var postRepository = scope.ServiceProvider.GetRequiredService<IXPostRepository>();
        var xApiClient = scope.ServiceProvider.GetRequiredService<IXApiClient>();
        var dataProtectionProvider = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>();

        var protector = dataProtectionProvider.CreateProtector("Crs.X.Tokens");

        var connections = await connectionRepository.GetAllAsync(cancellationToken);
        if (!connections.Any())
        {
            _logger.LogInformation("No X connections found");
            stopwatch.Stop();
            _metrics.RecordDuration("job.duration", stopwatch.Elapsed, BuildContext("x-ingestion", "empty"));
            return;
        }

        foreach (var connection in connections)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("X ingestion job cancelled");
                break;
            }

            try
            {
                var accessToken = Unprotect(protector, connection.AccessTokenEncrypted);
                var refreshToken = Unprotect(protector, connection.RefreshTokenEncrypted);

                if (connection.TokenExpiresAt.HasValue && connection.TokenExpiresAt.Value <= DateTime.UtcNow.AddMinutes(1))
                {
                    var refreshed = await xApiClient.RefreshTokenAsync(refreshToken, cancellationToken);
                    accessToken = refreshed.AccessToken;
                    refreshToken = refreshed.RefreshToken;
                    connection.AccessTokenEncrypted = Protect(protector, accessToken);
                    connection.RefreshTokenEncrypted = Protect(protector, refreshToken);
                    connection.TokenExpiresAt = DateTime.UtcNow.AddSeconds(refreshed.ExpiresIn);
                    connection.Scopes = refreshed.Scope;
                    await connectionRepository.UpsertAsync(connection, cancellationToken);
                }

                var selectedAccounts = await selectedAccountRepository.GetByUserIdAsync(connection.UserId, cancellationToken);
                if (!selectedAccounts.Any())
                {
                    _logger.LogInformation("No selected X accounts for user {UserId}", connection.UserId);
                    continue;
                }

                var posts = new List<XPost>();
                foreach (var selected in selectedAccounts)
                {
                    if (selected.FollowedAccount == null)
                    {
                        continue;
                    }

                    var recentPosts = await xApiClient.GetRecentPostsAsync(
                        accessToken,
                        selected.FollowedAccount.XUserId,
                        DateTime.UtcNow.AddDays(-2),
                        cancellationToken);

                    foreach (var post in recentPosts)
                    {
                        posts.Add(new XPost
                        {
                            UserId = connection.UserId,
                            XSelectedAccountId = selected.Id,
                            PostId = post.PostId,
                            Text = post.Text,
                            Url = post.Url,
                            PostCreatedAt = post.CreatedAt,
                            AuthorXUserId = post.Author.XUserId,
                            AuthorHandle = post.Author.Handle,
                            AuthorName = post.Author.DisplayName,
                            AuthorProfileImageUrl = post.Author.ProfileImageUrl,
                            MediaJson = post.Media.Any() ? JsonSerializer.Serialize(post.Media) : null,
                            LikeCount = post.LikeCount,
                            ReplyCount = post.ReplyCount,
                            RepostCount = post.RepostCount,
                            QuoteCount = post.QuoteCount
                        });
                    }
                }

                await postRepository.UpsertRangeAsync(posts, cancellationToken);
                await postRepository.PruneOldPostsAsync(connection.UserId, KeepPerAccount, cancellationToken);

                _logger.LogInformation(
                    "Ingested {Count} posts for user {UserId}",
                    posts.Count,
                    connection.UserId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to ingest X posts for user {UserId}", connection.UserId);
            }
        }

        _logger.LogInformation("X ingestion job completed");
        stopwatch.Stop();
        _metrics.Increment("job.success.count", context: BuildContext("x-ingestion", "success"));
        _metrics.RecordDuration("job.duration", stopwatch.Elapsed, BuildContext("x-ingestion", "success"));
    }

    private static string Protect(IDataProtector protector, string value)
    {
        return string.IsNullOrEmpty(value) ? string.Empty : protector.Protect(value);
    }

    private static string Unprotect(IDataProtector protector, string value)
    {
        return string.IsNullOrEmpty(value) ? string.Empty : protector.Unprotect(value);
    }

    private static MetricContext BuildContext(string jobName, string outcome)
    {
        return new MetricContext(
            Dimensions: new Dictionary<string, string>
            {
                ["JobName"] = jobName,
                ["Operation"] = "job.run",
                ["Outcome"] = outcome
            });
    }
}
