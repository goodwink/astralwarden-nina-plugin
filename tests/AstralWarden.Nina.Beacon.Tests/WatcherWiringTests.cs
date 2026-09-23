using AstralWarden.Nina.Beacon.Mapping;
using AstralWarden.Nina.Beacon.Server;
using AstralWarden.Nina.Beacon.Watchers;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Plugin.Interfaces;
using NSubstitute;
using Xunit;

namespace AstralWarden.Nina.Beacon.Tests;

/// <summary>
/// The watcher wiring — the lines between a well-tested mapper and the socket. Each one here has a
/// specific promise in ch.4 that no mapper test can check.
/// </summary>
public class WatcherWiringTests
{
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(300);

    private static async Task<(BeaconServer Server, WireReader Reader)> WiredAsync()
    {
        var server = new BeaconServer(port: 0);
        server.Start();
        var reader = new WireReader();
        await reader.ConnectAsync(server);
        return (server, reader);
    }

    // ---- TargetSchedulerWatcher -------------------------------------------------------------

    private static IMessage TsMessage(string topic, Dictionary<string, object> headers)
    {
        var message = Substitute.For<IMessage>();
        message.Topic.Returns(topic);
        message.Content.Returns(DateTime.UtcNow);
        message.CustomHeaders.Returns(headers);
        return message;
    }

    [Fact]
    public async Task A_late_joining_agent_is_replayed_the_last_target_start_not_the_last_ts_message()
    {
        // ch.4: "Late joiners start from truth ... the last ts.targetstart". An agent that
        // reconnects mid-night needs the target its frames belong to. Replaying whatever TS said
        // last instead — a wait, or a completion — leaves every frame unattributed until the next
        // real targetstart, which on a TS rig can be an hour away.
        var server = new BeaconServer(port: 0);
        server.Start();
        using var watcher = new TargetSchedulerWatcher(Substitute.For<IMessageBroker>(), server);

        await watcher.OnMessageReceived(TsMessage(TsMessageMapper.TargetStart, new Dictionary<string, object>
        {
            ["ProjectName"] = "Broadband",
            ["TargetName"] = "M 31",
        }));
        await watcher.OnMessageReceived(TsMessage(TsMessageMapper.WaitStart, new Dictionary<string, object>
        {
            ["ProjectName"] = "Broadband",
            ["TargetName"] = "NGC 7000",
            ["SecondsUntilNextTarget"] = 900,
        }));

        // Now the agent connects, having seen none of that.
        using var reader = new WireReader();
        await reader.ConnectAsync(server);
        await reader.WaitForAsync("ts.targetstart");

        var replayed = Assert.Single(reader.Envelopes);
        Assert.Equal("ts.targetstart", replayed.GetProperty("type").GetString());
        Assert.Equal("M 31", replayed.GetProperty("payload").GetProperty("target").GetString());
        server.Dispose();
    }

    // ---- DeviceWatcherBase ------------------------------------------------------------------

    private sealed class TestDeviceWatcher : DeviceWatcherBase<CameraInfo>
    {
        public TestDeviceWatcher(BeaconServer server) : base(server, "camera") { }

        protected override object? Map(CameraInfo info) => new { temperature = info.Temperature };

        public void Push(CameraInfo info) => Update(info);
    }

    [Fact]
    public async Task A_device_connecting_or_disconnecting_is_reported_at_once_not_at_the_throttle()
    {
        // Every device watcher inherits this. Ordinary state changes are throttled to one every
        // ten seconds so the stream stays quiet; a connection flip is not an ordinary change — it
        // is the difference between a prompt roof-closed or camera-lost alert and one that arrives
        // up to ten seconds late, having also skipped the state that says what happened.
        var (server, reader) = await WiredAsync();
        using var watcher = new TestDeviceWatcher(server);

        watcher.Push(new CameraInfo { Connected = true, Name = "ASI2600", Temperature = -9 });
        await reader.WaitForAsync("device.state");
        Assert.Equal(new[] { "device.connection", "device.state" }, reader.Types);

        // A plain state change inside the throttle window stays off the wire.
        watcher.Push(new CameraInfo { Connected = true, Name = "ASI2600", Temperature = -10 });
        await Task.Delay(Settle); // an absence: only elapsed time can show the throttle held
        Assert.Equal(2, reader.Types.Length);

        // The disconnect does not wait for it.
        watcher.Push(new CameraInfo { Connected = false, Name = "ASI2600" });
        await reader.WaitForCountAsync("device.state", 2);
        Assert.Equal(
            new[] { "device.connection", "device.state", "device.connection", "device.state" },
            reader.Types);
        Assert.False(watcher.IsConnected);

        reader.Dispose();
        server.Dispose();
    }

