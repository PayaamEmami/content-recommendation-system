using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Crs.Core.Entities;
using Crs.Core.Interfaces;
using Crs.Core.Models;
using Crs.Core.Observability;
using Crs.Infrastructure.Configuration;
using Crs.Jobs.Jobs;

namespace Crs.Tests.Unit.Jobs;

[TestClass]
public sealed class LocalVectorIndexSyncJobTests
{
    [TestMethod]
    public async Task ExecuteAsync_WhenIndexIsAlreadySynchronized_DoesNothing()
    {
        var contentRepository = new Mock<IContentRepository>(MockBehavior.Strict);
        var embeddingService = new Mock<IEmbeddingService>(MockBehavior.Strict);
        var vectorStore = new Mock<IVectorStore>(MockBehavior.Strict);

        var content = new List<Content>
        {
            new BlogPost { Id = Guid.NewGuid(), Title = "A", Url = "https://example.com/a", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
            new Video { Id = Guid.NewGuid(), Title = "B", Url = "https://example.com/b", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow }
        };

        contentRepository.Setup(repo => repo.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(content);
        vectorStore.Setup(store => store.GetAllDocumentIdsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(content.Select(item => item.Id).ToHashSet());

        var provider = BuildProvider(contentRepository.Object, embeddingService.Object, vectorStore.Object);
        var job = new LocalVectorIndexSyncJob(
            provider,
            Options.Create(new OpenSearchSettings { Mode = OpenSearchMode.Local }),
            NullLogger<LocalVectorIndexSyncJob>.Instance,
            NullObservabilityMetrics.Instance);

        await job.ExecuteAsync(CancellationToken.None);

        embeddingService.VerifyNoOtherCalls();
        vectorStore.Verify(store => store.GetAllDocumentIdsAsync(It.IsAny<CancellationToken>()), Times.Once);
        vectorStore.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task ExecuteAsync_WhenIndexIsMissingAndHasOrphans_ReconcilesIt()
    {
        var contentRepository = new Mock<IContentRepository>(MockBehavior.Strict);
        var embeddingService = new Mock<IEmbeddingService>(MockBehavior.Strict);
        var vectorStore = new Mock<IVectorStore>(MockBehavior.Strict);

        var retained = new BlogPost { Id = Guid.NewGuid(), Title = "Retained", Url = "https://example.com/retained", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        var missing = new Video { Id = Guid.NewGuid(), Title = "Missing", Url = "https://example.com/missing", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        var orphanedId = Guid.NewGuid();

        contentRepository.Setup(repo => repo.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Content[] { retained, missing });
        vectorStore.Setup(store => store.GetAllDocumentIdsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<Guid> { retained.Id, orphanedId });
        embeddingService.Setup(service => service.GenerateEmbeddingsAsync(
                It.Is<List<string>>(texts => texts.Count == 1 && texts[0].Contains("Missing")),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<float[]> { new[] { 0.42f } });
        vectorStore.Setup(store => store.UpsertDocumentsAsync(
                It.Is<IEnumerable<ContentDocument>>(docs =>
                    docs.Count() == 1 &&
                    docs.Single().Id == missing.Id &&
                    docs.Single().Type == missing.Type),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        vectorStore.Setup(store => store.DeleteDocumentAsync(orphanedId, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var provider = BuildProvider(contentRepository.Object, embeddingService.Object, vectorStore.Object);
        var job = new LocalVectorIndexSyncJob(
            provider,
            Options.Create(new OpenSearchSettings { Mode = OpenSearchMode.Local }),
            NullLogger<LocalVectorIndexSyncJob>.Instance,
            NullObservabilityMetrics.Instance);

        await job.ExecuteAsync(CancellationToken.None);

        vectorStore.VerifyAll();
        embeddingService.VerifyAll();
    }

    [TestMethod]
    public async Task ExecuteAsync_WhenModeIsNotLocal_SkipsWork()
    {
        var contentRepository = new Mock<IContentRepository>(MockBehavior.Strict);
        var embeddingService = new Mock<IEmbeddingService>(MockBehavior.Strict);
        var vectorStore = new Mock<IVectorStore>(MockBehavior.Strict);

        var provider = BuildProvider(contentRepository.Object, embeddingService.Object, vectorStore.Object);
        var job = new LocalVectorIndexSyncJob(
            provider,
            Options.Create(new OpenSearchSettings { Mode = OpenSearchMode.Aws }),
            NullLogger<LocalVectorIndexSyncJob>.Instance,
            NullObservabilityMetrics.Instance);

        await job.ExecuteAsync(CancellationToken.None);

        contentRepository.VerifyNoOtherCalls();
        embeddingService.VerifyNoOtherCalls();
        vectorStore.VerifyNoOtherCalls();
    }

    private static ServiceProvider BuildProvider(
        IContentRepository contentRepository,
        IEmbeddingService embeddingService,
        IVectorStore vectorStore)
    {
        var services = new ServiceCollection();
        services.AddSingleton(contentRepository);
        services.AddSingleton(embeddingService);
        services.AddSingleton(vectorStore);
        return services.BuildServiceProvider();
    }
}
