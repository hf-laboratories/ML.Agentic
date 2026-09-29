namespace HFLabs.ML.Agentic;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// A single conversational or task turn within an agent's working trajectory.
/// </summary>
/// <param name="Role">Speaker role (e.g., "User", "Assistant", "Tool", "System").</param>
/// <param name="Content">Turn message content.</param>
/// <param name="Timestamp">UTC timestamp of creation.</param>
/// <param name="IsAnchor">Whether this turn is a pinned semantic anchor that must never be evicted during compaction.</param>
public sealed record ConversationTurn(
    string Role,
    string Content,
    DateTime Timestamp,
    bool IsAnchor = false);

/// <summary>
/// Snapshot of compacted context with consolidated historical summary and active recent window.
/// </summary>
/// <param name="ConsolidatedSummary">Hierarchically summarized historical context.</param>
/// <param name="ActiveTurns">Active sliding window of uncompacted recent turns and pinned anchors.</param>
/// <param name="OriginalTurnCount">Cumulative total turns ingested across the session.</param>
/// <param name="TokensSavedEstimate">Estimated prompt tokens saved via compaction.</param>
public sealed record CompactedContextSnapshot(
    string ConsolidatedSummary,
    IReadOnlyList<ConversationTurn> ActiveTurns,
    int OriginalTurnCount,
    int TokensSavedEstimate);

/// <summary>
/// Contract for synthesizing and condensing batches of historical conversation turns conforming to PAT-CS-265.
/// </summary>
public interface IContextSummarizer
{
    /// <summary>
    /// Condenses evicted turns into the running historical summary.
    /// </summary>
    /// <param name="existingSummary">The prior summary before this compaction cycle.</param>
    /// <param name="turnsToCompact">New batch of evicted turns to incorporate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An updated consolidated summary string.</returns>
    Task<string> SummarizeTurnsAsync(
        string existingSummary,
        IReadOnlyList<ConversationTurn> turnsToCompact,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Sliding window context compactor preserving semantic anchors and hierarchically summarizing older turns conforming to PAT-CS-265.
/// </summary>
public sealed class RollingContextCompactor(
    IContextSummarizer summarizer,
    int maxActiveTurns = 6)
{
    private readonly IContextSummarizer _summarizer = summarizer ?? throw new ArgumentNullException(nameof(summarizer));
    private readonly int _maxActiveTurns = Math.Max(2, maxActiveTurns);
    private readonly List<ConversationTurn> _history = [];
    private string _currentSummary = string.Empty;
    private int _compactedTurnCount;
    private readonly Lock _sync = new();

    /// <summary>
    /// Appends a new turn to the active history, triggering compaction if unpinned turns exceed capacity.
    /// </summary>
    /// <param name="turn">The turn to append.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task AddTurnAsync(ConversationTurn turn, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(turn);

        List<ConversationTurn> turnsToCompact = [];

        lock (_sync)
        {
            _history.Add(turn);

            // Count unpinned turns
            int unpinnedCount = _history.Count(t => !t.IsAnchor);
            if (unpinnedCount > _maxActiveTurns)
            {
                int toEvict = unpinnedCount - _maxActiveTurns;
                for (int i = 0; i < _history.Count && turnsToCompact.Count < toEvict;)
                {
                    if (!_history[i].IsAnchor)
                    {
                        turnsToCompact.Add(_history[i]);
                        _history.RemoveAt(i);
                    }
                    else
                    {
                        i++;
                    }
                }
            }
        }

        if (turnsToCompact.Count > 0)
        {
            string updatedSummary = await _summarizer.SummarizeTurnsAsync(
                _currentSummary,
                turnsToCompact,
                cancellationToken).ConfigureAwait(false);

            lock (_sync)
            {
                _currentSummary = updatedSummary;
                _compactedTurnCount += turnsToCompact.Count;
            }
        }
    }

    /// <summary>
    /// Generates a point-in-time compacted context snapshot for injection into reasoning prompts.
    /// </summary>
    /// <returns>A compacted context snapshot.</returns>
    public CompactedContextSnapshot GetCompactedSnapshot()
    {
        lock (_sync)
        {
            return new CompactedContextSnapshot(
                _currentSummary,
                _history.ToList(),
                _compactedTurnCount + _history.Count,
                _compactedTurnCount * 120); // Baseline token savings estimate
        }
    }
}
