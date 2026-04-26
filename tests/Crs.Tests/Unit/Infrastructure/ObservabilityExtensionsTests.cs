using System.Text.Json;
using Crs.Core.Observability;
using Crs.Infrastructure.Observability;
using Crs.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Crs.Tests.Unit.Infrastructure;

[TestClass]
public sealed class ObservabilityExtensionsTests
{
    [TestMethod]
    public void AddCrsObservability_EmitsCloudWatchEmbeddedMetricPayload()
    {
        var previousOut = Console.Out;
        using var writer = new StringWriter();
        Console.SetOut(writer);

        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Observability:ServiceName"] = "crs-tests",
                    ["Observability:Environment"] = "Testing",
                    ["Observability:ExecutionEnvironment"] = "local"
                })
                .Build();

            var services = new ServiceCollection();
            services.AddCrsObservability(configuration, new TestHostEnvironment(), "crs-tests");

            using var provider = services.BuildServiceProvider();
            var metrics = provider.GetRequiredService<IObservabilityMetrics>();

            metrics.Increment(
                "api.request.count",
                context: new MetricContext(
                    Dimensions: new Dictionary<string, string>
                    {
                        ["Operation"] = "health",
                        ["Outcome"] = "success",
                        ["StatusClass"] = "2xx"
                    }));

            var output = writer.ToString().Trim();
            Assert.IsFalse(string.IsNullOrWhiteSpace(output));

            var payload = JsonSerializer.Deserialize<JsonElement>(output);
            Assert.IsTrue(payload.TryGetProperty("_aws", out _));
            Assert.AreEqual("crs-tests", payload.GetProperty("Service").GetString());
            Assert.AreEqual("Testing", payload.GetProperty("Environment").GetString());
            Assert.AreEqual("local", payload.GetProperty("ExecutionEnvironment").GetString());
            Assert.AreEqual(1d, payload.GetProperty("api.request.count").GetDouble(), 0.001d);
        }
        finally
        {
            Console.SetOut(previousOut);
        }
    }

    [TestMethod]
    public void AddCrsObservability_DisabledMetrics_UsesNullMetrics()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Observability:ServiceName"] = "crs-tests",
                ["Observability:Environment"] = "Testing",
                ["Observability:ExecutionEnvironment"] = "local",
                ["Observability:EnableMetrics"] = "false"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddCrsObservability(configuration, new TestHostEnvironment(), "crs-tests");

        using var provider = services.BuildServiceProvider();
        var metrics = provider.GetRequiredService<IObservabilityMetrics>();

        Assert.IsInstanceOfType<NullObservabilityMetrics>(metrics);
    }
}
