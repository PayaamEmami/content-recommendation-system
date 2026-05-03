using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using Crs.Core.Interfaces;
using Crs.Core.Observability;
using Crs.Llm.Services;

namespace Crs.Jobs.Jobs;

/// <summary>
/// Background job that periodically ingests content from all active sources.
/// Acts as a batcher around <see cref="ISourceIngestionService"/>: fetches active sources,
/// chunks them, applies a per-source timeout, and aggregates totals/metrics.
/// </summary>
public class SourceIngestionJob
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<SourceIngestionJob> _logger;
    private readonly IObservabilityMetrics _metrics;
    private const int BatchSize = 5;
    private static readonly TimeSpan PerSourceTimeout = TimeSpan.FromSeconds(120);

    public SourceIngestionJob(
        IServiceProvider serviceProvider,
        ILogger<SourceIngestionJob> logger,
        IObservabilityMetrics metrics)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _metrics = metrics;
    }

    /// <summary>
    /// Execute the source ingestion job for all active sources.
    /// </summary>
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        using var activity = CrsTelemetry.ActivitySource.StartActivity("job.source_ingestion");
        activity?.SetTag(CrsTelemetry.Tags.JobName, "ingestion");

        var startedAt = Stopwatch.StartNew();
        var runId = Guid.NewGuid().ToString("n");
        using var logScope = _logger.BeginScope(new Dictionary<string, object?>
        {
            ["job.name"] = "ingestion",
            ["job.run_id"] = runId,
            ["job.trigger"] = "manual"
        });

        _logger.LogInformation("Starting source ingestion job");

        using var scope = _serviceProvider.CreateScope();
        var sourceRepository = scope.ServiceProvider.GetRequiredService<ISourceRepository>();
        var ingestionService = scope.ServiceProvider.GetRequiredService<ISourceIngestionService>();

        try
        {
            var sourcesList = (await sourceRepository.GetActiveSourcesAsync(cancellationToken)).ToList();

            if (!sourcesList.Any())
            {
                _logger.LogInformation("No active sources to ingest");
                startedAt.Stop();
                _metrics.RecordDuration("job.duration", startedAt.Elapsed, BuildJobContext("ingestion", "empty"));
                return;
            }

            _logger.LogInformation(
                "Found {Count} active sources to process (batch size {BatchSize})",
                sourcesList.Count,
                BatchSize);

            var totalIngested = 0;
            var totalEmbedded = 0;

            var batches = sourcesList.Chunk(BatchSize).ToList();
            var batchNumber = 0;

            foreach (var batch in batches)
            {
                batchNumber++;
                _logger.LogInformation(
                    "Processing batch {BatchNumber}/{TotalBatches} with {BatchCount} sources",
                    batchNumber,
                    batches.Count,
                    batch.Length);

                if (cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning("Ingestion job cancelled");
                    break;
                }

                foreach (var source in batch)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        _logger.LogWarning("Ingestion job cancelled during batch");
                        break;
                    }

                    using var perSourceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    perSourceCts.CancelAfter(PerSourceTimeout);

                    try
                    {
                        var summary = await ingestionService.IngestSourceAsync(source, perSourceCts.Token);
                        totalIngested += summary.Saved;
                        totalEmbedded += summary.Embedded;
                    }
                    catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        _logger.LogWarning(
                            "Ingestion timed out for source {SourceName} after {TimeoutSeconds}s",
                            source.Name,
                            PerSourceTimeout.TotalSeconds);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing source {SourceId}", source.Id);
                    }
                }
            }

            _logger.LogInformation(
                "Source ingestion job completed: {Ingested} content ingested, {Embedded} embedded",
                totalIngested,
                totalEmbedded);
            startedAt.Stop();
            _metrics.Increment("job.success.count", context: BuildJobContext("ingestion", "success"));
            _metrics.RecordDuration("job.duration", startedAt.Elapsed, BuildJobContext("ingestion", "success"));
            _metrics.RecordValue("ingestion.items.saved", totalIngested, "Count", BuildOperationContext("sources", "success"));
            _metrics.RecordValue("ingestion.items.indexed", totalEmbedded, "Count", BuildOperationContext("sources", "success"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in source ingestion job");
            startedAt.Stop();
            _metrics.Increment("job.failure.count", context: BuildJobContext("ingestion", "failed"));
            _metrics.RecordDuration("job.duration", startedAt.Elapsed, BuildJobContext("ingestion", "failed"));
            // Swallow to avoid tight retry loops; individual source errors are already handled.
            // The worker will log and retry on its normal schedule.
        }
    }

    private static MetricContext BuildJobContext(string jobName, string outcome)
    {
        return new MetricContext(
            Dimensions: new Dictionary<string, string>
            {
                ["JobName"] = jobName,
                ["Operation"] = "job.run",
                ["Outcome"] = outcome
            });
    }

    private static MetricContext BuildOperationContext(string operation, string outcome)
    {
        return new MetricContext(
            Dimensions: new Dictionary<string, string>
            {
                ["JobName"] = "ingestion",
                ["Operation"] = operation,
                ["Outcome"] = outcome
            });
    }
}
