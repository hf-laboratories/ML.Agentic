namespace HFLabs.ML.Agentic;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Represents the fundamental contract for an autonomous or specialized cognitive agent.
/// </summary>
/// <typeparam name="TContext">The strongly typed input context for the agent.</typeparam>
/// <typeparam name="TResult">The strongly typed output result produced by the agent.</typeparam>
public interface IAgent<in TContext, TResult>
{
    /// <summary>
    /// Gets the unique, human-readable name of the agent.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Executes the agent's core cognitive or operational task.
    /// </summary>
    /// <param name="context">The contextual inputs required for execution.</param>
    /// <param name="cancellationToken">A token allowing cooperative cancellation.</param>
    /// <returns>A structured result envelope containing the outcome, telemetry, and optional error detail.</returns>
    Task<AgentResult<TResult>> ExecuteAsync(TContext context, CancellationToken cancellationToken = default);
}

/// <summary>
/// Standardized execution result envelope for agent operations conforming to PAT-CS-015 and PAT-CS-245.
/// </summary>
/// <typeparam name="T">The result payload type.</typeparam>
public sealed record AgentResult<T>(
    bool Success,
    T? Value,
    string? Error = null,
    int IterationsUsed = 1,
    IReadOnlyList<string>? Trace = null)
{
    /// <summary>
    /// Creates a successful agent outcome.
    /// </summary>
    public static AgentResult<T> Ok(T value, int iterations = 1, IReadOnlyList<string>? trace = null) =>
        new(true, value, null, iterations, trace);

    /// <summary>
    /// Creates a failed agent outcome with an explanatory error message.
    /// </summary>
    public static AgentResult<T> Fail(string error, int iterations = 1, IReadOnlyList<string>? trace = null) =>
        new(false, default, error, iterations, trace);
}
