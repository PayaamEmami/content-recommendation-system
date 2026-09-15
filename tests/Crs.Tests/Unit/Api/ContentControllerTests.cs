using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Crs.Api.Controllers;
using Crs.Api.DTOs.Common;
using Crs.Api.DTOs.Content.Responses;
using Crs.Api.Services;
using Crs.Core.Enums;

namespace Crs.Tests.Unit.Api;

[TestClass]
public sealed class ContentControllerTests
{
    private static ContentController CreateController(
        out Mock<IContentService> contentService,
        out Mock<IVoteService> voteService)
    {
        contentService = new Mock<IContentService>(MockBehavior.Strict);
        voteService = new Mock<IVoteService>(MockBehavior.Strict);

        return new ContentController(
            contentService.Object,
            voteService.Object,
            NullLogger<ContentController>.Instance);
    }

    [TestMethod]
    public async Task GetContent_WhenPageNumberInvalid_ReturnsBadRequest()
    {
        var controller = CreateController(out _, out _);

        var result = await controller.GetContent(0, 20, null, null, CancellationToken.None);

        var badRequest = result as BadRequestObjectResult;
        Assert.IsNotNull(badRequest);
        Assert.AreEqual("Page number must be greater than 0", badRequest.Value);
    }

    [TestMethod]
    public async Task GetContent_WhenPageSizeInvalid_ReturnsBadRequest()
    {
        var controller = CreateController(out _, out _);

        var result = await controller.GetContent(1, 101, null, null, CancellationToken.None);

        var badRequest = result as BadRequestObjectResult;
        Assert.IsNotNull(badRequest);
        Assert.AreEqual("Page size must be between 1 and 100", badRequest.Value);
    }

    [TestMethod]
    public async Task GetContent_WhenTopicIdsInvalid_ReturnsBadRequest()
    {
        var controller = CreateController(out _, out _);

        var result = await controller.GetContent(1, 20, null, "not-a-guid", CancellationToken.None);

        var badRequest = result as BadRequestObjectResult;
        Assert.IsNotNull(badRequest);
        Assert.AreEqual("Invalid topic IDs format", badRequest.Value);
    }

    [TestMethod]
    public async Task GetContent_WhenValidRequest_ReturnsOkWithResponse()
    {
        var controller = CreateController(out var contentService, out _);
        var expectedIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var expectedResponse = new PagedResponse<ContentResponse>
        {
            Items = new List<ContentResponse>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    Title = "Test Content",
                    Url = "https://example.com",
                    Type = ContentType.BlogPost,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                }
            },
            PageNumber = 1,
            PageSize = 20,
            TotalCount = 1,
            TotalPages = 1
        };

        contentService
            .Setup(service => service.GetContentAsync(
                1,
                20,
                ContentType.BlogPost,
                It.Is<List<Guid>>(ids => ids.SequenceEqual(expectedIds)),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedResponse);

        var result = await controller.GetContent(
            1,
            20,
            ContentType.BlogPost,
            $"{expectedIds[0]},{expectedIds[1]}",
            CancellationToken.None);

        var okResult = result as OkObjectResult;
        Assert.IsNotNull(okResult);
        Assert.AreSame(expectedResponse, okResult.Value);
    }

    [TestMethod]
    public async Task GetContentById_WhenMissing_ReturnsNotFound()
    {
        var controller = CreateController(out var contentService, out _);
        var contentId = Guid.NewGuid();

        contentService
            .Setup(service => service.GetContentByIdAsync(contentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ContentResponse?)null);

        var result = await controller.GetContentById(contentId, CancellationToken.None);

        Assert.IsInstanceOfType<NotFoundResult>(result);
    }

    [TestMethod]
    public async Task GetContentById_WhenFound_ReturnsOk()
    {
        var controller = CreateController(out var contentService, out _);
        var contentId = Guid.NewGuid();
        var response = new ContentResponse
        {
            Id = contentId,
            Title = "Existing Content",
            Url = "https://example.com/content",
            Type = ContentType.Video,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        contentService
            .Setup(service => service.GetContentByIdAsync(contentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);

        var result = await controller.GetContentById(contentId, CancellationToken.None);

        var okResult = result as OkObjectResult;
        Assert.IsNotNull(okResult);
        Assert.AreSame(response, okResult.Value);
    }

    [TestMethod]
    public async Task CreateContent_ReturnsForbid()
    {
        var controller = CreateController(out _, out _);

        var result = await controller.CreateContent(
            new Crs.Api.DTOs.Content.Requests.CreateContentRequest
            {
                Title = "Injected",
                Url = "https://example.com/injected",
                ContentType = ContentType.BlogPost
            },
            CancellationToken.None);

        Assert.IsInstanceOfType<ForbidResult>(result);
    }

    [TestMethod]
    public async Task UpdateContent_ReturnsForbid()
    {
        var controller = CreateController(out _, out _);

        var result = await controller.UpdateContent(
            Guid.NewGuid(),
            new Crs.Api.DTOs.Content.Requests.UpdateContentRequest(),
            CancellationToken.None);

        Assert.IsInstanceOfType<ForbidResult>(result);
    }

    [TestMethod]
    public async Task DeleteContent_ReturnsForbid()
    {
        var controller = CreateController(out _, out _);

        var result = await controller.DeleteContent(Guid.NewGuid(), CancellationToken.None);

        Assert.IsInstanceOfType<ForbidResult>(result);
    }
}
