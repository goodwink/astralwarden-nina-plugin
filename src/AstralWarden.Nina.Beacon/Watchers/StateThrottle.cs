namespace AstralWarden.Nina.Beacon.Watchers;

/// <summary>
/// Emission policy for one device's state pushes (pure, unit-tested): emit immediately on a
/// connection flip, at most once per <see cref="MinInterval"/> while the state is changing, and a
/// slow refresh every <see cref="RefreshInterval"/> while it is flat (so late-joining readers and
/// gap recovery converge without a chatty stream).
/// </summary>
public sealed class StateThrottle
{
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);

    private string? _lastEmitted;
    private DateTimeOffset _lastEmitAt = DateTimeOffset.MinValue;
    private bool? _lastConnected;

    /// <summary>True if the connection state flipped (caller emits device.connection).</summary>
    public bool ConnectionFlipped(bool connected)
    {
        var flipped = _lastConnected != connected;
        _lastConnected = connected;
        return flipped;
    }

    /// <summary>Decide whether to emit this serialized state now; records the emission if so.</summary>
    public bool ShouldEmit(string stateJson, DateTimeOffset now, bool force = false)
    {
        var changed = stateJson != _lastEmitted;
        var sinceLast = now - _lastEmitAt;
        var emit = force
            || (changed && sinceLast >= MinInterval)
            || (!changed && sinceLast >= RefreshInterval)
            || _lastEmitted is null;
        if (emit)
        {
            _lastEmitted = stateJson;
            _lastEmitAt = now;
        }
        return emit;
    }
}
