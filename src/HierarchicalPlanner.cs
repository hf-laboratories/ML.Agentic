namespace HFLabs.ML.Agentic;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Status lifecycle state for an individual plan step.
/// </summary>
public enum StepStatus
{
    /// <summary>
    /// Step is waiting to be dispatched once its prerequisites complete.
    /// </summary>
    Pending,

    /// <summary>
    /// Step is actively executing.
    /// </summary>
    Running,

    /// <summary>
    /// Step executed successfully and generated output.
    /// </summary>
    Completed,

    /// <summary>
    /// Step encountered an error during execution.
    /// </summary>
    Failed,

    /// <summary>
    /// Step was skipped due to upstream branch decisions.
    /// </summary>
    Skipped
}

/// <summary>
/// An individual action node within an execution plan.
/// </summary>
/// <param name="Id">Unique identifier for the step.</param>
/// <param name="Description">Actionable description of the task.</param>
/// <param name="DependsOn">List of prerequisite step IDs that must complete before this step runs.</param>
/// <param name="Status">Current execution status.</param>
/// <param name="Result">Output value produced upon success.</param>
/// <param name="Error">Error message if execution failed.</param>
public sealed record PlanStep(
    string Id,
    string Description,
    IReadOnlyList<string> DependsOn,
    StepStatus Status = StepStatus.Pending,
    string? Result = null,
    string? Error = null);

/// <summary>
/// Structured plan representing a goal decomposed into sequential or dependency-ordered steps.
/// </summary>
/// <param name="Goal">The top-level objective.</param>
/// <param name="Steps">Ordered collection of plan steps.</param>
/// <param name="IsCompleted">Whether all required steps have completed or reached terminal states.</param>
/// <param name="ReplansUsed">Number of dynamic re-planning iterations performed.</param>
public sealed record ExecutionPlan(
    string Goal,
    IReadOnlyList<PlanStep> Steps,
    bool IsCompleted,
    int ReplansUsed = 0);

/// <summary>
/// Contract for breaking down goals into structured plans and dynamically revising them on failure.
/// </summary>
/// <typeparam name="TContext">Context type available to the planner.</typeparam>
public interface ITaskPlanner<in TContext>
{
    /// <summary>
    /// Decomposes a high-level goal into an initial execution plan.
    /// </summary>
    /// <param name="goal">Target objective.</param>
    /// <param name="context">Task context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Initial execution plan.</returns>
    Task<ExecutionPlan> CreatePlanAsync(
        string goal,
        TContext context,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates an alternative or updated continuation plan when a step encounters a failure.
    /// </summary>
    /// <param name="currentPlan">The active plan with the failed step.</param>
    /// <param name="failedStep">The step that faulted.</param>
    /// <param name="failureReason">The error message or failure telemetry.</param>
    /// <param name="context">Task context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Revised execution plan.</returns>
    Task<ExecutionPlan> ReplanAsync(
        ExecutionPlan currentPlan,
        PlanStep failedStep,
        string failureReason,
        TContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Executes decomposed plans sequentially along dependency graphs and invokes replanning upon failure conforming to PAT-CS-252.
/// </summary>
/// <typeparam name="TContext">The contextual state type.</typeparam>
public sealed class HierarchicalPlanExecutor<TContext>(
    ITaskPlanner<TContext> planner,
    Func<PlanStep, TContext, CancellationToken, Task<string>> stepExecutor,
    int maxReplans = 2)
{
    private readonly ITaskPlanner<TContext> _planner = planner ?? throw new ArgumentNullException(nameof(planner));
    private readonly Func<PlanStep, TContext, CancellationToken, Task<string>> _stepExecutor = stepExecutor ?? throw new ArgumentNullException(nameof(stepExecutor));
    private readonly int _maxReplans = Math.Max(0, maxReplans);

    /// <summary>
    /// Executes the plan towards the specified goal with dynamic replanning on step failure.
    /// </summary>
    /// <param name="goal">The objective.</param>
    /// <param name="context">Contextual input.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The final execution plan state.</returns>
    public async Task<ExecutionPlan> ExecuteGoalAsync(
        string goal,
        TContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(goal);

        var plan = await _planner.CreatePlanAsync(goal, context, cancellationToken).ConfigureAwait(false);
        int replansUsed = 0;

        while (!plan.IsCompleted)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Find next runnable step: status is Pending and all dependencies are Completed
            var nextStep = plan.Steps.FirstOrDefault(s =>
                s.Status == StepStatus.Pending &&
                s.DependsOn.All(depId => plan.Steps.Any(p => p.Id == depId && p.Status == StepStatus.Completed)));

            if (nextStep == null)
            {
                // No more runnable steps; either all are done or dependencies failed
                bool anyFailed = plan.Steps.Any(s => s.Status == StepStatus.Failed);
                return plan with { IsCompleted = !anyFailed };
            }

            // Mark step as running
            plan = UpdateStep(plan, nextStep.Id, StepStatus.Running);

            try
            {
                string result = await _stepExecutor(nextStep, context, cancellationToken).ConfigureAwait(false);
                plan = UpdateStep(plan, nextStep.Id, StepStatus.Completed, result: result);
            }
            catch (Exception ex)
            {
                plan = UpdateStep(plan, nextStep.Id, StepStatus.Failed, error: ex.Message);

                if (replansUsed < _maxReplans)
                {
                    replansUsed++;
                    plan = await _planner.ReplanAsync(plan, nextStep, ex.Message, context, cancellationToken).ConfigureAwait(false);
                    plan = plan with { ReplansUsed = replansUsed };
                }
                else
                {
                    // Exhausted replanning quota
                    return plan with { IsCompleted = false };
                }
            }
        }

        return plan;
    }

    private static ExecutionPlan UpdateStep(
        ExecutionPlan plan,
        string stepId,
        StepStatus status,
        string? result = null,
        string? error = null)
    {
        var updatedSteps = plan.Steps.Select(s =>
            s.Id == stepId
                ? s with { Status = status, Result = result ?? s.Result, Error = error ?? s.Error }
                : s).ToList();

        bool allDone = updatedSteps.All(s => s.Status == StepStatus.Completed || s.Status == StepStatus.Skipped);
        return plan with { Steps = updatedSteps, IsCompleted = allDone };
    }
}
