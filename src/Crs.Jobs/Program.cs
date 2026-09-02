using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using Crs.Core.Observability;
using Crs.Infrastructure;
using Crs.Infrastructure.Observability;
using Crs.Jobs.Jobs;
using Crs.Llm;
using Crs.Recommendation;

var jobName = args.Length > 0 ? args[0] : null;
var isXIngestionJob = string.Equals(jobName, "x-ingestion", StringComparison.OrdinalIgnoreCase);

var builder = Host.CreateApplicationBuilder(args);

if (string.Equals(
        Environment.GetEnvironmentVariable("Observability__ExecutionEnvironment"),
        "local",
        StringComparison.OrdinalIgnoreCase))
{
    builder.Configuration.AddUserSecrets(Assembly.GetExecutingAssembly()!, optional: true);
}
builder.Logging.AddCrsLogging(builder.Environment);
builder.Services.AddCrsObservability(builder.Configuration, builder.Environment, "crs-jobs");

// Register services from other layers
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddLlmServices(builder.Configuration);
builder.Services.AddRecommendationEngine();

// Jobs
builder.Services.AddScoped<SourceIngestionJob>();
builder.Services.AddScoped<FeedGenerationJob>();
builder.Services.AddScoped<ReindexJob>();
builder.Services.AddScoped<XIngestionJob>();

var host = builder.Build();
var runId = Guid.NewGuid().ToString("n");

// X ingestion only needs PostgreSQL and the X API.
if (!isXIngestionJob)
{
  using var scope = host.Services.CreateScope();
  var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
  var metrics = scope.ServiceProvider.GetRequiredService<IObservabilityMetrics>();
  using var startupActivity = CrsTelemetry.ActivitySource.StartActivity("jobs.startup.initialize_vector_store");
  startupActivity?.SetTag(CrsTelemetry.Tags.ExecutionEnvironment, builder.Configuration["Observability:ExecutionEnvironment"]);
  startupActivity?.SetTag(CrsTelemetry.Tags.JobRunId, runId);
  var startupTimer = Stopwatch.StartNew();
  try
  {
    using var startupScope = logger.BeginScope(new Dictionary<string, object?>
    {
      ["execution_environment"] = builder.Configuration["Observability:ExecutionEnvironment"],
      ["job.run_id"] = runId
    });

    logger.LogInformation("Initializing vector store...");
    var vectorStore = scope.ServiceProvider.GetRequiredService<Crs.Core.Interfaces.IVectorStore>();
    await vectorStore.InitializeAsync();
    logger.LogInformation("Vector store initialized successfully");
    startupTimer.Stop();
    metrics.RecordDuration(
      "startup.duration",
      startupTimer.Elapsed,
      new MetricContext(
        Dimensions: new Dictionary<string, string>
        {
          ["Operation"] = "jobs.initialize_vector_store",
          ["Outcome"] = "success"
        },
        Properties: new Dictionary<string, object?>
        {
          ["JobRunId"] = runId
        }));
  }
  catch (Exception ex)
  {
    logger.LogError(ex, "Failed to initialize vector store");
    startupTimer.Stop();
    metrics.Increment(
      "startup.failure.count",
      context: new MetricContext(
        Dimensions: new Dictionary<string, string>
        {
          ["Operation"] = "jobs.initialize_vector_store",
          ["Outcome"] = "failed"
        },
        Properties: new Dictionary<string, object?>
        {
          ["JobRunId"] = runId
        }));
    Environment.Exit(1);
  }
}

if (string.IsNullOrWhiteSpace(jobName))
{
  Console.WriteLine("Usage: Crs.Jobs <job-name>");
  Console.WriteLine("Available jobs:");
  Console.WriteLine("  ingestion     - Run source ingestion job");
  Console.WriteLine("  feed          - Run feed generation job");
  Console.WriteLine("  reindex       - Reindex all content in vector store");
  Console.WriteLine("  x-ingestion   - Run X post ingestion job");
  Environment.Exit(1);
}

