using AstralWarden.Nina.Beacon.Contracts;
using AstralWarden.Nina.Beacon.Server;

namespace AstralWarden.Nina.Beacon.Watchers;

/// <summary>Non-generic view of a device watcher for the heartbeat's connected-device summary.</summary>
public interface IDeviceSummary
{
    string Device { get; }
    bool IsConnected { get; }
}

/// <summary>
/// Base for the per-device consumers: maps each pushed TInfo through the pure mapper, applies the
/// emission throttle, and broadcasts device.state / device.connection. Runs inside NINA's
/// broadcast path, so everything is try/caught and non-blocking. Re-broadcasts the last state when
/// a client connects so a late-joining agent starts from current truth.
/// </summary>
public abstract class DeviceWatcherBase<TInfo> : IDeviceSummary, IDisposable where TInfo : NINA.Equipment.Equipment.DeviceInfo
{
    private readonly BeaconServer _server;
    private readonly StateThrottle _throttle = new();
    private readonly object _gate = new();
    private DeviceStatePayload? _last;

    protected DeviceWatcherBase(BeaconServer server, string device)
    {
        _server = server;
        Device = device;
    }

    /// <summary>Register with the device's mediator, then join the server's late-joiner replay.
    /// In that order: the plugin drops a watcher whose constructor throws without disposing it, so
    /// nothing may be attached to the server until registration has succeeded.</summary>
    protected void Attach(Action registerWithMediator)
    {
        registerWithMediator();
        _server.ClientConnected += OnClientConnected;
    }

    public string Device { get; }
    public bool IsConnected { get; private set; }

    /// <summary>Map connected-device info to the kind-specific state record.</summary>
    protected abstract object? Map(TInfo info);

    protected void Update(TInfo info)
    {
        try
        {
            // Decide under the lock, send outside it: this runs on NINA's device-broadcast thread,
            // so it must never hold a lock across anything but in-memory work.
            DeviceConnectionPayload? connection = null;
            DeviceStatePayload? state = null;
            lock (_gate)
            {
                var connected = info.Connected;
                IsConnected = connected;
                var payload = new DeviceStatePayload(Device, connected,
                    connected ? info.Name : null, connected ? Map(info) : null);

                var force = false;
                if (_throttle.ConnectionFlipped(connected))
                {
                    connection = new DeviceConnectionPayload(Device, connected, info.Name);
                    force = true; // state right away on connect/disconnect
                }

                var json = BeaconJson.Serialize(payload);
                if (_throttle.ShouldEmit(json, DateTimeOffset.UtcNow, force))
                {
                    _last = payload;
                    state = payload;
                }
            }

            if (connection is not null) _server.Broadcast("device.connection", connection);
            if (state is not null) _server.Broadcast("device.state", state);
        }
        catch
        {
            // never let telemetry propagate into NINA's device broadcast
        }
    }

    private void OnClientConnected()
    {
        try
        {
            DeviceStatePayload? last;
            lock (_gate) last = _last;
            if (last is not null) _server.Broadcast("device.state", last);
        }
        catch { /* same rule */ }
    }

    public virtual void Dispose()
    {
        _server.ClientConnected -= OnClientConnected;
        GC.SuppressFinalize(this);
    }
}
