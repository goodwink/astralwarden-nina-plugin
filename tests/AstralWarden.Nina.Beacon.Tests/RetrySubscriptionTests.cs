using AstralWarden.Nina.Beacon.Watchers;
using Xunit;

namespace AstralWarden.Nina.Beacon.Tests;

public class RetrySubscriptionTests
{
    [Fact]
    public async Task Retries_until_the_mediator_is_ready_then_unsubscribes_on_dispose()
    {
        var attempts = 0;
        var unsubscribed = false;
        var ready = false;

        var sub = new RetrySubscription("test",
            subscribe: () =>
            {
                attempts++;
                if (!Volatile.Read(ref ready)) throw new NullReferenceException(); // mediator handler not registered yet
            },
            unsubscribe: () => unsubscribed = true,
            retryInterval: TimeSpan.FromMilliseconds(20));

        await WaitUntilAsync(() => attempts >= 2);
        Assert.False(sub.IsSubscribed);

        Volatile.Write(ref ready, true);
        await WaitUntilAsync(() => sub.IsSubscribed);

        var attemptsAtSuccess = attempts;
        await Task.Delay(100);
        Assert.Equal(attemptsAtSuccess, attempts); // loop stopped after success

        sub.Dispose();
        Assert.True(unsubscribed);
    }

    [Fact]
    public async Task Dispose_before_success_never_unsubscribes()
    {
        var unsubscribed = false;
        var sub = new RetrySubscription("test",
            subscribe: () => throw new NullReferenceException(),
            unsubscribe: () => unsubscribed = true,
            retryInterval: TimeSpan.FromMilliseconds(20));

        await Task.Delay(60);
        sub.Dispose();
        Assert.False(unsubscribed);
    }

    [Fact]
    public async Task Dispose_during_an_in_flight_subscribe_still_unsubscribes()
    {
        // The handlers live on NINA's own mediators, which outlive the plugin. If NINA tears the
        // plugin down while the background subscribe is part-way through its group, the subscribe
        // still finishes — so teardown must wait for it and then hand the handlers back. Otherwise
        // they stay attached and fire into a disposed server on the next event.
        var inSubscribe = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        var unsubscribes = 0;
        var sub = new RetrySubscription("test",
            subscribe: () => { inSubscribe.Set(); release.Wait(TimeSpan.FromSeconds(10)); },
            unsubscribe: () => Interlocked.Increment(ref unsubscribes));

        Assert.True(inSubscribe.Wait(TimeSpan.FromSeconds(5)), "subscribe never started");
        var disposing = Task.Run(sub.Dispose);
        await Task.Delay(50); // Dispose is now racing the subscribe that is still running
        release.Set();
        await disposing.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(50);

        Assert.Equal(1, Volatile.Read(ref unsubscribes));
    }

    [Fact]
    public async Task NINAs_log_records_which_feed_came_up_and_how_long_it_took()
    {
        // This class exists because of the NRE that killed the first live install: a mediator that
        // is not ready yet. When it recurs, the only artefact anyone can reach is NINA's own log
        // file on the rig — so the pair of lines is the diagnostic. "not ready yet" alone says a
        // feed was late; the success line naming the attempt number says whether it came up on the
        // second try or the fortieth, which is the difference between a race and a broken mediator.
        var logs = new List<string>();
        var ready = false;
        var sub = new RetrySubscription("mount events",
            subscribe: () => { if (!Volatile.Read(ref ready)) throw new NullReferenceException(); },
            unsubscribe: () => { },
            log: m => { lock (logs) logs.Add(m); },
            retryInterval: TimeSpan.FromMilliseconds(20));

        await WaitUntilAsync(() => { lock (logs) return logs.Count >= 1; });
        Volatile.Write(ref ready, true);
        await WaitUntilAsync(() => sub.IsSubscribed);
        sub.Dispose();

        lock (logs)
        {
            Assert.Contains(logs, l => l.Contains("mount events") && l.Contains("not ready yet"));
            var success = Assert.Single(logs, l => l.Contains("subscribed"));
            Assert.Contains("mount events", success);
            Assert.Matches(@"attempt [2-9]\d*", success); // it did not come up first time, and says so
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition(), "condition not met within timeout");
    }
}
