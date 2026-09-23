using System.Net;
using System.Net.Http;
using AstralWarden.Nina.Beacon.Optional;
using AstralWarden.Nina.Beacon.Server;
using Xunit;

namespace AstralWarden.Nina.Beacon.Tests;

/// <summary>
/// The APPM poller is the only thing in the Beacon that opens a network connection to something
/// other than its own listener, and the thing on the other end drives the mount. So the promises
/// here are the plugin's licence to exist at all:
///
///  • Read-only by construction: it may look at APPM, never touch it.
///    APPM's HTTP API is undocumented and its own UI drives mapping runs through it, so "GETs only"
///    is not a style preference — a mutation that reached a run/abort endpoint would command the
///    mount from a monitoring plugin.
///  • ch.4: "no APPM → silent". APPM is normally shut down long before imaging starts, so the
///    absent case is the NORMAL case and must produce no traffic, no error, and no dead loop.
///  • ch.4: "Late joiners start from truth ... the cached appm.model [is] re-broadcast when a client
///    connects" — which is the entire point, because by the time the agent connects APPM is gone.
///
/// AppmMapperTests covers the parsing; nothing covered the poller. `pollInterval:` joins the
/// documented seam table — the shipped cadence is five minutes.
/// </summary>
public class AppmPollerTests
{
    private static readonly TimeSpan FastPoll = TimeSpan.FromMilliseconds(20);

    private const string TwoPoints = """
        {"MappingPoints":[
          {"HourAngle":-2.5,"Dec":40.0,"RaDelta":3.0,"DecDelta":4.0,"Side":"East","Status":"Solved"},
          {"HourAngle":1.5,"Dec":10.0,"RaDelta":-3.0,"DecDelta":-4.0,"Side":"West","Status":"Solved"}
        ]}
        """;

    /// <summary>Stands in for APPM's local HTTP API and records every request that reached it.</summary>
    private sealed class FakeAppm : HttpMessageHandler
    {
        private readonly List<(HttpMethod Method, string Url)> _requests = new();

        /// <summary>Maps a request path to a body; throwing stands in for APPM not running.</summary>
        public Func<string, string> Respond { get; set; } = _ => throw new HttpRequestException("refused");

        public (HttpMethod Method, string Url)[] Requests { get { lock (_requests) return _requests.ToArray(); } }

        /// <summary>
        /// Ticks of the poll loop. Counted on the run-status endpoint because that is the first
        /// request of every cycle: when APPM is not running it is also the only one, since a refused
        /// connection there ends the cycle before the points are asked for.
        /// </summary>
        public int Ticks => Requests.Count(r => r.Url.EndsWith("Status", StringComparison.Ordinal));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            lock (_requests) _requests.Add((request.Method, url));
            string body;
            try { body = Respond(url); }
            catch (Exception ex) { return Task.FromException<HttpResponseMessage>(ex); }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    private static string ModelOrStatus(string url, string points) =>
        url.EndsWith("Status", StringComparison.Ordinal) ? "\"Complete\"" : points;

    private static async Task<T> EventuallyAsync<T>(Func<T> read, Func<T, bool> done, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var value = read();
            if (done(value)) return value;
            await Task.Delay(10);
        }
        Assert.Fail($"timed out waiting for {what}");
        throw new UnreachableException();
    }

    private sealed class UnreachableException : Exception;

