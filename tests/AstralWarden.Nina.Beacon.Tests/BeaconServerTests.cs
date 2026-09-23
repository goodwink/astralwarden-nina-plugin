using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using AstralWarden.Nina.Beacon.Contracts;
using AstralWarden.Nina.Beacon.Server;
using Xunit;

namespace AstralWarden.Nina.Beacon.Tests;

public class BeaconServerTests
{
    private static BeaconServer StartServer(int capacity = 2000, Func<object>? hello = null)
    {
        var server = new BeaconServer(port: 0, queueCapacity: capacity, helloFactory: hello);
        server.Start();
        return server;
    }

    private static async Task<StreamReader> ConnectAsync(BeaconServer server)
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", server.Port);
        return new StreamReader(tcp.GetStream());
    }

    private static async Task<JsonElement> ReadEnvelopeAsync(StreamReader reader)
    {
        var line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(line);
        return JsonDocument.Parse(line!).RootElement;
    }

    [Fact]
    public async Task Hello_is_the_first_message_and_envelope_is_well_formed()
    {
        using var server = StartServer(hello: () => new HelloPayload(1, "0.1.0", "3.3", "Default", "2026-07-21T00:00:00.000Z"));
        using var reader = await ConnectAsync(server);

        var envelope = await ReadEnvelopeAsync(reader);
        Assert.Equal(1, envelope.GetProperty("v").GetInt32());
        Assert.Equal("hello", envelope.GetProperty("type").GetString());
        Assert.Equal(1, envelope.GetProperty("seq").GetInt64());
        Assert.EndsWith("Z", envelope.GetProperty("ts").GetString());
        var payload = envelope.GetProperty("payload");
        Assert.Equal("Default", payload.GetProperty("profileName").GetString());
        Assert.Equal(1, payload.GetProperty("schemaVersion").GetInt32());
    }

    [Fact]
    public async Task Broadcast_reaches_every_client_with_per_connection_seq()
    {
        using var server = StartServer();
        using var readerA = await ConnectAsync(server);
        await WaitForClientsAsync(server, 1);
        using var readerB = await ConnectAsync(server);
        await WaitForClientsAsync(server, 2);

        server.Broadcast("heartbeat", new HeartbeatPayload(1.0, 2, 0));

        foreach (var reader in new[] { readerA, readerB })
        {
            var envelope = await ReadEnvelopeAsync(reader);
            Assert.Equal("heartbeat", envelope.GetProperty("type").GetString());
            Assert.Equal(1, envelope.GetProperty("seq").GetInt64());
            Assert.Equal(2, envelope.GetProperty("payload").GetProperty("clients").GetInt32());
        }
    }

    [Fact]
    public async Task Stalled_reader_drops_oldest_and_broadcast_never_blocks()
    {
        const int capacity = 8;
        using var server = StartServer(capacity);
        var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", server.Port);
        await WaitForClientsAsync(server, 1);

        // Don't read: the pump writes a few lines into the OS socket buffer, then the channel
        // fills. Broadcasts must stay fast and the oldest queued messages must be discarded.
        var stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < 50_000; i++)
            server.Broadcast("heartbeat", new HeartbeatPayload(i, 1, 0));
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"broadcasting took {stopwatch.Elapsed}");
        Assert.True(server.TakeDroppedSinceLast() > 0, "expected drops from the stalled reader");
        tcp.Close();
    }

    [Fact]
    public async Task Seq_gap_is_visible_to_a_client_that_fell_behind()
    {
        const int capacity = 4;
        using var server = StartServer(capacity, hello: () => new HelloPayload(1, "0.1.0", null, null, "t"));
        var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", server.Port);
        await WaitForClientsAsync(server, 1);

        // Broadcast until the server has ACTUALLY dropped for this unread client, rather than
        // assuming some fixed count overflows. How much hides in the OS socket buffer varies by
        // platform, and a fixed 500 made this test depend on that: it passed locally and timed out on
        // CI's Windows runner. Drops are the precondition for the gap, so wait for the real signal.
        var pad = new string('x', 2000);
        long dropped = 0;
        for (var i = 0; i < 20_000 && dropped == 0; i++)
        {
            server.Broadcast("heartbeat", new { i, pad });
            if (i % 50 == 49) dropped += server.TakeDroppedSinceLast(); // Take() resets — accumulate
        }
        Assert.True(dropped > 0, "expected the server to drop for the unread client");

        // Now drain EVERYTHING still queued or buffered; seqs must be strictly increasing and must
        // contain at least one gap (the dropped middle). Draining fully matters in the other
        // direction too: an early count cap could stop inside the buffered prefix, before the gap.
        using var reader = new StreamReader(tcp.GetStream());
        var seqs = new List<long>();
        while (seqs.Count < 100_000) // runaway guard only
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (TimeoutException)
            {
                break; // the server has nothing left to send — that IS the end of the data
            }
            if (line is null) break;
            seqs.Add(JsonDocument.Parse(line).RootElement.GetProperty("seq").GetInt64());
        }

        Assert.True(seqs.Count >= 2, "expected to read at least a few messages");
        Assert.Equal(seqs.OrderBy(s => s).ToList(), seqs); // monotonic
        var span = seqs[^1] - seqs[0] + 1;
        Assert.True(span > seqs.Count, "expected a seq gap where messages were dropped");
    }

    [Fact]
    public async Task Shutdown_sends_bye_then_closes()
    {
        var server = StartServer();
        using var reader = await ConnectAsync(server);
        await WaitForClientsAsync(server, 1);

        await server.ShutdownAsync("shutdown");

        var envelope = await ReadEnvelopeAsync(reader);
        Assert.Equal("bye", envelope.GetProperty("type").GetString());
        Assert.Equal("shutdown", envelope.GetProperty("payload").GetProperty("reason").GetString());
        var after = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(after); // socket closed
    }

    [Fact]
    public async Task Disconnected_client_is_unregistered()
    {
        using var server = StartServer();
        var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", server.Port);
        await WaitForClientsAsync(server, 1);

        tcp.Close();
        // The pump only notices when a write fails, which can take a few attempts while the OS
        // buffers the first ones.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (server.ClientCount != 0 && DateTime.UtcNow < deadline)
        {
            server.Broadcast("heartbeat", new HeartbeatPayload(0, 1, 0));
            await Task.Delay(20);
        }
        Assert.Equal(0, server.ClientCount);
    }

    // A peer that dies mid-hello used to be added to the client set after its own pump had already
    // fired Closed — an entry nothing removes, accumulating one per agent restart inside NINA. That
    // window exists only if hello can reach the wire before the client is registered, so that is
    // what this pins. Holding the server's gate is the only way to see it: registering and
    // unregistering both go through that one lock, so from outside there is nothing else to observe.
    [Fact]
    public async Task A_client_joins_the_set_before_its_hello_can_reach_the_wire()
    {
        using var server = StartServer(hello: () => new HelloPayload(1, "0.1.0", null, null, "t"));
        var gate = typeof(BeaconServer)
            .GetField("_gate", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(server)!;

        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        // On its own thread: Monitor is thread-affine, so the gate can't be held across an await.
        var holder = new Thread(() => { lock (gate) { held.Set(); release.Wait(); } }) { IsBackground = true };
        holder.Start();
        Assert.True(held.Wait(TimeSpan.FromSeconds(5)), "expected to take the server's gate");

        using var reader = await ConnectAsync(server);
        var first = reader.ReadLineAsync();
        await Task.Delay(300);
        try
        {
            Assert.False(first.IsCompleted, "hello reached the client before it joined the client set");
        }
        finally
        {
            release.Set();
            holder.Join();
        }

        var line = await first.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("hello", JsonDocument.Parse(line!).RootElement.GetProperty("type").GetString());
        Assert.Equal(1, server.ClientCount);
    }

    // The Beacon must survive a port it cannot have. Start() is called from the plugin
    // constructor, so anything it throws kills the plugin at MEF composition — on a rig nobody can
    // walk over to.
    [Fact]
    public void An_occupied_port_leaves_the_server_alive_and_retrying()
    {
        using var squatter = new TcpListener(IPAddress.Loopback, 0);
        squatter.Start();
        var taken = ((IPEndPoint)squatter.LocalEndpoint).Port;
        var logged = new List<string>();

        using var server = new BeaconServer(port: taken, log: logged.Add,
            rebindInterval: TimeSpan.FromMilliseconds(50));
        var start = Record.Exception(() => server.Start());

        Assert.Null(start);
        Assert.False(server.IsListening);
        Assert.Equal(taken, server.Port); // still reports the port it wants
        Assert.Contains(logged, m => m.Contains("unavailable"));
        // And broadcasting into a server with no socket is a no-op, not a throw.
        Assert.Null(Record.Exception(() => server.Broadcast("heartbeat", new HeartbeatPayload(1, 0, 0))));
    }

    [Fact]
    public async Task The_port_is_claimed_as_soon_as_it_frees_up()
    {
        var squatter = new TcpListener(IPAddress.Loopback, 0);
        squatter.Start();
        var port = ((IPEndPoint)squatter.LocalEndpoint).Port;

        using var server = new BeaconServer(port: port, rebindInterval: TimeSpan.FromMilliseconds(50),
            helloFactory: () => new HelloPayload(1, "0.1.0", null, null, "t"));
        server.Start();
        Assert.False(server.IsListening);

        squatter.Stop();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!server.IsListening && DateTime.UtcNow < deadline) await Task.Delay(25);
        Assert.True(server.IsListening, "expected the server to rebind once the port freed up");

        using var reader = await ConnectAsync(server);
        Assert.Equal("hello", (await ReadEnvelopeAsync(reader)).GetProperty("type").GetString());
    }

    [Fact]
    public async Task A_listener_killed_underneath_the_accept_loop_is_rebuilt()
    {
        // Dispose() is the only public teardown, so simulate a broken listener the way the OS would
        // present one: stop accepting, then confirm a fresh bind on the same port still works.
        var port = FreePort();
        using (var first = new BeaconServer(port: port, rebindInterval: TimeSpan.FromMilliseconds(50)))
        {
            first.Start();
            Assert.True(first.IsListening);
        }

        using var second = new BeaconServer(port: port, rebindInterval: TimeSpan.FromMilliseconds(50));
        second.Start();
        Assert.True(second.IsListening);
        using var reader = await ConnectAsync(second);
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static async Task WaitForClientsAsync(BeaconServer server, int expected)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (server.ClientCount != expected)
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail($"expected {expected} clients, saw {server.ClientCount}");
            await Task.Delay(10);
        }
    }
}