using (var scope = host.Services.CreateScope())
{
  var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
  var metrics = scope.ServiceProvider.GetRequiredService<IObservabilityMetrics>();
  using var scopeLog = logger.BeginScope(new Dictionary<string, object?>
  {
    ["execution_environment"] = builder.Configuration["Observability:ExecutionEnvironment"],
    ["job.name"] = jobName,
    ["job.run_id"] = runId
  });
  using var activity = CrsTelemetry.ActivitySource.StartActivity("jobs.execute");
  activity?.SetTag(CrsTelemetry.Tags.JobName, jobName);
  activity?.SetTag(CrsTelemetry.Tags.JobRunId, runId);
  activity?.SetTag(CrsTelemetry.Tags.ExecutionEnvironment, builder.Configuration["Observability:ExecutionEnvironment"]);
  var stopwatch = Stopwatch.StartNew();

  try
  {
    logger.LogInformation("Starting job: {JobName}", jobName);
    metrics.Increment(
      "job.host.heartbeat",
      context: new MetricContext(
        Dimensions: new Dictionary<string, string>
        {
          ["JobName"] = jobName,
          ["Operation"] = "job.host",
          ["Outcome"] = "started"
        },
        Properties: new Dictionary<string, object?>
        {
          ["JobRunId"] = runId
        }));

    switch (jobName.ToLowerInvariant())
    {
      case "ingestion":
        var ingestionJob = scope.ServiceProvider.GetRequiredService<SourceIngestionJob>();
        await ingestionJob.ExecuteAsync(CancellationToken.None);
        logger.LogInformation("Ingestion job completed successfully");
        break;

      case "feed":
        var feedJob = scope.ServiceProvider.GetRequiredService<FeedGenerationJob>();
        await feedJob.ExecuteAsync(CancellationToken.None);
        logger.LogInformation("Feed generation job completed successfully");
        break;

      case "reindex":
        var reindexJob = scope.ServiceProvider.GetRequiredService<ReindexJob>();
        await reindexJob.ExecuteAsync(CancellationToken.None);
        logger.LogInformation("Reindex job completed successfully");
        break;

      case "x-ingestion":
        var xIngestionJob = scope.ServiceProvider.GetRequiredService<XIngestionJob>();
        await xIngestionJob.ExecuteAsync(CancellationToken.None);
        logger.LogInformation("X ingestion job completed successfully");
        break;

      default:
        logger.LogError("Unknown job name: {JobName}", jobName);
        Console.WriteLine($"Error: Unknown job '{jobName}'");
        Console.WriteLine("Available jobs: ingestion, feed, reindex, x-ingestion");
        Environment.Exit(1);
        break;
    }

    logger.LogInformation("Job {JobName} exited successfully", jobName);
    stopwatch.Stop();
    metrics.Increment(
      "job.wrapper.success.count",
      context: new MetricContext(
        Dimensions: new Dictionary<string, string>
        {
          ["JobName"] = jobName,
          ["Operation"] = "job.wrapper",
          ["Outcome"] = "success"
        },
        Properties: new Dictionary<string, object?>
        {
          ["JobRunId"] = runId
        }));
    metrics.RecordDuration(
      "job.wrapper.duration",
      stopwatch.Elapsed,
      new MetricContext(
        Dimensions: new Dictionary<string, string>
        {
          ["JobName"] = jobName,
          ["Operation"] = "job.wrapper",
          ["Outcome"] = "success"
        },
        Properties: new Dictionary<string, object?>
        {
          ["JobRunId"] = runId
        }));
    Environment.Exit(0);
  }
  catch (Exception ex)
  {
    logger.LogError(ex, "Job {JobName} failed with error", jobName);
    stopwatch.Stop();
    metrics.Increment(
      "job.wrapper.failure.count",
      context: new MetricContext(
        Dimensions: new Dictionary<string, string>
        {
          ["JobName"] = jobName!,
          ["Operation"] = "job.wrapper",
          ["Outcome"] = "failed"
        },
        Properties: new Dictionary<string, object?>
        {
          ["JobRunId"] = runId
        }));
    metrics.RecordDuration(
      "job.wrapper.duration",
      stopwatch.Elapsed,
      new MetricContext(
        Dimensions: new Dictionary<string, string>
        {
          ["JobName"] = jobName!,
          ["Operation"] = "job.wrapper",
          ["Outcome"] = "failed"
        },
        Properties: new Dictionary<string, object?>
        {
          ["JobRunId"] = runId
        }));
    Environment.Exit(1);
  }
}
