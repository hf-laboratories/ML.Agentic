namespace HFLabs.ML.Agentic.Escalation;

/// <summary>How a <see cref="BoundedEscalation"/> run ended.</summary>
public enum BoundedEscalationStatus
{
    /// <summary>The key had no attempts left; nothing was built or sent.</summary>
    BudgetExhausted,

    /// <summary>An attempt was recorded and the proposer returned no proposal.</summary>
    ProposerUnavailable,

    /// <summary>The proposer returned a proposal and the validator produced a verdict.</summary>
    Validated
}

/// <summary>Result of one <see cref="BoundedEscalation"/> run.</summary>
/// <param name="Status">How the run ended.</param>
/// <param name="Request">The request that was sent, or <c>default</c> when the budget was exhausted.</param>
/// <param name="Proposal">The proposer's output, when there was one.</param>
/// <param name="Verdict">The validator's verdict, set only when <paramref name="Status"/> is <see cref="BoundedEscalationStatus.Validated"/>.</param>
public sealed record BoundedEscalationRun<TRequest, TProposal, TVerdict>(
    BoundedEscalationStatus Status,
    TRequest? Request,
    TProposal? Proposal,
    TVerdict? Verdict)
    where TProposal : class;

/// <summary>
/// Runs one bounded escalation to an untrusted proposer (typically a language model): check the budget, record
/// the attempt, build the request, ask the proposer, and pass any proposal through a validator. The proposer's
/// output never reaches the caller except through the validator's verdict.
/// </summary>
public static class BoundedEscalation
{
    /// <summary>Runs the escalation pipeline once for <paramref name="key"/>.</summary>
    /// <param name="key">Budget key, usually a turn id.</param>
    /// <param name="budget">The attempt budget; the attempt is recorded before the request is built.</param>
    /// <param name="buildRequest">Builds the request (snapshots, prompt) once the attempt is allowed.</param>
    /// <param name="propose">Asks the proposer; returns <c>null</c> when it is unavailable.</param>
    /// <param name="validate">The gate every proposal must pass.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<BoundedEscalationRun<TRequest, TProposal, TVerdict>> RunAsync<TRequest, TProposal, TVerdict>(
        string key,
        IEscalationBudget budget,
        Func<CancellationToken, Task<TRequest>> buildRequest,
        Func<TRequest, CancellationToken, Task<TProposal?>> propose,
        Func<TRequest, TProposal, CancellationToken, Task<TVerdict>> validate,
        CancellationToken ct = default)
        where TProposal : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(budget);
        ArgumentNullException.ThrowIfNull(buildRequest);
        ArgumentNullException.ThrowIfNull(propose);
        ArgumentNullException.ThrowIfNull(validate);

        if (!budget.CanEscalate(key))
        {
            return new(BoundedEscalationStatus.BudgetExhausted, default, null, default);
        }

        budget.RecordAttempt(key);

        TRequest request = await buildRequest(ct).ConfigureAwait(false);
        TProposal? proposal = await propose(request, ct).ConfigureAwait(false);
        if (proposal is null)
        {
            return new(BoundedEscalationStatus.ProposerUnavailable, request, null, default);
        }

        TVerdict verdict = await validate(request, proposal, ct).ConfigureAwait(false);
        return new(BoundedEscalationStatus.Validated, request, proposal, verdict);
    }
}
