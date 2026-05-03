using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Crs.Api.DTOs.Sources.Requests;
using Crs.Api.Services;
using Crs.Core.Entities;
using Crs.Core.Enums;
using Crs.Core.Interfaces;

namespace Crs.Tests.Unit.Api;

[TestClass]
public sealed class SourceServiceTests
{
    private static SourceService CreateService(
        out Mock<ISourceRepository> sourceRepository,
        out Mock<IUserRepository> userRepository)
    {
        sourceRepository = new Mock<ISourceRepository>(MockBehavior.Strict);
        userRepository = new Mock<IUserRepository>(MockBehavior.Strict);
        return new SourceService(sourceRepository.Object, userRepository.Object);
    }

    [TestMethod]
    public async Task CreateSourceAsync_WhenUserMissing_Throws()
    {
        var service = CreateService(out _, out var userRepository);
        userRepository.Setup(repo => repo.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((User?)null);

        await TestAssert.ThrowsAsync<KeyNotFoundException>(() =>
            service.CreateSourceAsync(Guid.NewGuid(), new CreateSourceRequest(), CancellationToken.None));
    }

    [TestMethod]
    public async Task CreateSourceAsync_WhenUrlExists_Throws()
    {
        var service = CreateService(out var sourceRepository, out var userRepository);
        var userId = Guid.NewGuid();
        userRepository.Setup(repo => repo.GetByIdAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new User { Id = userId });
        sourceRepository.Setup(repo => repo.UrlExistsForUserAsync(userId, "https://example.com", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var request = new CreateSourceRequest
        {
            Name = "Test",
            Url = "https://example.com",
            Category = ContentType.BlogPost
        };

        await TestAssert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateSourceAsync(userId, request, CancellationToken.None));
    }

    [TestMethod]
    public async Task CreateSourceAsync_WhenValid_ReturnsResponse()
    {
        var service = CreateService(out var sourceRepository, out var userRepository);
        var userId = Guid.NewGuid();
        userRepository.Setup(repo => repo.GetByIdAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new User { Id = userId });
        sourceRepository.Setup(repo => repo.UrlExistsForUserAsync(userId, "https://example.com", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var createdSource = new Source
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = "Test",
            Url = "https://example.com",
            Category = ContentType.BlogPost,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        sourceRepository.Setup(repo => repo.AddAsync(It.IsAny<Source>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(createdSource);

        var response = await service.CreateSourceAsync(userId, new CreateSourceRequest
        {
            Name = "Test",
            Url = "https://example.com",
            Category = ContentType.BlogPost
        }, CancellationToken.None);

        Assert.AreEqual(createdSource.Id, response.Id);
        Assert.AreEqual(createdSource.Url, response.Url);
    }

    [TestMethod]
    public async Task UpdateSourceAsync_WhenMissing_Throws()
    {
        var service = CreateService(out var sourceRepository, out _);
        sourceRepository.Setup(repo => repo.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Source?)null);

        await TestAssert.ThrowsAsync<KeyNotFoundException>(() =>
            service.UpdateSourceAsync(Guid.NewGuid(), new UpdateSourceRequest(), CancellationToken.None));
    }

    [TestMethod]
    public async Task UpdateSourceAsync_WhenUrlExists_Throws()
    {
        var service = CreateService(out var sourceRepository, out _);
        var source = new Source
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            Url = "https://old.com"
        };

        sourceRepository.Setup(repo => repo.GetByIdAsync(source.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(source);
        sourceRepository.Setup(repo => repo.UrlExistsForUserAsync(source.UserId, "https://new.com", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await TestAssert.ThrowsAsync<InvalidOperationException>(() =>
            service.UpdateSourceAsync(source.Id, new UpdateSourceRequest { Url = "https://new.com" }, CancellationToken.None));
    }

    [TestMethod]
    public async Task UpdateSourceAsync_WhenValid_UpdatesFields()
    {
        var service = CreateService(out var sourceRepository, out _);
        var source = new Source
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            Name = "Old",
            Url = "https://old.com",
            IsActive = true
        };

        sourceRepository.Setup(repo => repo.GetByIdAsync(source.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(source);
        sourceRepository.Setup(repo => repo.UrlExistsForUserAsync(source.UserId, "https://new.com", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        sourceRepository.Setup(repo => repo.UpdateAsync(source, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var response = await service.UpdateSourceAsync(source.Id, new UpdateSourceRequest
        {
            Name = "New",
            Url = "https://new.com",
            IsActive = false
        }, CancellationToken.None);

        Assert.AreEqual("New", response.Name);
        Assert.AreEqual("https://new.com", response.Url);
        Assert.IsFalse(response.IsActive);
    }

    [TestMethod]
    public async Task DeleteSourceAsync_WhenMissing_Throws()
    {
        var service = CreateService(out var sourceRepository, out _);
        sourceRepository.Setup(repo => repo.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Source?)null);

        await TestAssert.ThrowsAsync<KeyNotFoundException>(() =>
            service.DeleteSourceAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [TestMethod]
    public async Task DeleteSourceAsync_WhenExists_Deletes()
    {
        var service = CreateService(out var sourceRepository, out _);
        var sourceId = Guid.NewGuid();
        sourceRepository.Setup(repo => repo.GetByIdAsync(sourceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Source { Id = sourceId });
        sourceRepository.Setup(repo => repo.DeleteAsync(sourceId, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await service.DeleteSourceAsync(sourceId, CancellationToken.None);

        sourceRepository.Verify(repo => repo.DeleteAsync(sourceId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task BulkImportSourcesAsync_WhenUserMissing_Throws()
    {
        var service = CreateService(out _, out var userRepository);
        userRepository.Setup(repo => repo.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((User?)null);

        await TestAssert.ThrowsAsync<KeyNotFoundException>(() =>
            service.BulkImportSourcesAsync(Guid.NewGuid(), new BulkImportSourcesRequest(), CancellationToken.None));
    }

    [TestMethod]
    public async Task BulkImportSourcesAsync_MixedResults_ReturnsCounts()
    {
        var service = CreateService(out var sourceRepository, out var userRepository);
        var userId = Guid.NewGuid();
        userRepository.Setup(repo => repo.GetByIdAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new User { Id = userId });

        sourceRepository.Setup(repo => repo.UrlExistsForUserAsync(userId, "https://dup.com", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        sourceRepository.Setup(repo => repo.UrlExistsForUserAsync(userId, "https://new.com", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        sourceRepository.Setup(repo => repo.AddAsync(It.IsAny<Source>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Source { Id = Guid.NewGuid(), UserId = userId });

        var result = await service.BulkImportSourcesAsync(userId, new BulkImportSourcesRequest
        {
            Sources = new List<BulkImportSourceItem>
            {
                new() { Name = "Dup", Url = "https://dup.com", Category = ContentType.Video },
                new() { Name = "New", Url = "https://new.com", Category = ContentType.Video }
            }
        }, CancellationToken.None);

        Assert.AreEqual(1, result.Imported);
        Assert.AreEqual(1, result.Failed);
        Assert.HasCount(1, result.Errors);
    }
}
