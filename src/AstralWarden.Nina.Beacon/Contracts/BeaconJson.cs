using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AstralWarden.Nina.Beacon.Contracts;

/// <summary>Single serializer configuration for everything the Beacon puts on the wire.</summary>
public static class BeaconJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public static string Serialize<T>(T payload) => JsonSerializer.Serialize(payload, Options);

    /// <summary>
    /// UTC ISO-8601 with milliseconds — the envelope `ts` format.
    /// InvariantCulture is REQUIRED, not decorative: in a custom date format string ':' is the
    /// culture's time separator placeholder, not a literal, so on a rig whose locale separates time
    /// with something else every timestamp we emit would be malformed. Rig PCs at hosting sites are
    /// whatever locale their owner installed.
    /// </summary>
    public static string Timestamp(DateTimeOffset at) =>
        at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
