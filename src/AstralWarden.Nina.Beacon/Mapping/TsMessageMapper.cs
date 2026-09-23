using AstralWarden.Nina.Beacon.Contracts;
using NINA.Astrometry;

namespace AstralWarden.Nina.Beacon.Mapping;

/// <summary>
/// Pure mapping of Target Scheduler broker messages (topic + content + custom headers) to wire
/// payloads. Shapes verified against TS 5.10.3 source (PubSub/*Publisher.cs): everything rides
/// CustomHeaders; "Coordinates" is a live NINA Coordinates object; gain/offset are strings that
/// may be "(camera)".
/// </summary>
public static class TsMessageMapper
{
    public const string WaitStart = "TargetScheduler-WaitStart";
    public const string TargetStart = "TargetScheduler-TargetStart";
    public const string NewTargetStart = "TargetScheduler-NewTargetStart";
    public const string TargetComplete = "TargetScheduler-TargetComplete";
    public const string ContainerStopped = "TargetScheduler-ContainerStopped";

    public static readonly string[] Topics = [WaitStart, TargetStart, NewTargetStart, TargetComplete, ContainerStopped];

    /// <summary>Returns the (message type, payload) to broadcast, or null for unknown topics.</summary>
    public static (string Type, object Payload)? Map(string topic, object? content, IDictionary<string, object>? headers)
    {
        headers ??= new Dictionary<string, object>();
        var (ra, dec) = Coords(headers);
        return topic switch
        {
            WaitStart => ("ts.waitstart", new TsWaitStartPayload(
                Str(headers, "ProjectName"), Str(headers, "TargetName"), ra, dec,
                Dbl(headers, "Rotation"), Int(headers, "SecondsUntilNextTarget"))),

            TargetStart or NewTargetStart => ("ts.targetstart", new TsTargetStartPayload(
                NewTarget: topic == NewTargetStart,
                Str(headers, "ProjectName"), Str(headers, "TargetName"), ra, dec,
                Dbl(headers, "Rotation"),
                Str(headers, "ExposureFilterName"), Dbl(headers, "ExposureLength"),
                Str(headers, "ExposureGain"), Str(headers, "ExposureOffset"),
                Str(headers, "ExposureBinning"))),

            TargetComplete => ("ts.targetcomplete", new TsTargetCompletePayload(
                Str(headers, "ProjectName"),
                Str(headers, "TargetName") ?? content as string, ra, dec, Dbl(headers, "Rotation"))),

            ContainerStopped => ("ts.containerstopped", new TsContainerStoppedPayload(
                headers.TryGetValue("StoppedAt", out var at) && at is DateTime dt
                    ? Contracts.BeaconJson.Timestamp(dt.ToUniversalTime())
                    : null)),

            _ => null,
        };
    }

    private static string? Str(IDictionary<string, object> h, string name) =>
        h.TryGetValue(name, out var v) && v is string s && !string.IsNullOrWhiteSpace(s) ? s : null;

    private static int? Int(IDictionary<string, object> h, string name) =>
        h.TryGetValue(name, out var v) ? v switch { int i => i, long l => (int)l, _ => null } : null;

    private static double? Dbl(IDictionary<string, object> h, string name) =>
        h.TryGetValue(name, out var v)
            ? v switch
            {
                double d when !double.IsNaN(d) => d,
                float f when !float.IsNaN(f) => f,
                int i => i,
                _ => null,
            }
            : null;

    private static (double? Ra, double? Dec) Coords(IDictionary<string, object> h)
    {
        if (h.TryGetValue("Coordinates", out var v) && v is Coordinates c)
        {
            return (double.IsNaN(c.RA) ? null : c.RA, double.IsNaN(c.Dec) ? null : c.Dec);
        }
        return (null, null);
    }
}
