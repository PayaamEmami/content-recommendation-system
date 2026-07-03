using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Crs.Core.Interfaces;
using Crs.Core.Observability;
using Crs.Infrastructure.Configuration;

namespace Crs.Infrastructure.Services;

/// <summary>
/// Direct OpenAI API-based embedding service.
/// </summary>
public class OpenAIEmbeddingService : IEmbeddingService
{
    private readonly HttpClient _httpClient;
    private readonly EmbeddingSettings _settings;
    private readonly ILogger<OpenAIEmbeddingService> _logger;
    private readonly IObservabilityMetrics _metrics;
    private readonly IHostEnvironment _environment;
    private readonly ObservabilitySettings _observabilitySettings;

    public OpenAIEmbeddingService(
        HttpClient httpClient,
        IOptions<EmbeddingSettings> settings,
        IConfiguration configuration,
        ILogger<OpenAIEmbeddingService> logger,
        IObservabilityMetrics metrics,
        IHostEnvironment environment,
        IOptions<ObservabilitySettings> observabilitySettings)
    {
        _httpClient = httpClient;
        _settings = settings.Value;
        _logger = logger;
        _metrics = metrics;
        _environment = environment;
        _observabilitySettings = observabilitySettings.Value;

        // Configure HttpClient for OpenAI API
        _httpClient.BaseAddress = new Uri("https://api.openai.com/v1/");

        var apiKey = configuration["OpenAI:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogWarning("OpenAI API key is missing for embeddings. Set OpenAI__ApiKey.");
        }
        else
        {
            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", apiKey);
        }
    }

    public int Dimensions => _settings.Dimensions;

    public async Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            _logger.LogWarning("Empty text provided for embedding generation");
            return new float[Dimensions];
        }

        try
        {
            using var activity = CrsTelemetry.ActivitySource.StartActivity("openai.embeddings.generate");
            activity?.SetTag(CrsTelemetry.Tags.Dependency, "openai");
            var stopwatch = Stopwatch.StartNew();

            var requestBody = new
            {
                model = _settings.ModelName,
                input = text,
                dimensions = _settings.Dimensions
            };

            var jsonContent = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync("embeddings", content, cancellationToken);
            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError(
                    "OpenAI API request failed: {StatusCode}. Body: {Body}",
                    response.StatusCode,
                    FormatResponseBody(responseContent));
                stopwatch.Stop();
                RecordMetric("embeddings.generate", "failed", stopwatch.Elapsed);
                throw new HttpRequestException($"OpenAI API request failed: {response.StatusCode}");
            }

            var result = JsonSerializer.Deserialize<JsonElement>(responseContent);
            var embeddingArray = result.GetProperty("data")[0].GetProperty("embedding");

            var embedding = new float[Dimensions];
            int i = 0;
            foreach (var value in embeddingArray.EnumerateArray())
            {
                embedding[i++] = (float)value.GetDouble();
            }

            _logger.LogDebug("Generated embedding for text of length {Length}", text.Length);
            stopwatch.Stop();
            RecordMetric("embeddings.generate", "success", stopwatch.Elapsed);
            return embedding;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating embedding for text");
            throw;
        }
    }

    public async Task<IList<float[]>> GenerateEmbeddingsAsync(
        IEnumerable<string> texts,
        CancellationToken cancellationToken = default)
    {
        var textList = texts.ToList();
        if (!textList.Any())
        {
            return new List<float[]>();
        }

        try
        {
            using var activity = CrsTelemetry.ActivitySource.StartActivity("openai.embeddings.generate_batch");
            activity?.SetTag(CrsTelemetry.Tags.Dependency, "openai");
            var stopwatch = Stopwatch.StartNew();
            var results = new List<float[]>();

            // Process in batches to avoid API limits
            var batches = textList
                .Select((text, index) => new { text, index })
                .GroupBy(x => x.index / _settings.MaxBatchSize)
                .Select(g => g.Select(x => x.text).ToList());

            foreach (var batch in batches)
            {
                var requestBody = new
                {
                    model = _settings.ModelName,
                    input = batch,
                    dimensions = _settings.Dimensions
                };

                var jsonContent = JsonSerializer.Serialize(requestBody);
                var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync("embeddings", content, cancellationToken);
                var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError(
                        "OpenAI API request failed: {StatusCode}. Body: {Body}",
                        response.StatusCode,
                        FormatResponseBody(responseContent));
                    stopwatch.Stop();
                    RecordMetric("embeddings.generate_batch", "failed", stopwatch.Elapsed);
                    throw new HttpRequestException($"OpenAI API request failed: {response.StatusCode}");
                }

                var result = JsonSerializer.Deserialize<JsonElement>(responseContent);
                foreach (var item in result.GetProperty("data").EnumerateArray())
                {
                    var embeddingArray = item.GetProperty("embedding");
                    var embedding = new float[Dimensions];
                    int i = 0;
                    foreach (var value in embeddingArray.EnumerateArray())
                    {
                        embedding[i++] = (float)value.GetDouble();
                    }
                    results.Add(embedding);
                }

                _logger.LogDebug("Generated {Count} embeddings in batch", batch.Count);
            }

            _logger.LogInformation("Generated {Count} embeddings total", results.Count);
            stopwatch.Stop();
            RecordMetric("embeddings.generate_batch", "success", stopwatch.Elapsed, results.Count);
            return results;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating embeddings for batch");
            throw;
        }
    }

    private void RecordMetric(string operation, string outcome, TimeSpan duration, int? count = null)
    {
        var properties = count.HasValue
            ? new Dictionary<string, object?> { ["Count"] = count.Value }
            : null;
        DependencyMetrics.RecordCall(_metrics, "OpenAI", operation, outcome, duration, properties);
    }

    private string FormatResponseBody(string responseContent)
    {
        var allowFullBody = _observabilitySettings.EnableSensitiveBodyLogging || _environment.IsDevelopment();
        return ResponseBodyFormatter.Format(responseContent, allowFullBody);
    }
}
