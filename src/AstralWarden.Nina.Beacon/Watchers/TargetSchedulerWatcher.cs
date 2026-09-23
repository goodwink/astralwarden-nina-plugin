using AstralWarden.Nina.Beacon.Mapping;
using AstralWarden.Nina.Beacon.Server;
using NINA.Plugin.Interfaces;

namespace AstralWarden.Nina.Beacon.Watchers;

/// <summary>
/// In-process Target Scheduler feed via NINA's official inter-plugin message broker — no HTTP API,
/// no touching TS's SQLite database. Subscribing to topics nobody publishes is harmless, so this
/// is registered unconditionally and simply stays silent when TS isn't installed.
/// The last target-start is re-broadcast to late-joining clients (frame attribution context).
/// </summary>
public sealed class TargetSchedulerWatcher : ISubscriber, IDisposable
{
    private readonly IMessageBroker _broker;
    private readonly BeaconServer _server;
    private sealed record LastBroadcast(string Type, object Payload);
    private volatile LastBroadcast? _lastTargetStart;

    public TargetSchedulerWatcher(IMessageBroker broker, BeaconServer server)
    {
        _broker = broker;
        _server = server;
        // Per-topic guard: one rejected topic must not leave the others subscribed with no owner to
        // unsubscribe them (the ctor's caller drops us on an exception).
        foreach (var topic in TsMessageMapper.Topics)
        {
            try { _broker.Subscribe(topic, this); } catch { /* that topic stays silent */ }
        }
        _server.ClientConnected += OnClientConnected;
    }

    public Task OnMessageReceived(IMessage message)
    {
        try
        {
            var mapped = TsMessageMapper.Map(message.Topic, message.Content, message.CustomHeaders);
            if (mapped is { } m)
            {
                if (m.Type == "ts.targetstart") _lastTargetStart = new LastBroadcast(m.Type, m.Payload);
                _server.Broadcast(m.Type, m.Payload);
            }
        }
        catch
        {
            // never propagate into the broker's dispatch
        }
        return Task.CompletedTask;
    }

    private void OnClientConnected()
    {
        try
        {
            if (_lastTargetStart is { } last) _server.Broadcast(last.Type, last.Payload);
        }
        catch { }
    }

    public void Dispose()
    {
        _server.ClientConnected -= OnClientConnected;
        foreach (var topic in TsMessageMapper.Topics)
        {
            try { _broker.Unsubscribe(topic, this); } catch { }
        }
    }
}
