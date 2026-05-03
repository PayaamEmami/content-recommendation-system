using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Crs.Core.Entities;
using Crs.Core.Interfaces;
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
        var ingestionService = new Mock<ISourceIngestionService>(MockBehavior.Strict);

        sourceRepository.Setup(repo => repo.GetActiveSourcesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Source>());

        var provider = BuildProvider(sourceRepository.Object, ingestionService.Object);
        var job = new SourceIngestionJob(provider, NullLogger<SourceIngestionJob>.Instance, NullObservabilityMetrics.Instance);

        await job.ExecuteAsync(CancellationToken.None);

        ingestionService.Verify(
            svc => svc.IngestSourceAsync(It.IsAny<Source>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [TestMethod]
    public async Task ExecuteAsync_DelegatesEachSourceToIngestionService()
    {
        var sourceRepository = new Mock<ISourceRepository>(MockBehavior.Strict);
        var ingestionService = new Mock<ISourceIngestionService>(MockBehavior.Strict);

        var source1 = new Source { Id = Guid.NewGuid(), Name = "A", Url = "https://example.com/a" };
        var source2 = new Source { Id = Guid.NewGuid(), Name = "B", Url = "https://example.com/b" };

        sourceRepository.Setup(repo => repo.GetActiveSourcesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Source> { source1, source2 });

        ingestionService.Setup(svc => svc.IngestSourceAsync(It.IsAny<Source>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SourceIngestionSummary { Success = true, Saved = 3, Embedded = 3 });

        var provider = BuildProvider(sourceRepository.Object, ingestionService.Object);
        var job = new SourceIngestionJob(provider, NullLogger<SourceIngestionJob>.Instance, NullObservabilityMetrics.Instance);

        await job.ExecuteAsync(CancellationToken.None);

        ingestionService.Verify(svc => svc.IngestSourceAsync(source1, It.IsAny<CancellationToken>()), Times.Once);
        ingestionService.Verify(svc => svc.IngestSourceAsync(source2, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task ExecuteAsync_ContinuesWhenSourceThrows()
    {
        var sourceRepository = new Mock<ISourceRepository>(MockBehavior.Strict);
        var ingestionService = new Mock<ISourceIngestionService>(MockBehavior.Strict);

        var source1 = new Source { Id = Guid.NewGuid(), Name = "A", Url = "https://example.com/a" };
        var source2 = new Source { Id = Guid.NewGuid(), Name = "B", Url = "https://example.com/b" };

        sourceRepository.Setup(repo => repo.GetActiveSourcesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Source> { source1, source2 });

        ingestionService.Setup(svc => svc.IngestSourceAsync(source1, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        ingestionService.Setup(svc => svc.IngestSourceAsync(source2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SourceIngestionSummary { Success = true, Saved = 1, Embedded = 1 });

        var provider = BuildProvider(sourceRepository.Object, ingestionService.Object);
        var job = new SourceIngestionJob(provider, NullLogger<SourceIngestionJob>.Instance, NullObservabilityMetrics.Instance);

        await job.ExecuteAsync(CancellationToken.None);

        ingestionService.Verify(svc => svc.IngestSourceAsync(source2, It.IsAny<CancellationToken>()), Times.Once);
    }

    private static ServiceProvider BuildProvider(
        ISourceRepository sourceRepository,
        ISourceIngestionService ingestionService)
    {
        var services = new ServiceCollection();
        services.AddSingleton(sourceRepository);
        services.AddSingleton(ingestionService);
        return services.BuildServiceProvider();
    }
}
