using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Crs.Core.Entities;
using Crs.Core.Enums;
using Crs.Core.Interfaces;
using Crs.Core.Observability;
using Crs.Jobs.Jobs;
using Crs.Recommendation.Services;
using RecommendationEntity = Crs.Core.Entities.Recommendation;

namespace Crs.Tests.Unit.Jobs;

[TestClass]
public sealed class DailyFeedGenerationJobTests
{
    [TestMethod]
    public async Task ExecuteAsync_WhenNoUsers_ReturnsEarly()
    {
        var userRepository = new Mock<IUserRepository>(MockBehavior.Strict);
        var feedGenerator = new Mock<IFeedGenerator>(MockBehavior.Strict);

        userRepository.Setup(repo => repo.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<User>());

        var provider = BuildProvider(userRepository.Object, feedGenerator.Object);
        var job = new DailyFeedGenerationJob(provider, NullLogger<DailyFeedGenerationJob>.Instance, NullObservabilityMetrics.Instance);

        await job.ExecuteAsync(CancellationToken.None);

        feedGenerator.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task ExecuteAsync_WhenUsersExist_GeneratesFeeds()
    {
        var userRepository = new Mock<IUserRepository>(MockBehavior.Strict);
        var feedGenerator = new Mock<IFeedGenerator>(MockBehavior.Strict);
        var user = new User { Id = Guid.NewGuid(), Email = "user@example.com" };

        userRepository.Setup(repo => repo.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { user });
        feedGenerator.Setup(service => service.GenerateFeedAsync(
                user.Id,
                It.IsAny<ContentType>(),
                It.IsAny<DateOnly>(),
                5,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<RecommendationEntity> { new() });

        var provider = BuildProvider(userRepository.Object, feedGenerator.Object);
        var job = new DailyFeedGenerationJob(provider, NullLogger<DailyFeedGenerationJob>.Instance, NullObservabilityMetrics.Instance);

        await job.ExecuteAsync(CancellationToken.None);

        feedGenerator.Verify(service => service.GenerateFeedAsync(
            user.Id,
            It.IsAny<ContentType>(),
            It.IsAny<DateOnly>(),
            5,
            It.IsAny<CancellationToken>()), Times.Exactly(Enum.GetValues<ContentType>().Length));
    }

    [TestMethod]
    public async Task ExecuteForUserAsync_UsesProvidedDate()
    {
        var userRepository = new Mock<IUserRepository>(MockBehavior.Strict);
        var feedGenerator = new Mock<IFeedGenerator>(MockBehavior.Strict);
        var userId = Guid.NewGuid();
        var targetDate = new DateOnly(2025, 1, 1);

        feedGenerator.Setup(service => service.GenerateAllFeedsAsync(userId, targetDate, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<RecommendationEntity>());

        var provider = BuildProvider(userRepository.Object, feedGenerator.Object);
        var job = new DailyFeedGenerationJob(provider, NullLogger<DailyFeedGenerationJob>.Instance, NullObservabilityMetrics.Instance);

        await job.ExecuteForUserAsync(userId, targetDate, CancellationToken.None);

        feedGenerator.VerifyAll();
    }

    private static ServiceProvider BuildProvider(IUserRepository userRepository, IFeedGenerator feedGenerator)
    {
        var services = new ServiceCollection();
        services.AddSingleton(userRepository);
        services.AddSingleton(feedGenerator);
        return services.BuildServiceProvider();
    }
}
