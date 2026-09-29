namespace HFLabs.ML.Agentic;

using System.Collections.Concurrent;

/// <summary>Publishes agent lifecycle events.</summary>
public interface IAgentEventPublisher<in TEvent>
{
    Task PublishAsync(TEvent evt, CancellationToken ct = default);
}

/// <summary>Receives events relayed by an <see cref="AgentEventBus{TEvent}"/>.</summary>
public interface IAgentEventSubscriber<in TEvent>
{
    string SubscriberId { get; }

    Task OnEventAsync(TEvent evt, CancellationToken ct);
}

/// <summary>
/// In-memory pub/sub fan-out. Delivery is best-effort and isolated per subscriber: a subscriber that
/// throws never stops delivery to the others and never propagates back to the publisher, so a broken
/// console/log/ledger sink cannot break the orchestration that is publishing.
/// </summary>
public class AgentEventBus<TEvent> : IAgentEventPublisher<TEvent>
{
    private readonly ConcurrentDictionary<string, IAgentEventSubscriber<TEvent>> _subscribers = new(StringComparer.Ordinal);

    /// <summary>Subscribes (or replaces the subscriber with the same id).</summary>
    public void Subscribe(IAgentEventSubscriber<TEvent> subscriber)
    {
        ArgumentNullException.ThrowIfNull(subscriber);
        _subscribers[subscriber.SubscriberId] = subscriber;
    }

    public bool Unsubscribe(string subscriberId) => _subscribers.TryRemove(subscriberId, out _);

    public IReadOnlyCollection<string> SubscriberIds => _subscribers.Keys.ToArray();

    public async Task PublishAsync(TEvent evt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(evt);
        foreach (IAgentEventSubscriber<TEvent> subscriber in _subscribers.Values)
        {
            try
            {
                await subscriber.OnEventAsync(evt, ct).ConfigureAwait(false);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // Isolation by design: one sink's failure must not affect the others or the publisher.
            }
        }
    }
}
