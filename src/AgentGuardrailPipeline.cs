namespace HFLabs.ML.Agentic;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Represents the evaluation outcome of a guardrail policy check.
/// </summary>
/// <param name="Allowed">Whether the payload is allowed to proceed.</param>
/// <param name="RuleName">The name of the rule that was triggered (if blocked).</param>
/// <param name="Remediation">Actionable advice or explanation for the policy violation.</param>
public sealed record GuardrailVerdict(
    bool Allowed,
    string? RuleName = null,
    string? Remediation = null)
{
    /// <summary>
    /// Creates a passing guardrail verdict.
    /// </summary>
    public static GuardrailVerdict Pass() => new(true);

    /// <summary>
    /// Creates a blocked guardrail verdict with rule name and remediation advice.
    /// </summary>
    public static GuardrailVerdict Block(string ruleName, string remediation) =>
        new(false, ruleName, remediation);
}

/// <summary>
/// Contract for pre-execution ingress security, schema, and policy guardrails.
/// </summary>
/// <typeparam name="TInput">The input payload type.</typeparam>
public interface IInputGuardrail<in TInput>
{
    /// <summary>
    /// Gets the unique identifier or rule name for the guardrail.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Evaluates inbound input prior to cognitive or tool processing.
    /// </summary>
    /// <param name="input">The inbound input.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A verdict indicating if input is permitted.</returns>
    Task<GuardrailVerdict> ValidateInputAsync(TInput input, CancellationToken cancellationToken = default);
}

/// <summary>
/// Contract for post-execution egress security, fact-grounding, and compliance guardrails.
/// </summary>
/// <typeparam name="TOutput">The output candidate type.</typeparam>
/// <typeparam name="TContext">The original input context type.</typeparam>
public interface IOutputGuardrail<in TOutput, in TContext>
{
    /// <summary>
    /// Gets the unique identifier or rule name for the guardrail.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Evaluates model or agent output prior to returning to the caller.
    /// </summary>
    /// <param name="output">The candidate output to inspect.</param>
    /// <param name="context">The contextual input against which grounding or constraints are evaluated.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A verdict indicating if output is permitted.</returns>
    Task<GuardrailVerdict> ValidateOutputAsync(TOutput output, TContext context, CancellationToken cancellationToken = default);
}

/// <summary>
/// Enforces defense-in-depth pre- and post-execution guardrails conforming to PAT-CS-250.
/// </summary>
/// <typeparam name="TInput">The input payload type.</typeparam>
/// <typeparam name="TOutput">The output candidate type.</typeparam>
public sealed class AgentGuardrailPipeline<TInput, TOutput>(
    IReadOnlyList<IInputGuardrail<TInput>>? inputGuardrails = null,
    IReadOnlyList<IOutputGuardrail<TOutput, TInput>>? outputGuardrails = null)
{
    private readonly IReadOnlyList<IInputGuardrail<TInput>> _inputGuardrails = inputGuardrails ?? [];
    private readonly IReadOnlyList<IOutputGuardrail<TOutput, TInput>> _outputGuardrails = outputGuardrails ?? [];

    /// <summary>
    /// Evaluates all configured ingress guardrails sequentially.
    /// </summary>
    /// <param name="input">The inbound input to inspect.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The first blocking verdict encountered, or a passing verdict if all clear.</returns>
    public async Task<GuardrailVerdict> EvaluateInputAsync(TInput input, CancellationToken cancellationToken = default)
    {
        foreach (var guard in _inputGuardrails)
        {
            var verdict = await guard.ValidateInputAsync(input, cancellationToken).ConfigureAwait(false);
            if (!verdict.Allowed)
            {
                return verdict;
            }
        }

        return GuardrailVerdict.Pass();
    }

    /// <summary>
    /// Evaluates all configured egress guardrails sequentially.
    /// </summary>
    /// <param name="output">The output candidate to inspect.</param>
    /// <param name="context">The original context.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The first blocking verdict encountered, or a passing verdict if all clear.</returns>
    public async Task<GuardrailVerdict> EvaluateOutputAsync(TOutput output, TInput context, CancellationToken cancellationToken = default)
    {
        foreach (var guard in _outputGuardrails)
        {
            var verdict = await guard.ValidateOutputAsync(output, context, cancellationToken).ConfigureAwait(false);
            if (!verdict.Allowed)
            {
                return verdict;
            }
        }

        return GuardrailVerdict.Pass();
    }

    /// <summary>
    /// Executes a cognitive or tool delegate wrapped within ingress and egress guardrail checkpoints.
    /// </summary>
    /// <param name="input">The input context.</param>
    /// <param name="executionDelegate">The core execution logic to guard.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>An AgentResult containing the generated value if valid, or a descriptive failure if blocked.</returns>
    public async Task<AgentResult<TOutput>> ExecuteGuardedAsync(
        TInput input,
        Func<TInput, CancellationToken, Task<TOutput>> executionDelegate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(executionDelegate);

        var inVerdict = await EvaluateInputAsync(input, cancellationToken).ConfigureAwait(false);
        if (!inVerdict.Allowed)
        {
            return AgentResult<TOutput>.Fail($"Ingress guardrail violation [{inVerdict.RuleName}]: {inVerdict.Remediation}");
        }

        TOutput output;
        try
        {
            output = await executionDelegate(input, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return AgentResult<TOutput>.Fail($"Execution exception: {ex.Message}");
        }

        var outVerdict = await EvaluateOutputAsync(output, input, cancellationToken).ConfigureAwait(false);
        if (!outVerdict.Allowed)
        {
            return AgentResult<TOutput>.Fail($"Egress guardrail violation [{outVerdict.RuleName}]: {outVerdict.Remediation}");
        }

        return AgentResult<TOutput>.Ok(output);
    }
}
