using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using Crs.Infrastructure.Configuration;
using Crs.Infrastructure.Services;
using Crs.Core.Observability;
using Crs.Tests.Infrastructure;

namespace Crs.Tests.Unit.Infrastructure;

[TestClass]
public class OpenAIEmbeddingServiceTests
{
    private Mock<HttpMessageHandler> _mockHttpMessageHandler = null!;
    private HttpClient _httpClient = null!;
    private Mock<ILogger<OpenAIEmbeddingService>> _mockLogger = null!;
    private Mock<IConfiguration> _mockConfiguration = null!;
    private EmbeddingSettings _settings = null!;
    private OpenAIEmbeddingService _service = null!;
    private TestHostEnvironment _hostEnvironment = null!;
    private IOptions<ObservabilitySettings> _observabilityOptions = null!;

    [TestInitialize]
    public void Setup()
    {
        _mockHttpMessageHandler = new Mock<HttpMessageHandler>();
        _httpClient = new HttpClient(_mockHttpMessageHandler.Object);
        _mockLogger = new Mock<ILogger<OpenAIEmbeddingService>>();
        _mockConfiguration = new Mock<IConfiguration>();
        _hostEnvironment = new TestHostEnvironment();
        _observabilityOptions = Options.Create(new ObservabilitySettings());

        // Mock the OpenAI API key from configuration
        _mockConfiguration.Setup(c => c["OpenAI:ApiKey"]).Returns("test-api-key");

        _settings = new EmbeddingSettings
        {
            ModelName = "text-embedding-3-small",
            Dimensions = 1536,
            MaxBatchSize = 100
        };

        var options = Options.Create(_settings);
        _service = new OpenAIEmbeddingService(
            _httpClient,
            options,
            _mockConfiguration.Object,
            _mockLogger.Object,
            NullObservabilityMetrics.Instance,
            _hostEnvironment,
            _observabilityOptions);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _httpClient?.Dispose();
    }

    [TestMethod]
    public void Dimensions_ReturnsConfiguredValue()
    {
        // Assert
        Assert.AreEqual(1536, _service.Dimensions);
    }

    [TestMethod]
    public async Task GenerateEmbeddingAsync_SuccessfulRequest_ReturnsEmbedding()
    {
        // Arrange
        var text = "This is a test text";
        var embeddingValues = new float[1536];
        for (int i = 0; i < 1536; i++)
        {
            embeddingValues[i] = 0.1f + (i * 0.001f);
        }

        var responseJson = CreateEmbeddingResponseJson(embeddingValues);

        _mockHttpMessageHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(req =>
                    req.Method == HttpMethod.Post &&
                    req.RequestUri!.ToString().Contains("embeddings")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
            });

        // Act
        var result = await _service.GenerateEmbeddingAsync(text);

