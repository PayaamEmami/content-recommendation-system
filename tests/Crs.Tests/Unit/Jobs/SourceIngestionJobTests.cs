using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Crs.Core.Entities;
using Crs.Core.Interfaces;
using Crs.Core.Models;
using Crs.Core.Observability;
using Crs.Jobs.Jobs;
using Crs.Llm.Models;
using Crs.Llm.Services;

namespace Crs.Tests.Unit.Jobs;

[TestClass]
public sealed class SourceIngestionJobTests
{
    [TestMethod]
    public async Task ExecuteAsync_WhenNoActiveSources_ReturnsEarly()
    {
        var sourceRepository = new Mock<ISourceRepository>(MockBehavior.Strict);
        var contentRepository = new Mock<IContentRepository>(MockBehavior.Strict);
        var ingestionAgent = new Mock<IIngestionAgent>(MockBehavior.Strict);
        var embeddingService = new Mock<IEmbeddingService>(MockBehavior.Strict);
        var vectorStore = new Mock<IVectorStore>(MockBehavior.Strict);

        sourceRepository.Setup(repo => repo.GetActiveSourcesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Source>());

        var provider = BuildProvider(
            sourceRepository.Object,
            contentRepository.Object,
            ingestionAgent.Object,
            embeddingService.Object,
            vectorStore.Object);

        var job = new SourceIngestionJob(provider, NullLogger<SourceIngestionJob>.Instance, NullObservabilityMetrics.Instance);

        await job.ExecuteAsync(CancellationToken.None);

        ingestionAgent.VerifyNoOtherCalls();
        contentRepository.VerifyNoOtherCalls();
        vectorStore.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task ExecuteAsync_WhenIngestionFails_SkipsContentSave()
    {
        var sourceRepository = new Mock<ISourceRepository>(MockBehavior.Strict);
        var contentRepository = new Mock<IContentRepository>(MockBehavior.Strict);
        var ingestionAgent = new Mock<IIngestionAgent>(MockBehavior.Strict);
        var embeddingService = new Mock<IEmbeddingService>(MockBehavior.Strict);
        var vectorStore = new Mock<IVectorStore>(MockBehavior.Strict);

        var source = new Source { Id = Guid.NewGuid(), Name = "Test", Url = "https://example.com" };
        sourceRepository.Setup(repo => repo.GetActiveSourcesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Source> { source });

        ingestionAgent.Setup(agent => agent.IngestFromUrlAsync(source.Url, source.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IngestionResult { Success = false, ErrorMessage = "failed" });

        var provider = BuildProvider(
            sourceRepository.Object,
            contentRepository.Object,
            ingestionAgent.Object,
            embeddingService.Object,
            vectorStore.Object);

        var job = new SourceIngestionJob(provider, NullLogger<SourceIngestionJob>.Instance, NullObservabilityMetrics.Instance);

        await job.ExecuteAsync(CancellationToken.None);

        contentRepository.Verify(repo => repo.AddAsync(It.IsAny<Content>(), It.IsAny<CancellationToken>()), Times.Never);
        vectorStore.Verify(store => store.UpsertDocumentsAsync(It.IsAny<IEnumerable<ContentDocument>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static ServiceProvider BuildProvider(
        ISourceRepository sourceRepository,
        IContentRepository contentRepository,
        IIngestionAgent ingestionAgent,
        IEmbeddingService embeddingService,
        IVectorStore vectorStore)
    {
        var services = new ServiceCollection();
        services.AddSingleton(sourceRepository);
        services.AddSingleton(contentRepository);
        services.AddSingleton(ingestionAgent);
        services.AddSingleton(embeddingService);
        services.AddSingleton(vectorStore);
        return services.BuildServiceProvider();
    }
}
