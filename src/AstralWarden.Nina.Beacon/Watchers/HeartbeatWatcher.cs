using AstralWarden.Nina.Beacon.Contracts;
using AstralWarden.Nina.Beacon.Server;

namespace AstralWarden.Nina.Beacon.Watchers;

/// <summary>Broadcasts a heartbeat every 5s: NINA is alive, the Beacon is alive, and here's how
/// well the socket is keeping up. Later phases enrich the payload (devices, sequence state).</summary>
public sealed class HeartbeatWatcher : IDisposable
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    private readonly BeaconServer _server;
    private readonly Func<IReadOnlyDictionary<string, bool>?>? _devices;
    private readonly Func<(bool Running, string? Instruction)>? _sequence;
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    public HeartbeatWatcher(BeaconServer server,
        Func<IReadOnlyDictionary<string, bool>?>? devices = null,
        Func<(bool Running, string? Instruction)>? sequence = null,
        Action<string>? log = null,
        TimeSpan? interval = null)
    {
        _server = server;
        _devices = devices;
        _sequence = sequence;
        _loop = Task.Run(() => PeriodicLoop.RunAsync("heartbeat", interval ?? Interval, Tick, log, _cts.Token));
    }

    private void Tick()
    {
        var sequence = TrySequence();
        _server.Broadcast("heartbeat", new HeartbeatPayload(
            UptimeSec: Math.Round((DateTimeOffset.UtcNow - _startedAt).TotalSeconds, 1),
            Clients: _server.ClientCount,
            DroppedSinceLastHeartbeat: _server.TakeDroppedSinceLast(),
            Devices: TryDevices(),
            SequenceRunning: sequence?.Running,
            Instruction: sequence?.Instruction));
    }

    private IReadOnlyDictionary<string, bool>? TryDevices()
    {
        try { return _devices?.Invoke(); }
        catch { return null; }
    }

    private (bool Running, string? Instruction)? TrySequence()
    {
        try { return _sequence?.Invoke(); }
        catch { return null; }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
