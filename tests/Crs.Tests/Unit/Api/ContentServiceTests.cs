using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Crs.Api.DTOs.Content.Requests;
using Crs.Api.Services;
using Crs.Core.Entities;
using Crs.Core.Enums;
using Crs.Core.Interfaces;

namespace Crs.Tests.Unit.Api;

[TestClass]
public sealed class ContentServiceTests
{
    private static ContentService CreateService(
        out Mock<IContentRepository> contentRepository,
        out Mock<ISourceRepository> sourceRepository)
    {
        contentRepository = new Mock<IContentRepository>(MockBehavior.Strict);
        sourceRepository = new Mock<ISourceRepository>(MockBehavior.Strict);
        return new ContentService(contentRepository.Object, sourceRepository.Object, NullLogger<ContentService>.Instance);
    }

    [TestMethod]
    public async Task GetContentAsync_WhenTypeProvided_DelegatesToPagedRepo()
    {
        var service = CreateService(out var contentRepository, out _);
        var newest = new Video { Id = Guid.NewGuid(), Title = "New", CreatedAt = DateTime.UtcNow.AddDays(1) };

        contentRepository.Setup(repo => repo.GetPagedAsync(
                1,
                1,
                ContentType.Video,
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(((IReadOnlyList<Content>)new List<Content> { newest }, 2));

        var response = await service.GetContentAsync(1, 1, ContentType.Video, null, CancellationToken.None);

        Assert.AreEqual(2, response.TotalCount);
        Assert.HasCount(1, response.Items);
        Assert.AreEqual(newest.Id, response.Items[0].Id);
    }

    [TestMethod]
    public async Task GetContentAsync_WhenSourceFilterApplied_PassesSourceIdsToRepo()
    {
        var service = CreateService(out var contentRepository, out _);
        var sourceId = Guid.NewGuid();
        var item = new BlogPost
        {
            Id = Guid.NewGuid(),
            SourceId = sourceId,
            Source = new Source { Id = sourceId },
            CreatedAt = DateTime.UtcNow
        };

        contentRepository.Setup(repo => repo.GetPagedAsync(
                1,
                10,
                null,
                It.Is<IReadOnlyCollection<Guid>?>(ids => ids != null && ids.Contains(sourceId)),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(((IReadOnlyList<Content>)new List<Content> { item }, 1));

        var response = await service.GetContentAsync(1, 10, null, new List<Guid> { sourceId }, CancellationToken.None);

        Assert.AreEqual(1, response.TotalCount);
        Assert.HasCount(1, response.Items);
        Assert.AreEqual(sourceId, response.Items[0].SourceInfo?.Id);
    }

    [TestMethod]
    public async Task CreateContentAsync_WhenSourceMissing_Throws()
    {
        var service = CreateService(out _, out var sourceRepository);
        var sourceId = Guid.NewGuid();
        sourceRepository.Setup(repo => repo.GetByIdAsync(sourceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Source?)null);

        var request = new CreateContentRequest
        {
            Title = "Test",
            Url = "https://example.com",
            ContentType = ContentType.Video,
            SourceId = sourceId
        };

        await TestAssert.ThrowsAsync<ArgumentException>(() =>
            service.CreateContentAsync(request, CancellationToken.None));
    }

    [TestMethod]
    public async Task CreateContentAsync_WhenTypeInvalid_Throws()
    {
        var service = CreateService(out _, out var sourceRepository);
        sourceRepository.Setup(repo => repo.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Source { Id = Guid.NewGuid() });

        var request = new CreateContentRequest
        {
            Title = "Test",
            Url = "https://example.com",
            ContentType = (ContentType)99
        };

        await TestAssert.ThrowsAsync<ArgumentException>(() =>
            service.CreateContentAsync(request, CancellationToken.None));
    }

    [TestMethod]
    public async Task CreateContentAsync_WhenValid_CreatesContent()
    {
        var service = CreateService(out var contentRepository, out _);
        contentRepository.Setup(repo => repo.CreateAsync(It.IsAny<Content>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Content content, CancellationToken _) => content);

        var response = await service.CreateContentAsync(new CreateContentRequest
        {
            Title = "Title",
            Url = "https://example.com",
            ContentType = ContentType.Paper
        }, CancellationToken.None);

        Assert.AreEqual("Title", response.Title);
        Assert.AreEqual(ContentType.Paper, response.Type);
        contentRepository.Verify(repo => repo.CreateAsync(It.IsAny<Content>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task UpdateContentAsync_WhenMissing_Throws()
    {
        var service = CreateService(out var contentRepository, out _);
        contentRepository.Setup(repo => repo.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Content?)null);

        await TestAssert.ThrowsAsync<KeyNotFoundException>(() =>
            service.UpdateContentAsync(Guid.NewGuid(), new UpdateContentRequest(), CancellationToken.None));
    }

    [TestMethod]
    public async Task UpdateContentAsync_WhenSourceMissing_Throws()
    {
        var service = CreateService(out var contentRepository, out var sourceRepository);
        var content = new Paper { Id = Guid.NewGuid(), Title = "Old" };
        contentRepository.Setup(repo => repo.GetByIdAsync(content.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(content);
        sourceRepository.Setup(repo => repo.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Source?)null);

        await TestAssert.ThrowsAsync<ArgumentException>(() =>
            service.UpdateContentAsync(content.Id, new UpdateContentRequest { SourceId = Guid.NewGuid() }, CancellationToken.None));
    }

    [TestMethod]
    public async Task UpdateContentAsync_WhenValid_UpdatesAndReturns()
    {
        var service = CreateService(out var contentRepository, out var sourceRepository);
        var content = new Paper { Id = Guid.NewGuid(), Title = "Old", Url = "https://old.com" };
        contentRepository.Setup(repo => repo.GetByIdAsync(content.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(content);
        sourceRepository.Setup(repo => repo.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Source { Id = Guid.NewGuid() });
        contentRepository.Setup(repo => repo.UpdateAsync(content, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Content contentToUpdate, CancellationToken _) => contentToUpdate);

        var response = await service.UpdateContentAsync(content.Id, new UpdateContentRequest
        {
            Title = "New",
            Url = "https://new.com",
            SourceId = Guid.NewGuid()
        }, CancellationToken.None);

        Assert.AreEqual("New", response.Title);
        Assert.AreEqual("https://new.com", response.Url);
    }

    [TestMethod]
    public async Task DeleteContentAsync_WhenMissing_Throws()
    {
        var service = CreateService(out var contentRepository, out _);
        contentRepository.Setup(repo => repo.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Content?)null);

        await TestAssert.ThrowsAsync<KeyNotFoundException>(() =>
            service.DeleteContentAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [TestMethod]
    public async Task DeleteContentAsync_WhenExists_Deletes()
    {
        var service = CreateService(out var contentRepository, out _);
        var contentId = Guid.NewGuid();
        contentRepository.Setup(repo => repo.GetByIdAsync(contentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BlogPost { Id = contentId });
        contentRepository.Setup(repo => repo.DeleteAsync(contentId, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await service.DeleteContentAsync(contentId, CancellationToken.None);

        contentRepository.Verify(repo => repo.DeleteAsync(contentId, It.IsAny<CancellationToken>()), Times.Once);
    }
}
