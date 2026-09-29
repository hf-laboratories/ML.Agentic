namespace HFLabs.ML.Agentic;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// A typed knowledge atom posted to the shared agent blackboard.
/// </summary>
/// <typeparam name="T">Payload type.</typeparam>
/// <param name="Key">Unique slot or topic key.</param>
/// <param name="Value">Typed data value.</param>
/// <param name="AuthorAgent">Name of the agent that posted or revised the slot.</param>
/// <param name="Revision">Monotonically increasing version number.</param>
/// <param name="UpdatedAt">UTC timestamp of update.</param>
public sealed record BlackboardEntry<T>(
    string Key,
    T Value,
    string AuthorAgent,
    long Revision,
    DateTime UpdatedAt);

/// <summary>
/// Contract for a shared cognitive blackboard with revision tracking and reactive subscriptions conforming to PAT-CS-256.
/// </summary>
public interface IAgentBlackboard
{
    /// <summary>
    /// Reads a typed slot from the blackboard.
    /// </summary>
    /// <typeparam name="T">Expected payload type.</typeparam>
    /// <param name="key">Slot key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The typed entry if found and compatible; otherwise null.</returns>
    Task<BlackboardEntry<T>?> ReadAsync<T>(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes or updates a typed slot on the blackboard, incrementing its revision and notifying subscribers.
    /// </summary>
    /// <typeparam name="T">Payload type.</typeparam>
    /// <param name="key">Slot key.</param>
    /// <param name="value">Data value.</param>
    /// <param name="authorAgent">Name of the submitting agent.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The persisted entry with new revision metadata.</returns>
    Task<BlackboardEntry<T>> WriteAsync<T>(string key, T value, string authorAgent, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a snapshot of all entries currently on the blackboard.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>All active entries.</returns>
    Task<IReadOnlyList<BlackboardEntry<object>>> GetAllEntriesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Subscribes an observer callback to slots matching a key prefix.
    /// </summary>
    /// <param name="keyPrefix">Key prefix filter (case-insensitive).</param>
    /// <param name="onSlotChanged">Callback invoked with (key, value, authorAgent) when a matching slot is written.</param>
    /// <returns>A disposable subscription token.</returns>
    IDisposable Subscribe(string keyPrefix, Action<string, object, string> onSlotChanged);
}

/// <summary>
/// Thread-safe shared cognitive workspace implementing atomic revision tracking and observer pub/sub conforming to PAT-CS-256.
/// </summary>
public sealed class AgentBlackboard : IAgentBlackboard
{
    private readonly Dictionary<string, BlackboardEntry<object>> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Prefix, Action<string, object, string> Callback)> _subscribers = [];
    private readonly Lock _sync = new();
    private long _currentRevision;

    /// <summary>
    /// Gets the current highest revision number across all blackboard slots.
    /// </summary>
    public long CurrentRevision
    {
        get
        {
            lock (_sync)
            {
                return _currentRevision;
            }
        }
    }

    /// <inheritdoc/>
    public Task<BlackboardEntry<T>?> ReadAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        lock (_sync)
        {
            if (_entries.TryGetValue(key, out var entry) && entry.Value is T typedValue)
            {
                return Task.FromResult<BlackboardEntry<T>?>(
                    new BlackboardEntry<T>(entry.Key, typedValue, entry.AuthorAgent, entry.Revision, entry.UpdatedAt));
            }

            return Task.FromResult<BlackboardEntry<T>?>(null);
        }
    }

    /// <inheritdoc/>
    public Task<BlackboardEntry<T>> WriteAsync<T>(string key, T value, string authorAgent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(authorAgent);

        List<Action<string, object, string>> callbacksToInvoke = [];
        BlackboardEntry<T> result;

        lock (_sync)
        {
            _currentRevision++;
            var entry = new BlackboardEntry<object>(key, value, authorAgent, _currentRevision, DateTime.UtcNow);
            _entries[key] = entry;
            result = new BlackboardEntry<T>(key, value, authorAgent, _currentRevision, entry.UpdatedAt);

            for (int i = 0; i < _subscribers.Count; i++)
            {
                var sub = _subscribers[i];
                if (key.StartsWith(sub.Prefix, StringComparison.OrdinalIgnoreCase))
                {
                    callbacksToInvoke.Add(sub.Callback);
                }
            }
        }

        // Invoke callbacks outside the lock to prevent deadlocks from re-entrant reads/writes
        for (int i = 0; i < callbacksToInvoke.Count; i++)
        {
            try
            {
                callbacksToInvoke[i](key, value, authorAgent);
            }
            catch
            {
                // Isolate observer exceptions from mutating thread
            }
        }

        return Task.FromResult(result);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<BlackboardEntry<object>>> GetAllEntriesAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            return Task.FromResult<IReadOnlyList<BlackboardEntry<object>>>(_entries.Values.ToList());
        }
    }

    /// <inheritdoc/>
    public IDisposable Subscribe(string keyPrefix, Action<string, object, string> onSlotChanged)
    {
        ArgumentNullException.ThrowIfNull(keyPrefix);
        ArgumentNullException.ThrowIfNull(onSlotChanged);

        lock (_sync)
        {
            var subscription = (keyPrefix, onSlotChanged);
            _subscribers.Add(subscription);
            return new SubscriptionToken(() =>
            {
                lock (_sync)
                {
                    _subscribers.Remove(subscription);
                }
            });
        }
    }

    private sealed class SubscriptionToken(Action onDispose) : IDisposable
    {
        private Action? _dispose = onDispose;

        public void Dispose()
        {
            Interlocked.Exchange(ref _dispose, null)?.Invoke();
        }
    }
}
