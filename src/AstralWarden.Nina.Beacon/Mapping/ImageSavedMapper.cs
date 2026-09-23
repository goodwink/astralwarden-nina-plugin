using AstralWarden.Nina.Beacon.Contracts;
using NINA.WPF.Base.Interfaces.Mediator;

namespace AstralWarden.Nina.Beacon.Mapping;

/// <summary>
/// Pure ImageSaved → wire mapping. Lives here rather than inline in the watcher because this is
/// where a frame's metadata becomes the record the cloud reasons about — the fields that decide
/// "is the data good" — and NINA hands it to us with two kinds of absence that must not reach the
/// wire as if they were measurements:
///
///  • <c>double.NaN</c> for an unread analog value (temperature, airmass, exposure time), and
///  • <c>-1</c> for an unknown integer (gain, offset, exposure number) — a sentinel, not a value.
///
/// Both become null. A NaN would serialize as invalid JSON or a null-ish number depending on the
/// serializer; a -1 gain is worse, because it is a perfectly plausible number that would quietly
/// pollute per-gain grouping in the cloud. Empty strings (NINA's default for an unset image type
/// or target name) are absence too, and become null for the same reason.
/// </summary>
public static class ImageSavedMapper
{
    /// <summary>Aggregate star-detection extras, as read off the analysis object by
    /// <see cref="Watchers.HocusFocusProbe.AnalysisExtras"/>.</summary>
    public readonly record struct AnalysisExtras(double? Fwhm, double? FwhmMad, double? Ecc, double? EccMad);

    /// <param name="thumbnail">Base64 JPEG of the display image, or null — the watcher encodes it
    /// (WPF, off <c>e.Image</c>) for light frames only; the mapper stays pure and just carries it.</param>
    public static ImageSavedPayload Map(ImageSavedEventArgs e, string? detector, AnalysisExtras extras,
        string? thumbnail = null)
    {
        var meta = e.MetaData;
        var stats = e.Statistics;
        var analysis = e.StarDetectionAnalysis;
        var rms = meta?.Image?.RecordedRMS;

        return new ImageSavedPayload(
            Path: e.PathToImage?.LocalPath,
            ImageType: Text(meta?.Image?.ImageType),
            Filter: Text(e.Filter),
            ExposureSec: D(meta?.Image?.ExposureTime),
            ExposureStart: meta?.Image?.ExposureStart is { } start && start != default
                ? BeaconJson.Timestamp(start.ToUniversalTime())
                : null,
            ExposureNumber: Counted(meta?.Image?.ExposureNumber),
            Target: Text(meta?.Target?.Name),
            TargetRa: D(meta?.Target?.Coordinates?.RA),
            TargetDec: D(meta?.Target?.Coordinates?.Dec),
            // The PLANNED position angle from the target definition — what the sequence asked for,
            // which is 0 on a target set up without one. NOT the camera's actual orientation: a rig
            // can sit at rotator 109 deg while every target says 0. The achieved angle is below.
            TargetRotation: D(meta?.Target?.PositionAngle),
            RotatorMechanical: D(meta?.Rotator?.MechanicalPosition),
            RotatorSky: D(meta?.Rotator?.Position),
            WcsRotation: meta?.WorldCoordinateSystem is { } wcs ? D(wcs.Rotation) : null,
            WcsFlipped: meta?.WorldCoordinateSystem?.Flipped,
            // The MOUNT's reported position, not the solved one. A mount reports back the
            // coordinates it was commanded to, so this agrees with Target to ~1 arcsecond on every
            // frame whether or not the telescope is physically there. Differencing the two measures
            // nothing, and it cannot see a miss the mount is unaware of; the solved centre above is
            // what carries truth. Measured on the real rig the two sit 0.07-0.40 arcmin apart on
            // targeted lights.
            Ra: D(meta?.Telescope?.Coordinates?.RA),
            Dec: D(meta?.Telescope?.Coordinates?.Dec),
            Airmass: D(meta?.Telescope?.Airmass),
            PierSide: PierSide(meta?.Telescope?.SideOfPier),
            CameraTemp: D(meta?.Camera?.Temperature),
            CameraSetPoint: D(meta?.Camera?.SetPoint),
            Gain: Counted(meta?.Camera?.Gain),
            Offset: Counted(meta?.Camera?.Offset),
            FocuserPosition: meta?.Focuser?.Position,
            FocuserTemp: D(meta?.Focuser?.Temperature),
            Stats: stats is null
                ? null
                : new ImageStats(Math.Round(stats.Mean, 1), stats.Median, stats.Min, stats.Max,
                    Math.Round(stats.StDev, 1), stats.MedianAbsoluteDeviation),
            Quality: analysis is null
                ? null
                : new ImageQuality(
                    D(analysis.HFR), D(analysis.HFRStDev), analysis.DetectedStars,
                    extras.Fwhm, extras.FwhmMad, extras.Ecc, extras.EccMad),
            // DataPoints == 0 means guiding wasn't running; an all-zero RMS is not a measurement.
            Guiding: rms is null || rms.DataPoints == 0
                ? null
                : new ExposureGuiding(D(rms.RA), D(rms.Dec), D(rms.Total), D(rms.Scale), rms.DataPoints),
            Detector: detector,
            Thumbnail: thumbnail);
    }

    /// <summary>
    /// Whether a frame of this type should produce a per-star payload. Calibration frames (dark,
    /// bias, flat) have no stars worth analysing, and an unset type is treated as a light: NINA
    /// leaves ImageType empty in some capture paths, and dropping real light frames' star data is
    /// the worse error of the two.
    /// </summary>
    public static bool EmitsStars(string? imageType) =>
        string.IsNullOrWhiteSpace(imageType)
        || imageType.Equals("LIGHT", StringComparison.OrdinalIgnoreCase)
        || imageType.Equals("SNAPSHOT", StringComparison.OrdinalIgnoreCase);

    private static double? D(double? v) =>
        v is null || double.IsNaN(v.Value) || double.IsInfinity(v.Value) ? null : v;

    /// <summary>NINA initialises unknown counts to -1; no real gain/offset/frame number is negative.</summary>
    private static int? Counted(int? v) => v is null or < 0 ? null : v;

    private static string? Text(string? v) => string.IsNullOrWhiteSpace(v) ? null : v;

    /// <summary>An unknown pier side must not ship as the literal "pierUnknown" — meridian-flip
    /// analysis would read it as a side the mount was actually on.</summary>
    private static string? PierSide(NINA.Core.Enum.PierSide? side) =>
        side is null || side == NINA.Core.Enum.PierSide.pierUnknown ? null : side.ToString();
}
