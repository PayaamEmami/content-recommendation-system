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
}

