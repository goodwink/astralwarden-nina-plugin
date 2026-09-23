using System.Net;
using System.Net.Sockets;
using AstralWarden.Nina.Beacon.Contracts;

namespace AstralWarden.Nina.Beacon.Server;

/// <summary>
/// The Beacon's one-way telemetry socket: a loopback-only TCP listener that pushes newline-
/// delimited JSON to every connected client. Clients never send anything; nothing here reads from
/// the socket. Broadcast is non-blocking by construction (see BeaconClientConnection) so watchers
/// can call it from any NINA event handler without risk to imaging.
/// </summary>
public sealed class BeaconServer : IDisposable
{
    /// <summary>Wait between bind attempts while the port is unavailable.</summary>
    public static readonly TimeSpan RebindInterval = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan DefaultMinAcceptBackoff = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan MaxAcceptBackoff = TimeSpan.FromSeconds(5);
    internal const int MaxConsecutiveAcceptFailures = 10;

    private readonly int _configuredPort;
    private readonly int _queueCapacity;
    private readonly Func<object>? _helloFactory;
    private readonly Action<string>? _log;
    private readonly TimeSpan _rebindInterval;
    private readonly TimeSpan _minAcceptBackoff;
    private int _disposed;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private readonly List<BeaconClientConnection> _clients = new();
    private TcpListener _listener;
    private long _droppedFromClosed;
    private long _droppedReported;
    private Task? _runner;
    private volatile bool _listening;

    public BeaconServer(int port = BeaconProtocol.DefaultPort, int queueCapacity = 2000,
        Func<object>? helloFactory = null, Action<string>? log = null, TimeSpan? rebindInterval = null,
        TimeSpan? minAcceptBackoff = null)
    {
        _configuredPort = port;
        _queueCapacity = queueCapacity;
        _helloFactory = helloFactory;
        _log = log;
        _rebindInterval = rebindInterval ?? RebindInterval;
        _minAcceptBackoff = minAcceptBackoff ?? DefaultMinAcceptBackoff;
        _listener = new TcpListener(IPAddress.Loopback, port);
    }

    /// <summary>Actual bound port (useful when constructed with port 0 in tests); the configured
    /// port while unbound.</summary>
    public int Port
    {
        get
        {
            try { return _listening ? ((IPEndPoint)_listener.LocalEndpoint).Port : _configuredPort; }
            catch { return _configuredPort; }
        }
    }

    /// <summary>False while the port is unavailable — the Beacon stays alive and keeps retrying.</summary>
    public bool IsListening => _listening;

    /// <summary>Raised after a client is accepted (and its hello queued). Watchers use this to
    /// re-broadcast current state so a late joiner starts from truth.</summary>
    public event Action? ClientConnected;

    public int ClientCount { get { lock (_gate) return _clients.Count; } }

    /// <summary>
    /// Bind and start accepting. A failure to bind (port 1999 already taken by a stale NINA, another
    /// Beacon, or anything else) must never take the plugin down with it — the Beacon runs on the one
    /// machine the user can't walk over and fix. So this never throws: it logs and retries in the
    /// background, and every other feed keeps working meanwhile.
    /// </summary>
    public void Start()
    {
        if (_runner is not null) return;
        var bound = TryBind();
        _log?.Invoke(bound
            ? $"listening on 127.0.0.1:{Port}"
            : $"port {_configuredPort} is unavailable — telemetry is off, retrying every " +
              $"{_rebindInterval.TotalSeconds:0}s (NINA is unaffected)");
        _runner = Task.Run(() => RunAsync(bound, _cts.Token));
    }

    private bool TryBind()
    {
        try
        {
            _listener.Start();
            _listening = true;
            return true;
        }
        catch (Exception ex)
        {
            _listening = false;
            _log?.Invoke($"bind failed: {ex.Message}");
            return false;
        }
    }

    private void StopListener()
    {
        _listening = false;
        try { _listener.Stop(); } catch { /* already down */ }
        // A listener whose socket faulted can't be trusted to rebind; start from a fresh one.
        _listener = new TcpListener(IPAddress.Loopback, _configuredPort);
    }

    /// <summary>Owns the listener for the lifetime of the server: bind → accept → (on a broken
    /// listener) rebind, forever, until disposal.</summary>
    private async Task RunAsync(bool bound, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (!bound)
                {
                    try { await Task.Delay(_rebindInterval, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                    bound = TryBind();
                    if (bound) _log?.Invoke($"listening on 127.0.0.1:{Port}");
                    continue;
                }

                var accept = (CancellationToken token) => _listener.AcceptTcpClientAsync(token).AsTask();
                await AcceptLoopAsync(accept, ct).ConfigureAwait(false); // mutation-gate: ignore — ConfigureAwait is unobservable here: this whole loop runs under Task.Run, with no SynchronizationContext to capture
                if (ct.IsCancellationRequested) return;

                StopListener();
                bound = false;
                _log?.Invoke("listener stopped accepting; rebinding");
            }
        }
        catch (Exception ex)
        {
            // Terminal-safe: if this loop ever dies the socket is gone for the session, so say so
            // once and loudly rather than going quiet.
            _listening = false;
            _log?.Invoke($"listen loop stopped unexpectedly — telemetry socket is down: {ex}");
        }
    }

