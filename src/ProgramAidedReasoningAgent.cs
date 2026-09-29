namespace HFLabs.ML.Agentic;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Result envelope produced by an expression execution sandbox.
/// </summary>
/// <param name="Success">Whether execution completed without exception.</param>
/// <param name="Output">Output value or evaluation return payload.</param>
/// <param name="Error">Error description if evaluation faulted.</param>
/// <param name="ExecutionTime">Wall-clock execution duration.</param>
public sealed record ProgramExecutionResult(
    bool Success,
    object? Output,
    string? Error = null,
    TimeSpan ExecutionTime = default)
{
    /// <summary>
    /// Creates a successful execution outcome.
    /// </summary>
    public static ProgramExecutionResult Ok(object output, TimeSpan duration = default) =>
        new(true, output, null, duration);

    /// <summary>
    /// Creates a failed execution outcome with error detail.
    /// </summary>
    public static ProgramExecutionResult Fail(string error, TimeSpan duration = default) =>
        new(false, null, error, duration);
}

/// <summary>
/// Contract for compiling and executing code expressions, scripts, or queries conforming to PAT-CS-264.
/// </summary>
public interface IExpressionExecutor
{
    /// <summary>
    /// Evaluates or executes a synthesized code expression or script in a sandboxed runtime.
    /// </summary>
    /// <param name="codeExpression">Synthesized code string (e.g., C# expression, LINQ query, Cypher path).</param>
    /// <param name="parameters">Input parameters exposed to the code environment.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Execution result.</returns>
    Task<ProgramExecutionResult> ExecuteExpressionAsync(
        string codeExpression,
        IReadOnlyDictionary<string, object> parameters,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Agent that synthesizes executable code to compute exact, deterministic answers conforming to PAT-CS-264.
/// </summary>
/// <typeparam name="TContext">Task context type.</typeparam>
/// <typeparam name="TResult">Final result type.</typeparam>
public sealed class ProgramAidedReasoningAgent<TContext, TResult>(
    string name,
    Func<TContext, CancellationToken, Task<string>> codeGenerator,
    IExpressionExecutor executor,
    Func<TContext, object, TResult> resultProjector)
    : IAgent<TContext, TResult>
{
    private readonly Func<TContext, CancellationToken, Task<string>> _codeGenerator =
        codeGenerator ?? throw new ArgumentNullException(nameof(codeGenerator));
    private readonly IExpressionExecutor _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    private readonly Func<TContext, object, TResult> _resultProjector =
        resultProjector ?? throw new ArgumentNullException(nameof(resultProjector));

    /// <inheritdoc/>
    public string Name { get; } = name ?? throw new ArgumentNullException(nameof(name));

    /// <inheritdoc/>
    public async Task<AgentResult<TResult>> ExecuteAsync(
        TContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        List<string> trace = [$"[{Name}] Synthesizing code expression for input."];

        try
        {
            string code = await _codeGenerator(context, cancellationToken).ConfigureAwait(false);
            trace.Add($"[{Name}] Code generated: {code}");

            var execResult = await _executor.ExecuteExpressionAsync(
                code,
                new Dictionary<string, object>(),
                cancellationToken).ConfigureAwait(false);

            if (!execResult.Success || execResult.Output is null)
            {
                string error = $"Program evaluation failed: {execResult.Error}";
                trace.Add($"[{Name}] {error}");
                return AgentResult<TResult>.Fail(error, iterations: 1, trace);
            }

            trace.Add($"[{Name}] Program evaluated successfully in {execResult.ExecutionTime.TotalMilliseconds:F2}ms.");
            TResult finalResult = _resultProjector(context, execResult.Output);

            return AgentResult<TResult>.Ok(finalResult, iterations: 1, trace);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            trace.Add($"[{Name}] Execution exception: {ex.Message}");
            return AgentResult<TResult>.Fail(ex.Message, iterations: 1, trace);
        }
    }
}
