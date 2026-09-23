using System.IO;
using System.Net.Sockets;
using System.Text.Json;
using AstralWarden.Nina.Beacon.Server;

namespace AstralWarden.Nina.Beacon.Tests;

/// <summary>
/// A connected Beacon client that keeps reading in the background and records what arrived. Reading
/// continuously (rather than a read-with-timeout per assertion) matters: an abandoned ReadLineAsync
/// leaves the stream mid-operation and the next read throws.
/// </summary>
internal sealed class WireReader : IDisposable
{
    private readonly TcpClient _client = new();
    private readonly List<JsonElement> _envelopes = new();

    public async Task ConnectAsync(BeaconServer server)
    {
        await _client.ConnectAsync("127.0.0.1", server.Port);
        while (server.ClientCount == 0) await Task.Delay(5);
        _ = Task.Run(async () =>
        {
            using var reader = new StreamReader(_client.GetStream());
            try
            {
                string? line;
                while ((line = await reader.ReadLineAsync()) is not null)
                {
                    var envelope = JsonDocument.Parse(line).RootElement.Clone();
                    lock (_envelopes) _envelopes.Add(envelope);
                }
            }
            catch { /* the server closed */ }
        });
    }

    public JsonElement[] Envelopes { get { lock (_envelopes) return _envelopes.ToArray(); } }

    public string[] Types => Envelopes.Select(e => e.GetProperty("type").GetString()!).ToArray();

    public JsonElement[] OfType(string type) =>
        Envelopes.Where(e => e.GetProperty("type").GetString() == type).ToArray();

    /// <summary>
    /// Waits for the arrival of a message rather than sleeping a fixed settle time. A wall-clock
    /// sleep long enough to be reliable on a loaded CI runner is a slow suite, and one short enough
    /// to be quick is a suite that goes red at random — which only teaches people to re-run it.
    /// </summary>
    public async Task<JsonElement[]> WaitForCountAsync(string type, int count, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));
        while (DateTime.UtcNow < deadline)
        {
            var hits = OfType(type);
            if (hits.Length >= count) return hits;
            await Task.Delay(10);
        }
        throw new TimeoutException(
            $"only {OfType(type).Length} of {count} '{type}' messages arrived; saw [{string.Join(", ", Types)}]");
    }

    public async Task<JsonElement> WaitForAsync(string type, TimeSpan? timeout = null) =>
        (await WaitForCountAsync(type, 1, timeout))[0];

    public void Dispose() => _client.Dispose();
}
