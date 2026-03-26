using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Crs.Core.Entities;
using Crs.Core.Interfaces;
using Crs.Core.Models;
using Crs.Core.Observability;
using Crs.Jobs.Jobs;

namespace Crs.Tests.Unit.Jobs;

[TestClass]
public sealed class XIngestionJobTests
{
    [TestMethod]
    public async Task ExecuteAsync_WhenNoConnections_ReturnsEarly()
    {
        var connectionRepository = new Mock<IXConnectionRepository>(MockBehavior.Strict);
        var selectedRepository = new Mock<IXSelectedAccountRepository>(MockBehavior.Strict);
        var postRepository = new Mock<IXPostRepository>(MockBehavior.Strict);
        var xApiClient = new Mock<IXApiClient>(MockBehavior.Strict);
        var dataProtectionProvider = DataProtectionProvider.Create("crs-tests");

        connectionRepository.Setup(repo => repo.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<XConnection>());

        var provider = BuildProvider(
            connectionRepository.Object,
            selectedRepository.Object,
            postRepository.Object,
            xApiClient.Object,
            dataProtectionProvider);

        var job = new XIngestionJob(provider, NullLogger<XIngestionJob>.Instance, NullObservabilityMetrics.Instance);

        await job.ExecuteAsync(CancellationToken.None);

        selectedRepository.VerifyNoOtherCalls();
        postRepository.VerifyNoOtherCalls();
        xApiClient.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task ExecuteAsync_WhenNoSelectedAccounts_SkipsPosts()
    {
        var connectionRepository = new Mock<IXConnectionRepository>(MockBehavior.Strict);
        var selectedRepository = new Mock<IXSelectedAccountRepository>(MockBehavior.Strict);
        var postRepository = new Mock<IXPostRepository>(MockBehavior.Strict);
        var xApiClient = new Mock<IXApiClient>(MockBehavior.Strict);

        var dataProtectionProvider = DataProtectionProvider.Create("crs-tests");
        var protector = dataProtectionProvider.CreateProtector("Crs.X.Tokens");
        var connection = new XConnection
        {
            UserId = Guid.NewGuid(),
            XUserId = "x-user",
            AccessTokenEncrypted = protector.Protect("access"),
            RefreshTokenEncrypted = protector.Protect("refresh")
        };

        connectionRepository.Setup(repo => repo.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<XConnection> { connection });
        selectedRepository.Setup(repo => repo.GetByUserIdAsync(connection.UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<XSelectedAccount>());

        var provider = BuildProvider(
            connectionRepository.Object,
            selectedRepository.Object,
            postRepository.Object,
            xApiClient.Object,
            dataProtectionProvider);

        var job = new XIngestionJob(provider, NullLogger<XIngestionJob>.Instance, NullObservabilityMetrics.Instance);

        await job.ExecuteAsync(CancellationToken.None);

        xApiClient.Verify(client => client.GetRecentPostsAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<DateTime?>(),
            It.IsAny<CancellationToken>()), Times.Never);
        postRepository.Verify(repo => repo.UpsertRangeAsync(It.IsAny<List<XPost>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static ServiceProvider BuildProvider(
        IXConnectionRepository connectionRepository,
        IXSelectedAccountRepository selectedAccountRepository,
        IXPostRepository postRepository,
        IXApiClient xApiClient,
        IDataProtectionProvider dataProtectionProvider)
    {
        var services = new ServiceCollection();
        services.AddSingleton(connectionRepository);
        services.AddSingleton(selectedAccountRepository);
        services.AddSingleton(postRepository);
        services.AddSingleton(xApiClient);
        services.AddSingleton(dataProtectionProvider);
        return services.BuildServiceProvider();
    }
}
