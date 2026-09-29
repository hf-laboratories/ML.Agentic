namespace HFLabs.ML.Agentic;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Composes an ordered sequence of agent stages, passing transformed context through each step.
/// Conforms to PAT-CS-245.
/// </summary>
public sealed class SequentialAgent<TContext, TResult>(
    string name,
    IReadOnlyList<Func<TContext, CancellationToken, Task<TContext>>> steps,
    Func<TContext, TResult> projector) : IAgent<TContext, TResult>
{
    public string Name { get; } = name ?? throw new ArgumentNullException(nameof(name));

    public async Task<AgentResult<TResult>> ExecuteAsync(TContext context, CancellationToken cancellationToken = default)
    {
        TContext current = context;
        List<string> trace = [];

        try
        {
            for (int i = 0; i < steps.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                trace.Add($"[{Name}] Step {i + 1}/{steps.Count} started");
                current = await steps[i](current, cancellationToken).ConfigureAwait(false);
                trace.Add($"[{Name}] Step {i + 1}/{steps.Count} completed");
            }

            TResult projected = projector(current);
            return AgentResult<TResult>.Ok(projected, steps.Count, trace);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            trace.Add($"[{Name}] Failed: {ex.Message}");
            return AgentResult<TResult>.Fail(ex.Message, trace.Count, trace);
        }
    }
}

/// <summary>
/// Executes multiple agents concurrently over shared or partitioned context, aggregating their results.
/// Conforms to PAT-CS-245.
/// </summary>
public sealed class ParallelAgent<TContext, TItemResult, TAggregateResult>(
    string name,
    IReadOnlyList<IAgent<TContext, TItemResult>> workers,
    Func<IReadOnlyList<TItemResult>, TAggregateResult> aggregator) : IAgent<TContext, TAggregateResult>
{
    public string Name { get; } = name ?? throw new ArgumentNullException(nameof(name));

    public async Task<AgentResult<TAggregateResult>> ExecuteAsync(TContext context, CancellationToken cancellationToken = default)
    {
        List<string> trace = [$"[{Name}] Fan-out to {workers.Count} workers"];

        try
        {
            var tasks = workers.Select(w => w.ExecuteAsync(context, cancellationToken)).ToList();
            AgentResult<TItemResult>[] results = await Task.WhenAll(tasks).ConfigureAwait(false);

            List<TItemResult> successfulItems = [];
            int totalIterations = 0;

            for (int i = 0; i < results.Length; i++)
            {
                var res = results[i];
                totalIterations += res.IterationsUsed;
                if (res.Success && res.Value is not null)
                {
                    successfulItems.Add(res.Value);
                    trace.Add($"[{Name}] Worker '{workers[i].Name}' succeeded");
                }
                else
                {
                    trace.Add($"[{Name}] Worker '{workers[i].Name}' failed: {res.Error}");
                }
            }

            if (successfulItems.Count == 0 && workers.Count > 0)
            {
                return AgentResult<TAggregateResult>.Fail($"All {workers.Count} parallel workers failed", totalIterations, trace);
            }

            TAggregateResult aggregated = aggregator(successfulItems);
            return AgentResult<TAggregateResult>.Ok(aggregated, totalIterations, trace);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            trace.Add($"[{Name}] Parallel aggregation failed: {ex.Message}");
            return AgentResult<TAggregateResult>.Fail(ex.Message, 1, trace);
        }
    }
}

/// <summary>
/// Executes an iterative agent loop until a stopping condition is met or the iteration cap is reached.
/// Conforms to PAT-CS-245.
/// </summary>
public sealed class LoopAgent<TContext, TState>(
    string name,
    Func<TContext, TState, CancellationToken, Task<TState>> stepAction,
    Func<TState, bool> stoppingCondition,
    int maxIterations = 10) : IAgent<TContext, TState>
{
    public string Name { get; } = name ?? throw new ArgumentNullException(nameof(name));

    public async Task<AgentResult<TState>> ExecuteAsync(TContext context, CancellationToken cancellationToken = default)
    {
        List<string> trace = [];
        TState state = default!;
        int iterations = 0;

        try
        {
            while (iterations < maxIterations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                iterations++;
                trace.Add($"[{Name}] Iteration {iterations}/{maxIterations} executing");

                state = await stepAction(context, state, cancellationToken).ConfigureAwait(false);

                if (stoppingCondition(state))
                {
                    trace.Add($"[{Name}] Stopping condition satisfied at iteration {iterations}");
                    return AgentResult<TState>.Ok(state, iterations, trace);
                }
            }

            trace.Add($"[{Name}] Reached maximum iteration ceiling ({maxIterations})");
            return AgentResult<TState>.Ok(state, iterations, trace);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            trace.Add($"[{Name}] Loop faulted at iteration {iterations}: {ex.Message}");
            return AgentResult<TState>.Fail(ex.Message, iterations, trace);
        }
    }
}

/// <summary>
/// Coordinates multiple specialist sub-agents dynamically based on task planning or intent.
/// Conforms to PAT-CS-245.
/// </summary>
public sealed class CoordinatorAgent<TTask, TResult>(
    string name,
    IReadOnlyDictionary<string, IAgent<TTask, TResult>> specialists,
    Func<TTask, IReadOnlyList<string>> planner) : IAgent<TTask, IReadOnlyList<TResult>>
{
    public string Name { get; } = name ?? throw new ArgumentNullException(nameof(name));

    public async Task<AgentResult<IReadOnlyList<TResult>>> ExecuteAsync(TTask task, CancellationToken cancellationToken = default)
    {
        List<string> trace = [];
        IReadOnlyList<string> plan = planner(task);
        trace.Add($"[{Name}] Planned execution steps: [{string.Join(" -> ", plan)}]");

        List<TResult> results = [];
        int totalIterations = 0;

        for (int i = 0; i < plan.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string specialistKey = plan[i];

            if (!specialists.TryGetValue(specialistKey, out var specialist))
            {
                string error = $"Specialist '{specialistKey}' not found in registry";
                trace.Add($"[{Name}] Error: {error}");
                return AgentResult<IReadOnlyList<TResult>>.Fail(error, totalIterations, trace);
            }

            trace.Add($"[{Name}] Delegating subtask to '{specialist.Name}'");
            var stepResult = await specialist.ExecuteAsync(task, cancellationToken).ConfigureAwait(false);
            totalIterations += stepResult.IterationsUsed;

            if (!stepResult.Success || stepResult.Value is null)
            {
                string error = $"Specialist '{specialist.Name}' failed: {stepResult.Error}";
                trace.Add($"[{Name}] {error}");
                return AgentResult<IReadOnlyList<TResult>>.Fail(error, totalIterations, trace);
            }

            results.Add(stepResult.Value);
        }

        return AgentResult<IReadOnlyList<TResult>>.Ok(results, totalIterations, trace);
    }
}
