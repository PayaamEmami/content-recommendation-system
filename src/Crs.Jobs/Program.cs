using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Crs.Infrastructure;
using Crs.Infrastructure.Observability;
using Crs.Jobs.Jobs;
using Crs.Llm;
using Crs.Recommendation;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddCrsLogging(builder.Environment);
builder.Services.AddCrsObservability(builder.Configuration, builder.Environment, "crs-jobs");

// Register services from other layers
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddLlmServices(builder.Configuration);
builder.Services.AddRecommendationEngine();

// Jobs
builder.Services.AddScoped<SourceIngestionJob>();
builder.Services.AddScoped<DailyFeedGenerationJob>();
builder.Services.AddScoped<ReindexJob>();
builder.Services.AddScoped<XIngestionJob>();
builder.Services.AddScoped<LocalVectorIndexSyncJob>();

var host = builder.Build();

// Initialize vector store index on startup
using (var scope = host.Services.CreateScope())
{
  var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
  try
  {
    logger.LogInformation("Initializing vector store...");
    var vectorStore = scope.ServiceProvider.GetRequiredService<Crs.Core.Interfaces.IVectorStore>();
    await vectorStore.InitializeAsync();
    logger.LogInformation("Vector store initialized successfully");
  }
  catch (Exception ex)
  {
    logger.LogError(ex, "Failed to initialize vector store");
    Environment.Exit(1);
  }
}

// Determine which job to run from command-line arguments
var jobName = args.Length > 0 ? args[0] : null;

if (string.IsNullOrWhiteSpace(jobName))
{
  Console.WriteLine("Usage: Crs.Jobs <job-name>");
  Console.WriteLine("Available jobs:");
  Console.WriteLine("  ingestion     - Run source ingestion job");
  Console.WriteLine("  feed          - Run daily feed generation job");
  Console.WriteLine("  reindex       - Reindex all content in vector store");
  Console.WriteLine("  sync-index    - Reconcile local vector index with database content");
  Console.WriteLine("  x-ingestion   - Run X post ingestion job");
  Environment.Exit(1);
}

using (var scope = host.Services.CreateScope())
{
  var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

  try
  {
    logger.LogInformation("Starting job: {JobName}", jobName);

    // When using local OpenSearch, keep the index converged with the database before
    // running jobs that depend on semantic search quality.
    if (!string.Equals(jobName, "reindex", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(jobName, "sync-index", StringComparison.OrdinalIgnoreCase))
    {
      var indexSyncJob = scope.ServiceProvider.GetRequiredService<LocalVectorIndexSyncJob>();
      await indexSyncJob.ExecuteAsync(CancellationToken.None);
    }

    switch (jobName.ToLowerInvariant())
    {
      case "ingestion":
        var ingestionJob = scope.ServiceProvider.GetRequiredService<SourceIngestionJob>();
        await ingestionJob.ExecuteAsync(CancellationToken.None);
        logger.LogInformation("Ingestion job completed successfully");
        break;

      case "feed":
        var feedJob = scope.ServiceProvider.GetRequiredService<DailyFeedGenerationJob>();
        await feedJob.ExecuteAsync(CancellationToken.None);
        logger.LogInformation("Feed generation job completed successfully");
        break;

      case "reindex":
        var reindexJob = scope.ServiceProvider.GetRequiredService<ReindexJob>();
        await reindexJob.ExecuteAsync(CancellationToken.None);
        logger.LogInformation("Reindex job completed successfully");
        break;

      case "sync-index":
        var indexSyncJob = scope.ServiceProvider.GetRequiredService<LocalVectorIndexSyncJob>();
        await indexSyncJob.ExecuteAsync(CancellationToken.None);
        logger.LogInformation("Index sync job completed successfully");
        break;

      case "x-ingestion":
        var xIngestionJob = scope.ServiceProvider.GetRequiredService<XIngestionJob>();
        await xIngestionJob.ExecuteAsync(CancellationToken.None);
        logger.LogInformation("X ingestion job completed successfully");
        break;

      default:
        logger.LogError("Unknown job name: {JobName}", jobName);
        Console.WriteLine($"Error: Unknown job '{jobName}'");
        Console.WriteLine("Available jobs: ingestion, feed, reindex, sync-index, x-ingestion");
        Environment.Exit(1);
        break;
    }

    logger.LogInformation("Job {JobName} exited successfully", jobName);
    Environment.Exit(0);
  }
  catch (Exception ex)
  {
    logger.LogError(ex, "Job {JobName} failed with error", jobName);
    Environment.Exit(1);
  }
}
