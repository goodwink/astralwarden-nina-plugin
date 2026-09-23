using System.Net.Http;
using System.Text.Json;
using AstralWarden.Nina.Beacon.Contracts;
using AstralWarden.Nina.Beacon.Mapping;
using AstralWarden.Nina.Beacon.Server;
using AstralWarden.Nina.Beacon.Watchers;

namespace AstralWarden.Nina.Beacon.Optional;

/// <summary>
/// Opportunistic reader of APPM's local HTTP API (127.0.0.1:60011). APPM normally only runs while
/// a pointing/tracking model is being built, so this polls slowly, treats connection-refused as a
/// normal absence, and caches the last model seen this session — re-emitting it to late-joining
/// clients so the agent gets the model even though APPM is long gone by imaging time. GETs only.
/// </summary>
public sealed class AppmPoller : IDisposable
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);
    private const string BaseUrl = "http://127.0.0.1:60011/api";

    private readonly BeaconServer _server;
    private readonly HttpClient _http;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private volatile AppmModelPayload? _lastModel;
    private string? _lastJson;

    public AppmPoller(BeaconServer server, HttpClient? http = null, Action<string>? log = null,
        TimeSpan? pollInterval = null)
    {
        _server = server;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        _server.ClientConnected += OnClientConnected;
        // tickImmediately: catch a model-building session that is already underway.
        _loop = Task.Run(() => PeriodicLoop.RunAsync("appm poll", pollInterval ?? PollInterval, PollOnceAsync,
            log, _cts.Token, tickImmediately: true));
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        try
        {
            string? runStatus = null;
            try
            {
                using var statusDoc = JsonDocument.Parse(
                    await _http.GetStringAsync($"{BaseUrl}/MappingRun/Status", ct).ConfigureAwait(false));
                runStatus = statusDoc.RootElement.ValueKind == JsonValueKind.String
                    ? statusDoc.RootElement.GetString()
                    : statusDoc.RootElement.ToString();
            }
            catch (JsonException)
            {
                runStatus = null; // status endpoint answered non-JSON; points may still work
            }

            using var pointsDoc = JsonDocument.Parse(
                await _http.GetStringAsync($"{BaseUrl}/MappingPoints", ct).ConfigureAwait(false));
            var model = AppmMapper.Map(pointsDoc.RootElement, runStatus);
            if (model.PointCount == 0) return;

            var json = BeaconJson.Serialize(model);
            if (json == _lastJson) return;
            _lastJson = json;
            _lastModel = model;
            _server.Broadcast("appm.model", model);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // APPM not running / mid-shutdown — the normal state; keep the cached model.
        }
    }

    private void OnClientConnected()
    {
        try
        {
            if (_lastModel is { } model) _server.Broadcast("appm.model", model);
        }
        catch { }
    }

    public void Dispose()
    {
        _server.ClientConnected -= OnClientConnected;
        _cts.Cancel();
        _cts.Dispose();
        _http.Dispose();
    }
}
