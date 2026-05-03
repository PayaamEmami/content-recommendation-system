using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Crs.Api.Controllers;
using Crs.Api.DTOs.Sources.Requests;
using Crs.Api.DTOs.Sources.Responses;
using Crs.Api.Services;
using Crs.Core.Enums;

namespace Crs.Tests.Unit.Api;

[TestClass]
public sealed class SourcesControllerTests
{
    private static SourcesController CreateController(out Mock<ISourceService> sourceService)
    {
        sourceService = new Mock<ISourceService>(MockBehavior.Strict);
        return new SourcesController(sourceService.Object, NullLogger<SourcesController>.Instance);
    }

    [TestMethod]
    public async Task GetSourceById_WhenMissingUser_ReturnsUnauthorized()
    {
        var controller = CreateController(out _);
        ControllerTestHelpers.SetUser(controller, null);

        var result = await controller.GetSourceById(Guid.NewGuid(), CancellationToken.None);

        Assert.IsInstanceOfType<UnauthorizedResult>(result);
    }

    [TestMethod]
    public async Task GetSourceById_WhenMissing_ReturnsNotFound()
    {
        var controller = CreateController(out var sourceService);
        ControllerTestHelpers.SetUser(controller, Guid.NewGuid());
        var id = Guid.NewGuid();

        sourceService.Setup(service => service.GetSourceByIdAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((SourceResponse?)null);

        var result = await controller.GetSourceById(id, CancellationToken.None);

        Assert.IsInstanceOfType<NotFoundObjectResult>(result);
    }

    [TestMethod]
    public async Task GetSourceById_WhenDifferentOwner_ReturnsForbid()
    {
        var controller = CreateController(out var sourceService);
        var userId = Guid.NewGuid();
        ControllerTestHelpers.SetUser(controller, userId);
        var id = Guid.NewGuid();

        sourceService.Setup(service => service.GetSourceByIdAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SourceResponse
            {
                Id = id,
                UserId = Guid.NewGuid(),
                Name = "Other user's source",
                Url = "https://example.com"
            });

        var result = await controller.GetSourceById(id, CancellationToken.None);

        Assert.IsInstanceOfType<ForbidResult>(result);
    }

    [TestMethod]
    public async Task GetSourceById_WhenOwner_ReturnsOk()
    {
        var controller = CreateController(out var sourceService);
        var userId = Guid.NewGuid();
        ControllerTestHelpers.SetUser(controller, userId);
        var id = Guid.NewGuid();
        var response = new SourceResponse
        {
            Id = id,
            UserId = userId,
            Name = "My source",
            Url = "https://example.com"
        };

        sourceService.Setup(service => service.GetSourceByIdAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);

        var result = await controller.GetSourceById(id, CancellationToken.None);

        var okResult = result as OkObjectResult;
        Assert.IsNotNull(okResult);
        Assert.AreSame(response, okResult.Value);
    }

    [TestMethod]
    public async Task GetUserSources_WhenMissingUser_ReturnsUnauthorized()
    {
        var controller = CreateController(out _);
        ControllerTestHelpers.SetUser(controller, null);

        var result = await controller.GetUserSources(CancellationToken.None);

        Assert.IsInstanceOfType<UnauthorizedResult>(result);
    }

    [TestMethod]
    public async Task GetUserSources_WhenAuthorized_ReturnsOk()
    {
        var controller = CreateController(out var sourceService);
        var userId = Guid.NewGuid();
        ControllerTestHelpers.SetUser(controller, userId);

        var sources = new List<SourceResponse>
        {
            new() { Id = Guid.NewGuid(), UserId = userId, Name = "Test", Url = "https://example.com" }
        };

        sourceService.Setup(service => service.GetUserSourcesAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(sources);

        var result = await controller.GetUserSources(CancellationToken.None);

        var okResult = result as OkObjectResult;
        Assert.IsNotNull(okResult);
        Assert.AreSame(sources, okResult.Value);
    }

    [TestMethod]
    public async Task GetActiveUserSources_WhenMissingUser_ReturnsUnauthorized()
    {
        var controller = CreateController(out _);
        ControllerTestHelpers.SetUser(controller, null);

        var result = await controller.GetActiveUserSources(CancellationToken.None);

        Assert.IsInstanceOfType<UnauthorizedResult>(result);
    }

    [TestMethod]
    public async Task GetActiveUserSources_WhenAuthorized_ReturnsOk()
    {
        var controller = CreateController(out var sourceService);
        var userId = Guid.NewGuid();
        ControllerTestHelpers.SetUser(controller, userId);

        var sources = new List<SourceResponse>
        {
            new() { Id = Guid.NewGuid(), UserId = userId, Name = "Active", Url = "https://example.com" }
        };

        sourceService.Setup(service => service.GetActiveUserSourcesAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(sources);

        var result = await controller.GetActiveUserSources(CancellationToken.None);

        var okResult = result as OkObjectResult;
        Assert.IsNotNull(okResult);
        Assert.AreSame(sources, okResult.Value);
    }

    [TestMethod]
    public async Task GetSourcesByCategory_ReturnsOk()
    {
        var controller = CreateController(out var sourceService);
        var sources = new List<SourceResponse> { new() { Id = Guid.NewGuid(), Name = "Cat", Url = "https://example.com" } };

        sourceService.Setup(service => service.GetSourcesByCategoryAsync(ContentType.Paper, It.IsAny<CancellationToken>()))
            .ReturnsAsync(sources);

        var result = await controller.GetSourcesByCategory(ContentType.Paper, CancellationToken.None);

        var okResult = result as OkObjectResult;
        Assert.IsNotNull(okResult);
        Assert.AreSame(sources, okResult.Value);
    }

    [TestMethod]
    public async Task CreateSource_WhenMissingUser_ReturnsUnauthorized()
    {
        var controller = CreateController(out _);
        ControllerTestHelpers.SetUser(controller, null);

        var result = await controller.CreateSource(new CreateSourceRequest(), CancellationToken.None);

        Assert.IsInstanceOfType<UnauthorizedResult>(result);
    }

    [TestMethod]
    public async Task CreateSource_WhenServiceThrows_ReturnsBadRequest()
    {
        var controller = CreateController(out var sourceService);
        var userId = Guid.NewGuid();
        ControllerTestHelpers.SetUser(controller, userId);

        sourceService.Setup(service => service.CreateSourceAsync(userId, It.IsAny<CreateSourceRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("duplicate"));

        var result = await controller.CreateSource(new CreateSourceRequest(), CancellationToken.None);

        Assert.IsInstanceOfType<BadRequestObjectResult>(result);
    }

    [TestMethod]
    public async Task CreateSource_WhenSuccess_ReturnsCreatedAtAction()
    {
        var controller = CreateController(out var sourceService);
        var userId = Guid.NewGuid();
        ControllerTestHelpers.SetUser(controller, userId);

        var response = new SourceResponse { Id = Guid.NewGuid(), UserId = userId, Name = "Test", Url = "https://example.com" };
        sourceService.Setup(service => service.CreateSourceAsync(userId, It.IsAny<CreateSourceRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);

        var result = await controller.CreateSource(new CreateSourceRequest(), CancellationToken.None);

        var created = result as CreatedAtActionResult;
        Assert.IsNotNull(created);
        Assert.AreSame(response, created.Value);
    }

    [TestMethod]
    public async Task UpdateSource_WhenNotFound_PropagatesKeyNotFound()
    {
        // The controller no longer catches not-found; the global ExceptionHandlingMiddleware maps
        // KeyNotFoundException to a 404 ProblemDetails response.
        var controller = CreateController(out var sourceService);
        var id = Guid.NewGuid();

        sourceService.Setup(service => service.UpdateSourceAsync(id, It.IsAny<UpdateSourceRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new KeyNotFoundException("missing"));

        await TestAssert.ThrowsAsync<KeyNotFoundException>(() =>
            controller.UpdateSource(id, new UpdateSourceRequest(), CancellationToken.None));
    }

    [TestMethod]
    public async Task UpdateSource_WhenInvalid_ReturnsBadRequest()
    {
        var controller = CreateController(out var sourceService);
        var id = Guid.NewGuid();

        sourceService.Setup(service => service.UpdateSourceAsync(id, It.IsAny<UpdateSourceRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("duplicate"));

        var result = await controller.UpdateSource(id, new UpdateSourceRequest(), CancellationToken.None);

        Assert.IsInstanceOfType<BadRequestObjectResult>(result);
    }

    [TestMethod]
    public async Task UpdateSource_WhenSuccess_ReturnsOk()
    {
        var controller = CreateController(out var sourceService);
        var id = Guid.NewGuid();
        var response = new SourceResponse { Id = id, Name = "Updated", Url = "https://example.com" };

        sourceService.Setup(service => service.UpdateSourceAsync(id, It.IsAny<UpdateSourceRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);

        var result = await controller.UpdateSource(id, new UpdateSourceRequest(), CancellationToken.None);

        var okResult = result as OkObjectResult;
        Assert.IsNotNull(okResult);
        Assert.AreSame(response, okResult.Value);
    }

    [TestMethod]
    public async Task DeleteSource_WhenNotFound_PropagatesKeyNotFound()
    {
        // The controller no longer catches not-found; the global ExceptionHandlingMiddleware maps
        // KeyNotFoundException to a 404 ProblemDetails response.
        var controller = CreateController(out var sourceService);
        var id = Guid.NewGuid();

        sourceService.Setup(service => service.DeleteSourceAsync(id, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new KeyNotFoundException("missing"));

        await TestAssert.ThrowsAsync<KeyNotFoundException>(() =>
            controller.DeleteSource(id, CancellationToken.None));
    }

    [TestMethod]
    public async Task DeleteSource_WhenSuccess_ReturnsNoContent()
    {
        var controller = CreateController(out var sourceService);
        var id = Guid.NewGuid();

        sourceService.Setup(service => service.DeleteSourceAsync(id, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var result = await controller.DeleteSource(id, CancellationToken.None);

        Assert.IsInstanceOfType<NoContentResult>(result);
    }

    [TestMethod]
    public async Task BulkImportSources_WhenMissingUser_ReturnsUnauthorized()
    {
        var controller = CreateController(out _);
        ControllerTestHelpers.SetUser(controller, null);

        var result = await controller.BulkImportSources(new BulkImportSourcesRequest(), CancellationToken.None);

        Assert.IsInstanceOfType<UnauthorizedResult>(result);
    }

    [TestMethod]
    public async Task BulkImportSources_WhenUserMissing_PropagatesKeyNotFound()
    {
        // The controller no longer catches not-found; the global ExceptionHandlingMiddleware maps
        // KeyNotFoundException to a 404 ProblemDetails response.
        var controller = CreateController(out var sourceService);
        var userId = Guid.NewGuid();
        ControllerTestHelpers.SetUser(controller, userId);

        sourceService.Setup(service => service.BulkImportSourcesAsync(userId, It.IsAny<BulkImportSourcesRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new KeyNotFoundException("user not found"));

        await TestAssert.ThrowsAsync<KeyNotFoundException>(() =>
            controller.BulkImportSources(new BulkImportSourcesRequest(), CancellationToken.None));
    }

    [TestMethod]
    public async Task BulkImportSources_WhenSuccess_ReturnsOk()
    {
        var controller = CreateController(out var sourceService);
        var userId = Guid.NewGuid();
        ControllerTestHelpers.SetUser(controller, userId);

        var response = new BulkImportResult { Imported = 1, Failed = 0 };
        sourceService.Setup(service => service.BulkImportSourcesAsync(userId, It.IsAny<BulkImportSourcesRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);

        var result = await controller.BulkImportSources(new BulkImportSourcesRequest(), CancellationToken.None);

        var okResult = result as OkObjectResult;
        Assert.IsNotNull(okResult);
        Assert.AreSame(response, okResult.Value);
    }
}
