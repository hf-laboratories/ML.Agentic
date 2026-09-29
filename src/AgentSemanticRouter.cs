namespace HFLabs.ML.Agentic;

using System;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Defines the execution tiers for intent-routed processing, ordering from lowest compute cost to highest cognitive reasoning capability.
/// </summary>
public enum ExecutionTier
{
    /// <summary>
    /// Deterministic, zero-token local execution (regex, exact lookup, cached answers, or symbolic rule evaluation).
    /// </summary>
    Deterministic = 0,

    /// <summary>
    /// Fast, lightweight execution (vector similarity search, single-hop graph lookups, small language models).
    /// </summary>
    LightweightVector = 1,

    /// <summary>
    /// Compute-intensive multi-hop reasoning (BESS reasoners, iterative CoT, frontier LLMs, or swarm planners).
    /// </summary>
    FrontierReasoning = 2
}

/// <summary>
/// Represents the routing outcome produced by an intent classifier.
/// </summary>
/// <param name="Intent">Identified semantic intent category.</param>
/// <param name="Tier">Recommended compute and execution tier.</param>
/// <param name="Confidence">Classifier confidence score between 0.0 and 1.0.</param>
/// <param name="Rationale">Human- or agent-readable explanation of why this tier was selected.</param>
public sealed record RoutingDecision(
    string Intent,
    ExecutionTier Tier,
    double Confidence,
    string Rationale);

/// <summary>
/// Evaluates an inbound input and classifies its intent and required execution tier.
/// </summary>
/// <typeparam name="TInput">The input type being classified.</typeparam>
public interface IIntentClassifier<in TInput>
{
    /// <summary>
    /// Classifies the input and produces a routing decision.
    /// </summary>
    /// <param name="input">The input context or user query.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A structured routing decision.</returns>
    Task<RoutingDecision> ClassifyAsync(TInput input, CancellationToken cancellationToken = default);
}

/// <summary>
/// Routes inbound requests to optimal execution tiers based on classified semantic intent conforming to PAT-CS-248.
/// </summary>
/// <typeparam name="TInput">The input payload type.</typeparam>
/// <typeparam name="TOutput">The execution output type.</typeparam>
public sealed class SemanticIntentRouter<TInput, TOutput>(
    IIntentClassifier<TInput> classifier,
    Func<TInput, CancellationToken, Task<TOutput>> deterministicHandler,
    Func<TInput, CancellationToken, Task<TOutput>> lightweightHandler,
    Func<TInput, CancellationToken, Task<TOutput>> frontierHandler)
{
    private readonly IIntentClassifier<TInput> _classifier = classifier ?? throw new ArgumentNullException(nameof(classifier));
    private readonly Func<TInput, CancellationToken, Task<TOutput>> _deterministicHandler = deterministicHandler ?? throw new ArgumentNullException(nameof(deterministicHandler));
    private readonly Func<TInput, CancellationToken, Task<TOutput>> _lightweightHandler = lightweightHandler ?? throw new ArgumentNullException(nameof(lightweightHandler));
    private readonly Func<TInput, CancellationToken, Task<TOutput>> _frontierHandler = frontierHandler ?? throw new ArgumentNullException(nameof(frontierHandler));

    /// <summary>
    /// Evaluates input intent, dispatches execution to the corresponding tier handler, and returns the result and routing decision.
    /// </summary>
    /// <param name="input">The input context to process.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A tuple containing the executed result and routing decision.</returns>
    public async Task<(TOutput Result, RoutingDecision Decision)> RouteAndExecuteAsync(
        TInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        RoutingDecision decision = await _classifier.ClassifyAsync(input, cancellationToken).ConfigureAwait(false);

        TOutput result = decision.Tier switch
        {
            ExecutionTier.Deterministic => await _deterministicHandler(input, cancellationToken).ConfigureAwait(false),
            ExecutionTier.LightweightVector => await _lightweightHandler(input, cancellationToken).ConfigureAwait(false),
            ExecutionTier.FrontierReasoning => await _frontierHandler(input, cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidOperationException($"Unsupported execution tier: {decision.Tier}")
        };

        return (result, decision);
    }
}
