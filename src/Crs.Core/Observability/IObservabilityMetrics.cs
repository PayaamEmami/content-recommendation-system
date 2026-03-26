namespace Crs.Core.Observability;

/// <summary>
/// Low-cardinality application metrics contract used across the solution.
/// </summary>
public interface IObservabilityMetrics
{
    void Increment(
        string name,
        double value = 1,
        string unit = "Count",
        MetricContext? context = null);

    void RecordValue(
        string name,
        double value,
        string unit,
        MetricContext? context = null);

    void RecordDuration(
        string name,
        TimeSpan duration,
        MetricContext? context = null);
}

/// <summary>
/// Metric dimensions and properties for EMF emission.
/// </summary>
public sealed record MetricContext(
    IReadOnlyDictionary<string, string>? Dimensions = null,
    IReadOnlyDictionary<string, object?>? Properties = null);

/// <summary>
/// No-op implementation for tests and fallback scenarios.
/// </summary>
public sealed class NullObservabilityMetrics : IObservabilityMetrics
{
    public static readonly NullObservabilityMetrics Instance = new();

    public void Increment(string name, double value = 1, string unit = "Count", MetricContext? context = null)
    {
    }

    public void RecordDuration(string name, TimeSpan duration, MetricContext? context = null)
    {
    }

    public void RecordValue(string name, double value, string unit, MetricContext? context = null)
    {
    }
}
