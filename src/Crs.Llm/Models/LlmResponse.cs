namespace Crs.Llm.Models;

/// <summary>
/// Represents a response from the LLM.
/// </summary>
public class LlmResponse
{
    /// <summary>
    /// The text content of the response.
    /// </summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// Tool/function calls requested by the LLM.
    /// </summary>
    public List<ToolCall> ToolCalls { get; set; } = new();

    /// <summary>
    /// Whether the LLM requested tool calls.
    /// </summary>
    public bool HasToolCalls => ToolCalls.Any();

    /// <summary>
    /// Whether this is the final response (no more tool calls needed).
    /// </summary>
    public bool IsComplete { get; set; }

    /// <summary>
    /// Full conversation history for continuing the conversation.
    /// </summary>
    public List<object> ConversationHistory { get; set; } = new();

    /// <summary>
    /// Reason the completion finished: "stop", "length", "content_filter", "tool_calls", etc.
    /// </summary>
    public string? FinishReason { get; set; }

    /// <summary>
    /// Whether the response was truncated due to token limit.
    /// </summary>
    public bool IsTruncated => FinishReason == "length";

    /// <summary>
    /// Total tokens used (prompt + completion).
    /// </summary>
    public int? TotalTokens { get; set; }

    /// <summary>
    /// Completion tokens used.
    /// </summary>
    public int? CompletionTokens { get; set; }
}

/// <summary>
/// Represents a tool/function call requested by the LLM.
/// </summary>
public class ToolCall
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;
}
