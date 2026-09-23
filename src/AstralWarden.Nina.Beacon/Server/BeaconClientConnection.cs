using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;

namespace AstralWarden.Nina.Beacon.Server;

/// <summary>
/// One connected reader. Owns a bounded queue and a write pump so a slow (or stalled) client can
/// never block the thread that produced a message: enqueue is a non-blocking TryWrite with
/// drop-oldest, and only the pump task touches the socket.
/// </summary>
internal sealed class BeaconClientConnection : IDisposable
{
    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly Channel<string> _queue;
    private readonly object _seqGate = new();
    private long _seq;
    private long _dropped;
    private int _disposed;
    private readonly CancellationTokenSource _cts;
    private readonly Task _pump;

    /// <summary>Fired (once) when the pump ends for any reason; the server unregisters us.</summary>
    public event Action<BeaconClientConnection>? Closed;

    public BeaconClientConnection(TcpClient tcp, int queueCapacity, CancellationToken serverCt)
    {
        _tcp = tcp;
        _stream = tcp.GetStream();
        _queue = Channel.CreateBounded<string>(
            new BoundedChannelOptions(queueCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
            },
            _ => Interlocked.Increment(ref _dropped));
        _cts = CancellationTokenSource.CreateLinkedTokenSource(serverCt);
        _pump = Task.Run(() => PumpAsync(_cts.Token));
    }

    /// <summary>Messages silently dropped because the client wasn't keeping up. Never resets.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>
    /// Stamp this connection's next seq onto a pre-serialized payload and enqueue the full line.
    /// Non-blocking; drops (oldest-first) when the queue is full. Seq is per-connection so a
    /// client's own gaps are exactly the messages it lost.
    /// </summary>
    public void Enqueue(string type, string ts, string payloadJson)
    {
        lock (_seqGate)
        {
            var line = $"{{\"v\":{Contracts.BeaconProtocol.Version},\"type\":\"{type}\",\"seq\":{++_seq},\"ts\":\"{ts}\",\"payload\":{payloadJson}}}";
            _queue.Writer.TryWrite(line);
        }
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var line in _queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                var bytes = Encoding.UTF8.GetBytes(line + "\n");
                await _stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }          // client went away
        catch (ObjectDisposedException) { }
        catch (SocketException) { }
        finally
        {
            Closed?.Invoke(this);
        }
    }

    /// <summary>Stop accepting new messages, give the pump a moment to drain, then close.</summary>
    public async Task DrainAndCloseAsync(TimeSpan timeout)
    {
        _queue.Writer.TryComplete();
        await Task.WhenAny(_pump, Task.Delay(timeout)).ConfigureAwait(false);
        Dispose();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _queue.Writer.TryComplete();
        _cts.Cancel();
        try { _tcp.Close(); } catch { /* already closed */ }
        _cts.Dispose();
    }
}
