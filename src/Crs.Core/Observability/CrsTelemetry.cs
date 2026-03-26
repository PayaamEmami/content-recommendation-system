using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Crs.Core.Observability;

/// <summary>
/// Shared activity and metric names for CRS observability.
/// </summary>
public static class CrsTelemetry
{
    public const string ActivitySourceName = "Crs.Observability";
    public const string MeterName = "Crs.Observability";

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);
    public static readonly Meter Meter = new(MeterName);

    public static class Tags
    {
        public const string Dependency = "crs.dependency";
        public const string FeedType = "crs.feed_type";
        public const string JobName = "crs.job.name";
        public const string ResultCount = "crs.result_count";
        public const string UserId = "enduser.id";
    }
}
