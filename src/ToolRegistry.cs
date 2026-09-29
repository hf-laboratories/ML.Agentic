namespace HFLabs.ML.Agentic;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Declares the specification for an individual tool parameter.
/// </summary>
/// <param name="Name">Parameter name.</param>
/// <param name="Type">Expected data type name (e.g., "string", "int", "boolean").</param>
/// <param name="Description">Human- or model-readable explanation of the parameter's purpose.</param>
/// <param name="Required">Whether the parameter must be supplied upon invocation.</param>
public sealed record ToolParameterSchema(
    string Name,
    string Type,
    string Description,
    bool Required = true);

/// <summary>
/// Declarative metadata schema describing a tool's capabilities, inputs, and safety tier.
/// </summary>
/// <param name="Name">Unique tool name.</param>
/// <param name="Description">Description of the tool's behavior and outputs.</param>
/// <param name="Parameters">Declared parameter schemas.</param>
/// <param name="RequiresApproval">Whether execution requires Human-in-the-Loop approval under PAT-CS-255.</param>
public sealed record ToolDefinition(
    string Name,
    string Description,
    IReadOnlyList<ToolParameterSchema> Parameters,
    bool RequiresApproval = false);

/// <summary>
/// Execution outcome envelope for a tool invocation.
/// </summary>
/// <param name="Success">Whether the tool executed without error.</param>
/// <param name="Output">Output payload or serialized string result.</param>
/// <param name="Error">Error description if execution failed.</param>
/// <param name="Duration">Wall-clock execution duration.</param>
public sealed record ToolExecutionResult(
    bool Success,
    string? Output,
    string? Error = null,
    TimeSpan Duration = default)
{
    /// <summary>
    /// Creates a successful tool execution outcome.
    /// </summary>
    public static ToolExecutionResult Ok(string output, TimeSpan duration = default) =>
        new(true, output, null, duration);

    /// <summary>
    /// Creates a failed tool execution outcome with error detail.
    /// </summary>
    public static ToolExecutionResult Fail(string error, TimeSpan duration = default) =>
        new(false, null, error, duration);
}

/// <summary>
/// Contract for an executable agent tool conforming to PAT-CS-251.
/// </summary>
public interface ITool
{
    /// <summary>
    /// Gets the tool's declarative specification and schema.
    /// </summary>
    ToolDefinition Definition { get; }

    /// <summary>
    /// Executes the tool with the supplied argument dictionary.
    /// </summary>
    /// <param name="arguments">Key-value parameter pairs matching the tool schema.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Execution result envelope.</returns>
    Task<ToolExecutionResult> ExecuteAsync(
        IReadOnlyDictionary<string, object> arguments,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Manages tool registration, parameter validation, and sandboxed execution conforming to PAT-CS-251.
/// </summary>
public sealed class ToolRegistry(TimeSpan? defaultTimeout = null)
{
    private readonly Dictionary<string, ITool> _tools = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _defaultTimeout = defaultTimeout ?? TimeSpan.FromSeconds(30);
    private readonly Lock _sync = new();

    /// <summary>
    /// Registers a tool in the catalog.
    /// </summary>
    /// <param name="tool">The tool implementation to register.</param>
    public void Register(ITool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        lock (_sync)
        {
            _tools[tool.Definition.Name] = tool;
        }
    }

    /// <summary>
    /// Attempts to retrieve a registered tool by name.
    /// </summary>
    /// <param name="toolName">The unique tool name.</param>
    /// <param name="tool">The matched tool instance if found.</param>
    /// <returns>True if the tool is registered; otherwise false.</returns>
    public bool TryGetTool(string toolName, out ITool? tool)
    {
        lock (_sync)
        {
            return _tools.TryGetValue(toolName, out tool);
        }
    }

    /// <summary>
    /// Lists all registered tool definitions.
    /// </summary>
    /// <returns>A read-only snapshot of available tool definitions.</returns>
    public IReadOnlyList<ToolDefinition> ListTools()
    {
        lock (_sync)
        {
            return _tools.Values.Select(t => t.Definition).ToList();
        }
    }

    /// <summary>
    /// Validates parameters and executes a named tool inside a sandboxed timeout envelope.
    /// </summary>
    /// <param name="toolName">The name of the tool to execute.</param>
    /// <param name="arguments">Input parameters.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A structured execution result.</returns>
    public async Task<ToolExecutionResult> ExecuteToolAsync(
        string toolName,
        IReadOnlyDictionary<string, object> arguments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(toolName);
        ArgumentNullException.ThrowIfNull(arguments);

        ITool? tool;
        lock (_sync)
        {
            if (!_tools.TryGetValue(toolName, out tool))
            {
                return ToolExecutionResult.Fail($"Tool '{toolName}' is not registered in catalog.");
            }
        }

        // Schema validation: verify all required parameters are present
        foreach (var param in tool.Definition.Parameters)
        {
            if (param.Required && !arguments.ContainsKey(param.Name))
            {
                return ToolExecutionResult.Fail($"Missing required parameter '{param.Name}' for tool '{toolName}'.");
            }
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_defaultTimeout);

        DateTime start = DateTime.UtcNow;
        try
        {
            return await tool.ExecuteAsync(arguments, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ToolExecutionResult.Fail(
                $"Tool '{toolName}' execution timed out after {_defaultTimeout.TotalSeconds}s.",
                DateTime.UtcNow - start);
        }
        catch (Exception ex)
        {
            return ToolExecutionResult.Fail(
                $"Tool '{toolName}' unhandled execution fault: {ex.Message}",
                DateTime.UtcNow - start);
        }
    }
}
