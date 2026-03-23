using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using Crs.Infrastructure.Configuration;
using Crs.Infrastructure.Services;

namespace Crs.Tests.Unit.Infrastructure;

[TestClass]
public class XApiClientTests
{
    private Mock<HttpMessageHandler> _mockHttpMessageHandler = null!;
    private HttpClient _httpClient = null!;
    private XApiSettings _settings = null!;
    private XApiClient _client = null!;

    [TestInitialize]
    public void Setup()
    {
        _mockHttpMessageHandler = new Mock<HttpMessageHandler>();
        _httpClient = new HttpClient(_mockHttpMessageHandler.Object);

        _settings = new XApiSettings
        {
            ClientId = "test-client-id",
            ClientSecret = "test-client-secret",
            RedirectUri = "https://localhost/callback",
            BaseUrl = "https://api.x.com",
            TokenUrl = "https://api.x.com/2/oauth2/token"
        };

        var options = Options.Create(_settings);
        _client = new XApiClient(_httpClient, options, NullLogger<XApiClient>.Instance);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _httpClient?.Dispose();
    }

    [TestMethod]
    public async Task ExchangeCodeAsync_SuccessfulExchange_ReturnsTokens()
    {
        // Arrange
        var code = "auth-code";
        var codeVerifier = "code-verifier";
        var redirectUri = "https://localhost/callback";

        var responseJson = @"{
            ""access_token"": ""test-access-token"",
            ""refresh_token"": ""test-refresh-token"",
            ""expires_in"": 7200,
            ""scope"": ""tweet.read users.read follows.read offline.access"",
            ""token_type"": ""bearer""
        }";

