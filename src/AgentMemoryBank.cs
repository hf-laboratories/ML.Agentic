namespace HFLabs.ML.Agentic;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Represents a structured memory atom stored in working or episodic memory.
/// </summary>
/// <param name="Id">Unique identifier for the memory record.</param>
/// <param name="Content">The text or serialized cognitive payload.</param>
/// <param name="Tags">Metadata tags for semantic filtering and categorization.</param>
/// <param name="Timestamp">UTC timestamp when the memory was registered.</param>
/// <param name="Embedding">Optional dense vector embedding for semantic similarity search.</param>
public sealed record AgentMemoryEntry(
    string Id,
    string Content,
    IReadOnlyDictionary<string, string> Tags,
    DateTime Timestamp,
    float[]? Embedding = null);

/// <summary>
/// Contract for multi-tiered working and episodic memory stores conforming to PAT-CS-249.
/// </summary>
public interface IAgentMemoryStore
{
    /// <summary>
    /// Stores an entry in working memory (bounded) and persists it to episodic memory.
    /// </summary>
    /// <param name="entry">The memory entry to store.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task StoreAsync(AgentMemoryEntry entry, CancellationToken cancellationToken = default);

    /// <summary>
    /// Recalls episodic memories matching a semantic search query or tag criteria.
    /// </summary>
    /// <param name="query">The text query or keyword.</param>
    /// <param name="maxResults">The maximum number of entries to return.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A list of matching memory entries ordered by relevance and recency.</returns>
    Task<IReadOnlyList<AgentMemoryEntry>> RecallAsync(string query, int maxResults = 5, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves all current items in working memory.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A snapshot of active working memory.</returns>
    Task<IReadOnlyList<AgentMemoryEntry>> GetWorkingMemoryAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Flushes working memory without clearing long-term episodic storage.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task ClearWorkingMemoryAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Bounded multi-tier memory store implementing working memory capacity capping and episodic accumulation conforming to PAT-CS-249.
/// </summary>
public sealed class BoundedAgentMemoryBank(int workingMemoryCapacity = 20) : IAgentMemoryStore
{
    private readonly int _workingMemoryCapacity = workingMemoryCapacity > 0
        ? workingMemoryCapacity
        : throw new ArgumentOutOfRangeException(nameof(workingMemoryCapacity), "Capacity must be positive.");

    private readonly List<AgentMemoryEntry> _workingMemory = [];
    private readonly List<AgentMemoryEntry> _episodicStore = [];
    private readonly Lock _sync = new();

    /// <summary>
    /// Gets the count of active items in working memory.
    /// </summary>
    public int WorkingMemoryCount
    {
        get
        {
            lock (_sync)
            {
                return _workingMemory.Count;
            }
        }
    }

    /// <summary>
    /// Gets the count of accumulated items in episodic memory.
    /// </summary>
    public int EpisodicMemoryCount
    {
        get
        {
            lock (_sync)
            {
                return _episodicStore.Count;
            }
        }
    }

    /// <inheritdoc/>
    public Task StoreAsync(AgentMemoryEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        lock (_sync)
        {
            if (_workingMemory.Count >= _workingMemoryCapacity)
            {
                _workingMemory.RemoveAt(0); // FIFO eviction from working memory
            }

            _workingMemory.Add(entry);
            _episodicStore.Add(entry);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<AgentMemoryEntry>> RecallAsync(string query, int maxResults = 5, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (maxResults <= 0)
        {
            return Task.FromResult<IReadOnlyList<AgentMemoryEntry>>([]);
        }

        lock (_sync)
        {
            var matches = _episodicStore
                .Where(e => e.Content.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                            e.Tags.Values.Any(v => v.Contains(query, StringComparison.OrdinalIgnoreCase)) ||
                            e.Tags.Keys.Any(k => k.Contains(query, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(e => e.Timestamp)
                .Take(maxResults)
                .ToList();

            return Task.FromResult<IReadOnlyList<AgentMemoryEntry>>(matches);
        }
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<AgentMemoryEntry>> GetWorkingMemoryAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            return Task.FromResult<IReadOnlyList<AgentMemoryEntry>>(_workingMemory.ToList());
        }
    }

    /// <inheritdoc/>
    public Task ClearWorkingMemoryAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            _workingMemory.Clear();
        }

        return Task.CompletedTask;
    }
}
