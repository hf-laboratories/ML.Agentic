namespace HFLabs.ML.Agentic;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Operational priority levels for agent tasks.
/// </summary>
public enum TaskPriority
{
    /// <summary>
    /// Background asynchronous maintenance, re-indexing, or offline batch sweeps.
    /// </summary>
    Background = 0,

    /// <summary>
    /// Standard priority for routine agent tasks.
    /// </summary>
    Normal = 1,

    /// <summary>
    /// Elevated priority for interactive user requests.
    /// </summary>
    High = 2,

    /// <summary>
    /// Top priority for critical safety alerts, health probes, or sovereign boundary enforcement.
    /// </summary>
    Critical = 3
}

/// <summary>
/// A strongly typed task envelope assigned an operational priority and creation timestamp.
/// </summary>
/// <typeparam name="TContext">Task context type.</typeparam>
/// <typeparam name="TResult">Task result type.</typeparam>
/// <param name="Id">Unique task identifier.</param>
/// <param name="BasePriority">Initial assigned priority.</param>
/// <param name="Context">Contextual input payload.</param>
/// <param name="ExecutionDelegate">Execution logic.</param>
/// <param name="CreatedAt">UTC creation timestamp.</param>
public sealed record PrioritizedTask<TContext, TResult>(
    string Id,
    TaskPriority BasePriority,
    TContext Context,
    Func<TContext, CancellationToken, Task<TResult>> ExecutionDelegate,
    DateTime CreatedAt)
{
    /// <summary>
    /// Calculates dynamic priority factoring in age to prevent starvation of low-priority tasks.
    /// </summary>
    /// <param name="agingWindow">Time window over which priority increases by 1 tier.</param>
    /// <returns>Effective priority score.</returns>
    public double CalculateEffectivePriority(TimeSpan agingWindow)
    {
        if (agingWindow <= TimeSpan.Zero)
        {
            return (double)BasePriority;
        }

        double ageRatio = (DateTime.UtcNow - CreatedAt).TotalSeconds / agingWindow.TotalSeconds;
        // Age boost capped at 3.0 tiers
        return (double)BasePriority + Math.Clamp(ageRatio, 0.0, 3.0);
    }
}

/// <summary>
/// Bounded concurrency scheduler prioritizing execution and preventing starvation conforming to PAT-CS-261.
/// </summary>
public sealed class PrioritizedAgentScheduler(
    int maxConcurrentTasks = 4,
    TimeSpan? agingWindow = null) : IDisposable
{
    private readonly SemaphoreSlim _concurrencySemaphore = new(Math.Max(1, maxConcurrentTasks), Math.Max(1, maxConcurrentTasks));
    private readonly TimeSpan _agingWindow = agingWindow ?? TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets the aging window configured for dynamic priority boosting.
    /// </summary>
    public TimeSpan AgingWindow => _agingWindow;

    /// <summary>
    /// Schedules and immediately acquires execution quota for a prioritized task.
    /// </summary>
    /// <typeparam name="TContext">Context type.</typeparam>
    /// <typeparam name="TResult">Result type.</typeparam>
    /// <param name="task">The prioritized task.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Task execution outcome.</returns>
    public async Task<TResult> ScheduleAndExecuteAsync<TContext, TResult>(
        PrioritizedTask<TContext, TResult> task,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);

        await _concurrencySemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await task.ExecutionDelegate(task.Context, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _concurrencySemaphore.Release();
        }
    }

    /// <summary>
    /// Selects the highest effective priority task from a candidate pool factoring in dynamic aging.
    /// </summary>
    /// <typeparam name="TContext">Context type.</typeparam>
    /// <typeparam name="TResult">Result type.</typeparam>
    /// <param name="tasks">Available pending tasks.</param>
    /// <returns>The highest priority task.</returns>
    public PrioritizedTask<TContext, TResult> SelectNextTask<TContext, TResult>(
        IReadOnlyList<PrioritizedTask<TContext, TResult>> tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        if (tasks.Count == 0)
        {
            throw new ArgumentException("Task pool must contain at least one task.", nameof(tasks));
        }

        return tasks.OrderByDescending(t => t.CalculateEffectivePriority(_agingWindow)).First();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _concurrencySemaphore.Dispose();
    }
}
