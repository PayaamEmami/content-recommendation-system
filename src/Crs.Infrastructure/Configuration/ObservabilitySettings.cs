namespace Crs.Infrastructure.Configuration;

/// <summary>
/// Shared observability configuration for API and jobs.
/// </summary>
public class ObservabilitySettings
{
    public const string SectionName = "Observability";

    public string Environment { get; set; } = string.Empty;

    public string ServiceName { get; set; } = string.Empty;

    public string ServiceNamespace { get; set; } = "crs";

    public double TraceSampleRatio { get; set; } = 1.0;

    public string MetricsNamespace { get; set; } = "CRS/Application";

    public bool EnableSensitiveBodyLogging { get; set; }

    public void ApplyDefaults(string serviceName, string environmentName)
    {
        if (string.IsNullOrWhiteSpace(ServiceName))
        {
            ServiceName = serviceName;
        }

        if (string.IsNullOrWhiteSpace(Environment))
        {
            Environment = environmentName;
        }

        TraceSampleRatio = Math.Clamp(TraceSampleRatio, 0.0, 1.0);
    }
}
