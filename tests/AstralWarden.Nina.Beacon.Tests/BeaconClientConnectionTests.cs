using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using AstralWarden.Nina.Beacon.Server;
using Xunit;

namespace AstralWarden.Nina.Beacon.Tests;

/// <summary>
/// Per-connection teardown. A connection owns a socket, a linked CancellationTokenSource and a
/// write-pump task; the agent reconnects on every restart, and NINA runs for days on a machine
/// nobody can walk over to. So what teardown owes:
///
///  • the FIRST Dispose releases everything — socket closed, pump ended, CTS cancelled. Anything
///    that survives it accumulates one set per agent reconnect for the life of NINA;
///  • Dispose is idempotent (the IDisposable contract): later calls do nothing and never throw,
///    because both the shutdown drain and the server's own teardown dispose the same connection;
///  • DrainAndCloseAsync means what it says — a message queued just before shutdown still reaches
///    the wire, and the call returns as soon as the pump has drained rather than sitting out its
///    whole timeout.
/// </summary>
public class BeaconClientConnectionTests : IDisposable
{
    private readonly TcpListener _listener;
    private readonly List<IDisposable> _open = new();

    public BeaconClientConnectionTests()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
    }

    public void Dispose()
    {
        foreach (var d in _open) { try { d.Dispose(); } catch { } }
        _listener.Stop();
    }

    /// <summary>A real loopback pair: the connection under test, and the peer that reads it.</summary>
    private async Task<(BeaconClientConnection Connection, StreamReader Peer)> PairAsync(int capacity = 32)
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)_listener.LocalEndpoint).Port);
        var accepted = await _listener.AcceptTcpClientAsync();
        var connection = new BeaconClientConnection(accepted, capacity, CancellationToken.None);
        _open.Add(connection);
        _open.Add(client);
        return (connection, new StreamReader(client.GetStream()));
    }

    private static Task PumpOf(BeaconClientConnection connection) =>
        (Task)typeof(BeaconClientConnection)
            .GetField("_pump", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(connection)!;

    private static CancellationTokenSource CtsOf(BeaconClientConnection connection) =>
        (CancellationTokenSource)typeof(BeaconClientConnection)
            .GetField("_cts", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(connection)!;

    [Fact]
    public async Task The_first_dispose_closes_the_socket_and_ends_the_write_pump()
    {
        var (connection, peer) = await PairAsync();
        connection.Enqueue("heartbeat", "2026-08-01T00:00:00.000Z", "{}");
        Assert.NotNull(await peer.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));

        connection.Dispose();

        // The peer sees end-of-stream: the socket really is closed, not merely idle.
        Assert.Null(await peer.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        // And the pump task is finished, so its cancellation actually fired.
        await PumpOf(connection).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(CtsOf(connection).IsCancellationRequested);
    }

    [Fact]
    public async Task Dispose_is_idempotent()
    {
        // Both DrainAndCloseAsync and the server's Closed handler dispose the same connection, and
        // the IDisposable contract says every call after the first is a no-op.
        var (connection, _) = await PairAsync();

        connection.Dispose();
        var second = Record.Exception(() => connection.Dispose());
        var third = Record.Exception(() => connection.Dispose());

        Assert.Null(second);
        Assert.Null(third);
    }

    [Fact]
    public async Task Draining_lets_a_queued_message_reach_the_wire_before_the_socket_closes()
    {
        // NINA's shutdown queues `bye` and then drains. If the drain closed the socket underneath
        // the pump, the agent would only ever see a dead connection and could not tell a clean NINA
        // shutdown from a crash.
        var (connection, peer) = await PairAsync();
        connection.Enqueue("bye", "2026-08-01T00:00:00.000Z", "{\"reason\":\"shutdown\"}");

        await connection.DrainAndCloseAsync(TimeSpan.FromSeconds(10));

        var line = await peer.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("\"type\":\"bye\"", line);
        Assert.Null(await peer.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Draining_returns_as_soon_as_the_pump_is_done_not_after_the_whole_timeout()
    {
        // The timeout is a ceiling for a stalled client, not a sleep. NINA's shutdown walks every
        // connected client in turn, so paying the full ceiling per client would hold up NINA's exit.
        var (connection, peer) = await PairAsync();
        connection.Enqueue("bye", "2026-08-01T00:00:00.000Z", "{}");
        _ = peer.ReadLineAsync();

        var stopwatch = Stopwatch.StartNew();
        await connection.DrainAndCloseAsync(TimeSpan.FromSeconds(30));
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10),
            $"drain took {stopwatch.Elapsed} of a 30s ceiling — it waited the timeout out instead of " +
            "completing the queue");
    }
}
