namespace HFLabs.ML.Agentic;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Operational decisions emitted by a human reviewer or approval authority.
/// </summary>
public enum HitlDecision
{
    /// <summary>
    /// Action is authorized to proceed with original parameters.
    /// </summary>
    Approved,

    /// <summary>
    /// Action is rejected and blocked from execution.
    /// </summary>
    Rejected,

    /// <summary>
    /// Action is approved with modified or sanitized parameter overrides.
    /// </summary>
    Modified
}

/// <summary>
/// Checkpoint state capturing execution snapshot and resumption parameters.
/// </summary>
/// <typeparam name="TContext">The contextual state type.</typeparam>
/// <param name="TokenId">Unique resumption token.</param>
/// <param name="ActionName">Name of the intercepted action.</param>
/// <param name="Context">Snapshot of the agent context.</param>
/// <param name="Parameters">Proposed execution arguments.</param>
/// <param name="Timestamp">When the checkpoint was minted.</param>
/// <param name="ExpiresAt">Expiration deadline for human review.</param>
public sealed record CheckpointState<TContext>(
    string TokenId,
    string ActionName,
    TContext Context,
    IReadOnlyDictionary<string, object> Parameters,
    DateTime Timestamp,
    DateTime ExpiresAt);

/// <summary>
/// The outcome of a human-in-the-loop review.
/// </summary>
/// <param name="Decision">Review decision (Approved, Rejected, Modified).</param>
/// <param name="ReviewerNotes">Optional notes or justification provided by the human reviewer.</param>
/// <param name="ModifiedParameters">Parameter overrides if decision is Modified.</param>
public sealed record HitlVerdict(
    HitlDecision Decision,
    string? ReviewerNotes = null,
    IReadOnlyDictionary<string, object>? ModifiedParameters = null);

/// <summary>
/// Contract for intercepting high-risk actions, persisting checkpoints, and awaiting operator approval conforming to PAT-CS-255.
/// </summary>
/// <typeparam name="TAction">The action or command type.</typeparam>
/// <typeparam name="TContext">The contextual state type.</typeparam>
public interface IApprovalGate<in TAction, TContext>
{
    /// <summary>
    /// Evaluates whether the proposed action requires human authorization.
    /// </summary>
    /// <param name="action">The proposed action.</param>
    /// <param name="context">Active contextual state.</param>
    /// <returns>True if the action must be paused for approval.</returns>
    bool RequiresApproval(TAction action, TContext context);

    /// <summary>
    /// Freezes execution state and issues a resumption token.
    /// </summary>
    /// <param name="action">The intercepted action.</param>
    /// <param name="context">Active contextual state.</param>
    /// <param name="parameters">Proposed arguments.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Persisted checkpoint state.</returns>
    Task<CheckpointState<TContext>> CreateCheckpointAsync(
        TAction action,
        TContext context,
        IReadOnlyDictionary<string, object> parameters,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Awaits human review and returns the verdict or times out.
    /// </summary>
    /// <param name="tokenId">Unique token identifier.</param>
    /// <param name="timeout">Maximum review wait window.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Review verdict.</returns>
    Task<HitlVerdict> WaitForApprovalAsync(
        string tokenId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Governs execution of high-risk actions behind human approval gates conforming to PAT-CS-255.
/// </summary>
/// <typeparam name="TAction">Action type.</typeparam>
/// <typeparam name="TContext">Context type.</typeparam>
public sealed class HumanInTheLoopGate<TAction, TContext>(
    IApprovalGate<TAction, TContext> gate,
    TimeSpan defaultTimeout)
{
    private readonly IApprovalGate<TAction, TContext> _gate = gate ?? throw new ArgumentNullException(nameof(gate));
    private readonly TimeSpan _defaultTimeout = defaultTimeout;

    /// <summary>
    /// Executes an action directly if safe, or routes through approval checkpoints if deemed high-impact.
    /// </summary>
    /// <typeparam name="TResult">Action return type.</typeparam>
    /// <param name="action">The action to run.</param>
    /// <param name="context">The contextual state.</param>
    /// <param name="parameters">Execution arguments.</param>
    /// <param name="executeAction">Execution delegate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>AgentResult containing the executed value or rejection explanation.</returns>
    public async Task<AgentResult<TResult>> ExecuteWithGateAsync<TResult>(
        TAction action,
        TContext context,
        IReadOnlyDictionary<string, object> parameters,
        Func<TAction, IReadOnlyDictionary<string, object>, Task<TResult>> executeAction,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(executeAction);

        if (!_gate.RequiresApproval(action, context))
        {
            TResult directResult = await executeAction(action, parameters).ConfigureAwait(false);
            return AgentResult<TResult>.Ok(directResult);
        }

        var checkpoint = await _gate.CreateCheckpointAsync(action, context, parameters, cancellationToken).ConfigureAwait(false);
        var verdict = await _gate.WaitForApprovalAsync(checkpoint.TokenId, _defaultTimeout, cancellationToken).ConfigureAwait(false);

        return verdict.Decision switch
        {
            HitlDecision.Approved => AgentResult<TResult>.Ok(await executeAction(action, parameters).ConfigureAwait(false)),
            HitlDecision.Modified => AgentResult<TResult>.Ok(await executeAction(action, verdict.ModifiedParameters ?? parameters).ConfigureAwait(false)),
            HitlDecision.Rejected => AgentResult<TResult>.Fail($"Action '{checkpoint.ActionName}' rejected by reviewer: {verdict.ReviewerNotes ?? "No reason given"}"),
            _ => AgentResult<TResult>.Fail($"Unsupported approval decision: {verdict.Decision}")
        };
    }
}
