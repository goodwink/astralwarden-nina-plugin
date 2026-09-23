using AstralWarden.Nina.Beacon.Contracts;
using NINA.Image.ImageAnalysis;

namespace AstralWarden.Nina.Beacon.Mapping;

/// <summary>
/// Pure star-list → wire mapping (the highest-value tested function in the plugin). Caps the raw
/// list at <see cref="MaxStars"/> (brightest first) and always computes an 8×8 grid of per-cell
/// medians over ALL stars — the grid is the tilt/curvature signal and stays tiny no matter how
/// many stars HocusFocus found.
/// </summary>
public static class StarListMapper
{
    public const int MaxStars = 500;
    public const int GridSize = 8;

    /// <summary>Per-star (fwhm, ecc, theta) accessor — null when the active detector doesn't provide them.</summary>
    public delegate (double? Fwhm, double? Ecc, double? Theta) StarExtras(DetectedStar star);

    public static ImageStarsPayload Map(
        IReadOnlyList<DetectedStar> stars, int? width, int? height, StarExtras? extras,
        string? path, string? detector)
    {
        // Grid geometry: sensor dims when known, else the observed star extent (stars cover the
        // field in practice, and a consistent-per-frame grid is what the analysis needs).
        var w = width is > 0 ? width.Value : Extent(stars, s => s.Position.X);
        var h = height is > 0 ? height.Value : Extent(stars, s => s.Position.Y);

        var points = new List<StarPoint>(Math.Min(stars.Count, MaxStars));
        foreach (var star in stars.OrderByDescending(s => s.MaxBrightness).Take(MaxStars))
        {
            // A star whose detection produced a non-finite position or HFR is not a measurement.
            // These three come straight off the detector with no upstream guard (unlike the PSF
            // extras), and the serializer is configured to write NaN as the string "NaN" rather
            // than throw — so an unguarded value would ship as a string in a numeric field.
            if (!Finite(star.Position.X) || !Finite(star.Position.Y) || !Finite(star.HFR)) continue;

            var (fwhm, ecc, theta) = extras?.Invoke(star) ?? (null, null, null);
            points.Add(new StarPoint(
                Math.Round(star.Position.X, 1), Math.Round(star.Position.Y, 1),
                Math.Round(star.HFR, 3), Round3(fwhm), Round3(ecc), Round3(theta),
                Finite(star.MaxBrightness) ? Math.Round(star.MaxBrightness, 1) : 0));
        }

        return new ImageStarsPayload(path, detector, w, h, stars.Count, points, BuildGrid(stars, w, h, extras));
    }

    private static IReadOnlyList<IReadOnlyList<GridCell?>> BuildGrid(
        IReadOnlyList<DetectedStar> stars, int width, int height, StarExtras? extras)
    {
        var cells = new List<(double Hfr, double? Fwhm, double? Ecc)>[GridSize, GridSize];
        foreach (var star in stars)
        {
            // Same rule as the emitted list: a non-finite star would drag its cell's median to NaN,
            // and (int)NaN is unspecified — it must not reach the cell arithmetic at all.
            if (!Finite(star.Position.X) || !Finite(star.Position.Y) || !Finite(star.HFR)) continue;

            var cx = Math.Clamp((int)(star.Position.X * GridSize / width), 0, GridSize - 1);
            var cy = Math.Clamp((int)(star.Position.Y * GridSize / height), 0, GridSize - 1);
            var (fwhm, ecc, _) = extras?.Invoke(star) ?? (null, null, null);
            (cells[cy, cx] ??= new List<(double, double?, double?)>()).Add((star.HFR, fwhm, ecc));
        }

        var grid = new List<IReadOnlyList<GridCell?>>(GridSize);
        for (var y = 0; y < GridSize; y++)
        {
            var row = new List<GridCell?>(GridSize);
            for (var x = 0; x < GridSize; x++)
            {
                var cell = cells[y, x];
                row.Add(cell is null
                    ? null
                    : new GridCell(
                        cell.Count,
                        Round3(Median(cell.Select(v => (double?)v.Hfr))),
                        Round3(Median(cell.Select(v => v.Fwhm))),
                        Round3(Median(cell.Select(v => v.Ecc)))));
            }
            grid.Add(row);
        }
        return grid;
    }

    private static double? Median(IEnumerable<double?> values)
    {
        var sorted = values.Where(v => v.HasValue).Select(v => v!.Value).OrderBy(v => v).ToList();
        if (sorted.Count == 0) return null;
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    /// <summary>Observed star extent along one axis, used only when the sensor dims are unknown.
    /// Always ≥ 1 so the grid arithmetic can never divide by zero or a negative.</summary>
    private static int Extent(IReadOnlyList<DetectedStar> stars, Func<DetectedStar, double> axis)
    {
        var max = 0.0;
        foreach (var star in stars)
        {
            var v = axis(star);
            if (Finite(v) && v > max) max = v;
        }
        return Math.Max(1, (int)Math.Ceiling(max) + 1);
    }

    private static double? Round3(double? v) =>
        v is null || !Finite(v.Value) ? null : Math.Round(v.Value, 3);

    private static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
}
