using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Crs.Api.DTOs.Users.Requests;
using Crs.Api.Services;
using Crs.Core.Entities;
using Crs.Core.Interfaces;

namespace Crs.Tests.Unit.Api;

[TestClass]
public sealed class UserServiceTests
{
    private static UserService CreateService(
        out Mock<IUserRepository> userRepository)
    {
        userRepository = new Mock<IUserRepository>(MockBehavior.Strict);
        return new UserService(userRepository.Object, NullLogger<UserService>.Instance);
    }

    [TestMethod]
    public async Task GetUserByIdAsync_WhenMissing_ReturnsNull()
    {
        var service = CreateService(out var userRepository);
        userRepository.Setup(repo => repo.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((User?)null);

        var result = await service.GetUserByIdAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task GetUserByIdAsync_WhenFound_MapsSources()
    {
        var service = CreateService(out var userRepository);
        var userId = Guid.NewGuid();
        var source = new Source
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = "Source",
            Url = "https://example.com",
            Content = new List<Content> { new BlogPost { Id = Guid.NewGuid() } }
        };

        userRepository.Setup(repo => repo.GetByIdAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new User
            {
                Id = userId,
                Email = "user@example.com",
                DisplayName = "User",
                Sources = new List<Source> { source }
            });

        var result = await service.GetUserByIdAsync(userId, CancellationToken.None);

        Assert.IsNotNull(result);
        Assert.HasCount(1, result.Sources);
        Assert.AreEqual(1, result.Sources[0].ContentCount);
    }

    [TestMethod]
    public async Task UpdateUserAsync_WhenMissing_Throws()
    {
        var service = CreateService(out var userRepository);
        userRepository.Setup(repo => repo.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((User?)null);

        await TestAssert.ThrowsAsync<KeyNotFoundException>(() =>
            service.UpdateUserAsync(Guid.NewGuid(), new UpdateUserRequest(), CancellationToken.None));
    }

    [TestMethod]
    public async Task UpdateUserAsync_WhenValid_UpdatesDisplayName()
    {
        var service = CreateService(out var userRepository);
        var userId = Guid.NewGuid();
        var user = new User { Id = userId, Email = "user@example.com", DisplayName = "Old" };

        userRepository.Setup(repo => repo.GetByIdAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);
        userRepository.Setup(repo => repo.UpdateAsync(user, It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);

        var response = await service.UpdateUserAsync(userId, new UpdateUserRequest { DisplayName = "New" }, CancellationToken.None);

        Assert.AreEqual("New", response.DisplayName);
    }
}
