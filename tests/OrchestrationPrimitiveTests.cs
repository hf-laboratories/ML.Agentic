namespace HFLabs.ML.Agentic.Tests;

using System.Collections.Concurrent;
using HFLabs.ML.Agentic;
using Xunit;

public sealed class AgentEventBusTests
{
    private sealed class Recorder(string id, bool fail = false) : IAgentEventSubscriber<string>
    {
        public ConcurrentQueue<string> Seen { get; } = new();
        public string SubscriberId => id;

        public Task OnEventAsync(string evt, CancellationToken ct)
        {
            if (fail)
            {
                throw new InvalidOperationException("sink broken");
            }

            Seen.Enqueue(evt);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Publish_IsolatesFailingSubscriber_AndDeliversToOthers()
    {
        var bus = new AgentEventBus<string>();
        var good = new Recorder("good");
        bus.Subscribe(new Recorder("bad", fail: true));
        bus.Subscribe(good);

        await bus.PublishAsync("e1");

        Assert.Equal(["e1"], good.Seen);
    }

    [Fact]
    public async Task Subscribe_ReplacesSameId_AndUnsubscribeStopsDelivery()
    {
        var bus = new AgentEventBus<string>();
        var first = new Recorder("sink");
        var second = new Recorder("sink");
        bus.Subscribe(first);
        bus.Subscribe(second);

        await bus.PublishAsync("e1");
        Assert.True(bus.Unsubscribe("sink"));
        await bus.PublishAsync("e2");

        Assert.Empty(first.Seen);
        Assert.Equal(["e1"], second.Seen);
    }
}

public sealed class DependencyLayerExecutorTests
{
    private sealed record Item(string Id, int Order, params string[] Deps);

    private static DependencyLayerExecutor<Item, string> Executor() => new(i => i.Id, i => i.Deps, i => i.Order);

    [Fact]
    public async Task RunsLayersInDependencyOrder_AndReportsLayerCount()
    {
        Item[] items = [new("a", 1), new("b", 2), new("c", 3, "a", "b"), new("d", 4, "c")];
        var layers = new List<string>();

        var result = await Executor().RunAsync(
            items,
            (item, done, _) => Task.FromResult($"{item.Id}<-{string.Join("+", item.Deps.Select(d => done[d]))}"),
            (_, _) => Task.FromResult("unresolvable"),
            (index, layer, _) => { layers.Add($"{index}:{string.Join(",", layer.Select(l => l.Id))}"); return Task.CompletedTask; });

        Assert.Equal(["0:a,b", "1:c", "2:d"], layers);
        Assert.Equal(3, result.LayerCount);
        Assert.Equal("d<-c<-a<-+b<-", result.Outcomes["d"]);
        Assert.Empty(result.UnresolvableTaskIds);
    }

    [Fact]
    public async Task RunsLayerConcurrently()
    {
        var gate = new TaskCompletionSource();
        int started = 0;
        Item[] items = [new("a", 1), new("b", 2)];

        Task<DependencyLayerRunResult<string>> run = Executor().RunAsync(
            items,
            async (item, _, _) =>
            {
                if (Interlocked.Increment(ref started) == 2)
                {
                    gate.SetResult();
                }

                await gate.Task;
                return item.Id;
            },
            (_, _) => Task.FromResult("x"));

        Assert.Same(run, await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(5))));
        Assert.Equal(2, (await run).Outcomes.Count);
    }

    [Fact]
    public async Task CycleAndUnknownDependency_AreUnresolvable_NotInfinite()
    {
        Item[] items = [new("ok", 1), new("x", 2, "y"), new("y", 3, "x"), new("orphan", 4, "missing")];

        var result = await Executor().RunAsync(items, (i, _, _) => Task.FromResult("ran"), (_, _) => Task.FromResult("stuck"));

        Assert.Equal("ran", result.Outcomes["ok"]);
        Assert.Equal(["x", "y", "orphan"], result.UnresolvableTaskIds);
        Assert.All(result.UnresolvableTaskIds, id => Assert.Equal("stuck", result.Outcomes[id]));
    }

    [Fact]
    public async Task DuplicateIds_Throw()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Executor().RunAsync(
            [new Item("a", 1), new Item("a", 2)], (i, _, _) => Task.FromResult(""), (_, _) => Task.FromResult("")));
    }
}

public sealed class AgentFailoverRetryTests
{
    private sealed record Target(string Id);

    private static readonly Target A = new("a");
    private static readonly Target B = new("b");
    private static readonly FailoverRetryOptions NoDelay = new() { MaxAttempts = 3, BaseBackoff = TimeSpan.Zero };

    private static Target SelectAvoiding(Target? lastFailed) => lastFailed == A ? B : A;

    [Fact]
    public async Task TransportFailure_FailsOverToAnotherTarget()
    {
        var failed = new List<(string, int, bool)>();

        var result = await AgentFailoverRetry.ExecuteAsync(
            SelectAvoiding,
            (t, _) => t == A ? throw new HttpRequestException("down") : Task.FromResult("ok from " + t.Id),
            NoDelay,
            new FailoverRetryHooks<Target> { OnAttemptFailed = (t, n, _, more, _) => { failed.Add((t.Id, n, more)); return Task.CompletedTask; } });

        Assert.True(result.Succeeded);
        Assert.Equal("ok from b", result.Result);
        Assert.Equal(2, result.Attempts);
        Assert.Equal([("a", 1, true)], failed);
    }

    [Fact]
    public async Task ReturnedValue_IsNeverRetried()
    {
        int calls = 0;

        var result = await AgentFailoverRetry.ExecuteAsync<Target, string>(
            _ => A,
            (_, _) => { calls++; return Task.FromResult("semantic failure"); },
            NoDelay);

        Assert.True(result.Succeeded);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ExhaustedAttempts_ReturnLastException()
    {
        var result = await AgentFailoverRetry.ExecuteAsync<Target, string>(
            SelectAvoiding,
            (t, _) => throw new HttpRequestException("down " + t.Id),
            NoDelay);

        Assert.False(result.Succeeded);
        Assert.Equal(3, result.Attempts);
        Assert.Equal("down a", result.LastException!.Message);
    }

    [Fact]
    public async Task Cancellation_IsNotRetried()
    {
        int calls = 0;

        await Assert.ThrowsAsync<OperationCanceledException>(() => AgentFailoverRetry.ExecuteAsync<Target, string>(
            _ => A,
            (_, _) => { calls++; throw new OperationCanceledException(); },
            NoDelay));

        Assert.Equal(1, calls);
    }

    [Fact]
    public void Backoff_DoublesPerAttempt_AndMaxAttemptsHasFloorOfOne()
    {
        var options = new FailoverRetryOptions { BaseBackoff = TimeSpan.FromMilliseconds(100), MaxAttempts = 0 };

        Assert.Equal(1, options.MaxAttempts);
        Assert.Equal(TimeSpan.FromMilliseconds(100), options.BackoffForAttempt(1));
        Assert.Equal(TimeSpan.FromMilliseconds(400), options.BackoffForAttempt(3));
    }
}
