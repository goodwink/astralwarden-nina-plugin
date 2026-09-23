namespace AstralWarden.Nina.Beacon.Watchers;

/// <summary>
/// The one driver behind every background poll loop in the Beacon (heartbeat, sequence poll).
/// A loop that dies to an unanticipated exception is a feed that goes quiet with nobody noticing —
/// on a rig the user can't walk over to. So: each tick is individually guarded, so one bad tick
/// never ends the loop; a fault is logged exactly once per loop (a fault that repeats every tick
/// must not flood NINA's log); and the loop itself ends only on cancellation.
/// </summary>
public static class PeriodicLoop
{
    /// <param name="name">Loop name, used in the one-shot fault log.</param>
    public static async Task RunAsync(string name, TimeSpan interval, Func<CancellationToken, Task> tick,
        Action<string>? log, CancellationToken ct)
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
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                await GuardedTickAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            log?.Invoke($"{name} loop stopped unexpectedly and will not restart: {ex}");
        }
    }

    /// <summary>How long teardown waits for a tick already in progress to finish.</summary>
    public static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Cancel a loop and wait (bounded) for any tick in progress to finish before returning. Teardown
    /// calls this: a tick that outlives it is our code still reading NINA's state, or still making a
    /// request, after the plugin has said it stopped. The wait is bounded so a tick stuck in someone
    /// else's code can delay NINA's exit by at most <see cref="StopTimeout"/>, never hang it. Ticks
    /// run on the thread pool with no captured context, so blocking here cannot deadlock them.
    /// </summary>
    public static void Stop(CancellationTokenSource cts, Task loop)
    {
        try { cts.Cancel(); } catch (ObjectDisposedException) { return; }
        try { loop.Wait(StopTimeout); } catch { /* the loop's own faults are already logged */ }
        cts.Dispose();
    }

    /// <summary>Synchronous-tick overload for the loops that only touch in-memory state.</summary>
    public static Task RunAsync(string name, TimeSpan interval, Action tick,
        Action<string>? log, CancellationToken ct) =>
        RunAsync(name, interval, _ => { tick(); return Task.CompletedTask; }, log, ct);
}
