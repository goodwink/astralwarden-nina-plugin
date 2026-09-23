using System.Net.Sockets;
using System.Text.Json;
using AstralWarden.Nina.Beacon.Server;
using AstralWarden.Nina.Beacon.Watchers;
using Xunit;

namespace AstralWarden.Nina.Beacon.Tests;

/// <summary>
/// The heartbeat is the Beacon's proof of life and the bridge's ONLY liveness signal. ch.4: the
/// bridge enforces "a 20-second read timeout on the Beacon's 5s heartbeat" inside its own RunAsync
/// rather than leaning on the host's emission-silence watchdog, precisely because a healthy Beacon
/// is legitimately silent on an idle or clouded night — the heartbeat is the one thing that never
/// stops. A heartbeat that slows, stalls or throws therefore reports a perfectly healthy rig as
/// dead, at 3am, on a machine nobody can walk over to.
///
/// Nothing named HeartbeatWatcher before this file. Every promise checked here is written down
/// somewhere other than the implementation: ch.4's message table ("every 5s | uptime, client count,
/// drops since last, per-device connected flags, sequence summary"), its envelope invariant that
/// "the heartbeat reports the server-wide drop count", and fault isolation (a brittle
/// source is isolated, never cascading).
/// </summary>
public class HeartbeatWatcherTests
{
    /// <summary>
    /// The bridge's read timeout, from ch.4 ("Liveness is a 20-second read timeout on the Beacon's
    /// 5s heartbeat"). It lives in the agent's solution, so it is restated here rather than
    /// referenced; the relation below is what makes the Beacon-side constant correct.
    /// </summary>
    private static readonly TimeSpan BridgeReadTimeout = TimeSpan.FromSeconds(20);

    [Fact]
    public void The_interval_leaves_room_for_a_missed_beat_inside_the_bridges_read_timeout()
    {
        // At the shipped 5 s a whole beat can be lost — a dropped queue slot, a GC pause, NINA busy
        // writing a 100 MB sub — and the next one still lands with time to spare. Anything past
        // ~6.6 s makes a single missed beat enough for the bridge to declare the rig offline and
        // wake the owner. This is the constant the 20 s on the other side was chosen against.
        Assert.True(HeartbeatWatcher.Interval * 3 <= BridgeReadTimeout,
            $"heartbeat every {HeartbeatWatcher.Interval.TotalSeconds}s cannot survive one missed " +
            $"beat inside the bridge's {BridgeReadTimeout.TotalSeconds}s read timeout");
        Assert.Equal(TimeSpan.FromSeconds(5), HeartbeatWatcher.Interval); // ch.4's stated cadence
    }

    [Fact]
    public async Task Every_heartbeat_carries_uptime_clients_devices_and_the_sequence_summary()
    {
        // ch.4's message table names all five fields. Each is the only route by which the cloud
        // learns that fact between the slower feeds: with `devices` gone the fleet view cannot say
        // which gear is connected on a rig that is idle, and with the sequence summary gone it
        // cannot say whether the rig is imaging at all.
        using var server = new BeaconServer(port: 0);
        server.Start();
        using var reader = new WireReader();
        await reader.ConnectAsync(server);

        using var heartbeat = new HeartbeatWatcher(server,
            devices: () => new Dictionary<string, bool> { ["camera"] = true, ["mount"] = false },
            sequence: () => (true, "Take Exposure"),
            interval: TimeSpan.FromMilliseconds(30));

        var payload = (await reader.WaitForAsync("heartbeat")).GetProperty("payload");

        Assert.True(payload.GetProperty("uptimeSec").GetDouble() >= 0);
        Assert.Equal(1, payload.GetProperty("clients").GetInt32());
        Assert.True(payload.GetProperty("devices").GetProperty("camera").GetBoolean());
        Assert.False(payload.GetProperty("devices").GetProperty("mount").GetBoolean());
        Assert.True(payload.GetProperty("sequenceRunning").GetBoolean());
        Assert.Equal("Take Exposure", payload.GetProperty("instruction").GetString());
    }

    [Fact]
    public async Task A_device_summary_that_throws_never_silences_the_heartbeat()
    {
        // Fault isolation: one brittle source is supervised and reported, never allowed to cascade.
        // The cascade here is the worst available: the suppliers are NINA's own mediators, which go
        // away during shutdown and during a device driver crash, and the thing they would take down
        // is the signal that says the rig is alive. The beat must keep landing, with the fields it
        // could not gather simply absent.
        using var server = new BeaconServer(port: 0);
        server.Start();
        using var reader = new WireReader();
        await reader.ConnectAsync(server);

        using var heartbeat = new HeartbeatWatcher(server,
            devices: () => throw new InvalidOperationException("mediator gone"),
            sequence: () => throw new InvalidOperationException("sequencer gone"),
            interval: TimeSpan.FromMilliseconds(30));

        var beats = await reader.WaitForCountAsync("heartbeat", 3);

        var payload = beats[0].GetProperty("payload");
        Assert.False(payload.TryGetProperty("devices", out _));
        Assert.False(payload.TryGetProperty("sequenceRunning", out _));
        Assert.True(payload.GetProperty("uptimeSec").GetDouble() >= 0);
    }

    [Fact]
    public async Task Drops_are_reported_since_the_last_beat_not_as_a_running_total()
    {
        // ch.4's envelope invariant: "a gap in a client's seq is exactly the set of messages that
        // client lost. The heartbeat reports the server-wide drop count." Since-last is what makes
        // it usable — a running total is non-zero for the rest of the night after one stalled
        // agent, so "is the socket keeping up right now" becomes unanswerable and any alert built
        // on it latches on forever.
        using var server = new BeaconServer(port: 0, queueCapacity: 4);
        server.Start();

        // A client that connects and never reads: the pump fills the OS buffer, then the bounded
        // queue overflows and the server drops for it. Closing it afterwards latches those drops on
        // the server and guarantees no NEW drops can occur while the heartbeats are observed.
        var stalled = new TcpClient();
        await stalled.ConnectAsync("127.0.0.1", server.Port);
        while (server.ClientCount == 0) await Task.Delay(5);
        var pad = new string('x', 4000);
        for (var i = 0; i < 5_000; i++) server.Broadcast("heartbeat", new { i, pad });
        stalled.Close();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (server.ClientCount > 0 && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.Equal(0, server.ClientCount);

        using var reader = new WireReader();
        await reader.ConnectAsync(server);
        using var heartbeat = new HeartbeatWatcher(server, interval: TimeSpan.FromMilliseconds(30));

        var beats = await reader.WaitForCountAsync("heartbeat", 3);
        var reported = beats
            .Select(b => b.GetProperty("payload").GetProperty("droppedSinceLastHeartbeat").GetInt64())
            .ToArray();

        Assert.True(reported[0] > 0, "the first beat should carry the drops the stalled client caused");
        Assert.All(reported.Skip(1), d => Assert.Equal(0, d));
    }
}
