using System.Net;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using Crs.Infrastructure.Services;

namespace Crs.Tests.Unit.Infrastructure;

[TestClass]
public class ContentFetcherServiceTests
{
    private Mock<HttpMessageHandler> _mockHttpMessageHandler = null!;
    private HttpClient _httpClient = null!;
    private Mock<ILogger<ContentFetcherService>> _mockLogger = null!;
    private ContentFetcherService _service = null!;

    [TestInitialize]
    public void Setup()
    {
        _mockHttpMessageHandler = new Mock<HttpMessageHandler>();
        _httpClient = new HttpClient(_mockHttpMessageHandler.Object);
        _mockLogger = new Mock<ILogger<ContentFetcherService>>();
        _service = new ContentFetcherService(_httpClient, _mockLogger.Object);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _httpClient?.Dispose();
    }

    [TestMethod]
    public async Task FetchContentAsync_SuccessfulRequest_ReturnsContent()
    {
        // Arrange
        var url = "https://example.com/page";
        var htmlContent = "<html><body><h1>Test Content</h1><p>Some text</p></body></html>";

        _mockHttpMessageHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(req => req.RequestUri!.ToString() == url),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(htmlContent)
            });

        // Act
        var result = await _service.FetchContentAsync(url);

        // Assert
        Assert.IsTrue(result.Success);
        Assert.IsNotNull(result.Content);
        Assert.AreEqual(200, result.StatusCode);
        Assert.Contains("Test Content", result.Content!);
        Assert.Contains("Some text", result.Content!);
    }

    [TestMethod]
    public async Task FetchContentAsync_RemovesScriptTags()
    {
        // Arrange
        var url = "https://example.com/page";
        var htmlContent = @"
            <html>
                <body>
                    <h1>Content</h1>
                    <script>alert('should be removed');</script>
                    <p>Visible text</p>
                    <script type='text/javascript'>console.log('also removed');</script>
                </body>
            </html>";

        _mockHttpMessageHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(htmlContent)
            });

        // Act
        var result = await _service.FetchContentAsync(url);

        // Assert
        Assert.IsTrue(result.Success);
        Assert.DoesNotContain("script", result.Content!);
        Assert.DoesNotContain("alert", result.Content!);
        Assert.DoesNotContain("console.log", result.Content!);
        Assert.Contains("Visible text", result.Content!);
    }

    [TestMethod]
    public async Task FetchContentAsync_RemovesStyleTags()
    {
        // Arrange
        var url = "https://example.com/page";
        var htmlContent = @"
            <html>
                <body>
                    <style>body { color: red; }</style>
                    <h1>Content</h1>
                    <style type='text/css'>.test { display: none; }</style>
                    <p>Visible text</p>
                </body>
            </html>";

        _mockHttpMessageHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(htmlContent)
            });

        // Act
        var result = await _service.FetchContentAsync(url);

        // Assert
        Assert.IsTrue(result.Success);
        Assert.DoesNotContain("style", result.Content!);
        Assert.DoesNotContain("color: red", result.Content!);
        Assert.DoesNotContain("display: none", result.Content!);
        Assert.Contains("Visible text", result.Content!);
    }

    [TestMethod]
    public async Task FetchContentAsync_RemovesHeadSection()
    {
        // Arrange
        var url = "https://example.com/page";
        var htmlContent = @"
            <html>
                <head>
                    <title>Page Title</title>
                    <meta name='description' content='test'>
                    <link rel='stylesheet' href='style.css'>
                </head>
                <body>
                    <h1>Body Content</h1>
                </body>
            </html>";

        _mockHttpMessageHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(htmlContent)
            });

        // Act
        var result = await _service.FetchContentAsync(url);

        // Assert
        Assert.IsTrue(result.Success);
        Assert.DoesNotContain("Page Title", result.Content!);
        Assert.DoesNotContain("meta", result.Content!);
        Assert.DoesNotContain("link", result.Content!);
        Assert.Contains("Body Content", result.Content!);
    }

    [TestMethod]
    public async Task FetchContentAsync_RemovesNavElements()
    {
        // Arrange
        var url = "https://example.com/page";
        var htmlContent = @"
            <html>
                <body>
                    <nav>
                        <a href='/home'>Home</a>
                        <a href='/about'>About</a>
                    </nav>
                    <h1>Main Content</h1>
                </body>
            </html>";

        _mockHttpMessageHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(htmlContent)
            });

        // Act
        var result = await _service.FetchContentAsync(url);

        // Assert
        Assert.IsTrue(result.Success);
        Assert.DoesNotContain("nav", result.Content!);
        Assert.DoesNotContain("Home", result.Content!);
        Assert.DoesNotContain("About", result.Content!);
        Assert.Contains("Main Content", result.Content!);
    }

    [TestMethod]
    public async Task FetchContentAsync_RemovesFooterElements()
    {
        // Arrange
        var url = "https://example.com/page";
        var htmlContent = @"
            <html>
                <body>
                    <h1>Main Content</h1>
                    <footer>
                        <p>Copyright 2025</p>
                        <a href='/privacy'>Privacy</a>
                    </footer>
                </body>
            </html>";

        _mockHttpMessageHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(htmlContent)
            });

        // Act
        var result = await _service.FetchContentAsync(url);

        // Assert
        Assert.IsTrue(result.Success);
        Assert.DoesNotContain("footer", result.Content!);
        Assert.DoesNotContain("Copyright", result.Content!);
        Assert.DoesNotContain("Privacy", result.Content!);
        Assert.Contains("Main Content", result.Content!);
    }

    [TestMethod]
    public async Task FetchContentAsync_RemovesSvgElements()
    {
        // Arrange
        var url = "https://example.com/page";
        var htmlContent = @"
            <html>
                <body>
                    <h1>Content</h1>
                    <svg width='100' height='100'>
                        <circle cx='50' cy='50' r='40' />
                    </svg>
                    <p>Text content</p>
                </body>
            </html>";

        _mockHttpMessageHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(htmlContent)
            });

        // Act
        var result = await _service.FetchContentAsync(url);

        // Assert
        Assert.IsTrue(result.Success);
        Assert.DoesNotContain("svg", result.Content!);
        Assert.DoesNotContain("circle", result.Content!);
        Assert.Contains("Text content", result.Content!);
    }

    [TestMethod]
    public async Task FetchContentAsync_RemovesHtmlComments()
    {
        // Arrange
        var url = "https://example.com/page";
        var htmlContent = @"
            <html>
                <body>
                    <!-- This is a comment -->
                    <h1>Content</h1>
                    <!-- Another comment
                         spanning multiple lines -->
                    <p>Text</p>
                </body>
            </html>";

        _mockHttpMessageHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(htmlContent)
            });

        // Act
        var result = await _service.FetchContentAsync(url);

        // Assert
        Assert.IsTrue(result.Success);
        Assert.DoesNotContain("<!--", result.Content!);
        Assert.DoesNotContain("This is a comment", result.Content!);
        Assert.DoesNotContain("Another comment", result.Content!);
        Assert.Contains("Content", result.Content!);
    }

    [TestMethod]
    public async Task FetchContentAsync_CollapsesWhitespace()
    {
        // Arrange
        var url = "https://example.com/page";
        var htmlContent = @"
            <html>
                <body>
                    <h1>Title</h1>


                    <p>Paragraph    with    extra    spaces</p>
                </body>
            </html>";

        _mockHttpMessageHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(htmlContent)
            });

        // Act
        var result = await _service.FetchContentAsync(url);

        // Assert
        Assert.IsTrue(result.Success);
        Assert.DoesNotContain("    ", result.Content!); // No multiple spaces
        Assert.DoesNotContain("\n\n", result.Content!); // No multiple newlines
    }

    [TestMethod]
    public async Task FetchContentAsync_HttpErrorStatus_ReturnsFailure()
    {
        // Arrange
        var url = "https://example.com/notfound";

        _mockHttpMessageHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.NotFound,
                ReasonPhrase = "Not Found"
            });

        // Act
        var result = await _service.FetchContentAsync(url);

        // Assert
        Assert.IsFalse(result.Success);
        Assert.AreEqual(404, result.StatusCode);
        Assert.IsNotNull(result.ErrorMessage);
        Assert.Contains("Not Found", result.ErrorMessage);
    }

    [TestMethod]
    public async Task FetchContentAsync_HttpRequestException_ReturnsFailure()
    {
        // Arrange
        var url = "https://example.com/error";

        _mockHttpMessageHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Network error"));

        // Act
        var result = await _service.FetchContentAsync(url);

        // Assert
        Assert.IsFalse(result.Success);
        Assert.IsNotNull(result.ErrorMessage);
        Assert.Contains("Network error", result.ErrorMessage);
    }

    [TestMethod]
    public async Task FetchContentAsync_TaskCanceledException_ReturnsTimeout()
    {
        // Arrange
        var url = "https://example.com/slow";

        _mockHttpMessageHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new TaskCanceledException("Request timed out"));

        // Act
        var result = await _service.FetchContentAsync(url);

        // Assert
        Assert.IsFalse(result.Success);
        Assert.IsNotNull(result.ErrorMessage);
        Assert.Contains("timed out", result.ErrorMessage);
    }

    [TestMethod]
    public async Task FetchContentAsync_UnexpectedException_ReturnsFailure()
    {
        // Arrange
        var url = "https://example.com/error";

        _mockHttpMessageHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Unexpected error"));

        // Act
        var result = await _service.FetchContentAsync(url);

        // Assert
        Assert.IsFalse(result.Success);
        Assert.IsNotNull(result.ErrorMessage);
        Assert.Contains("Unexpected error", result.ErrorMessage);
    }

    [TestMethod]
    public async Task FetchContentAsync_EmptyContent_ReturnsEmptyString()
    {
        // Arrange
        var url = "https://example.com/empty";

        _mockHttpMessageHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent("")
            });

        // Act
        var result = await _service.FetchContentAsync(url);

        // Assert
        Assert.IsTrue(result.Success);
        Assert.AreEqual(string.Empty, result.Content);
    }

    [TestMethod]
    public async Task FetchContentAsync_ComplexHtml_CleansAllElements()
    {
        // Arrange
        var url = "https://example.com/complex";
        var htmlContent = @"
            <!DOCTYPE html>
            <html>
                <head>
                    <title>Complex Page</title>
                    <meta charset='utf-8'>
                    <link rel='stylesheet' href='style.css'>
                    <script src='app.js'></script>
                </head>
                <body>
                    <nav>
                        <a href='/'>Home</a>
                    </nav>
                    <!-- Main content -->
                    <main>
                        <h1>Article Title</h1>
                        <style>.inline { color: blue; }</style>
                        <p>This is the main content we want to keep.</p>
                        <svg><circle r='5'/></svg>
                        <script>trackPageView();</script>
                    </main>
                    <footer>
                        <p>Copyright</p>
                    </footer>
                </body>
            </html>";

        _mockHttpMessageHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(htmlContent)
            });

        // Act
        var result = await _service.FetchContentAsync(url);

        // Assert
        Assert.IsTrue(result.Success);

        // Should keep
        Assert.Contains("Article Title", result.Content!);
        Assert.Contains("main content we want to keep", result.Content!);

        // Should remove
        Assert.DoesNotContain("head", result.Content!);
        Assert.DoesNotContain("Complex Page", result.Content!);
        Assert.DoesNotContain("nav", result.Content!);
        Assert.DoesNotContain("Home", result.Content!);
        Assert.DoesNotContain("style", result.Content!);
        Assert.DoesNotContain("script", result.Content!);
        Assert.DoesNotContain("svg", result.Content!);
        Assert.DoesNotContain("footer", result.Content!);
        Assert.DoesNotContain("Copyright", result.Content!);
        Assert.DoesNotContain("<!--", result.Content!);
    }

    [TestMethod]
    public async Task FetchContentAsync_BlocksPrivateIpWithoutSendingRequest()
    {
        var result = await _service.FetchContentAsync("http://169.254.169.254/latest/meta-data/");

        Assert.IsFalse(result.Success);
        Assert.AreEqual(400, result.StatusCode);
        _mockHttpMessageHandler
            .Protected()
            .Verify(
                "SendAsync",
                Times.Never(),
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>());
    }
}
