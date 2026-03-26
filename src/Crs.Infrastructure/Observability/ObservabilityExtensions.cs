using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;
using System.Text.Json;
using Crs.Core.Observability;
using Crs.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;
using OpenTelemetry.Exporter;
using OpenTelemetry.Extensions.AWS.Trace;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Crs.Infrastructure.Observability;

/// <summary>
/// Shared observability registration for API and jobs.
/// </summary>
public static class ObservabilityExtensions
{
    private static readonly string[] ReservedDimensions = ["Service", "Environment"];

    public static IServiceCollection AddCrsObservability(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment,
        string defaultServiceName)
    {
        var settings = configuration.GetSection(ObservabilitySettings.SectionName).Get<ObservabilitySettings>()
            ?? new ObservabilitySettings();
        settings.ApplyDefaults(defaultServiceName, environment.EnvironmentName);

        services.AddSingleton(settings);
        services.AddSingleton<IOptions<ObservabilitySettings>>(Options.Create(settings));
        services.AddSingleton<IEmbeddedMetricSink, ConsoleEmbeddedMetricSink>();
        services.AddSingleton<IObservabilityMetrics, EmbeddedMetricObservabilityMetrics>();

        ConfigurePropagators();

        var serviceVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown";
        var otlpEndpoint = ResolveOtlpEndpoint(configuration);

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(
                    serviceName: settings.ServiceName,
                    serviceNamespace: settings.ServiceNamespace,
                    serviceVersion: serviceVersion)
                .AddAttributes(new Dictionary<string, object>
                {
                    ["deployment.environment"] = settings.Environment,
                    ["service.namespace"] = settings.ServiceNamespace
                }))
            .WithTracing(tracing =>
            {
                tracing
                    .SetSampler(new ParentBasedSampler(new TraceIdRatioBasedSampler(settings.TraceSampleRatio)))
                    .AddSource(CrsTelemetry.ActivitySourceName)
                    .AddHttpClientInstrumentation(options =>
                    {
                        options.RecordException = true;
                        options.FilterHttpRequestMessage = request => !IsOtlpRequest(request.RequestUri);
                    });

                if (otlpEndpoint != null)
                {
                    tracing.AddOtlpExporter(options =>
                    {
                        options.Endpoint = otlpEndpoint;
                        options.Protocol = OtlpExportProtocol.Grpc;
                    });
                }
            });

