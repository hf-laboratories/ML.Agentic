namespace HFLabs.ML.Agentic.Escalation;

/// <summary>One system-plus-user chat turn sent to a hosted language model.</summary>
/// <param name="SystemPrompt">The system prompt.</param>
/// <param name="UserMessage">The single user message.</param>
/// <param name="JsonOutput">Ask the provider for a JSON-object response where it supports that.</param>
public sealed record ChatCompletionRequest(string SystemPrompt, string UserMessage, bool JsonOutput = true);

/// <summary>
/// Provider-neutral seam for a hosted chat-completion model used as a <em>bounded proposer</em>: its output is
/// only ever a proposal that the caller validates before use.
/// </summary>
/// <remarks>
/// Implementations fail closed. A transport failure, timeout or empty response returns <c>null</c> rather than
/// throwing, so callers treat "the model is unavailable" as an ordinary outcome and never need a catch-all.
/// </remarks>
public interface IChatCompletionClient
{
    /// <summary>Sends one turn and returns the first choice's text, or <c>null</c> when the model is unavailable.</summary>
    Task<string?> CompleteAsync(ChatCompletionRequest request, CancellationToken ct = default);
}
