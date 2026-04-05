using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Logging;
using Moq;
using Crs.Api.Middleware;
using Crs.Core.Observability;
using Crs.Infrastructure.Configuration;

namespace Crs.Tests.Unit.Api;

[TestClass]
public sealed class RequestLoggingMiddlewareTests
{
    [TestMethod]
    public async Task InvokeAsync_LogsCompletionAndRecordsMetrics()
    {
        var metrics = new RecordingMetrics();
        var logger = new Mock<ILogger<RequestLoggingMiddleware>>();
        IEnumerable<KeyValuePair<string, object?>>? capturedScope = null;

        logger.Setup(log => log.BeginScope(It.IsAny<It.IsAnyType>()))
            .Callback(new InvocationAction(invocation =>
            {
                capturedScope = invocation.Arguments[0] as IEnumerable<KeyValuePair<string, object?>>;
            }))
            .Returns(Mock.Of<IDisposable>());

        logger.Setup(log => log.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                (Func<It.IsAnyType, Exception?, string>)It.IsAny<object>()))
            .Verifiable();

        var middleware = new RequestLoggingMiddleware(
            async context =>
            {
                context.Response.StatusCode = StatusCodes.Status200OK;
                await context.Response.WriteAsync("ok");
            },
            logger.Object,
            metrics,
            new ObservabilitySettings
            {
                Environment = "Testing",
                ExecutionEnvironment = "local",
                ServiceName = "crs-tests"
            });

        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/health";
        context.Response.Body = new MemoryStream();
        context.SetEndpoint(new RouteEndpoint(
            _ => Task.CompletedTask,
            RoutePatternFactory.Parse("/health"),
            0,
            EndpointMetadataCollection.Empty,
            "health"));

        using var activity = new Activity("request").Start();

        await middleware.InvokeAsync(context);

        logger.Verify();
        Assert.IsNotNull(capturedScope);
        Assert.IsFalse(string.IsNullOrWhiteSpace(context.Response.Headers[CrsTelemetry.CorrelationIdHeaderName]));
        Assert.IsTrue(capturedScope.Any(item => item.Key == "correlation_id" && item.Value != null));
        Assert.IsTrue(capturedScope.Any(item => item.Key == "execution_environment" && Equals(item.Value, "local")));
        Assert.IsTrue(capturedScope.Any(item => item.Key == "trace_id" && item.Value != null));
        Assert.IsFalse(capturedScope.Any(item => item.Key == "user_id"));
        Assert.IsTrue(metrics.Calls.Any(call => call.Name == "api.request.count"));
        Assert.IsTrue(metrics.Calls.Any(call => call.Name == "api.request.duration"));
    }

    private sealed class RecordingMetrics : IObservabilityMetrics
    {
        public List<(string Name, double Value, string Unit, MetricContext? Context)> Calls { get; } = [];

        public void Increment(string name, double value = 1, string unit = "Count", MetricContext? context = null)
        {
            Calls.Add((name, value, unit, context));
        }

        public void RecordDuration(string name, TimeSpan duration, MetricContext? context = null)
        {
            Calls.Add((name, duration.TotalMilliseconds, "Milliseconds", context));
        }

        public void RecordValue(string name, double value, string unit, MetricContext? context = null)
        {
            Calls.Add((name, value, unit, context));
        }
    }
}
