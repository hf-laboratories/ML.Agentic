namespace HFLabs.ML.Agentic;

/// <summary>Outcome of <see cref="DependencyLayerExecutor{TTask,TOutcome}.RunAsync"/>.</summary>
/// <param name="Outcomes">Outcome per task id, including unresolvable tasks.</param>
/// <param name="LayerCount">Number of layers dispatched.</param>
/// <param name="UnresolvableTaskIds">Tasks never dispatched because their dependencies could not resolve (cycle or unknown id).</param>
public sealed record DependencyLayerRunResult<TOutcome>(
    IReadOnlyDictionary<string, TOutcome> Outcomes,
    int LayerCount,
    IReadOnlyList<string> UnresolvableTaskIds);

/// <summary>
/// Runs a task DAG one dependency layer at a time: every task whose dependencies all have outcomes forms the
/// next layer, a layer's tasks run concurrently, and the executor waits for the whole layer before advancing.
/// Tasks left over when no layer can form (a cycle, or a dependency id outside the set) are handed to
/// <c>onUnresolvable</c> instead of spinning. The executor does not interpret outcomes: whether a failed
/// dependency should skip its dependents is the caller's policy, applied inside <c>execute</c> using the
/// outcomes it receives.
/// </summary>
public sealed class DependencyLayerExecutor<TTask, TOutcome>
{
    private readonly Func<TTask, string> _idOf;
    private readonly Func<TTask, IEnumerable<string>> _dependsOn;
    private readonly Func<TTask, int> _orderOf;

    public DependencyLayerExecutor(Func<TTask, string> idOf, Func<TTask, IEnumerable<string>> dependsOn, Func<TTask, int>? orderOf = null)
    {
        _idOf = idOf ?? throw new ArgumentNullException(nameof(idOf));
        _dependsOn = dependsOn ?? throw new ArgumentNullException(nameof(dependsOn));
        _orderOf = orderOf ?? (_ => 0);
    }

    /// <param name="tasks">The DAG's tasks. Ids must be unique.</param>
    /// <param name="execute">Runs one task; receives the outcomes resolved so far (all of its dependencies included).</param>
    /// <param name="onUnresolvable">Produces the outcome for a task whose dependencies can never resolve.</param>
    /// <param name="onLayerStarted">Optional notification before each layer runs (layer index, tasks in the layer).</param>
    public async Task<DependencyLayerRunResult<TOutcome>> RunAsync(
        IEnumerable<TTask> tasks,
        Func<TTask, IReadOnlyDictionary<string, TOutcome>, CancellationToken, Task<TOutcome>> execute,
        Func<TTask, CancellationToken, Task<TOutcome>> onUnresolvable,
        Func<int, IReadOnlyList<TTask>, CancellationToken, Task>? onLayerStarted = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(execute);
        ArgumentNullException.ThrowIfNull(onUnresolvable);

        TTask[] all = tasks.ToArray();
        HashSet<string> ids = new(StringComparer.Ordinal);
        foreach (TTask task in all)
        {
            if (!ids.Add(_idOf(task)))
            {
                throw new ArgumentException($"Duplicate task id '{_idOf(task)}'.", nameof(tasks));
            }
        }

        Dictionary<string, TOutcome> outcomes = new(StringComparer.Ordinal);
        List<TTask> remaining = [.. all];
        List<string> unresolvable = [];
        int layerIndex = 0;

        while (remaining.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            TTask[] layer = remaining
                .Where(t => _dependsOn(t).All(outcomes.ContainsKey))
                .OrderBy(_orderOf)
                .ToArray();

            if (layer.Length == 0)
            {
                foreach (TTask stuck in remaining.OrderBy(_orderOf))
                {
                    outcomes[_idOf(stuck)] = await onUnresolvable(stuck, ct).ConfigureAwait(false);
                    unresolvable.Add(_idOf(stuck));
                }

                break;
            }

            if (onLayerStarted is not null)
            {
                await onLayerStarted(layerIndex, layer, ct).ConfigureAwait(false);
            }

            IReadOnlyDictionary<string, TOutcome> snapshot = new Dictionary<string, TOutcome>(outcomes, StringComparer.Ordinal);
            TOutcome[] layerOutcomes = await Task.WhenAll(layer.Select(t => execute(t, snapshot, ct))).ConfigureAwait(false);
            for (int i = 0; i < layer.Length; i++)
            {
                outcomes[_idOf(layer[i])] = layerOutcomes[i];
                remaining.Remove(layer[i]);
            }

            layerIndex++;
        }

        return new DependencyLayerRunResult<TOutcome>(outcomes, layerIndex, unresolvable);
    }
}
