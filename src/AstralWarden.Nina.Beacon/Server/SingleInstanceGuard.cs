namespace AstralWarden.Nina.Beacon.Server;

/// <summary>
/// Decides which NINA instance on this PC runs the Beacon: the first to claim. A PC can run several
/// NINA instances (one per camera or profile), but the Beacon's socket is one fixed port and the
/// agent monitors one rig per PC. Without this, a second instance would retry the port until the
/// first closed, then take it over, and the agent would start receiving another rig's telemetry
/// as if nothing had changed.
///
/// The claim is a named mutex that the winner merely keeps open; it is never "owned" in the
/// thread sense, so it doesn't matter which thread NINA later tears the plugin down on. Creating it
/// is atomic, so exactly one of two instances starting together wins. A refused instance closes
/// its handle at once, so when the winner exits (or crashes, and Windows closes its handles) the
/// name is free for the next NINA to start. The refused instance itself stays off for its session.
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    /// <summary>Machine-wide, like the port it protects.</summary>
    public const string BeaconName = "Global\\AstralWardenBeacon";

    private Mutex? _mutex;

    private SingleInstanceGuard(Mutex mutex) => _mutex = mutex;

    /// <summary>The claim if this is the first instance; null if another instance holds it.</summary>
    public static SingleInstanceGuard? TryClaim(string name)
    {
        var mutex = new Mutex(initiallyOwned: false, name, out var createdNew);
        if (createdNew) return new SingleInstanceGuard(mutex);
        mutex.Dispose();
        return null;
    }

    public void Dispose() => Interlocked.Exchange(ref _mutex, null)?.Dispose();
}
