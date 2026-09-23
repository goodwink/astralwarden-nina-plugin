using AstralWarden.Nina.Beacon.Contracts;
using AstralWarden.Nina.Beacon.Mapping;
using AstralWarden.Nina.Beacon.Server;
using NINA.Sequencer.Interfaces.Mediator;

namespace AstralWarden.Nina.Beacon.Watchers;

/// <summary>
/// sequence.state: immediate on SequenceStarting/SequenceFinished, plus a 2s change-poll of the
/// advanced sequencer's currently-running items while a sequence runs (there is no per-instruction
/// event in NINA — polling the mediator is the supported way to see the current instruction).
/// Also feeds the heartbeat's sequenceRunning/instruction summary.
/// </summary>
public sealed class SequenceWatcher : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly ISequenceMediator _sequence;
    private readonly BeaconServer _server;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly RetrySubscription _events;
    private readonly object _publishGate = new();
    private string? _lastJson;
    private volatile SequenceStatePayload? _last;

    public SequenceWatcher(ISequenceMediator sequence, BeaconServer server, Action<string>? log = null,
        TimeSpan? pollInterval = null)
    {
        _sequence = sequence;
        _server = server;
        // SequenceMediator's events forward to a navigation VM that registers after plugin
        // composition — subscribing eagerly NREs. Retry until the sequencer exists; the 2s poll
        // covers state changes in the meantime.
        _events = new RetrySubscription("sequence events",
            subscribe: () =>
            {
                _sequence.SequenceStarting += OnSequenceStarting;
                _sequence.SequenceFinished += OnSequenceFinished;
            },
            unsubscribe: () =>
            {
                _sequence.SequenceStarting -= OnSequenceStarting;
                _sequence.SequenceFinished -= OnSequenceFinished;
            },
            log);
        _server.ClientConnected += OnClientConnected;
        _loop = Task.Run(() => PeriodicLoop.RunAsync("sequence poll", pollInterval ?? PollInterval,
            () => Publish(force: false), log, _cts.Token));
    }

    /// <summary>(running, innermost instruction name) for the heartbeat.</summary>
    public (bool Running, string? Instruction) Summary
    {
        get
        {
            var last = _last;
            return (last?.Running ?? false, last?.Items.Count > 0 ? last.Items[^1].Name : null);
        }
    }

    private Task OnSequenceStarting(object sender, EventArgs e)
    {
        Publish(force: true);
        return Task.CompletedTask;
    }

    private Task OnSequenceFinished(object sender, EventArgs e)
    {
        Publish(force: true);
        return Task.CompletedTask;
    }

    private void OnClientConnected()
    {
        try
        {
            var last = _last;
            if (last is not null) _server.Broadcast("sequence.state", last);
        }
        catch { }
    }

    private void Publish(bool force)
    {
        try
        {
            var running = _sequence.Initialized && _sequence.IsAdvancedSequenceRunning();
            var items = (running
                ? _sequence.GetAdvancedSequencerCurrentRunningItems()
                : null) ?? Array.Empty<NINA.Sequencer.SequenceItem.ISequenceItem>();
            var payload = SequenceItemMapper.Map(running, items);
            var json = BeaconJson.Serialize(payload);

            // The 2s poll and NINA's SequenceStarting/Finished callbacks both land here on
            // different threads; without this the change check and its update interleave and the
            // same state gets broadcast twice (or a change is swallowed). Broadcast outside the
            // lock — it runs on NINA's thread in the event case.
            lock (_publishGate)
            {
                if (!force && json == _lastJson) return;
                _lastJson = json;
                _last = payload;
            }
            _server.Broadcast("sequence.state", payload);
        }
        catch
        {
            // sequencer not initialized yet / mid-teardown — try again next tick
        }
    }

    public void Dispose()
    {
        _events.Dispose();
        _server.ClientConnected -= OnClientConnected;
        PeriodicLoop.Stop(_cts, _loop);
    }
}
