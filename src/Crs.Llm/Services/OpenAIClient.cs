using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Crs.Core.Observability;
using Crs.Llm.Configuration;
using Crs.Llm.Models;

namespace Crs.Llm.Services;

/// <summary>
/// Implementation of ILlmClient using OpenAI's API with function calling.
/// </summary>
public class OpenAIClient : ILlmClient
{
    private readonly HttpClient _httpClient;
    private readonly OpenAISettings _settings;
    private readonly ILogger<OpenAIClient> _logger;
    private readonly IHostEnvironment _environment;
    private readonly IObservabilityMetrics _metrics;

    public OpenAIClient(
        HttpClient httpClient,
        IOptions<OpenAISettings> settings,
        ILogger<OpenAIClient> logger,
        IHostEnvironment environment,
        IObservabilityMetrics metrics)
    {
        _httpClient = httpClient;
        _settings = settings.Value;
        _logger = logger;
        _environment = environment;
        _metrics = metrics;

        // Standard OpenAI configuration
        var baseUrl = _settings.BaseUrl ?? "https://api.openai.com/v1/";
        _httpClient.BaseAddress = new Uri(baseUrl);
        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _settings.ApiKey);
    }

    public async Task<LlmResponse> SendMessageAsync(
        string systemPrompt,
        string userMessage,
        List<object>? tools = null,
        CancellationToken cancellationToken = default)
    {
        var messages = new List<object>
        {
            new { role = "system", content = systemPrompt },
            new { role = "user", content = userMessage }
        };

        return await CallApiAsync(messages, tools, cancellationToken);
    }


    public async Task<LlmResponse> ContinueConversationAsync(
        List<object> conversationHistory,
        List<ToolResult> toolResults,
        CancellationToken cancellationToken = default)
    {
        var messages = new List<object>(conversationHistory);

        // Add tool results to conversation
        foreach (var toolResult in toolResults)
        {
            messages.Add(new
            {
                role = "tool",
                tool_call_id = toolResult.ToolCallId,
                content = toolResult.Result
            });
        }

        return await CallApiAsync(messages, null, cancellationToken);
    }

    private async Task<LlmResponse> CallApiAsync(
        List<object> messages,
        List<object>? tools,
        CancellationToken cancellationToken)
    {
        try
        {
            using var activity = CrsTelemetry.ActivitySource.StartActivity("openai.chat_completions");
            activity?.SetTag(CrsTelemetry.Tags.Dependency, "openai");
            var stopwatch = Stopwatch.StartNew();
            // Some OpenAI models (e.g., gpt-5-nano) only accept the default temperature (1).
            var temperature = _settings.Temperature;
            if (string.Equals(_settings.Model, "gpt-5-nano", StringComparison.OrdinalIgnoreCase))
            {
                temperature = 1;
            }

            var requestBody = new Dictionary<string, object>
            {
                ["model"] = _settings.Model,
                ["messages"] = messages,
                ["temperature"] = temperature,
                ["max_completion_tokens"] = _settings.MaxTokens
            };

            if (tools != null && tools.Any())
            {
                requestBody["tools"] = tools;
                requestBody["tool_choice"] = "auto";
            }

            // Force JSON response format when no tools are specified
            // This ensures the model returns valid JSON (required for ingestion)
            if (tools == null || !tools.Any())
            {
                requestBody["response_format"] = new { type = "json_object" };
            }

            var jsonContent = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            _logger.LogInformation("Sending request to OpenAI API with model {Model}", _settings.Model);

            var response = await _httpClient.PostAsync("chat/completions", content, cancellationToken);
            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError(
                    "OpenAI API request failed: {StatusCode}. Body: {Body}",
                    response.StatusCode,
                    FormatResponseBody(responseContent));
                stopwatch.Stop();
                RecordMetric("chat_completions", "failed", stopwatch.Elapsed);
                throw new HttpRequestException($"OpenAI API request failed: {response.StatusCode}");
            }

            var result = JsonSerializer.Deserialize<JsonElement>(responseContent);
            var choice = result.GetProperty("choices")[0];
            var message = choice.GetProperty("message");

            var llmResponse = new LlmResponse
            {
                ConversationHistory = new List<object>(messages)
            };

            // Capture finish_reason and token usage
            if (choice.TryGetProperty("finish_reason", out var finishReasonProp))
            {
                llmResponse.FinishReason = finishReasonProp.GetString();
            }

            if (result.TryGetProperty("usage", out var usage))
            {
                if (usage.TryGetProperty("total_tokens", out var totalTokens))
                    llmResponse.TotalTokens = totalTokens.GetInt32();
                if (usage.TryGetProperty("completion_tokens", out var completionTokens))
                    llmResponse.CompletionTokens = completionTokens.GetInt32();
            }

            // Log if truncated
            if (llmResponse.IsTruncated)
            {
                _logger.LogWarning(
                    "OpenAI response truncated due to token limit. Completion tokens: {CompletionTokens}, Finish reason: {FinishReason}",
                    llmResponse.CompletionTokens, llmResponse.FinishReason);
            }

            stopwatch.Stop();
            RecordMetric("chat_completions", "success", stopwatch.Elapsed, llmResponse.TotalTokens ?? 0);

            // Check if there are tool calls
            if (message.TryGetProperty("tool_calls", out var toolCalls))
            {
                foreach (var toolCall in toolCalls.EnumerateArray())
                {
                    var function = toolCall.GetProperty("function");
                    llmResponse.ToolCalls.Add(new ToolCall
                    {
                        Id = toolCall.GetProperty("id").GetString()!,
                        Name = function.GetProperty("name").GetString()!,
                        Arguments = function.GetProperty("arguments").GetString()!
                    });
                }

                // Add assistant's message with tool calls to history
                llmResponse.ConversationHistory.Add(JsonSerializer.Deserialize<object>(
                    message.GetRawText())!);

                llmResponse.IsComplete = false;
            }
            else
            {
                // Regular text response
                llmResponse.Content = message.GetProperty("content").GetString() ?? string.Empty;
                llmResponse.IsComplete = true;
            }

            return llmResponse;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calling OpenAI API");
            throw;
        }
    }

    private void RecordMetric(string operation, string outcome, TimeSpan duration, int tokenCount = 0)
    {
        var properties = new Dictionary<string, object?> { ["TokenCount"] = tokenCount };
        DependencyMetrics.RecordCall(_metrics, "OpenAI", operation, outcome, duration, properties);
    }

    private string FormatResponseBody(string responseContent)
    {
        return ResponseBodyFormatter.Format(responseContent, _environment.IsDevelopment());
    }
}
