using System.Reflection;
using AstralWarden.Nina.Beacon.Mapping;
using NINA.Image.ImageAnalysis;
using NINA.Image.Interfaces;

namespace AstralWarden.Nina.Beacon.Watchers;

/// <summary>
/// Best-effort access to the richer per-star fields HocusFocus computes. Reflection-only so the
/// Beacon never references the HocusFocus assembly; degrades to nulls when HF isn't the detector.
///
/// Verified against HF 4.0.0.10: HocusFocusDetectedStar carries its PSF fit in a nested
/// <c>PSF</c> object (PSFModel: FWHMArcsecs, Eccentricity, RSquared, ...) — there are NO flat
/// per-star FWHM/Eccentricity values (the flat properties that exist on newer base DetectedStar
/// types stay 0). So: prefer PSF.* (fwhm in ARCSEC, matching HF's aggregate analysis FWHM), fall
/// back to flat properties only when nonzero, else null. PropertyInfo lookups cached per type.
/// </summary>
public static class HocusFocusProbe
{
    private sealed record StarProps(PropertyInfo? Psf, PropertyInfo? PsfFwhmArcsecs, PropertyInfo? PsfEccentricity,
        PropertyInfo? PsfTheta, PropertyInfo? FlatFwhm, PropertyInfo? FlatEcc);

    // Concurrent, not lock+Dictionary: the accessor resolves the PSF object's type once per star,
    // and this runs on NINA's image-save thread — a process-wide lock taken thousands of times per
    // frame is exactly the kind of contention that must not exist there.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, StarProps> Cache = new();

    /// <summary>Detector id for the wire: "hocusfocus" when the analysis came from HocusFocus.</summary>
    public static string DetectorId(IStarDetectionAnalysis analysis) =>
        analysis.GetType().Name.Contains("HocusFocus", StringComparison.OrdinalIgnoreCase) ? "hocusfocus" : "nina";

    /// <summary>
    /// Per-star accessor for the mapper, or null when the star type has no extras. The returned
    /// delegate carries a small per-frame cache, so it is NOT thread-safe — it is built fresh for
    /// each frame and used only within that frame's mapping call.
    /// </summary>
    public static StarListMapper.StarExtras? StarAccessor(IReadOnlyList<DetectedStar> stars)
    {
        if (stars.Count == 0) return null;
        var props = PropsFor(stars[0].GetType());
        if (props.Psf is null && props.FlatFwhm is null && props.FlatEcc is null) return null;

        // Every star in a frame carries the same PSF type, and this accessor runs once per star on
        // a frame that can hold tens of thousands of them — so resolve the PSF properties once and
        // reuse, rather than paying a GetType + dictionary lookup per star. (Kept as a check rather
        // than an assumption: a mixed list still resolves correctly, just without the shortcut.)
        Type? psfType = null;
        StarProps? psfProps = null;

        return star =>
        {
            try
            {
                // A per-star PSF fit can be absent (failed fit) even when HF is active.
                if (props.Psf?.GetValue(star) is { } psf)
                {
                    var type = psf.GetType();
                    if (!ReferenceEquals(type, psfType))
                    {
                        psfType = type;
                        psfProps = PropsFor(type);
                    }
                    var p = psfProps!;
                    return (ReadDouble(p.PsfFwhmArcsecs ?? p.FlatFwhm, psf),
                        ReadDouble(p.PsfEccentricity ?? p.FlatEcc, psf),
                        ReadDouble(p.PsfTheta, psf));
                }
                // Flat fields: 0 means "never populated" (no real star has fwhm or ecc of exactly 0).
                return (NonZero(ReadDouble(props.FlatFwhm, star)), NonZero(ReadDouble(props.FlatEcc, star)), null);
            }
            catch
            {
                return (null, null, null);
            }
        };
    }

    /// <summary>Aggregate extras off the analysis object (FWHM/Eccentricity + MADs when present).</summary>
    public static (double? Fwhm, double? FwhmMad, double? Ecc, double? EccMad) AnalysisExtras(IStarDetectionAnalysis analysis)
    {
        var type = analysis.GetType();
        return (NonZero(ReadDouble(Prop(type, "FWHM"), analysis)),
            ReadDouble(Prop(type, "FWHMMAD"), analysis),
            NonZero(ReadDouble(Prop(type, "Eccentricity"), analysis)),
            ReadDouble(Prop(type, "EccentricityMAD"), analysis));
    }

    private static StarProps PropsFor(Type type) =>
        Cache.GetOrAdd(type, t => new StarProps(
            PropAny(t, "PSF"),
            Prop(t, "FWHMArcsecs"),
            Prop(t, "Eccentricity"),
            Prop(t, "ThetaRadians"),
            Prop(t, "FWHM"),
            Prop(t, "Eccentricity")));

    private static PropertyInfo? Prop(Type type, string name)
    {
        try
        {
            var p = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            return p?.PropertyType == typeof(double) ? p : null;
        }
        catch
        {
            return null;
        }
    }

    private static PropertyInfo? PropAny(Type type, string name)
    {
        try { return type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance); }
        catch { return null; }
    }

    private static double? ReadDouble(PropertyInfo? prop, object target)
    {
        if (prop is null) return null;
        try
        {
            var v = (double)prop.GetValue(target)!;
            return double.IsNaN(v) || double.IsInfinity(v) ? null : v;
        }
        catch
        {
            return null;
        }
    }

    private static double? NonZero(double? v) => v is 0 ? null : v;
}
