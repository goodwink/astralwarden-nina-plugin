namespace AstralWarden.Nina.Beacon.Watchers;

/// <summary>
/// Several NINA mediator events forward their add/remove to a backing VM that only registers
/// AFTER plugin composition (SequenceMediator → sequenceNavigation, TelescopeMediator/
/// CameraMediator/ImageSaveMediator → handler), so subscribing in a plugin constructor throws
/// NullReferenceException — the failure that killed the first live install. This retries the
/// subscription until the mediator is ready. Group only events of ONE mediator per instance so a
/// mid-list failure can't double-subscribe on retry (a mediator's events share one handler field —
/// they become subscribable atomically).
/// </summary>
public sealed class RetrySubscription : IDisposable
{
    private readonly Action _subscribe;
    private readonly Action _unsubscribe;
    private readonly Action<string>? _log;
    private readonly string _name;
    private readonly CancellationTokenSource _cts = new();
    private volatile bool _subscribed;

    public RetrySubscription(string name, Action subscribe, Action unsubscribe,
        Action<string>? log = null, TimeSpan? retryInterval = null)
    {
        _name = name;
        _subscribe = subscribe;
        _unsubscribe = unsubscribe;
        _log = log;
        var interval = retryInterval ?? TimeSpan.FromSeconds(5);
        _ = Task.Run(() => LoopAsync(interval, _cts.Token));
    }

    public bool IsSubscribed => _subscribed;

    private async Task LoopAsync(TimeSpan interval, CancellationToken ct)
    {
        var attempts = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                _subscribe();
                _subscribed = true;
                _log?.Invoke($"{_name}: subscribed (attempt {attempts + 1})");
                return;
            }
            catch (Exception ex)
            {
                if (attempts++ == 0) _log?.Invoke($"{_name}: not ready yet ({ex.GetType().Name}), retrying");
            }
            try { await Task.Delay(interval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        if (_subscribed)
        {
            try { _unsubscribe(); } catch { /* mediator torn down first */ }
        }
    }
}