    private static int ClientConnectedHandlers(BeaconServer server)
    {
        // The event's subscriber list is the only place a leaked handler shows up.
        var field = typeof(BeaconServer).GetField("ClientConnected",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        return (field.GetValue(server) as Delegate)?.GetInvocationList().Length ?? 0;
    }

    [Fact]
    public void A_device_watcher_that_fails_to_register_leaves_nothing_behind()
    {
        // The plugin drops a watcher whose constructor throws, so nothing will ever dispose it.
        // Anything it attached to the server before failing would stay attached for the session.
        var server = new BeaconServer(port: 0);
        var mediator = Substitute.For<ICameraMediator>();
        mediator.When(m => m.RegisterConsumer(Arg.Any<ICameraConsumer>()))
            .Do(_ => throw new NullReferenceException("mediator not ready"));

        Assert.Throws<NullReferenceException>(() => new CameraWatcher(mediator, server));

        Assert.Equal(0, ClientConnectedHandlers(server));
        server.Dispose();
    }

    // ---- MountEventWatcher ------------------------------------------------------------------

    [Fact]
    public async Task Every_mount_lifecycle_event_reaches_the_wire()
    {
        // ch.4's mount.event row lists six kinds. They are the excursion markers the cloud
        // correlates HFR and guiding against, and a missing one is invisible: the watcher reports
        // healthy and that kind of event simply never arrives again.
        var mediator = Substitute.For<ITelescopeMediator>();
        var (server, reader) = await WiredAsync();
        using var watcher = new MountEventWatcher(mediator, server);

        await WaitForSubscriptionAsync(mediator, "add_Homed");

        mediator.Slewed += Raise.Event<Func<object, MountSlewedEventArgs, Task>>(
            this, new MountSlewedEventArgs(
                new NINA.Astrometry.Coordinates(1, 2, NINA.Astrometry.Epoch.J2000,
                    NINA.Astrometry.Coordinates.RAType.Hours),
                new NINA.Astrometry.Coordinates(3, 4, NINA.Astrometry.Epoch.J2000,
                    NINA.Astrometry.Coordinates.RAType.Hours)));
        mediator.Parked += Raise.Event<Func<object, EventArgs, Task>>(this, EventArgs.Empty);
        mediator.Unparked += Raise.Event<Func<object, EventArgs, Task>>(this, EventArgs.Empty);
        mediator.Homed += Raise.Event<Func<object, EventArgs, Task>>(this, EventArgs.Empty);

        await reader.WaitForCountAsync("mount.event", 4);

        var kinds = reader.Envelopes
            .Where(e => e.GetProperty("type").GetString() == "mount.event")
            .Select(e => e.GetProperty("payload").GetProperty("event").GetString())
            .ToArray();
        Assert.Equal(new[] { "slewed", "parked", "unparked", "homed" }, kinds);

        reader.Dispose();
        server.Dispose();
    }

    [Fact]
    public async Task Teardown_hands_every_mount_event_back_to_nina()
    {
        // The mirror of BeaconCompositionTests' late-joiner assertion, on the other side of the
        // plugin boundary: these handlers live on NINA's OWN telescope mediator, which outlives the
        // plugin. NINA tears a plugin down on disable and on shutdown, and a handler left behind
        // fires into a disposed BeaconServer on the next slew — inside NINA's process, from code
        // the user did not get from us. Six events subscribe as one group, so six must come back.
        var mediator = Substitute.For<ITelescopeMediator>();
        var (server, reader) = await WiredAsync();
        var watcher = new MountEventWatcher(mediator, server);
        await WaitForSubscriptionAsync(mediator, "add_Homed");

        watcher.Dispose();

        var removed = mediator.ReceivedCalls()
            .Select(c => c.GetMethodInfo().Name)
            .Where(n => n.StartsWith("remove_", StringComparison.Ordinal))
            .ToHashSet();
        Assert.Equal(
            new HashSet<string>
            {
                "remove_Slewed", "remove_Parked", "remove_Unparked",
                "remove_Homed", "remove_BeforeMeridianFlip", "remove_AfterMeridianFlip",
            },
            removed);

        reader.Dispose();
        server.Dispose();
    }

    private static async Task WaitForSubscriptionAsync(object substitute, string addMethod)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (substitute.ReceivedCalls().Any(c => c.GetMethodInfo().Name == addMethod)) return;
            await Task.Delay(5);
        }
        Assert.Fail($"{addMethod} was never subscribed");
    }

    // ---- RetrySubscription ------------------------------------------------------------------

    [Fact]
    public async Task Disposing_a_subscription_that_never_succeeded_stops_its_retry_loop()
    {
        // Every watcher subscribes through RetrySubscription. Teardown happens while NINA is
        // shutting down, so a loop that outlives Dispose keeps calling into a half-torn-down
        // mediator with nobody left to notice.
        var attempts = 0;
        var sub = new RetrySubscription("test",
            subscribe: () => { Interlocked.Increment(ref attempts); throw new NullReferenceException(); },
            unsubscribe: () => { },
            retryInterval: TimeSpan.FromMilliseconds(5));

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (Volatile.Read(ref attempts) < 5 && DateTime.UtcNow < deadline) await Task.Delay(5);
        Assert.True(Volatile.Read(ref attempts) >= 5, "the retry loop never got going");

        sub.Dispose();
        var atDispose = Volatile.Read(ref attempts);
        await Task.Delay(500); // ~100 further retries at 5 ms if the loop is still alive

        // One retry may already have been in flight when Dispose landed; a live loop is not close.
        Assert.InRange(Volatile.Read(ref attempts), atDispose, atDispose + 1);
    }
}