        // Assert
        Assert.IsNotNull(result);
        Assert.HasCount(1536, result);
        Assert.AreEqual(0.1f, result[0], 0.001f);
        Assert.AreEqual(0.1f + (100 * 0.001f), result[100], 0.001f);
    }

    [TestMethod]
    public async Task GenerateEmbeddingAsync_EmptyText_ReturnsZeroVector()
    {
        // Arrange
        var text = "";

        // Act
        var result = await _service.GenerateEmbeddingAsync(text);

        // Assert
        Assert.IsNotNull(result);
        Assert.HasCount(1536, result);
        Assert.AreEqual(0f, result[0]);
        Assert.AreEqual(0f, result[1535]);
    }

    [TestMethod]
    public async Task GenerateEmbeddingAsync_WhitespaceText_ReturnsZeroVector()
    {
        // Arrange
        var text = "   \n\t   ";

        // Act
        var result = await _service.GenerateEmbeddingAsync(text);

        // Assert
        Assert.IsNotNull(result);
        Assert.HasCount(1536, result);
        Assert.AreEqual(0f, result[0]);
    }

    [TestMethod]
    public async Task GenerateEmbeddingAsync_ApiError_ThrowsHttpRequestException()
    {
        // Arrange
        var text = "Test text";

        _mockHttpMessageHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.BadRequest,
                Content = new StringContent("{\"error\": \"Invalid request\"}")
            });

        // Act & Assert
        try
        {
            await _service.GenerateEmbeddingAsync(text);
            Assert.Fail("Expected HttpRequestException to be thrown");
        }
        catch (HttpRequestException)
        {
            // Expected
        }
    }

    [TestMethod]
    public async Task GenerateEmbeddingAsync_NetworkError_ThrowsException()
    {
        // Arrange
        var text = "Test text";

        _mockHttpMessageHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Network error"));

        // Act & Assert
        try
        {
            await _service.GenerateEmbeddingAsync(text);
            Assert.Fail("Expected HttpRequestException to be thrown");
        }
        catch (HttpRequestException)
        {
            // Expected
        }
    }

    [TestMethod]
    public async Task GenerateEmbeddingsAsync_EmptyList_ReturnsEmptyList()
    {
        // Arrange
        var texts = new List<string>();

        // Act
        var result = await _service.GenerateEmbeddingsAsync(texts);

        // Assert
        Assert.IsNotNull(result);
        Assert.IsEmpty(result);
    }

    [TestMethod]
    public async Task GenerateEmbeddingsAsync_SingleBatch_ReturnsEmbeddings()
    {
        // Arrange
        var texts = new List<string> { "Text 1", "Text 2", "Text 3" };

        var embedding1 = CreateEmbedding(0.1f);
        var embedding2 = CreateEmbedding(0.2f);
        var embedding3 = CreateEmbedding(0.3f);

        var responseJson = CreateBatchEmbeddingResponseJson(new[] { embedding1, embedding2, embedding3 });

        _mockHttpMessageHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(req =>
                    req.Method == HttpMethod.Post &&
                    req.RequestUri!.ToString().Contains("embeddings")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
            });

        // Act
        var result = await _service.GenerateEmbeddingsAsync(texts);

        // Assert
        Assert.IsNotNull(result);
        Assert.HasCount(3, result);
        Assert.AreEqual(0.1f, result[0][0], 0.001f);
        Assert.AreEqual(0.2f, result[1][0], 0.001f);
        Assert.AreEqual(0.3f, result[2][0], 0.001f);
    }

    [TestMethod]
    public async Task GenerateEmbeddingsAsync_MultipleBatches_ProcessesAllBatches()
    {
        // Arrange
        var batchSize = 2;
        _settings.MaxBatchSize = batchSize;
        var options = Options.Create(_settings);
        _service = new OpenAIEmbeddingService(
            _httpClient,
            options,
            _mockConfiguration.Object,
            _mockLogger.Object,
            NullObservabilityMetrics.Instance,
            _hostEnvironment,
            _observabilityOptions);

        var texts = new List<string> { "Text 1", "Text 2", "Text 3", "Text 4", "Text 5" };

        var batch1Embeddings = new[] { CreateEmbedding(0.1f), CreateEmbedding(0.2f) };
        var batch2Embeddings = new[] { CreateEmbedding(0.3f), CreateEmbedding(0.4f) };
        var batch3Embeddings = new[] { CreateEmbedding(0.5f) };

        var callCount = 0;
        _mockHttpMessageHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(req =>
                    req.Method == HttpMethod.Post &&
                    req.RequestUri!.ToString().Contains("embeddings")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() =>
            {
                callCount++;
                var embeddings = callCount switch
                {
                    1 => batch1Embeddings,
                    2 => batch2Embeddings,
                    3 => batch3Embeddings,
                    _ => throw new InvalidOperationException("Unexpected call")
                };
                return new HttpResponseMessage
                {
                    StatusCode = HttpStatusCode.OK,
                    Content = new StringContent(
                        CreateBatchEmbeddingResponseJson(embeddings),
                        Encoding.UTF8,
                        "application/json")
                };
            });

        // Act
        var result = await _service.GenerateEmbeddingsAsync(texts);

        // Assert
        Assert.IsNotNull(result);
        Assert.HasCount(5, result);
        Assert.AreEqual(3, callCount); // Should have made 3 API calls (2+2+1)
        Assert.AreEqual(0.1f, result[0][0], 0.001f);
        Assert.AreEqual(0.2f, result[1][0], 0.001f);
        Assert.AreEqual(0.3f, result[2][0], 0.001f);
        Assert.AreEqual(0.4f, result[3][0], 0.001f);
        Assert.AreEqual(0.5f, result[4][0], 0.001f);
    }

    [TestMethod]
    public async Task GenerateEmbeddingsAsync_ApiError_ThrowsHttpRequestException()
    {
        // Arrange
        var texts = new List<string> { "Text 1", "Text 2" };

        _mockHttpMessageHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.InternalServerError,
                Content = new StringContent("{\"error\": \"Server error\"}")
            });

        // Act & Assert
        try
        {
            await _service.GenerateEmbeddingsAsync(texts);
            Assert.Fail("Expected HttpRequestException to be thrown");
        }
        catch (HttpRequestException)
        {
            // Expected
        }
    }

    private float[] CreateEmbedding(float baseValue)
    {
        var embedding = new float[1536];
        for (int i = 0; i < 1536; i++)
        {
            embedding[i] = baseValue + (i * 0.0001f);
        }
        return embedding;
    }

    private string CreateEmbeddingResponseJson(float[] embedding)
    {
        var embeddingStr = string.Join(", ", embedding.Select(v => v.ToString("F4")));
        return $@"{{
            ""object"": ""list"",
            ""data"": [
                {{
                    ""object"": ""embedding"",
                    ""index"": 0,
                    ""embedding"": [{embeddingStr}]
                }}
            ],
            ""model"": ""text-embedding-3-small"",
            ""usage"": {{
                ""prompt_tokens"": 5,
                ""total_tokens"": 5
            }}
        }}";
    }

    private string CreateBatchEmbeddingResponseJson(float[][] embeddings)
    {
        var dataItems = embeddings.Select((emb, index) =>
        {
            var embeddingStr = string.Join(", ", emb.Select(v => v.ToString("F4")));
            return $@"{{
                ""object"": ""embedding"",
                ""index"": {index},
                ""embedding"": [{embeddingStr}]
            }}";
        });

        var dataStr = string.Join(",\n", dataItems);

        return $@"{{
            ""object"": ""list"",
            ""data"": [{dataStr}],
            ""model"": ""text-embedding-3-small"",
            ""usage"": {{
                ""prompt_tokens"": {embeddings.Length * 5},
                ""total_tokens"": {embeddings.Length * 5}
            }}
        }}";
    }
}