    [Fact]
    public async Task The_poller_only_ever_reads_it_never_commands_appm()
    {
        // Read-only by construction. APPM builds pointing models by slewing the mount; anything but a GET on its
        // API is the Beacon commanding hardware, which it must never do by construction.
        var appm = new FakeAppm { Respond = url => ModelOrStatus(url, TwoPoints) };
        using var server = new BeaconServer(port: 0);
        server.Start();
        using var poller = new AppmPoller(server, new HttpClient(appm), pollInterval: FastPoll);

        await EventuallyAsync(() => appm.Ticks, n => n >= 3, "three poll cycles");

        Assert.NotEmpty(appm.Requests);
        Assert.All(appm.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
        Assert.All(appm.Requests, r => Assert.StartsWith("http://127.0.0.1:60011/api/", r.Url));
    }

    [Fact]
    public async Task A_model_already_finished_when_nina_starts_is_read_at_once_not_a_poll_later()
    {
        // This loop is the only one that opts into tickImmediately, and the reason is stated at the
        // call site: catch a model-building session already underway. Waiting for the first
        // interval means five minutes, and a mapping run the owner is watching finish in APPM would
        // simply not exist for that long — on a rig where the whole point is knowing what is going
        // on. The interval here is ten minutes, so only an at-once tick can satisfy this.
        var appm = new FakeAppm { Respond = url => ModelOrStatus(url, TwoPoints) };
        using var server = new BeaconServer(port: 0);
        server.Start();
        using var reader = new WireReader();
        await reader.ConnectAsync(server);
        using var poller = new AppmPoller(server, new HttpClient(appm), pollInterval: TimeSpan.FromMinutes(10));

        var model = await reader.WaitForAsync("appm.model");
        Assert.Equal(2, model.GetProperty("payload").GetProperty("pointCount").GetInt32());
    }

    [Fact]
    public async Task Appm_absent_is_silent_and_appm_appearing_later_is_still_picked_up()
    {
        // ch.4: "no APPM → silent". Connection-refused is the normal state for most of every night,
        // so it must produce nothing on the wire. But APPM is started by hand, so it can appear at
        // any point in a session, and the poller's whole job is to notice — a refusal must leave no
        // trace that stops the next cycle from working.
        var appm = new FakeAppm(); // default: every request throws, as a refused connection does
        using var server = new BeaconServer(port: 0);
        server.Start();
        using var reader = new WireReader();
        await reader.ConnectAsync(server);
        using var poller = new AppmPoller(server, new HttpClient(appm), pollInterval: FastPoll);

        await EventuallyAsync(() => appm.Ticks, n => n >= 5, "five poll cycles");
        Assert.Empty(reader.OfType("appm.model"));

        appm.Respond = url => ModelOrStatus(url, TwoPoints); // the owner opens APPM mid-session

        var model = await reader.WaitForAsync("appm.model");
        Assert.Equal(2, model.GetProperty("payload").GetProperty("pointCount").GetInt32());
    }

    [Fact]
    public async Task An_unchanged_model_is_broadcast_once_however_often_it_is_polled()
    {
        // A finished model does not change, and APPM keeps answering with it for as long as it is
        // open. Re-broadcasting per poll would put the same payload on the socket all night for no
        // new information — the per-client queue is bounded drop-oldest, so that costs real
        // messages. Late joiners are served from the cache instead (below), not by re-polling.
        var appm = new FakeAppm { Respond = url => ModelOrStatus(url, TwoPoints) };
        using var server = new BeaconServer(port: 0);
        server.Start();
        using var reader = new WireReader();
        await reader.ConnectAsync(server);
        using var poller = new AppmPoller(server, new HttpClient(appm), pollInterval: FastPoll);

        await reader.WaitForAsync("appm.model");
        await EventuallyAsync(() => appm.Ticks, n => n >= 10, "ten poll cycles");

        Assert.Single(reader.OfType("appm.model"));
    }

    [Fact]
    public async Task A_model_that_gained_points_is_broadcast_again()
    {
        // The counterpart to the test above: collapsing repeats must not collapse progress. A
        // mapping run grows point by point, and that growth is the only view the owner has of it.
        var points = TwoPoints;
        var appm = new FakeAppm();
        appm.Respond = url => ModelOrStatus(url, points);
        using var server = new BeaconServer(port: 0);
        server.Start();
        using var reader = new WireReader();
        await reader.ConnectAsync(server);
        using var poller = new AppmPoller(server, new HttpClient(appm), pollInterval: FastPoll);

        var first = await reader.WaitForAsync("appm.model");
        Assert.Equal(2, first.GetProperty("payload").GetProperty("pointCount").GetInt32());

        points = """
            {"MappingPoints":[
              {"HourAngle":-2.5,"Dec":40.0,"RaDelta":3.0,"DecDelta":4.0,"Side":"East","Status":"Solved"},
              {"HourAngle":1.5,"Dec":10.0,"RaDelta":-3.0,"DecDelta":-4.0,"Side":"West","Status":"Solved"},
              {"HourAngle":3.5,"Dec":-5.0,"RaDelta":1.0,"DecDelta":1.0,"Side":"West","Status":"Solved"}
            ]}
            """;

        var models = await reader.WaitForCountAsync("appm.model", 2);
        Assert.Equal(3, models[1].GetProperty("payload").GetProperty("pointCount").GetInt32());
    }

    [Fact]
    public async Task An_empty_model_is_not_broadcast_as_if_it_were_one()
    {
        // APPM answers with an empty point list before a run has produced anything. Shipping that
        // as an appm.model would overwrite a real model the agent already holds with zero points.
        var appm = new FakeAppm { Respond = url => ModelOrStatus(url, """{"MappingPoints":[]}""") };
        using var server = new BeaconServer(port: 0);
        server.Start();
        using var reader = new WireReader();
        await reader.ConnectAsync(server);
        using var poller = new AppmPoller(server, new HttpClient(appm), pollInterval: FastPoll);

        await EventuallyAsync(() => appm.Ticks, n => n >= 5, "five poll cycles");

        Assert.Empty(reader.OfType("appm.model"));
    }

    [Fact]
    public async Task A_late_joining_agent_is_replayed_the_model_after_appm_has_gone()
    {
        // The whole reason this poller caches: APPM is shut down long before imaging starts, so the
        // agent that connects at dusk would otherwise never learn there is a pointing model at all.
        var appm = new FakeAppm { Respond = url => ModelOrStatus(url, TwoPoints) };
        using var server = new BeaconServer(port: 0);
        server.Start();
        using var poller = new AppmPoller(server, new HttpClient(appm), pollInterval: FastPoll);

        // Read the model while APPM is still up, through a client that then goes away — the agent
        // that was connected during the mapping run, restarting before dusk.
        using (var duringTheRun = new WireReader())
        {
            await duringTheRun.ConnectAsync(server);
            await duringTheRun.WaitForAsync("appm.model");
        }
        appm.Respond = _ => throw new HttpRequestException("refused"); // APPM closed

        using var reader = new WireReader();
        await reader.ConnectAsync(server);

        var model = await reader.WaitForAsync("appm.model");
        Assert.Equal(2, model.GetProperty("payload").GetProperty("pointCount").GetInt32());
        Assert.Equal(5.0, model.GetProperty("payload").GetProperty("totalRms").GetDouble());
    }
}