        return services;
    }

    public static ILoggingBuilder AddCrsLogging(this ILoggingBuilder logging, IHostEnvironment environment)
    {
        logging.ClearProviders();

        if (environment.IsDevelopment() || environment.IsEnvironment("Testing"))
        {
            logging.AddSimpleConsole(options =>
            {
                options.IncludeScopes = true;
                options.TimestampFormat = "HH:mm:ss ";
                options.SingleLine = true;
            });
        }
        else
        {
            logging.AddJsonConsole(options =>
            {
                options.IncludeScopes = true;
                options.TimestampFormat = "O";
                options.JsonWriterOptions = new JsonWriterOptions
                {
                    Indented = false
                };
            });
        }

        return logging;
    }

    private static void ConfigurePropagators()
    {
        Sdk.SetDefaultTextMapPropagator(
            new CompositeTextMapPropagator(
                [
                    new AWSXRayPropagator(),
                    new TraceContextPropagator(),
                    new BaggagePropagator()
                ]));
    }

    private static Uri? ResolveOtlpEndpoint(IConfiguration configuration)
    {
        var endpoint = configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        return Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ? uri : null;
    }

    private static bool IsOtlpRequest(Uri? requestUri)
    {
        return requestUri != null &&
               requestUri.IsLoopback &&
               requestUri.Port == 4317;
    }

    private interface IEmbeddedMetricSink
    {
        void Write(string payload);
    }

    private sealed class ConsoleEmbeddedMetricSink : IEmbeddedMetricSink
    {
        public void Write(string payload)
        {
            TextWriter.Synchronized(Console.Out).WriteLine(payload);
        }
    }

    private sealed class EmbeddedMetricObservabilityMetrics : IObservabilityMetrics
    {
        private static readonly IReadOnlySet<string> AllowedDimensions = new HashSet<string>(StringComparer.Ordinal)
        {
            "Service",
            "Environment",
            "Operation",
            "Outcome",
            "Dependency",
            "JobName",
            "FeedType",
            "StatusClass"
        };

        private readonly ObservabilitySettings _settings;
        private readonly IEmbeddedMetricSink _sink;
        private readonly ConcurrentDictionary<string, Counter<double>> _counters = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, Histogram<double>> _histograms = new(StringComparer.Ordinal);

        public EmbeddedMetricObservabilityMetrics(
            ObservabilitySettings settings,
            IEmbeddedMetricSink sink)
        {
            _settings = settings;
            _sink = sink;
        }

        public void Increment(
            string name,
            double value = 1,
            string unit = "Count",
            MetricContext? context = null)
        {
            _counters.GetOrAdd(name, static metricName => CrsTelemetry.Meter.CreateCounter<double>(metricName, unit: "Count"))
                .Add(value, ToTags(context?.Dimensions));

            Write(name, value, unit, context);
        }

        public void RecordValue(
            string name,
            double value,
            string unit,
            MetricContext? context = null)
        {
            _histograms.GetOrAdd(name, metricName => CrsTelemetry.Meter.CreateHistogram<double>(metricName, unit))
                .Record(value, ToTags(context?.Dimensions));

            Write(name, value, unit, context);
        }

        public void RecordDuration(
            string name,
            TimeSpan duration,
            MetricContext? context = null)
        {
            RecordValue(name, duration.TotalMilliseconds, "Milliseconds", context);
        }

        private void Write(string name, double value, string unit, MetricContext? context)
        {
            var dimensions = BuildDimensions(context?.Dimensions);
            var dimensionSets = BuildDimensionSets(dimensions);
            var payload = new Dictionary<string, object?>
            {
                ["_aws"] = new
                {
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    CloudWatchMetrics = new[]
                    {
                        new
                        {
                            Namespace = _settings.MetricsNamespace,
                            Dimensions = dimensionSets,
                            Metrics = new[]
                            {
                                new { Name = name, Unit = unit }
                            }
                        }
                    }
                },
                [name] = value
            };

            foreach (var dimension in dimensions)
            {
                payload[dimension.Key] = dimension.Value;
            }

            if (context?.Properties != null)
            {
                foreach (var property in context.Properties)
                {
                    if (!payload.ContainsKey(property.Key))
                    {
                        payload[property.Key] = property.Value;
                    }
                }
            }

            _sink.Write(JsonSerializer.Serialize(payload));
        }

        private IReadOnlyDictionary<string, string> BuildDimensions(IReadOnlyDictionary<string, string>? dimensions)
        {
            var merged = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Service"] = _settings.ServiceName,
                ["Environment"] = _settings.Environment
            };

            if (dimensions == null)
            {
                return new ReadOnlyDictionary<string, string>(merged);
            }

            foreach (var dimension in dimensions)
            {
                if (AllowedDimensions.Contains(dimension.Key) && !ReservedDimensions.Contains(dimension.Key, StringComparer.Ordinal))
                {
                    merged[dimension.Key] = dimension.Value;
                }
            }

            return new ReadOnlyDictionary<string, string>(merged);
        }

        private static string[][] BuildDimensionSets(IReadOnlyDictionary<string, string> dimensions)
        {
            var baseSet = ReservedDimensions.ToArray();
            var fullSet = dimensions.Keys.ToArray();
            var dimensionSets = new List<string[]>
            {
                baseSet
            };

            AddDimensionSetIfPresent(dimensionSets, dimensions, "Dependency");
            AddDimensionSetIfPresent(dimensionSets, dimensions, "JobName");
            AddDimensionSetIfPresent(dimensionSets, dimensions, "FeedType");
            AddDimensionSetIfPresent(dimensionSets, dimensions, "Operation");
            AddDimensionSetIfPresent(dimensionSets, dimensions, "Operation", "Outcome");
            AddDimensionSetIfPresent(dimensionSets, dimensions, "Operation", "Outcome", "StatusClass");

            if (!dimensionSets.Any(set => set.SequenceEqual(fullSet, StringComparer.Ordinal)))
            {
                dimensionSets.Add(fullSet);
            }

            return dimensionSets.ToArray();
        }

        private static void AddDimensionSetIfPresent(
            ICollection<string[]> dimensionSets,
            IReadOnlyDictionary<string, string> dimensions,
            params string[] dimensionNames)
        {
            if (dimensionNames.Any(name => !dimensions.ContainsKey(name)))
            {
                return;
            }

            var set = ReservedDimensions.Concat(dimensionNames).ToArray();
            if (!dimensionSets.Any(existing => existing.SequenceEqual(set, StringComparer.Ordinal)))
            {
                dimensionSets.Add(set);
            }
        }

        private static TagList ToTags(IReadOnlyDictionary<string, string>? dimensions)
        {
            var tags = new TagList();
            if (dimensions == null)
            {
                return tags;
            }

            foreach (var dimension in dimensions)
            {
                tags.Add(dimension.Key, dimension.Value);
            }

            return tags;
        }
    }
}
