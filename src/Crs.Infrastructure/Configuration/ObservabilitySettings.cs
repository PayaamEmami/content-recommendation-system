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

    public bool EnableMetrics { get; set; } = true;

    public string ExecutionEnvironment { get; set; } = string.Empty;

    public bool EnableSensitiveBodyLogging { get; set; }

    public RumObservabilitySettings Rum { get; set; } = new();

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

        if (string.IsNullOrWhiteSpace(ExecutionEnvironment))
        {
            ExecutionEnvironment = IsAwsExecutionEnvironment()
                ? "aws"
                : "local";
        }

        TraceSampleRatio = Math.Clamp(TraceSampleRatio, 0.0, 1.0);
        Rum.SessionSampleRate = Math.Clamp(Rum.SessionSampleRate, 0.0, 1.0);
    }

    private static bool IsAwsExecutionEnvironment()
    {
        return !string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable("AWS_REGION")) ||
               !string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable("AWS_EXECUTION_ENV"));
    }
}

public sealed class RumObservabilitySettings
{
    public bool Enabled { get; set; }

    public string AppMonitorId { get; set; } = string.Empty;

    public string AppMonitorName { get; set; } = string.Empty;

    public string Region { get; set; } = string.Empty;

    public double SessionSampleRate { get; set; } = 0.1;

    public bool AllowCookies { get; set; } = true;

    public bool EnableXRay { get; set; } = true;

    public string[] Telemetries { get; set; } = ["errors", "performance", "http"];

    public string Endpoint { get; set; } = "https://client.rum.us-east-1.amazonaws.com/1.0.2/cwr.js";

    public string IdentityPoolId { get; set; } = string.Empty;

    public string GuestRoleArn { get; set; } = string.Empty;

    public string[] IncludedPages { get; set; } = [];

    public string[] ExcludedPages { get; set; } = [];

    public bool IsConfigured()
    {
        return Enabled &&
               !string.IsNullOrWhiteSpace(AppMonitorId) &&
               !string.IsNullOrWhiteSpace(AppMonitorName) &&
               !string.IsNullOrWhiteSpace(Region) &&
               !string.IsNullOrWhiteSpace(IdentityPoolId);
    }
}
