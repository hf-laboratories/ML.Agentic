using System.Collections.Concurrent;

namespace HFLabs.ML.Agentic.Escalation;

/// <summary>
/// Per-key attempt budget for escalations, so a caller never enters an escalate-reject-escalate loop. A key is
/// usually a conversation turn id. Every attempt counts, whatever its outcome.
/// </summary>
public interface IEscalationBudget
{
    /// <summary>Returns whether <paramref name="key"/> may still attempt an escalation.</summary>
    bool CanEscalate(string key);

    /// <summary>Records one attempt against <paramref name="key"/>.</summary>
    void RecordAttempt(string key);

    /// <summary>Forgets the attempts recorded for <paramref name="key"/>.</summary>
    void Reset(string key);
}

/// <summary>Thread-safe in-memory <see cref="IEscalationBudget"/> that allows a fixed number of attempts per key.</summary>
public sealed class PerKeyEscalationBudget : IEscalationBudget
{
    private readonly ConcurrentDictionary<string, int> _attempts = new(StringComparer.Ordinal);

    /// <summary>Creates a budget of <paramref name="maxAttemptsPerKey"/> attempts per key; negative values mean zero.</summary>
    public PerKeyEscalationBudget(int maxAttemptsPerKey)
    {
        MaxAttemptsPerKey = Math.Max(0, maxAttemptsPerKey);
    }

    /// <summary>The number of attempts each key may make.</summary>
    public int MaxAttemptsPerKey { get; }

    /// <inheritdoc />
    public bool CanEscalate(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return _attempts.GetValueOrDefault(key) < MaxAttemptsPerKey;
    }

    /// <inheritdoc />
    public void RecordAttempt(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        _ = _attempts.AddOrUpdate(key, 1, static (_, count) => count + 1);
    }

    /// <inheritdoc />
    public void Reset(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        _ = _attempts.TryRemove(key, out _);
    }
}
