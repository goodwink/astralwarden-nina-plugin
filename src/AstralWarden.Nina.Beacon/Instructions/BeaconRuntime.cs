namespace AstralWarden.Nina.Beacon.Instructions;

/// <summary>
/// Static bridge between MEF-instantiated sequence items and the Beacon's server. NINA creates
/// instruction instances itself (and clones them freely), so they can't take the server by
/// constructor; the Beacon plugin sets these on startup and clears them on teardown.
/// </summary>
public static class BeaconRuntime
{
    /// <summary>Broadcast delegate (type, payload) — null until the Beacon has started.</summary>
    public static volatile Action<string, object>? Broadcast;

    /// <summary>Connected-client count for instruction validation hints.</summary>
    public static volatile Func<int>? ClientCount;
}
