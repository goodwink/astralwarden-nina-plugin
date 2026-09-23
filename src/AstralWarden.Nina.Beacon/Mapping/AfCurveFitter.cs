using AstralWarden.Nina.Beacon.Contracts;

namespace AstralWarden.Nina.Beacon.Mapping;

/// <summary>
/// Least-squares quadratic fit over the measured autofocus points (pure, unit-tested). NINA's own
/// engines fit richer models but never expose them in-process; a parabola around the minimum is
/// enough to give the agent a fitted focus position and an R² fit-quality signal per run.
/// </summary>
public static class AfCurveFitter
{
    public static AfFit? Fit(IReadOnlyList<AfMeasurePoint> points)
    {
        if (points.Count < 3) return null;

        // Normal equations for y = a·x² + b·x + c, with x shifted by its mean for conditioning
        // (focuser positions are ~1e4-1e5; x⁴ would otherwise reach 1e20).
        var xMean = points.Average(p => p.Position);
        int n = points.Count;
        double sx = 0, sx2 = 0, sx3 = 0, sx4 = 0, sy = 0, sxy = 0, sx2y = 0;
        foreach (var p in points)
        {
            var x = p.Position - xMean;
            var y = p.Hfr;
            var x2 = x * x;
            sx += x; sx2 += x2; sx3 += x2 * x; sx4 += x2 * x2;
            sy += y; sxy += x * y; sx2y += x2 * y;
        }

        // Solve the 3×3 system via Cramer's rule.
        var det = Det3(sx4, sx3, sx2, sx3, sx2, sx, sx2, sx, n);
        if (Math.Abs(det) < 1e-12) return null;
        var a = Det3(sx2y, sx3, sx2, sxy, sx2, sx, sy, sx, n) / det;
        var b = Det3(sx4, sx2y, sx2, sx3, sxy, sx, sx2, sy, n) / det;
        var c = Det3(sx4, sx3, sx2y, sx3, sx2, sxy, sx2, sx, sy) / det;

        if (a <= 0) return null; // no upward-opening parabola → no meaningful minimum

        var minX = -b / (2 * a);
        var minY = a * minX * minX + b * minX + c;

        var yMean = sy / n;
        double ssRes = 0, ssTot = 0;
        foreach (var p in points)
        {
            var x = p.Position - xMean;
            var predicted = a * x * x + b * x + c;
            ssRes += (p.Hfr - predicted) * (p.Hfr - predicted);
            ssTot += (p.Hfr - yMean) * (p.Hfr - yMean);
        }
        var r2 = ssTot < 1e-12 ? 0 : 1 - ssRes / ssTot;

        return new AfFit("quadratic", Math.Round(minX + xMean, 1), Math.Round(minY, 3),
            Math.Round(Math.Clamp(r2, 0, 1), 4));
    }

    private static double Det3(
        double a11, double a12, double a13,
        double a21, double a22, double a23,
        double a31, double a32, double a33) =>
        a11 * (a22 * a33 - a23 * a32) - a12 * (a21 * a33 - a23 * a31) + a13 * (a21 * a32 - a22 * a31);
}
