using System.Net;
using System.Text.Json;
using Blazored.LocalStorage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Crs.Core.Enums;
using Crs.Web.Services;

namespace Crs.Tests.Unit.Web;

[TestClass]
public sealed class FeedServiceTests
{
    [TestMethod]
    public async Task GetFeedAsync_WhenUnauthenticated_ReturnsEmptyAndNoRequests()
    {
        var localStorage = new Mock<ILocalStorageService>(MockBehavior.Strict);
        localStorage.Setup(store => store.GetItemAsync<AuthState>(It.IsAny<string>()))
            .ReturnsAsync((AuthState?)null);

        var authService = CreateAuthService(localStorage);
        var handler = new HttpTestHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var service = new FeedService(new HttpClient(handler) { BaseAddress = new Uri("https://example.com") }, authService, NullLogger<FeedService>.Instance);

        var result = await service.GetFeedAsync();

        Assert.HasCount(0, result);
        Assert.HasCount(0, handler.Requests);
    }

    [TestMethod]
    public async Task GetFeedAsync_WhenSuccess_ReturnsSortedContent()
    {
        var localStorage = new Mock<ILocalStorageService>(MockBehavior.Strict);
        localStorage.Setup(store => store.GetItemAsync<AuthState>(It.IsAny<string>()))
            .ReturnsAsync(new AuthState
            {
                IsAuthenticated = true,
                AccessToken = "access",
                ExpiresAt = DateTime.UtcNow.AddMinutes(5)
            });

        var authService = CreateAuthService(localStorage);
        await authService.InitializeAsync();

        var payload = new List<FeedRecommendationsResponse>
        {
            new()
            {
                FeedType = ContentType.Video,
                Recommendations = new List<RecommendationItemResponse>
                {
                    new()
                    {
                        Content = new ContentItemResponse
                        {
                            Id = Guid.NewGuid(),
                            Title = "Older",
                            Url = "https://example.com/old",
                            Type = ContentType.Video,
                            CreatedAt = DateTime.UtcNow.AddDays(-2),
                            PublishedDate = DateTime.UtcNow.AddDays(-2)
                        }
                    },
                    new()
                    {
                        Content = new ContentItemResponse
                        {
                            Id = Guid.NewGuid(),
                            Title = "Newer",
                            Url = "https://example.com/new",
                            Type = ContentType.Video,
                            CreatedAt = DateTime.UtcNow.AddDays(-1),
                            PublishedDate = DateTime.UtcNow.AddDays(-1)
                        }
                    }
                }
            }
        };

        var handler = new HttpTestHandler(_ =>
        {
            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json)
            };
        });

        var service = new FeedService(new HttpClient(handler) { BaseAddress = new Uri("https://example.com") }, authService, NullLogger<FeedService>.Instance);

        var result = await service.GetFeedAsync();

        Assert.HasCount(2, result);
        Assert.AreEqual("Newer", result[0].Title);
    }

    [TestMethod]
    public async Task GetVoteHistoryAsync_WhenSuccess_ReturnsMappedHistory()
    {
        var localStorage = new Mock<ILocalStorageService>(MockBehavior.Strict);
        localStorage.Setup(store => store.GetItemAsync<AuthState>(It.IsAny<string>()))
            .ReturnsAsync(new AuthState
            {
                IsAuthenticated = true,
                AccessToken = "access",
                ExpiresAt = DateTime.UtcNow.AddMinutes(5)
            });

        var authService = CreateAuthService(localStorage);
        await authService.InitializeAsync();

        var contentId = Guid.NewGuid();
        var payload = new List<VoteHistoryApiResponse>
        {
            new()
            {
                Id = Guid.NewGuid(),
                UserId = Guid.NewGuid(),
                ContentId = contentId,
                VoteType = VoteType.Upvote,
                CreatedAt = DateTime.UtcNow.AddDays(-1),
                UpdatedAt = DateTime.UtcNow,
                Title = "History item",
                Description = "A description",
                Url = "https://example.com/history",
                Type = ContentType.BlogPost,
                ContentDate = DateTime.UtcNow.AddDays(-2)
            }
        };

        var handler = new HttpTestHandler(_ =>
        {
            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json)
            };
        });

        var service = new FeedService(new HttpClient(handler) { BaseAddress = new Uri("https://example.com") }, authService, NullLogger<FeedService>.Instance);

        var result = await service.GetVoteHistoryAsync();

        Assert.HasCount(1, result);
        Assert.AreEqual(contentId, result[0].ContentId);
        Assert.AreEqual(VoteType.Upvote, result[0].VoteType);
        Assert.AreEqual("History item", result[0].Title);
        Assert.AreEqual("/api/v1/users/me/vote-history", handler.Requests.Single().RequestUri!.AbsolutePath);
    }

    private static AuthService CreateAuthService(Mock<ILocalStorageService> localStorage)
    {
        var configuration = new ConfigurationBuilder().Build();
        var authHandler = new HttpTestHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var authHttpClient = new HttpClient(authHandler) { BaseAddress = new Uri("https://example.com") };

        return new AuthService(authHttpClient, localStorage.Object, configuration, NullLogger<AuthService>.Instance);
    }
}
