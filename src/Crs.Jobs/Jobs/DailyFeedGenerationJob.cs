using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Crs.Core.Enums;
using Crs.Core.Interfaces;
using Crs.Core.Observability;
using Crs.Recommendation.Services;

namespace Crs.Jobs.Jobs;

/// <summary>
/// Background job that generates daily personalized feeds for all users.
/// </summary>
public class DailyFeedGenerationJob
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<DailyFeedGenerationJob> _logger;
    private readonly IObservabilityMetrics _metrics;

    public DailyFeedGenerationJob(
        IServiceProvider serviceProvider,
        ILogger<DailyFeedGenerationJob> logger,
        IObservabilityMetrics metrics)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _metrics = metrics;
    }

    /// <summary>
    /// Execute the daily feed generation job for all users.
    /// </summary>
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        using var activity = CrsTelemetry.ActivitySource.StartActivity("job.feed_generation");
        activity?.SetTag(CrsTelemetry.Tags.JobName, "feed");
        var stopwatch = Stopwatch.StartNew();
        var runId = Guid.NewGuid().ToString("n");
        using var logScope = _logger.BeginScope(new Dictionary<string, object?>
        {
            ["job.name"] = "feed",
            ["job.run_id"] = runId,
            ["job.trigger"] = "manual"
        });

        _logger.LogInformation("Starting daily feed generation job");

        using var scope = _serviceProvider.CreateScope();
        var userRepository = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        var feedGenerator = scope.ServiceProvider.GetRequiredService<IFeedGenerator>();

        try
        {
            // Get all users
            var users = await userRepository.GetAllAsync(cancellationToken);
            var usersList = users.ToList();

            if (!usersList.Any())
            {
                _logger.LogInformation("No users found");
                stopwatch.Stop();
                _metrics.RecordDuration("job.duration", stopwatch.Elapsed, BuildContext("feed", "empty"));
                return;
            }

            _logger.LogInformation("Generating feeds for {Count} users", usersList.Count);

            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            int totalFeedsGenerated = 0;
            int usersProcessed = 0;

            foreach (var user in usersList)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning("Feed generation job cancelled");
                    break;
                }

                try
                {
                    _logger.LogInformation("Generating feeds for user {UserId}", user.Id);

                    // Generate feeds for all content types
                    var feedTypes = Enum.GetValues<ContentType>();
                    int userFeedCount = 0;

                    foreach (var feedType in feedTypes)
                    {
                        try
                        {
                            var recommendations = await feedGenerator.GenerateFeedAsync(
                                user.Id,
                                feedType,
                                today,
                                count: 5, // Generate 5 recommendations per feed
                                cancellationToken);

                            userFeedCount += recommendations.Count;
                            totalFeedsGenerated += recommendations.Count;

                            _logger.LogDebug(
                                "Generated {Count} recommendations for user {Email}, feed type {FeedType}",
                                recommendations.Count,
                                user.Id,
                                feedType);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(
                                ex,
                                "Error generating {FeedType} feed for user {UserId}",
                                feedType,
                                user.Id);
                        }
                    }

                    usersProcessed++;
                    _logger.LogInformation(
                        "Completed feed generation for user {UserId}: {Count} total recommendations",
                        user.Id,
                        userFeedCount);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing user {UserId}", user.Id);
                }
            }

            _logger.LogInformation(
                "Daily feed generation job completed: {UsersProcessed} users processed, {TotalFeeds} recommendations generated",
                usersProcessed,
                totalFeedsGenerated);
            stopwatch.Stop();
            _metrics.Increment("job.success.count", context: BuildContext("feed", "success"));
            _metrics.RecordDuration("job.duration", stopwatch.Elapsed, BuildContext("feed", "success"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in daily feed generation job");
            stopwatch.Stop();
            _metrics.Increment("job.failure.count", context: BuildContext("feed", "failed"));
            _metrics.RecordDuration("job.duration", stopwatch.Elapsed, BuildContext("feed", "failed"));
            throw;
        }
    }

    /// <summary>
    /// Execute the feed generation for a specific user.
    /// Useful for on-demand feed refreshes.
    /// </summary>
    public async Task ExecuteForUserAsync(
        Guid userId,
        DateOnly? date = null,
        CancellationToken cancellationToken = default)
    {
        using var activity = CrsTelemetry.ActivitySource.StartActivity("job.feed_generation_for_user");
        activity?.SetTag(CrsTelemetry.Tags.UserId, userId.ToString());
        _logger.LogInformation("Starting feed generation for user {UserId}", userId);

        using var scope = _serviceProvider.CreateScope();
        var feedGenerator = scope.ServiceProvider.GetRequiredService<IFeedGenerator>();

        try
        {
            var targetDate = date ?? DateOnly.FromDateTime(DateTime.UtcNow);
            var recommendations = await feedGenerator.GenerateAllFeedsAsync(
                userId,
                targetDate,
                cancellationToken);

            _logger.LogInformation(
                "Completed feed generation for user {UserId}: {Count} recommendations",
                userId,
                recommendations.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating feeds for user {UserId}", userId);
            throw;
        }
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
