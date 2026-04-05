using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Crs.Api.Middleware;
using Crs.Infrastructure.Configuration;

namespace Crs.Tests.Unit.Api;

[TestClass]
public sealed class ExceptionHandlingMiddlewareTests
{
    [TestMethod]
    public async Task InvokeAsync_WhenArgumentException_ReturnsBadRequestProblem()
    {
        var middleware = CreateMiddleware(new ArgumentException("bad request"), Environments.Development);
        var context = CreateHttpContext();

        await middleware.InvokeAsync(context);

        Assert.AreEqual(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        var problem = await ReadProblemAsync(context);
        Assert.AreEqual("Bad Request", problem.Title);
        Assert.AreEqual("bad request", problem.Detail);
    }

    [TestMethod]
    public async Task InvokeAsync_WhenKeyNotFound_ReturnsNotFoundProblem()
    {
        var middleware = CreateMiddleware(new KeyNotFoundException("missing"), Environments.Development);
        var context = CreateHttpContext();

        await middleware.InvokeAsync(context);

        Assert.AreEqual(StatusCodes.Status404NotFound, context.Response.StatusCode);
        var problem = await ReadProblemAsync(context);
        Assert.AreEqual("Not Found", problem.Title);
    }

    [TestMethod]
    public async Task InvokeAsync_WhenUnauthorized_ReturnsUnauthorizedProblem()
    {
        var middleware = CreateMiddleware(new UnauthorizedAccessException("nope"), Environments.Development);
        var context = CreateHttpContext();

        await middleware.InvokeAsync(context);

        Assert.AreEqual(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        var problem = await ReadProblemAsync(context);
        Assert.AreEqual("Unauthorized", problem.Title);
    }

    [TestMethod]
    public async Task InvokeAsync_WhenProduction_HidesExceptionMessage()
    {
        var middleware = CreateMiddleware(new Exception("sensitive"), Environments.Production);
        var context = CreateHttpContext();

        await middleware.InvokeAsync(context);

        Assert.AreEqual(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        var problem = await ReadProblemAsync(context);
        Assert.AreEqual("Internal Server Error", problem.Title);
        Assert.AreEqual("An error occurred processing your request.", problem.Detail);
        Assert.IsTrue(problem.Extensions.ContainsKey("correlationId"));
    }

    private static ExceptionHandlingMiddleware CreateMiddleware(Exception exception, string environment)
    {
        RequestDelegate next = _ => throw exception;
        var hostEnvironment = new TestHostEnvironment { EnvironmentName = environment };

        return new ExceptionHandlingMiddleware(
            next,
            NullLogger<ExceptionHandlingMiddleware>.Instance,
            hostEnvironment,
            new ObservabilitySettings
            {
                Environment = environment,
                ExecutionEnvironment = "local",
                ServiceName = "crs-tests"
            });
    }

    private static DefaultHttpContext CreateHttpContext()
    {
        return new DefaultHttpContext
        {
            Response =
            {
                Body = new MemoryStream()
            }
        };
    }

    private static async Task<ProblemDetails> ReadProblemAsync(HttpContext context)
    {
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        var json = await new StreamReader(context.Response.Body).ReadToEndAsync();
        return JsonSerializer.Deserialize<ProblemDetails>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        })!;
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
