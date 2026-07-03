namespace Crs.Core.Observability;

/// <summary>
/// Helper for emitting the standard dependency-call metric triple
/// (<c>dependency.call.count</c>, <c>dependency.call.duration</c> and, on failure,
/// <c>dependency.failure.count</c>) with consistent dimensions across HTTP clients.
/// </summary>
public static class DependencyMetrics
{
    /// <summary>
    /// Records a single outbound dependency call.
    /// </summary>
    /// <param name="metrics">The metrics sink.</param>
    /// <param name="dependency">The dependency name (e.g. "X", "OpenAI").</param>
    /// <param name="operation">The logical operation performed.</param>
    /// <param name="outcome">The outcome ("success" or "failed").</param>
    /// <param name="duration">How long the call took.</param>
    /// <param name="properties">Optional high-cardinality properties (e.g. token/item counts).</param>
    public static void RecordCall(
        IObservabilityMetrics metrics,
        string dependency,
        string operation,
        string outcome,
        TimeSpan duration,
        IReadOnlyDictionary<string, object?>? properties = null)
    {
        var context = new MetricContext(
            Dimensions: new Dictionary<string, string>
            {
                ["Dependency"] = dependency,
                ["Operation"] = operation,
                ["Outcome"] = outcome
            },
            Properties: properties);

        metrics.Increment("dependency.call.count", context: context);
        metrics.RecordDuration("dependency.call.duration", duration, context);
        if (outcome == "failed")
        {
            metrics.Increment("dependency.failure.count", context: context);
        }
    }
}
