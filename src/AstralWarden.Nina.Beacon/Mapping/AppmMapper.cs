using System.Text.Json;
using AstralWarden.Nina.Beacon.Contracts;

namespace AstralWarden.Nina.Beacon.Mapping;

/// <summary>
/// Pure parsing of APPM's /api/MappingPoints response into the wire model. The API is
/// undocumented, so parsing is shape-tolerant: accepts a bare array or the first array-valued
/// property of an object, and reads point fields case-insensitively (AppmMeasurementPoint:
/// HourAngle, Dec, RaDelta, DecDelta, Side, Status, ...). RMS is derived from the per-point
/// solve residuals.
/// </summary>
public static class AppmMapper
{
    public static AppmModelPayload Map(JsonElement root, string? runStatus)
    {
        var points = new List<AppmPoint>();
        foreach (var el in FindPointArray(root))
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            points.Add(new AppmPoint(
                Dbl(el, "HourAngle"), Dbl(el, "Dec") ?? Dbl(el, "Declination"),
                Dbl(el, "RaDelta"), Dbl(el, "DecDelta"),
                Str(el, "Side"), Str(el, "Status")));
        }

        var solved = points.Where(p => p.RaDelta.HasValue && p.DecDelta.HasValue).ToList();
        double? raRms = null, decRms = null, totalRms = null;
        if (solved.Count > 0)
        {
            raRms = Rms(solved.Select(p => p.RaDelta!.Value));
            decRms = Rms(solved.Select(p => p.DecDelta!.Value));
            totalRms = Math.Round(Math.Sqrt(raRms.Value * raRms.Value + decRms.Value * decRms.Value), 2);
        }

        return new AppmModelPayload(runStatus, points.Count, raRms, decRms, totalRms, points);
    }

    private static IEnumerable<JsonElement> FindPointArray(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
            return root.EnumerateArray();
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in root.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.Array)
                    return prop.Value.EnumerateArray();
            }
        }
        return [];
    }

    private static double Rms(IEnumerable<double> values)
    {
        var list = values.ToList();
        return Math.Round(Math.Sqrt(list.Sum(v => v * v) / list.Count), 2);
    }

    private static double? Dbl(JsonElement el, string name) =>
        TryProp(el, name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)
            && !double.IsNaN(d) && !double.IsInfinity(d)
            ? d
            : null;

    private static string? Str(JsonElement el, string name)
    {
        if (!TryProp(el, name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => v.ToString(),
            _ => null,
        };
    }

    private static bool TryProp(JsonElement el, string name, out JsonElement value)
    {
        foreach (var prop in el.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }
        value = default;
        return false;
    }
}
