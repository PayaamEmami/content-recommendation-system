using Crs.Llm.Models;

namespace Crs.Llm.Services;

/// <summary>
/// Interface for LLM client operations.
/// Abstracts the underlying LLM provider (OpenAI, Claude, etc.) for flexibility.
/// </summary>
public interface ILlmClient
{
    /// <summary>
    /// Sends a message to the LLM with function/tool calling support.
    /// </summary>
    /// <param name="systemPrompt">System instructions for the LLM</param>
    /// <param name="userMessage">The user's message/request</param>
    /// <param name="tools">Optional list of tools the LLM can call</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The LLM's response</returns>
    Task<LlmResponse> SendMessageAsync(
        string systemPrompt,
        string userMessage,
        List<object>? tools = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Continues a conversation by sending tool results back to the LLM.
    /// </summary>
    /// <param name="conversationHistory">Previous messages in the conversation</param>
    /// <param name="toolResults">Results from tool executions</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The LLM's response</returns>
    Task<LlmResponse> ContinueConversationAsync(
        List<object> conversationHistory,
        List<ToolResult> toolResults,
        CancellationToken cancellationToken = default);
}

