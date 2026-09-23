using System.Net;
using System.Net.Sockets;
using AstralWarden.Nina.Beacon.Contracts;
using AstralWarden.Nina.Beacon.Server;
using Xunit;

namespace AstralWarden.Nina.Beacon.Tests;

/// <summary>
/// The accept loop's failure accounting. Its own contract says a repeated accept failure means a
/// broken listener and recycles it — but recycling drops every connected agent, so the counter has
/// to mean what it claims:
///
///  • CONSECUTIVE. A client that vanishes mid-handshake is one failed accept and is normal. Over a
///    night on a rig that happens more than ten times, and those must never add up to a recycle:
///    any successful accept clears the count;
///  • ten in a row with nothing succeeding between them IS a broken listener — hand back and rebind
///    rather than spin the loop at 100% CPU on the imaging PC;
///  • one log line per streak. We are writing into NINA's own log file, which the user reads when
///    something else has gone wrong; a line per failed accept would bury it.
///
/// Teardown belongs here too: Dispose is reached twice on NINA's shutdown path (ShutdownAsync
/// disposes, and the plugin's teardown can dispose again), so it has to be idempotent.
/// </summary>
public class BeaconServerAcceptFailureTests
{
    private static BeaconServer Server(List<string> log) =>
        new(port: 0, log: m => { lock (log) log.Add(m); },
            minAcceptBackoff: TimeSpan.FromMilliseconds(1));

    /// <summary>A real connected TcpClient, so a "successful" accept hands back the real thing.</summary>
    private static TcpClient Connected()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var client = new TcpClient();
        client.Connect((IPEndPoint)listener.LocalEndpoint);
        var accepted = listener.AcceptTcpClient();
        listener.Stop();
        client.Dispose();
        return accepted;
    }

    /// <summary>Drives the real accept loop with a scripted sequence: true = accept, false = fail.</summary>
    private static async Task<(bool Returned, int Consumed, List<string> Log)> RunAsync(
        IEnumerable<bool> script, TimeSpan wait)
    {
        var log = new List<string>();
        using var server = Server(log);
        using var cts = new CancellationTokenSource();

        var steps = script.GetEnumerator();
        var consumed = 0;
        Task<TcpClient> Accept(CancellationToken ct)
        {
            if (!steps.MoveNext()) return Task.Delay(Timeout.Infinite, ct).ContinueWith(_ => (TcpClient)null!, ct);
            consumed++;
            return steps.Current
                ? Task.FromResult(Connected())
                : Task.FromException<TcpClient>(new SocketException((int)SocketError.ConnectionAborted));
        }

        var loop = server.AcceptLoopAsync(Accept, cts.Token);
        var returned = await Task.WhenAny(loop, Task.Delay(wait)) == loop;
        cts.Cancel();
        try { await loop.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        lock (log) return (returned, consumed, new List<string>(log));
    }

    [Fact]
    public async Task A_successful_accept_clears_the_failure_count()
    {
        // Nine failures, one client that connects, nine more failures: eighteen failed accepts in
        // total but never ten in a row. Over a night of brief client drops this is the ordinary
        // case, and recycling the listener here would disconnect every agent for no reason.
        const int justUnderTheLimit = BeaconServer.MaxConsecutiveAcceptFailures - 1;
        var script = Enumerable.Repeat(false, justUnderTheLimit)
            .Append(true)
            .Concat(Enumerable.Repeat(false, justUnderTheLimit));

        var (returned, consumed, _) = await RunAsync(script, TimeSpan.FromSeconds(5));

        Assert.False(returned, "the accept loop recycled the listener on non-consecutive failures");
        Assert.Equal(justUnderTheLimit * 2 + 1, consumed);
    }

    [Fact]
    public async Task Ten_failures_in_a_row_recycles_the_listener()
    {
        var (returned, _, log) = await RunAsync(
            Enumerable.Repeat(false, BeaconServer.MaxConsecutiveAcceptFailures * 2), TimeSpan.FromSeconds(10));

        Assert.True(returned, "the accept loop kept spinning on a listener that never accepts");
        Assert.Contains(log, m => m.Contains("recycling the listener"));
    }

    [Fact]
    public async Task A_streak_of_failures_logs_once_not_once_per_failure()
    {
        var (_, _, log) = await RunAsync(
            Enumerable.Repeat(false, 5).Append(true).Concat(Enumerable.Repeat(false, 5)),
            TimeSpan.FromSeconds(3));

        // Two streaks separated by a success → exactly two "accept failed" lines, not ten.
        Assert.Equal(2, log.Count(m => m.StartsWith("accept failed:")));
    }

    [Fact]
    public async Task Shutdown_then_dispose_does_not_throw()
    {
        // ShutdownAsync already disposes; NINA's teardown can reach Dispose again, and the second
        // call must be a no-op. Otherwise a `using` around a server that was shut down throws on
        // the way out of the scope.
        var server = new BeaconServer(port: 0, helloFactory: () => new HelloPayload(1, "0.1.0", null, null, "t"));
        server.Start();

        await server.ShutdownAsync("shutdown");

        Assert.Null(Record.Exception(() => server.Dispose()));
        Assert.Null(Record.Exception(() => server.Dispose()));
    }
}