        _mockHttpMessageHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(req =>
                    req.Method == HttpMethod.Post &&
                    req.RequestUri!.ToString().Contains("oauth2/token")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
            });

        // Act
        var result = await _client.ExchangeCodeAsync(code, codeVerifier, redirectUri);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("test-access-token", result.AccessToken);
        Assert.AreEqual("test-refresh-token", result.RefreshToken);
        Assert.AreEqual(7200, result.ExpiresIn);
        Assert.AreEqual("tweet.read users.read follows.read offline.access", result.Scope);
        Assert.AreEqual("bearer", result.TokenType);
    }

    [TestMethod]
    public async Task ExchangeCodeAsync_ApiError_ThrowsHttpRequestException()
    {
        // Arrange
        var code = "invalid-code";
        var codeVerifier = "code-verifier";
        var redirectUri = "https://localhost/callback";

        _mockHttpMessageHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.BadRequest,
                Content = new StringContent("{\"error\": \"invalid_grant\"}")
            });

        // Act & Assert
        try
        {
            await _client.ExchangeCodeAsync(code, codeVerifier, redirectUri);
            Assert.Fail("Expected HttpRequestException to be thrown");
        }
        catch (HttpRequestException)
        {
            // Expected
        }
    }

    [TestMethod]
    public async Task RefreshTokenAsync_SuccessfulRefresh_ReturnsNewTokens()
    {
        // Arrange
        var refreshToken = "old-refresh-token";

        var responseJson = @"{
            ""access_token"": ""new-access-token"",
            ""refresh_token"": ""new-refresh-token"",
            ""expires_in"": 7200,
            ""scope"": ""tweet.read users.read follows.read offline.access"",
            ""token_type"": ""bearer""
        }";

        _mockHttpMessageHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(req =>
                    req.Method == HttpMethod.Post &&
                    req.RequestUri!.ToString().Contains("oauth2/token")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
            });

        // Act
        var result = await _client.RefreshTokenAsync(refreshToken);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("new-access-token", result.AccessToken);
        Assert.AreEqual("new-refresh-token", result.RefreshToken);
        Assert.AreEqual(7200, result.ExpiresIn);
    }

    [TestMethod]
    public async Task GetCurrentUserAsync_SuccessfulRequest_ReturnsUserProfile()
    {
        // Arrange
        var accessToken = "test-access-token";

        var responseJson = @"{
            ""data"": {
                ""id"": ""123456"",
                ""username"": ""testuser"",
                ""name"": ""Test User"",
                ""profile_image_url"": ""https://pbs.twimg.com/profile_images/123456/avatar.jpg""
            }
        }";

        _mockHttpMessageHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(req =>
                    req.Method == HttpMethod.Get &&
                    req.RequestUri!.ToString().Contains("/users/me")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
            });

        // Act
        var result = await _client.GetCurrentUserAsync(accessToken);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("123456", result.XUserId);
        Assert.AreEqual("testuser", result.Handle);
        Assert.AreEqual("Test User", result.DisplayName);
        Assert.Contains("avatar.jpg", result.ProfileImageUrl!);
    }

    [TestMethod]
    public async Task GetCurrentUserAsync_NoData_ReturnsEmptyProfile()
    {
        // Arrange
        var accessToken = "test-access-token";

        var responseJson = @"{}";

        _mockHttpMessageHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
            });

        // Act
        var result = await _client.GetCurrentUserAsync(accessToken);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(string.Empty, result.XUserId);
    }

    [TestMethod]
    public async Task GetCurrentUserAsync_WhenProfileImageFieldIsForbidden_FallsBackToPlainUsersMe()
    {
        // Arrange
        var accessToken = "test-access-token";
        var fallbackResponseJson = @"{
            ""data"": {
                ""id"": ""123456"",
                ""username"": ""testuser"",
                ""name"": ""Test User""
            }
        }";

        var requestUris = new List<string>();
        _mockHttpMessageHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage req, CancellationToken ct) =>
            {
                requestUris.Add(req.RequestUri!.ToString());

                if (req.RequestUri!.ToString().Contains("user.fields=profile_image_url"))
                {
                    return new HttpResponseMessage
                    {
                        StatusCode = HttpStatusCode.Forbidden,
                        Content = new StringContent("{\"title\":\"Forbidden\",\"detail\":\"Forbidden\"}", Encoding.UTF8, "application/json")
                    };
                }

                return new HttpResponseMessage
                {
                    StatusCode = HttpStatusCode.OK,
                    Content = new StringContent(fallbackResponseJson, Encoding.UTF8, "application/json")
                };
            });

        // Act
        var result = await _client.GetCurrentUserAsync(accessToken);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("123456", result.XUserId);
        Assert.AreEqual("testuser", result.Handle);
        Assert.HasCount(2, requestUris);
        Assert.IsTrue(requestUris[0].Contains("/2/users/me?user.fields=profile_image_url", StringComparison.Ordinal));
        Assert.IsTrue(requestUris[1].EndsWith("/2/users/me", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task GetFollowedAccountsAsync_SinglePage_ReturnsAccounts()
    {
        // Arrange
        var accessToken = "test-access-token";
        var userId = "123456";

        var responseJson = @"{
            ""data"": [
                {
                    ""id"": ""111"",
                    ""username"": ""user1"",
                    ""name"": ""User One"",
                    ""profile_image_url"": ""https://example.com/user1.jpg""
                },
                {
                    ""id"": ""222"",
                    ""username"": ""user2"",
                    ""name"": ""User Two"",
                    ""profile_image_url"": ""https://example.com/user2.jpg""
                }
            ],
            ""meta"": {}
        }";

        _mockHttpMessageHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(req =>
                    req.Method == HttpMethod.Get &&
                    req.RequestUri!.ToString().Contains($"/users/{userId}/following")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
            });

        // Act
        var result = await _client.GetFollowedAccountsAsync(accessToken, userId);

        // Assert
        Assert.IsNotNull(result);
        Assert.HasCount(2, result);
        Assert.AreEqual("111", result[0].XUserId);
        Assert.AreEqual("user1", result[0].Handle);
        Assert.AreEqual("User One", result[0].DisplayName);
        Assert.AreEqual("222", result[1].XUserId);
    }

    [TestMethod]
    public async Task GetFollowedAccountsAsync_MultiplePagesWithPagination_ReturnsAllAccounts()
    {
        // Arrange
        var accessToken = "test-access-token";
        var userId = "123456";

        var page1Json = @"{
            ""data"": [
                { ""id"": ""111"", ""username"": ""user1"", ""name"": ""User One"" }
            ],
            ""meta"": { ""next_token"": ""token123"" }
        }";

        var page2Json = @"{
            ""data"": [
                { ""id"": ""222"", ""username"": ""user2"", ""name"": ""User Two"" }
            ],
            ""meta"": {}
        }";

        var callCount = 0;
        _mockHttpMessageHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(req =>
                    req.Method == HttpMethod.Get &&
                    req.RequestUri!.ToString().Contains($"/users/{userId}/following")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() =>
            {
                callCount++;
                var json = callCount == 1 ? page1Json : page2Json;
                return new HttpResponseMessage
                {
                    StatusCode = HttpStatusCode.OK,
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
            });

        // Act
        var result = await _client.GetFollowedAccountsAsync(accessToken, userId);

        // Assert
        Assert.IsNotNull(result);
        Assert.HasCount(2, result);
        Assert.AreEqual(2, callCount); // Should have made 2 requests
        Assert.AreEqual("111", result[0].XUserId);
        Assert.AreEqual("222", result[1].XUserId);
    }

    [TestMethod]
    public async Task GetRecentPostsAsync_SinglePage_ReturnsPosts()
    {
        // Arrange
        var accessToken = "test-access-token";
        var userId = "123456";

        var responseJson = @"{
            ""data"": [
                {
                    ""id"": ""1001"",
                    ""text"": ""Test post content"",
                    ""created_at"": ""2025-01-29T12:00:00.000Z"",
                    ""author_id"": ""123456"",
                    ""public_metrics"": {
                        ""like_count"": 10,
                        ""reply_count"": 2,
                        ""retweet_count"": 5,
                        ""quote_count"": 1
                    }
                }
            ],
            ""includes"": {
                ""users"": [
                    {
                        ""id"": ""123456"",
                        ""username"": ""testuser"",
                        ""name"": ""Test User"",
                        ""profile_image_url"": ""https://example.com/avatar.jpg""
                    }
                ]
            },
            ""meta"": {}
        }";

        _mockHttpMessageHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(req =>
                    req.Method == HttpMethod.Get &&
                    req.RequestUri!.ToString().Contains($"/users/{userId}/tweets")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
            });

        // Act
        var result = await _client.GetRecentPostsAsync(accessToken, userId, null);

        // Assert
        Assert.IsNotNull(result);
        Assert.HasCount(1, result);

        var post = result[0];
        Assert.AreEqual("1001", post.PostId);
        Assert.AreEqual("Test post content", post.Text);
        Assert.AreEqual(10, post.LikeCount);
        Assert.AreEqual(2, post.ReplyCount);
        Assert.AreEqual(5, post.RepostCount);
        Assert.AreEqual(1, post.QuoteCount);
        Assert.AreEqual("testuser", post.Author.Handle);
        Assert.Contains("https://x.com/testuser/status/1001", post.Url);
    }

    [TestMethod]
    public async Task GetRecentPostsAsync_WithMedia_ReturnsPostsWithMedia()
    {
        // Arrange
        var accessToken = "test-access-token";
        var userId = "123456";

        var responseJson = @"{
            ""data"": [
                {
                    ""id"": ""1001"",
                    ""text"": ""Post with media"",
                    ""created_at"": ""2025-01-29T12:00:00.000Z"",
                    ""author_id"": ""123456"",
                    ""public_metrics"": {
                        ""like_count"": 10,
                        ""reply_count"": 0,
                        ""retweet_count"": 0,
                        ""quote_count"": 0
                    },
                    ""attachments"": {
                        ""media_keys"": [""media1"", ""media2""]
                    }
                }
            ],
            ""includes"": {
                ""users"": [
                    { ""id"": ""123456"", ""username"": ""testuser"", ""name"": ""Test User"" }
                ],
                ""media"": [
                    {
                        ""media_key"": ""media1"",
                        ""type"": ""photo"",
                        ""url"": ""https://example.com/photo.jpg""
                    },
                    {
                        ""media_key"": ""media2"",
                        ""type"": ""video"",
                        ""preview_image_url"": ""https://example.com/video-thumb.jpg""
                    }
                ]
            },
            ""meta"": {}
        }";

        _mockHttpMessageHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
            });

        // Act
        var result = await _client.GetRecentPostsAsync(accessToken, userId, null);

        // Assert
        Assert.IsNotNull(result);
        Assert.HasCount(1, result);

        var post = result[0];
        Assert.HasCount(2, post.Media);
        Assert.AreEqual("photo", post.Media[0].Type);
        Assert.AreEqual("https://example.com/photo.jpg", post.Media[0].Url);
        Assert.AreEqual("video", post.Media[1].Type);
        Assert.AreEqual("https://example.com/video-thumb.jpg", post.Media[1].PreviewImageUrl);
    }

    [TestMethod]
    public async Task GetRecentPostsAsync_WithSinceParameter_IncludesSinceInRequest()
    {
        // Arrange
        var accessToken = "test-access-token";
        var userId = "123456";
        var since = new DateTime(2025, 01, 15, 0, 0, 0, DateTimeKind.Utc);

        var responseJson = @"{
            ""data"": [],
            ""meta"": {}
        }";

        HttpRequestMessage? capturedRequest = null;
        _mockHttpMessageHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage req, CancellationToken ct) =>
            {
                capturedRequest = req;
                return new HttpResponseMessage
                {
                    StatusCode = HttpStatusCode.OK,
                    Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
                };
            });

        // Act
        await _client.GetRecentPostsAsync(accessToken, userId, since);

        // Assert
        Assert.IsNotNull(capturedRequest);
        Assert.Contains("start_time=", capturedRequest.RequestUri!.ToString());
        Assert.Contains("2025-01-15", capturedRequest.RequestUri!.ToString());
    }

    [TestMethod]
    public async Task GetRecentPostsAsync_MultiplePagesWithPagination_ReturnsAllPosts()
    {
        // Arrange
        var accessToken = "test-access-token";
        var userId = "123456";

        var page1Json = @"{
            ""data"": [
                { ""id"": ""1001"", ""text"": ""Post 1"", ""created_at"": ""2025-01-29T12:00:00.000Z"", ""author_id"": ""123456"", ""public_metrics"": { ""like_count"": 1, ""reply_count"": 0, ""retweet_count"": 0, ""quote_count"": 0 } }
            ],
            ""includes"": { ""users"": [{ ""id"": ""123456"", ""username"": ""user"", ""name"": ""User"" }] },
            ""meta"": { ""next_token"": ""token123"" }
        }";

        var page2Json = @"{
            ""data"": [
                { ""id"": ""1002"", ""text"": ""Post 2"", ""created_at"": ""2025-01-29T11:00:00.000Z"", ""author_id"": ""123456"", ""public_metrics"": { ""like_count"": 2, ""reply_count"": 0, ""retweet_count"": 0, ""quote_count"": 0 } }
            ],
            ""includes"": { ""users"": [{ ""id"": ""123456"", ""username"": ""user"", ""name"": ""User"" }] },
            ""meta"": {}
        }";

        var callCount = 0;
        _mockHttpMessageHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(req =>
                    req.Method == HttpMethod.Get &&
                    req.RequestUri!.ToString().Contains($"/users/{userId}/tweets")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() =>
            {
                callCount++;
                var json = callCount == 1 ? page1Json : page2Json;
                return new HttpResponseMessage
                {
                    StatusCode = HttpStatusCode.OK,
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
            });

        // Act
        var result = await _client.GetRecentPostsAsync(accessToken, userId, null);

        // Assert
        Assert.IsNotNull(result);
        Assert.HasCount(2, result);
        Assert.AreEqual(2, callCount); // Should have made 2 requests
        Assert.AreEqual("1001", result[0].PostId);
        Assert.AreEqual("1002", result[1].PostId);
    }

    [TestMethod]
    public async Task GetFollowedAccountsAsync_ApiError_ThrowsHttpRequestException()
    {
        // Arrange
        var accessToken = "test-access-token";
        var userId = "123456";

        _mockHttpMessageHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.Unauthorized,
                Content = new StringContent("{\"error\": \"unauthorized\"}")
            });

        // Act & Assert
        try
        {
            await _client.GetFollowedAccountsAsync(accessToken, userId);
            Assert.Fail("Expected HttpRequestException to be thrown");
        }
        catch (HttpRequestException)
        {
            // Expected
        }
    }
}
