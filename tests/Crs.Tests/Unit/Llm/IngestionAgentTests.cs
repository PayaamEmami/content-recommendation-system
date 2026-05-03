using Microsoft.Extensions.Logging;
using Moq;
using Crs.Core.Enums;
using Crs.Core.Interfaces;
using Crs.Llm.Models;
using Crs.Llm.Services;

namespace Crs.Tests.Unit.Llm;

[TestClass]
public class IngestionAgentTests
{
    private Mock<ILlmClient> _mockLlmClient = null!;
    private Mock<IContentFetcherService> _mockContentFetcher = null!;
    private Mock<ILogger<IngestionAgent>> _mockLogger = null!;
    private IngestionAgent _agent = null!;

    [TestInitialize]
    public void Setup()
    {
        _mockLlmClient = new Mock<ILlmClient>();
        _mockContentFetcher = new Mock<IContentFetcherService>();
        _mockLogger = new Mock<ILogger<IngestionAgent>>();
        _agent = new IngestionAgent(_mockLlmClient.Object, _mockContentFetcher.Object, _mockLogger.Object);
    }

    [TestMethod]
    public async Task IngestFromUrlAsync_SuccessfulIngestion_ReturnsContent()
    {
        // Arrange
        var sourceUrl = "https://example.com/feed";
        var htmlContent = "<html><body>Some content</body></html>";

        _mockContentFetcher
            .Setup(x => x.FetchContentAsync(sourceUrl, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContentFetchResult { Success = true, Content = htmlContent });

        var jsonResponse = @"{
            ""content"": [
                {
                    ""title"": ""Test Paper"",
                    ""url"": ""https://example.com/paper1"",
                    ""description"": ""A test paper about AI"",
                    ""type"": ""Paper""
                },
                {
                    ""title"": ""Test Video"",
                    ""url"": ""https://example.com/video1"",
                    ""description"": ""A tutorial video"",
                    ""type"": ""Video""
                }
            ]
        }";

        _mockLlmClient
            .Setup(x => x.SendMessageAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlmResponse
            {
                Content = jsonResponse,
                FinishReason = "stop",
                CompletionTokens = 100
            });

        // Act
        var result = await _agent.IngestFromUrlAsync(sourceUrl);

        // Assert
        Assert.IsTrue(result.Success);
        Assert.AreEqual(sourceUrl, result.SourceUrl);
        Assert.HasCount(2, result.Content);
        Assert.AreEqual(2, result.TotalFound);

        var paper = result.Content[0];
        Assert.AreEqual("Test Paper", paper.Title);
        Assert.AreEqual("https://example.com/paper1", paper.Url);
        Assert.AreEqual("A test paper about AI", paper.Description);
        Assert.AreEqual(ContentType.Paper, paper.Type);

        var video = result.Content[1];
        Assert.AreEqual("Test Video", video.Title);
        Assert.AreEqual("https://example.com/video1", video.Url);
        Assert.AreEqual("A tutorial video", video.Description);
        Assert.AreEqual(ContentType.Video, video.Type);
    }

