namespace HFLabs.ML.Agentic;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Telemetry record for an individual fallback tier execution attempt.
/// </summary>
/// <param name="TierName">Name of the attempted tier.</param>
/// <param name="Succeeded">Whether execution in this tier succeeded.</param>
/// <param name="Error">Error message if tier failed.</param>
/// <param name="Duration">Wall-clock duration of the attempt.</param>
public sealed record FallbackAttempt(
    string TierName,
    bool Succeeded,
    string? Error = null,
    TimeSpan Duration = default);

/// <summary>
/// Comprehensive report of multi-tier fallback execution.
/// </summary>
/// <typeparam name="TResult">Result payload type.</typeparam>
/// <param name="Success">Whether any tier in the ladder succeeded.</param>
/// <param name="Value">The output payload produced by the winning tier.</param>
/// <param name="SuccessfulTier">Name of the tier that produced the result.</param>
/// <param name="Attempts">Chronological sequence of all tier attempts.</param>
public sealed record FallbackExecutionReport<TResult>(
    bool Success,
    TResult? Value,
    string SuccessfulTier,
    IReadOnlyList<FallbackAttempt> Attempts);

/// <summary>
/// Contract for an individual degradation tier in an agent fallback ladder conforming to PAT-CS-259.
/// </summary>
/// <typeparam name="TContext">Context input type.</typeparam>
/// <typeparam name="TResult">Result output type.</typeparam>
public interface IFallbackTier<in TContext, TResult>
{
    /// <summary>
    /// Gets the unique, human-readable name of the tier (e.g., "FrontierCloud", "LocalSLM", "CachedLookup").
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Executes the tier's cognitive or retrieval logic.
    /// </summary>
    /// <param name="context">The contextual task state.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Agent result envelope.</returns>
    Task<AgentResult<TResult>> ExecuteTierAsync(
        TContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Orchestrates graceful degradation across an ordered hierarchy of fallback tiers conforming to PAT-CS-259.
/// </summary>
/// <typeparam name="TContext">Context input type.</typeparam>
/// <typeparam name="TResult">Result output type.</typeparam>
public sealed class AgentFallbackChain<TContext, TResult>(
    IReadOnlyList<IFallbackTier<TContext, TResult>> tiers)
{
    private readonly IReadOnlyList<IFallbackTier<TContext, TResult>> _tiers = tiers ?? throw new ArgumentNullException(nameof(tiers));

    /// <summary>
    /// Attempts execution across configured tiers sequentially, halting on first success.
    /// </summary>
    /// <param name="context">The task context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Execution report detailing attempts and final value.</returns>
    public async Task<FallbackExecutionReport<TResult>> ExecuteAsync(
        TContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        List<FallbackAttempt> attempts = [];

        for (int i = 0; i < _tiers.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tier = _tiers[i];
            DateTime start = DateTime.UtcNow;

            try
            {
                var result = await tier.ExecuteTierAsync(context, cancellationToken).ConfigureAwait(false);
                var duration = DateTime.UtcNow - start;

                if (result.Success && result.Value is not null)
                {
                    attempts.Add(new FallbackAttempt(tier.Name, true, null, duration));
                    return new FallbackExecutionReport<TResult>(true, result.Value, tier.Name, attempts);
                }

                attempts.Add(new FallbackAttempt(tier.Name, false, result.Error ?? "Tier execution failed", duration));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                attempts.Add(new FallbackAttempt(tier.Name, false, ex.Message, DateTime.UtcNow - start));
            }
        }

        return new FallbackExecutionReport<TResult>(false, default, "None", attempts);
    }
}