    /// <summary>Serialize once, enqueue to every client. Never blocks, never throws.</summary>
    public void Broadcast(string type, object payload)
    {
        try
        {
            var json = BeaconJson.Serialize(payload);
            var ts = BeaconJson.Timestamp(DateTimeOffset.UtcNow);
            BeaconClientConnection[] clients;
            lock (_gate) clients = _clients.ToArray();
            foreach (var client in clients)
                client.Enqueue(type, ts, json);
        }
        catch (Exception ex)
        {
            // A telemetry failure must never propagate into a NINA event handler.
            _log?.Invoke($"broadcast failed: {ex.Message}");
        }
    }

    /// <summary>Total messages dropped across all clients since the last call (for heartbeat).</summary>
    public long TakeDroppedSinceLast()
    {
        long total = Interlocked.Read(ref _droppedFromClosed);
        lock (_gate)
        {
            foreach (var client in _clients)
                total += client.Dropped;
        }
        var delta = total - _droppedReported;
        _droppedReported = total;
        return delta;
    }

    /// <summary>Returns on cancellation, or when the listener has stopped being usable (the caller
    /// then rebinds). Never spins: a repeated accept failure backs off exponentially. The accept
    /// itself is a parameter so the failure accounting can be driven without a broken OS socket —
    /// that accounting is the difference between recycling a listener that is genuinely dead and
    /// disconnecting every agent because a few clients dropped over a night.</summary>
    internal async Task AcceptLoopAsync(Func<CancellationToken, Task<TcpClient>> accept, CancellationToken ct)
    {
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            TcpClient tcp;
            try
            {
                tcp = await accept(ct).ConfigureAwait(false); // mutation-gate: ignore — no SynchronizationContext on this path (Task.Run), so true and false behave identically
                failures = 0;
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (InvalidOperationException) { return; } // listener not started / stopped underneath us
            catch (SocketException ex)
            {
                // One failed accept is a client that vanished mid-handshake; a stream of them is a
                // broken listener, which would otherwise spin this loop at 100% CPU on the imaging
                // PC. Back off, then hand back to RunAsync to rebind.
                if (++failures == 1) _log?.Invoke($"accept failed: {ex.Message}");
                if (failures >= MaxConsecutiveAcceptFailures)
                {
                    _log?.Invoke($"accept failed {failures} times in a row; recycling the listener");
                    return;
                }
                var backoffMs = Math.Min(MaxAcceptBackoff.TotalMilliseconds,
                    _minAcceptBackoff.TotalMilliseconds * Math.Pow(2, failures - 1));
                try { await Task.Delay(TimeSpan.FromMilliseconds(backoffMs), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                continue;
            }

            var connection = new BeaconClientConnection(tcp, _queueCapacity, ct);
            connection.Closed += c =>
            {
                lock (_gate)
                {
                    if (_clients.Remove(c))
                        Interlocked.Add(ref _droppedFromClosed, c.Dropped);
                }
                c.Dispose();
                _log?.Invoke("client disconnected");
            };

            // hello goes first, before the connection can see any broadcast. Build it outside the
            // gate: the factory is NINA's code, and a broadcast must never end up waiting on it.
            string? hello = null;
            if (_helloFactory is not null)
            {
                try
                {
                    hello = BeaconJson.Serialize(_helloFactory());
                }
                catch (Exception ex)
                {
                    _log?.Invoke($"hello failed: {ex.Message}");
                }
            }

            // Queueing hello and joining the client set are one step. The pump can only fail — and
            // so can only fire Closed — once there is a line to write, and Closed unregisters
            // through this same gate. Enqueue outside it and a peer that dies mid-hello can be
            // removed before it was ever added, leaving a phantom nothing ever removes: written to
            // on every broadcast, for as long as NINA runs.
            lock (_gate)
            {
                if (hello is not null)
                    connection.Enqueue("hello", BeaconJson.Timestamp(DateTimeOffset.UtcNow), hello);
                _clients.Add(connection);
            }
            _log?.Invoke("client connected");
            try { ClientConnected?.Invoke(); }
            catch (Exception ex) { _log?.Invoke($"client-connected hook failed: {ex.Message}"); }
        }
    }

    /// <summary>Broadcast bye, drain briefly so it actually leaves, then tear everything down.</summary>
    public async Task ShutdownAsync(string reason)
    {
        Broadcast("bye", new ByePayload(reason));
        BeaconClientConnection[] clients;
        lock (_gate) { clients = _clients.ToArray(); _clients.Clear(); }
        foreach (var client in clients)
            await client.DrainAndCloseAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        Dispose();
    }

    /// <summary>Idempotent: ShutdownAsync already disposes, and NINA's teardown path can reach here
    /// again — a second call must be a no-op, not an ObjectDisposedException out of the CTS.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cts.Cancel();
        _listening = false;
        try { _listener.Stop(); } catch { /* already stopped */ }
        BeaconClientConnection[] clients;
        lock (_gate) { clients = _clients.ToArray(); _clients.Clear(); }
        foreach (var client in clients)
            client.Dispose();
        _cts.Dispose();
    }
}
