using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Crs.Api.Controllers;
using Crs.Api.DTOs.Users.Requests;
using Crs.Api.DTOs.Users.Responses;
using Crs.Api.DTOs.Votes.Responses;
using Crs.Api.Services;

namespace Crs.Tests.Unit.Api;

[TestClass]
public sealed class UsersControllerTests
{
    private static UsersController CreateController(
        out Mock<IUserService> userService,
        out Mock<IVoteService> voteService)
    {
        userService = new Mock<IUserService>(MockBehavior.Strict);
        voteService = new Mock<IVoteService>(MockBehavior.Strict);
        return new UsersController(userService.Object, voteService.Object, NullLogger<UsersController>.Instance);
    }

    [TestMethod]
    public async Task GetCurrentUser_WhenMissingUser_ReturnsUnauthorized()
    {
        var controller = CreateController(out _, out _);
        ControllerTestHelpers.SetUser(controller, null);

        var result = await controller.GetCurrentUser(CancellationToken.None);

        Assert.IsInstanceOfType<UnauthorizedResult>(result);
    }

    [TestMethod]
    public async Task GetCurrentUser_WhenNotFound_ReturnsNotFound()
    {
        var controller = CreateController(out var userService, out _);
        var userId = Guid.NewGuid();
        ControllerTestHelpers.SetUser(controller, userId);

        userService.Setup(service => service.GetUserByIdAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((UserDetailResponse?)null);

        var result = await controller.GetCurrentUser(CancellationToken.None);

        Assert.IsInstanceOfType<NotFoundResult>(result);
    }

    [TestMethod]
    public async Task GetCurrentUser_WhenFound_ReturnsOk()
    {
        var controller = CreateController(out var userService, out _);
        var userId = Guid.NewGuid();
        ControllerTestHelpers.SetUser(controller, userId);

        var response = new UserDetailResponse { Id = userId, Email = "user@example.com" };
        userService.Setup(service => service.GetUserByIdAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);

        var result = await controller.GetCurrentUser(CancellationToken.None);

        var ok = result as OkObjectResult;
        Assert.IsNotNull(ok);
        Assert.AreSame(response, ok.Value);
    }

    [TestMethod]
    public async Task GetUserById_WhenNotFound_ReturnsNotFound()
    {
        var controller = CreateController(out var userService, out _);
        var userId = Guid.NewGuid();

        userService.Setup(service => service.GetUserByIdAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((UserDetailResponse?)null);

        var result = await controller.GetUserById(userId, CancellationToken.None);

        Assert.IsInstanceOfType<NotFoundResult>(result);
    }

    [TestMethod]
    public async Task GetUserById_WhenFound_ReturnsOk()
    {
        var controller = CreateController(out var userService, out _);
        var userId = Guid.NewGuid();
        var response = new UserDetailResponse { Id = userId, Email = "user@example.com" };

        userService.Setup(service => service.GetUserByIdAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);

        var result = await controller.GetUserById(userId, CancellationToken.None);

        var ok = result as OkObjectResult;
        Assert.IsNotNull(ok);
        Assert.AreSame(response, ok.Value);
    }

    [TestMethod]
    public async Task UpdateCurrentUser_WhenMissingUser_ReturnsUnauthorized()
    {
        var controller = CreateController(out _, out _);
        ControllerTestHelpers.SetUser(controller, null);

        var result = await controller.UpdateCurrentUser(new UpdateUserRequest(), CancellationToken.None);

        Assert.IsInstanceOfType<UnauthorizedResult>(result);
    }

    [TestMethod]
    public async Task UpdateCurrentUser_ReturnsOk()
    {
        var controller = CreateController(out var userService, out _);
        var userId = Guid.NewGuid();
        ControllerTestHelpers.SetUser(controller, userId);

        var response = new UserResponse { Id = userId, Email = "user@example.com" };
        userService.Setup(service => service.UpdateUserAsync(userId, It.IsAny<UpdateUserRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);

        var result = await controller.UpdateCurrentUser(new UpdateUserRequest(), CancellationToken.None);

        var ok = result as OkObjectResult;
        Assert.IsNotNull(ok);
        Assert.AreSame(response, ok.Value);
    }

    [TestMethod]
    public async Task UpdateUser_WhenMissingUser_ReturnsUnauthorized()
    {
        var controller = CreateController(out _, out _);
        ControllerTestHelpers.SetUser(controller, null);

        var result = await controller.UpdateUser(Guid.NewGuid(), new UpdateUserRequest(), CancellationToken.None);

        Assert.IsInstanceOfType<UnauthorizedResult>(result);
    }

    [TestMethod]
    public async Task UpdateUser_WhenDifferentUser_ReturnsForbid()
    {
        var controller = CreateController(out _, out _);
        ControllerTestHelpers.SetUser(controller, Guid.NewGuid());

        var result = await controller.UpdateUser(Guid.NewGuid(), new UpdateUserRequest(), CancellationToken.None);

        Assert.IsInstanceOfType<ForbidResult>(result);
    }

    [TestMethod]
    public async Task UpdateUser_WhenSameUser_ReturnsOk()
    {
        var controller = CreateController(out var userService, out _);
        var userId = Guid.NewGuid();
        ControllerTestHelpers.SetUser(controller, userId);

        var response = new UserResponse { Id = userId, Email = "user@example.com" };
        userService.Setup(service => service.UpdateUserAsync(userId, It.IsAny<UpdateUserRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);

        var result = await controller.UpdateUser(userId, new UpdateUserRequest(), CancellationToken.None);

        var ok = result as OkObjectResult;
        Assert.IsNotNull(ok);
        Assert.AreSame(response, ok.Value);
    }

    [TestMethod]
    public async Task GetCurrentUserVotes_WhenMissingUser_ReturnsUnauthorized()
    {
        var controller = CreateController(out _, out _);
        ControllerTestHelpers.SetUser(controller, null);

        var result = await controller.GetCurrentUserVotes(CancellationToken.None);

        Assert.IsInstanceOfType<UnauthorizedResult>(result);
    }

    [TestMethod]
    public async Task GetCurrentUserVotes_ReturnsOk()
    {
        var controller = CreateController(out _, out var voteService);
        var userId = Guid.NewGuid();
        ControllerTestHelpers.SetUser(controller, userId);

        var votes = new List<VoteResponse> { new() { Id = Guid.NewGuid(), UserId = userId } };
        voteService.Setup(service => service.GetUserVotesAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(votes);

        var result = await controller.GetCurrentUserVotes(CancellationToken.None);

        var ok = result as OkObjectResult;
        Assert.IsNotNull(ok);
        Assert.AreSame(votes, ok.Value);
    }

    [TestMethod]
    public async Task GetCurrentUserVoteHistory_WhenMissingUser_ReturnsUnauthorized()
    {
        var controller = CreateController(out _, out _);
        ControllerTestHelpers.SetUser(controller, null);

        var result = await controller.GetCurrentUserVoteHistory(CancellationToken.None);

        Assert.IsInstanceOfType<UnauthorizedResult>(result);
    }

    [TestMethod]
    public async Task GetCurrentUserVoteHistory_ReturnsOk()
    {
        var controller = CreateController(out _, out var voteService);
        var userId = Guid.NewGuid();
        ControllerTestHelpers.SetUser(controller, userId);

        var history = new List<VoteHistoryItemResponse>
        {
            new() { Id = Guid.NewGuid(), UserId = userId, ContentId = Guid.NewGuid(), Title = "Voted content" }
        };

        voteService.Setup(service => service.GetUserVoteHistoryAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(history);

        var result = await controller.GetCurrentUserVoteHistory(CancellationToken.None);

        var ok = result as OkObjectResult;
        Assert.IsNotNull(ok);
        Assert.AreSame(history, ok.Value);
    }
}
