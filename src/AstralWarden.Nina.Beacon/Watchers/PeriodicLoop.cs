namespace AstralWarden.Nina.Beacon.Watchers;

/// <summary>
/// The one driver behind every background poll loop in the Beacon (heartbeat, sequence poll, APPM).
/// A loop that dies to an unanticipated exception is a feed that goes quiet with nobody noticing —
/// on a rig the user can't walk over to. So: each tick is individually guarded, so one bad tick
/// never ends the loop; a fault is logged exactly once per loop (a fault that repeats every tick
/// must not flood NINA's log); and the loop itself ends only on cancellation.
/// </summary>
public static class PeriodicLoop
{
    /// <param name="name">Loop name, used in the one-shot fault log.</param>
    /// <param name="tickImmediately">Run one tick before the first interval elapses.</param>
    public static async Task RunAsync(string name, TimeSpan interval, Func<CancellationToken, Task> tick,
        Action<string>? log, CancellationToken ct, bool tickImmediately = false)
    {
        var reported = false;

        async Task GuardedTickAsync()
        {
            try
            {
                await tick(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // cancellation ends the loop, as it should
            }
            catch (Exception ex)
            {
                if (!reported)
                {
                    reported = true;
                    log?.Invoke($"{name} tick failed (logged once; the loop continues): {ex}");
                }
            }
        }

        try
        {
            using var timer = new PeriodicTimer(interval);
            if (tickImmediately) await GuardedTickAsync().ConfigureAwait(false);
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                await GuardedTickAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            log?.Invoke($"{name} loop stopped unexpectedly and will not restart: {ex}");
        }
    }

    /// <summary>Synchronous-tick overload for the loops that only touch in-memory state.</summary>
    public static Task RunAsync(string name, TimeSpan interval, Action tick,
        Action<string>? log, CancellationToken ct, bool tickImmediately = false) =>
        RunAsync(name, interval, _ => { tick(); return Task.CompletedTask; }, log, ct, tickImmediately);
}
