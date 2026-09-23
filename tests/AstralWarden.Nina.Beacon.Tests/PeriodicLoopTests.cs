using AstralWarden.Nina.Beacon.Watchers;
using Xunit;

namespace AstralWarden.Nina.Beacon.Tests;

/// <summary>
/// The heartbeat, sequence poll and APPM poll all run on this. A background loop that
/// dies to an unanticipated exception is a feed that goes silent with no signal at all — the one
/// failure mode the Beacon must not have.
/// </summary>
public class PeriodicLoopTests
{
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(10);

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(5);
        }
        return condition();
    }

    [Fact]
    public async Task A_throwing_tick_does_not_end_the_loop()
    {
        using var cts = new CancellationTokenSource();
        var ticks = 0;
        var loop = PeriodicLoop.RunAsync("test", Tick,
            () => { Interlocked.Increment(ref ticks); throw new InvalidOperationException("boom"); },
            log: null, cts.Token);

        Assert.True(await WaitUntilAsync(() => Volatile.Read(ref ticks) >= 5),
            $"loop stopped after {ticks} failing ticks");
        cts.Cancel();
        await loop;
    }

    [Fact]
    public async Task A_repeating_failure_is_logged_exactly_once()
    {
        using var cts = new CancellationTokenSource();
        var logs = new List<string>();
        var ticks = 0;
        var loop = PeriodicLoop.RunAsync("appm poll", Tick,
            () => { Interlocked.Increment(ref ticks); throw new InvalidOperationException("boom"); },
            log: m => { lock (logs) logs.Add(m); }, cts.Token);

        await WaitUntilAsync(() => Volatile.Read(ref ticks) >= 10);
        cts.Cancel();
        await loop;

        lock (logs)
        {
            var line = Assert.Single(logs); // not one per tick — this floods NINA's log otherwise
            Assert.Contains("appm poll", line);
            Assert.Contains("boom", line);
        }
    }

    [Fact]
    public async Task Cancellation_ends_the_loop_quietly()
    {
        using var cts = new CancellationTokenSource();
        var logs = new List<string>();
        var ticks = 0;
        var loop = PeriodicLoop.RunAsync("test", Tick, () => Interlocked.Increment(ref ticks),
            log: m => { lock (logs) logs.Add(m); }, cts.Token);

        await WaitUntilAsync(() => Volatile.Read(ref ticks) >= 3);
        cts.Cancel();
        await loop.WaitAsync(TimeSpan.FromSeconds(5));

        lock (logs) Assert.Empty(logs);
    }

    [Fact]
    public async Task TickImmediately_runs_before_the_first_interval()
    {
        using var cts = new CancellationTokenSource();
        var ticked = new TaskCompletionSource();
        var loop = PeriodicLoop.RunAsync("test", TimeSpan.FromMinutes(10),
            () => ticked.TrySetResult(), log: null, cts.Token, tickImmediately: true);

        await ticked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();
        await loop;
    }

    [Fact]
    public async Task Without_it_the_first_tick_waits_for_the_interval()
    {
        // tickImmediately is opt-in, and only the APPM poll opts in — with a stated reason (catch a
        // model-building session already underway). The other two loops are constructed inside the
        // plugin constructor, which is exactly the moment NINA's mediators are not yet wired up:
        // ch.4's first landmine is "never subscribe eagerly in a constructor", and an at-once tick
        // is the same mistake by another route. If the default flipped, every Beacon start would
        // poll the sequencer and beat the heartbeat before composition had finished.
        // Both overloads: the async one drives the APPM poll, the sync one the heartbeat and the
        // sequence poll, and they carry the default separately.
        using var cts = new CancellationTokenSource();
        var syncTicks = 0;
        var asyncTicks = 0;
        var syncLoop = PeriodicLoop.RunAsync("sync", TimeSpan.FromMinutes(10),
            () => Interlocked.Increment(ref syncTicks), log: null, cts.Token);
        var asyncLoop = PeriodicLoop.RunAsync("async", TimeSpan.FromMinutes(10),
            _ => { Interlocked.Increment(ref asyncTicks); return Task.CompletedTask; }, log: null, cts.Token);

        await Task.Delay(250); // an immediate tick lands at ~0 ms; the real one is 10 minutes away
        Assert.Equal(0, Volatile.Read(ref syncTicks));
        Assert.Equal(0, Volatile.Read(ref asyncTicks));

        cts.Cancel();
        await syncLoop;
        await asyncLoop;
    }
}
