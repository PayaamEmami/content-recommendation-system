using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Crs.Api.Controllers;
using Crs.Api.DTOs.Ingestion.Requests;
using Crs.Core.Entities;
using Crs.Core.Enums;
using Crs.Core.Interfaces;
using Crs.Llm.Models;
using Crs.Llm.Services;

namespace Crs.Tests.Unit.Api;

[TestClass]
public sealed class IngestionControllerTests
{
    private static IngestionController CreateController(
        out Mock<IIngestionAgent> ingestionAgent,
        out Mock<ISourceIngestionService> sourceIngestionService,
        out Mock<ISourceRepository> sourceRepository)
    {
        ingestionAgent = new Mock<IIngestionAgent>(MockBehavior.Strict);
        sourceIngestionService = new Mock<ISourceIngestionService>(MockBehavior.Strict);
        sourceRepository = new Mock<ISourceRepository>(MockBehavior.Strict);
        return new IngestionController(
            ingestionAgent.Object,
            sourceIngestionService.Object,
            sourceRepository.Object,
            NullLogger<IngestionController>.Instance);
    }

    [TestMethod]
    public async Task IngestFromUrl_WhenInvalidUrl_ReturnsBadRequest()
    {
        var controller = CreateController(out _, out _, out _);

        var result = await controller.IngestFromUrl(new IngestUrlRequest { Url = "not-a-url" }, CancellationToken.None);

        var badRequest = result as BadRequestObjectResult;
        Assert.IsNotNull(badRequest);
        Assert.AreEqual("Invalid URL provided.", GetProperty<string?>(badRequest.Value!, "message"));
    }

    [TestMethod]
    public async Task IngestFromUrl_WhenIngestionFails_ReturnsServerError()
    {
        var controller = CreateController(out var ingestionAgent, out _, out _);

        ingestionAgent.Setup(agent => agent.IngestFromUrlAsync(It.IsAny<string>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IngestionResult
            {
                Success = false,
                ErrorMessage = "LLM error",
                TotalFound = 1,
                NewContent = 0,
                DuplicatesSkipped = 1
            });

        var result = await controller.IngestFromUrl(new IngestUrlRequest { Url = "https://example.com" }, CancellationToken.None);

        var objectResult = result as ObjectResult;
        Assert.IsNotNull(objectResult);
        Assert.AreEqual(StatusCodes.Status500InternalServerError, objectResult.StatusCode);
        Assert.AreEqual("Ingestion failed", GetProperty<string?>(objectResult.Value!, "message"));
    }

    [TestMethod]
    public async Task IngestFromUrl_WhenSuccess_ReturnsOk()
    {
        var controller = CreateController(out var ingestionAgent, out _, out _);
        var ingestionResult = new IngestionResult
        {
            Success = true,
            TotalFound = 2,
            NewContent = 2,
            DuplicatesSkipped = 0,
            Content = new List<ExtractedContent>
            {
                new() { Title = "One", Url = "https://example.com/1", Description = "Desc", Type = ContentType.Paper }
            }
        };

        ingestionAgent.Setup(agent => agent.IngestFromUrlAsync("https://example.com", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ingestionResult);

        var result = await controller.IngestFromUrl(new IngestUrlRequest { Url = "https://example.com" }, CancellationToken.None);

        var okResult = result as OkObjectResult;
        Assert.IsNotNull(okResult);
        Assert.IsTrue(GetProperty<bool>(okResult.Value!, "success"));
        Assert.AreEqual(2, GetProperty<int>(okResult.Value!, "totalFound"));
    }

    [TestMethod]
    public async Task IngestFromSource_WhenMissingUser_ReturnsUnauthorized()
    {
        var controller = CreateController(out _, out _, out _);
        ControllerTestHelpers.SetUser(controller, null);

        var result = await controller.IngestFromSource(Guid.NewGuid(), CancellationToken.None);

        Assert.IsInstanceOfType<UnauthorizedObjectResult>(result);
    }

    [TestMethod]
    public async Task IngestFromSource_WhenSourceMissing_ReturnsNotFound()
    {
        var controller = CreateController(out _, out _, out var sourceRepository);
        var userId = Guid.NewGuid();
        ControllerTestHelpers.SetUser(controller, userId);
        var sourceId = Guid.NewGuid();

        sourceRepository.Setup(repo => repo.GetByIdAsync(sourceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Source?)null);

        var result = await controller.IngestFromSource(sourceId, CancellationToken.None);

        Assert.IsInstanceOfType<NotFoundObjectResult>(result);
    }

    [TestMethod]
    public async Task IngestFromSource_WhenDifferentOwner_ReturnsForbid()
    {
        var controller = CreateController(out _, out _, out var sourceRepository);
        var userId = Guid.NewGuid();
        ControllerTestHelpers.SetUser(controller, userId);
        var sourceId = Guid.NewGuid();

        sourceRepository.Setup(repo => repo.GetByIdAsync(sourceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Source
            {
                Id = sourceId,
                UserId = Guid.NewGuid(),
                Url = "https://example.com"
            });

        var result = await controller.IngestFromSource(sourceId, CancellationToken.None);

        Assert.IsInstanceOfType<ForbidResult>(result);
    }

    [TestMethod]
    public async Task IngestFromSource_WhenSuccess_ReturnsOk()
    {
        var controller = CreateController(out _, out var sourceIngestionService, out var sourceRepository);
        var userId = Guid.NewGuid();
        ControllerTestHelpers.SetUser(controller, userId);
        var sourceId = Guid.NewGuid();
        var source = new Source
        {
            Id = sourceId,
            UserId = userId,
            Url = "https://example.com"
        };

        sourceRepository.Setup(repo => repo.GetByIdAsync(sourceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(source);

        sourceIngestionService.Setup(service => service.IngestSourceAsync(source, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SourceIngestionSummary
            {
                Success = true,
                Extracted = 1,
                Saved = 1,
                Duplicates = 0,
                Errors = 0,
                Embedded = 1
            });

        var result = await controller.IngestFromSource(sourceId, CancellationToken.None);

        var okResult = result as OkObjectResult;
        Assert.IsNotNull(okResult);
        Assert.IsTrue(GetProperty<bool>(okResult.Value!, "success"));
        Assert.AreEqual(1, GetProperty<int>(okResult.Value!, "saved"));
        Assert.AreEqual(1, GetProperty<int>(okResult.Value!, "embedded"));
    }

    [TestMethod]
    public async Task IngestFromSource_WhenServiceFails_ReturnsServerError()
    {
        var controller = CreateController(out _, out var sourceIngestionService, out var sourceRepository);
        var userId = Guid.NewGuid();
        ControllerTestHelpers.SetUser(controller, userId);
        var sourceId = Guid.NewGuid();
        var source = new Source
        {
            Id = sourceId,
            UserId = userId,
            Url = "https://example.com"
        };

        sourceRepository.Setup(repo => repo.GetByIdAsync(sourceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(source);

        sourceIngestionService.Setup(service => service.IngestSourceAsync(source, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SourceIngestionSummary
            {
                Success = false,
                ErrorMessage = "fetch failed"
            });

        var result = await controller.IngestFromSource(sourceId, CancellationToken.None);

        var objectResult = result as ObjectResult;
        Assert.IsNotNull(objectResult);
        Assert.AreEqual(StatusCodes.Status500InternalServerError, objectResult.StatusCode);
    }

    private static T? GetProperty<T>(object instance, string name)
    {
        var property = instance.GetType().GetProperty(name);
        return property == null ? default : (T?)property.GetValue(instance);
    }
}