    [TestMethod]
    public async Task IngestFromUrlAsync_ContentFetchFails_ReturnsFailure()
    {
        // Arrange
        var sourceUrl = "https://example.com/feed";

        _mockContentFetcher
            .Setup(x => x.FetchContentAsync(sourceUrl, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContentFetchResult
            {
                Success = false,
                ErrorMessage = "Network error"
            });

        // Act
        var result = await _agent.IngestFromUrlAsync(sourceUrl);

        // Assert: fetch failure is an infrastructure error, so Success must be false
        Assert.IsFalse(result.Success);
        Assert.AreEqual(sourceUrl, result.SourceUrl);
        Assert.IsEmpty(result.Content);
        Assert.AreEqual(0, result.TotalFound);
        Assert.IsNotNull(result.ErrorMessage);
        Assert.IsTrue(result.ErrorMessage.Contains("Network error") || result.ErrorMessage.Contains("Failed to fetch content"));
    }

    [TestMethod]
    public async Task IngestFromUrlAsync_EmptyContent_ReturnsFailure()
    {
        // Arrange
        var sourceUrl = "https://example.com/feed";

        _mockContentFetcher
            .Setup(x => x.FetchContentAsync(sourceUrl, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContentFetchResult
            {
                Success = true,
                Content = ""
            });

        // Act
        var result = await _agent.IngestFromUrlAsync(sourceUrl);

        // Assert: empty content is treated as a fetch failure (handled by the same branch)
        Assert.IsFalse(result.Success);
        Assert.IsEmpty(result.Content);
        Assert.IsNotNull(result.ErrorMessage);
    }

    [TestMethod]
    public async Task IngestFromUrlAsync_NoJsonInResponse_ReturnsEmptyResult()
    {
        // Arrange
        var sourceUrl = "https://example.com/feed";
        var htmlContent = "<html><body>Some content</body></html>";

        _mockContentFetcher
            .Setup(x => x.FetchContentAsync(sourceUrl, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContentFetchResult { Success = true, Content = htmlContent });

        _mockLlmClient
            .Setup(x => x.SendMessageAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlmResponse
            {
                Content = "No content found in this content.",
                FinishReason = "stop",
                CompletionTokens = 50
            });

        // Act
        var result = await _agent.IngestFromUrlAsync(sourceUrl);

        // Assert
        Assert.IsTrue(result.Success);
        Assert.IsEmpty(result.Content);
        Assert.IsNotNull(result.ErrorMessage);
    }

    [TestMethod]
    public async Task IngestFromUrlAsync_MalformedJson_ReturnsEmptyResult()
    {
        // Arrange
        var sourceUrl = "https://example.com/feed";
        var htmlContent = "<html><body>Some content</body></html>";

        _mockContentFetcher
            .Setup(x => x.FetchContentAsync(sourceUrl, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContentFetchResult { Success = true, Content = htmlContent });

        // Malformed JSON (missing closing braces/brackets)
        var malformedJson = @"{
            ""content"": [
                {
                    ""title"": ""Test Paper"",
                    ""url"": ""https://example.com/paper1""
                }
            ";

        _mockLlmClient
            .Setup(x => x.SendMessageAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlmResponse
            {
                Content = malformedJson,
                FinishReason = "length",
                CompletionTokens = 4096
            });

        // Act
        var result = await _agent.IngestFromUrlAsync(sourceUrl);

        // Assert
        Assert.IsTrue(result.Success);
        Assert.IsEmpty(result.Content);
        Assert.IsNotNull(result.ErrorMessage);
    }

    [TestMethod]
    public async Task IngestFromUrlAsync_EmptyContentArray_ReturnsEmptyResult()
    {
        // Arrange
        var sourceUrl = "https://example.com/feed";
        var htmlContent = "<html><body>Some content</body></html>";

        _mockContentFetcher
            .Setup(x => x.FetchContentAsync(sourceUrl, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContentFetchResult { Success = true, Content = htmlContent });

        var jsonResponse = @"{ ""content"": [] }";

        _mockLlmClient
            .Setup(x => x.SendMessageAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlmResponse
            {
                Content = jsonResponse,
                FinishReason = "stop",
                CompletionTokens = 50
            });

        // Act
        var result = await _agent.IngestFromUrlAsync(sourceUrl);

        // Assert
        Assert.IsTrue(result.Success);
        Assert.IsEmpty(result.Content);
        Assert.AreEqual(0, result.TotalFound);
    }

    [TestMethod]
    public async Task IngestFromUrlAsync_ContentMissingTitle_SkipsContent()
    {
        // Arrange
        var sourceUrl = "https://example.com/feed";
        var htmlContent = "<html><body>Some content</body></html>";

        _mockContentFetcher
            .Setup(x => x.FetchContentAsync(sourceUrl, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContentFetchResult { Success = true, Content = htmlContent });

        var jsonResponse = @"{
            ""content"": [
                {
                    ""url"": ""https://example.com/paper1"",
                    ""description"": ""A test paper without title"",
                    ""type"": ""Paper""
                },
                {
                    ""title"": ""Valid Paper"",
                    ""url"": ""https://example.com/paper2"",
                    ""description"": ""A valid paper"",
                    ""type"": ""Paper""
                }
            ]
        }";

        _mockLlmClient
            .Setup(x => x.SendMessageAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlmResponse
            {
                Content = jsonResponse,
                FinishReason = "stop",
                CompletionTokens = 100
            });

        // Act
        var result = await _agent.IngestFromUrlAsync(sourceUrl);

        // Assert
        Assert.IsTrue(result.Success);
        Assert.HasCount(1, result.Content);
        Assert.AreEqual("Valid Paper", result.Content[0].Title);
    }

    [TestMethod]
    public async Task IngestFromUrlAsync_ContentMissingUrl_SkipsContent()
    {
        // Arrange
        var sourceUrl = "https://example.com/feed";
        var htmlContent = "<html><body>Some content</body></html>";

        _mockContentFetcher
            .Setup(x => x.FetchContentAsync(sourceUrl, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContentFetchResult { Success = true, Content = htmlContent });

        var jsonResponse = @"{
            ""content"": [
                {
                    ""title"": ""Paper Without URL"",
                    ""description"": ""A test paper without URL"",
                    ""type"": ""Paper""
                },
                {
                    ""title"": ""Valid Paper"",
                    ""url"": ""https://example.com/paper2"",
                    ""description"": ""A valid paper"",
                    ""type"": ""Paper""
                }
            ]
        }";

        _mockLlmClient
            .Setup(x => x.SendMessageAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlmResponse
            {
                Content = jsonResponse,
                FinishReason = "stop",
                CompletionTokens = 100
            });

        // Act
        var result = await _agent.IngestFromUrlAsync(sourceUrl);

        // Assert
        Assert.IsTrue(result.Success);
        Assert.HasCount(1, result.Content);
        Assert.AreEqual("Valid Paper", result.Content[0].Title);
    }

    [TestMethod]
    public async Task IngestFromUrlAsync_DefaultsToContentTypePaper_WhenTypeNotSpecified()
    {
        // Arrange
        var sourceUrl = "https://example.com/feed";
        var htmlContent = "<html><body>Some content</body></html>";

        _mockContentFetcher
            .Setup(x => x.FetchContentAsync(sourceUrl, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContentFetchResult { Success = true, Content = htmlContent });

        var jsonResponse = @"{
            ""content"": [
                {
                    ""title"": ""Content Without Type"",
                    ""url"": ""https://example.com/content1"",
                    ""description"": ""A content without type field""
                }
            ]
        }";

        _mockLlmClient
            .Setup(x => x.SendMessageAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlmResponse
            {
                Content = jsonResponse,
                FinishReason = "stop",
                CompletionTokens = 50
            });

        // Act
        var result = await _agent.IngestFromUrlAsync(sourceUrl);

        // Assert
        Assert.IsTrue(result.Success);
        Assert.HasCount(1, result.Content);
        Assert.AreEqual(ContentType.Paper, result.Content[0].Type);
    }

    [TestMethod]
    public async Task IngestFromUrlAsync_ParsesAllContentTypes_Correctly()
    {
        // Arrange
        var sourceUrl = "https://example.com/feed";
        var htmlContent = "<html><body>Some content</body></html>";

        _mockContentFetcher
            .Setup(x => x.FetchContentAsync(sourceUrl, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContentFetchResult { Success = true, Content = htmlContent });

        var jsonResponse = @"{
            ""content"": [
                {
                    ""title"": ""Paper Content"",
                    ""url"": ""https://example.com/paper"",
                    ""description"": ""A paper"",
                    ""type"": ""Paper""
                },
                {
                    ""title"": ""Video Content"",
                    ""url"": ""https://example.com/video"",
                    ""description"": ""A video"",
                    ""type"": ""Video""
                },
                {
                    ""title"": ""BlogPost Content"",
                    ""url"": ""https://example.com/blog"",
                    ""description"": ""A blog post"",
                    ""type"": ""BlogPost""
                }
            ]
        }";

        _mockLlmClient
            .Setup(x => x.SendMessageAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlmResponse
            {
                Content = jsonResponse,
                FinishReason = "stop",
                CompletionTokens = 150
            });

        // Act
        var result = await _agent.IngestFromUrlAsync(sourceUrl);

        // Assert
        Assert.IsTrue(result.Success);
        Assert.HasCount(3, result.Content);
        Assert.AreEqual(ContentType.Paper, result.Content[0].Type);
        Assert.AreEqual(ContentType.Video, result.Content[1].Type);
        Assert.AreEqual(ContentType.BlogPost, result.Content[2].Type);
    }

    [TestMethod]
    public async Task IngestFromUrlAsync_HandlesInvalidContentType_DefaultsToPaper()
    {
        // Arrange
        var sourceUrl = "https://example.com/feed";
        var htmlContent = "<html><body>Some content</body></html>";

        _mockContentFetcher
            .Setup(x => x.FetchContentAsync(sourceUrl, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContentFetchResult { Success = true, Content = htmlContent });

        var jsonResponse = @"{
            ""content"": [
                {
                    ""title"": ""Content With Invalid Type"",
                    ""url"": ""https://example.com/content"",
                    ""description"": ""A content with invalid type"",
                    ""type"": ""InvalidType""
                }
            ]
        }";

        _mockLlmClient
            .Setup(x => x.SendMessageAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlmResponse
            {
                Content = jsonResponse,
                FinishReason = "stop",
                CompletionTokens = 50
            });

        // Act
        var result = await _agent.IngestFromUrlAsync(sourceUrl);

        // Assert
        Assert.IsTrue(result.Success);
        Assert.HasCount(1, result.Content);
        Assert.AreEqual(ContentType.Paper, result.Content[0].Type);
    }

    [TestMethod]
    public async Task IngestFromUrlAsync_HandlesException_ReturnsErrorResult()
    {
        // Arrange
        var sourceUrl = "https://example.com/feed";

        _mockContentFetcher
            .Setup(x => x.FetchContentAsync(sourceUrl, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Unexpected error"));

        // Act
        var result = await _agent.IngestFromUrlAsync(sourceUrl);

        // Assert: an unexpected exception is an infrastructure error, so Success must be false
        Assert.IsFalse(result.Success);
        Assert.IsEmpty(result.Content);
        Assert.IsNotNull(result.ErrorMessage);
        Assert.Contains("Unexpected error", result.ErrorMessage);
    }
}
