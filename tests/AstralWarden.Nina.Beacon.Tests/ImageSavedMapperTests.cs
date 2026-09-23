using System.Collections.Immutable;
using AstralWarden.Nina.Beacon.Contracts;
using AstralWarden.Nina.Beacon.Mapping;
using NINA.Astrometry;
using NINA.Core.Enum;
using NINA.Image.ImageData;
using NINA.Image.ImageAnalysis;
using NINA.Image.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;
using Xunit;

namespace AstralWarden.Nina.Beacon.Tests;

/// <summary>
/// The ImageSaved payload is the "is the data good" record, and it is built from a metadata
/// graph whose *defaults* are booby-trapped — NaN for unread analog values, -1 for unknown counts,
/// empty strings for unset text. These tests use real NINA metadata objects (they are POCOs with
/// public setters), so a default here is genuinely NINA's default, not our guess at one.
/// </summary>
public class ImageSavedMapperTests
{
    private sealed class FakeStats : IImageStatistics
    {
        public int BitDepth => 16;
        public double StDev { get; init; }
        public double Mean { get; init; }
        public double Median { get; init; }
        public double MedianAbsoluteDeviation { get; init; }
        public int Max { get; init; }
        public long MaxOccurrences => 0;
        public int Min { get; init; }
        public long MinOccurrences => 0;
        public ImmutableList<OxyPlot.DataPoint> Histogram => ImmutableList<OxyPlot.DataPoint>.Empty;
    }

    private sealed class FakeAnalysis : IStarDetectionAnalysis
    {
        public double HFR { get; set; }
        public double HFRStDev { get; set; }
        public int DetectedStars { get; set; }
        public System.Collections.Generic.List<DetectedStar> StarList { get; set; } = new();
#pragma warning disable CS0067 // required by the interface; nothing here raises it
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
#pragma warning restore CS0067
    }

    /// <summary>A metadata graph exactly as NINA hands it over with nothing populated.</summary>
    private static ImageSavedEventArgs Untouched() => new() { MetaData = new ImageMetaData() };

    // ---- absence must never look like a measurement ----

    [Fact]
    public void An_untouched_metadata_graph_maps_to_nulls_not_sentinels()
    {
        var p = ImageSavedMapper.Map(Untouched(), detector: null, extras: default);

        // NaN defaults
        Assert.Null(p.ExposureSec);
        Assert.Null(p.CameraTemp);
        Assert.Null(p.CameraSetPoint);
        Assert.Null(p.Airmass);
        Assert.Null(p.FocuserTemp);
        // -1 "unknown count" defaults — a -1 gain would silently pollute per-gain grouping
        Assert.Null(p.Gain);
        Assert.Null(p.Offset);
        Assert.Null(p.ExposureNumber);
        // empty-string defaults
        Assert.Null(p.ImageType);
        Assert.Null(p.Target);
        // absent objects
        Assert.Null(p.Ra);
        Assert.Null(p.Dec);
        Assert.Null(p.TargetRa);
        Assert.Null(p.ExposureStart);
        Assert.Null(p.Guiding);
        Assert.Null(p.Stats);
        Assert.Null(p.Quality);
        Assert.Null(p.PierSide);
    }

    [Fact]
    public void A_null_metadata_graph_does_not_throw()
    {
        var p = ImageSavedMapper.Map(new ImageSavedEventArgs(), detector: null, extras: default);
        Assert.Null(p.ExposureSec);
        Assert.Null(p.ImageType);
        Assert.Null(p.Path);
    }

    [Fact]
    public void Infinity_is_treated_as_absence_too()
    {
        var e = Untouched();
        e.MetaData.Telescope.Airmass = double.PositiveInfinity;
        e.MetaData.Camera.Temperature = double.NegativeInfinity;

        var p = ImageSavedMapper.Map(e, null, default);
        Assert.Null(p.Airmass);
        Assert.Null(p.CameraTemp);
    }

    [Fact]
    public void Zero_is_a_real_value_and_survives()
    {
        var e = Untouched();
        e.MetaData.Camera.Temperature = 0;
        e.MetaData.Camera.Gain = 0;
        e.MetaData.Camera.Offset = 0;

        var p = ImageSavedMapper.Map(e, null, default);
        Assert.Equal(0, p.CameraTemp);   // 0 °C is a temperature, not a missing reading
        Assert.Equal(0, p.Gain);         // gain 0 is a legitimate setting
        Assert.Equal(0, p.Offset);
    }

    // ---- populated frame ----

    [Fact]
    public void A_populated_light_frame_maps_every_field()
    {
        var e = new ImageSavedEventArgs
        {
            PathToImage = new Uri(@"C:\images\M42\LIGHT_001.fits"),
            Filter = "Ha",
            Statistics = new FakeStats { Mean = 1234.567, Median = 1200, Min = 5, Max = 65535, StDev = 89.44, MedianAbsoluteDeviation = 12 },
            StarDetectionAnalysis = new FakeAnalysis { HFR = 2.345, HFRStDev = 0.123, DetectedStars = 812 },
            MetaData = new ImageMetaData
            {
                Image = { ImageType = "LIGHT", ExposureTime = 300, ExposureNumber = 7,
                          ExposureStart = new DateTime(2026, 7, 22, 3, 14, 0, DateTimeKind.Utc) },
                Camera = { Temperature = -9.8, SetPoint = -10, Gain = 100, Offset = 30 },
                Telescope = { Airmass = 1.23, SideOfPier = PierSide.pierEast,
                              Coordinates = new NINA.Astrometry.Coordinates(83.8, -5.4, NINA.Astrometry.Epoch.J2000, NINA.Astrometry.Coordinates.RAType.Degrees) },
                Focuser = { Position = 15432, Temperature = 4.5 },
                Target = { Name = "M42", PositionAngle = 137.5,
                           Coordinates = new NINA.Astrometry.Coordinates(83.82, -5.39, NINA.Astrometry.Epoch.J2000, NINA.Astrometry.Coordinates.RAType.Degrees) },
            },
        };

        var p = ImageSavedMapper.Map(e, "hocusfocus", new ImageSavedMapper.AnalysisExtras(2.5, 0.2, 0.35, 0.05));

        Assert.EndsWith("LIGHT_001.fits", p.Path);
        Assert.Equal("LIGHT", p.ImageType);
        Assert.Equal("Ha", p.Filter);
        Assert.Equal(300, p.ExposureSec);
        Assert.Equal("2026-07-22T03:14:00.000Z", p.ExposureStart);
        Assert.Equal(7, p.ExposureNumber);
        Assert.Equal("M42", p.Target);
        Assert.Equal(137.5, p.TargetRotation);
        Assert.Equal(1.23, p.Airmass);
        Assert.Equal("pierEast", p.PierSide);
        Assert.Equal(-9.8, p.CameraTemp);
        Assert.Equal(-10, p.CameraSetPoint);
        Assert.Equal(100, p.Gain);
        Assert.Equal(30, p.Offset);
        Assert.Equal(15432, p.FocuserPosition);
        Assert.Equal(4.5, p.FocuserTemp);
        Assert.Equal("hocusfocus", p.Detector);

        Assert.Equal(1234.6, p.Stats!.Mean);   // rounded to 1dp on the wire
        Assert.Equal(65535, p.Stats.Max);
        Assert.Equal(2.345, p.Quality!.Hfr);
        Assert.Equal(812, p.Quality.DetectedStars);
        Assert.Equal(2.5, p.Quality.Fwhm);
        Assert.Equal(0.35, p.Quality.Eccentricity);
    }

    [Fact]
    public void Exposure_start_is_normalised_to_utc()
    {
        var e = Untouched();
        var local = new DateTime(2026, 7, 22, 3, 14, 0, DateTimeKind.Local);
        e.MetaData.Image.ExposureStart = local;

        var p = ImageSavedMapper.Map(e, null, default);
        Assert.Equal(BeaconJson.Timestamp(local.ToUniversalTime()), p.ExposureStart);
        Assert.EndsWith("Z", p.ExposureStart);
    }

    // ---- guiding ----

    [Fact]
    public void Guiding_is_absent_when_no_data_points_were_recorded()
    {
        var e = Untouched();
        e.MetaData.Image.RecordedRMS = new NINA.Core.Model.RMS(); // guiding off: zero data points
        Assert.Null(ImageSavedMapper.Map(e, null, default).Guiding);
    }

    [Fact]
    public void Guiding_is_mapped_when_the_exposure_was_guided()
    {
        var rms = new NINA.Core.Model.RMS();
        rms.SetScale(1.6);
        rms.AddDataPoint(0.4, -0.3);
        rms.AddDataPoint(-0.2, 0.5);

        var e = Untouched();
        e.MetaData.Image.RecordedRMS = rms;

        var guiding = ImageSavedMapper.Map(e, null, default).Guiding;
        Assert.NotNull(guiding);
        Assert.Equal(2, guiding.DataPoints);
        Assert.Equal(1.6, guiding.Scale);
        Assert.NotNull(guiding.Total);
    }

    // ---- calibration-frame gating ----

    [Theory]
    [InlineData("LIGHT", true)]
    [InlineData("light", true)]
    [InlineData("SNAPSHOT", true)]
    [InlineData(null, true)]   // NINA leaves this empty on some capture paths
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("DARK", false)]
    [InlineData("BIAS", false)]
    [InlineData("FLAT", false)]
    [InlineData("DARKFLAT", false)]
    public void Star_payloads_are_gated_on_frame_type(string? imageType, bool emits)
    {
        Assert.Equal(emits, ImageSavedMapper.EmitsStars(imageType));
    }

    // ---- detector / analysis absence ----

    [Fact]
    public void A_frame_with_no_star_detection_has_no_quality_block()
    {
        var p = ImageSavedMapper.Map(Untouched(), detector: null, extras: default);
        Assert.Null(p.Quality);
        Assert.Null(p.Detector);
    }

    [Fact]
    public void Stock_detector_quality_carries_hfr_but_no_psf_extras()
    {
        var e = Untouched();
        e.StarDetectionAnalysis = new FakeAnalysis { HFR = 3.1, HFRStDev = 0.4, DetectedStars = 250 };

        var p = ImageSavedMapper.Map(e, "nina", extras: default);

        Assert.Equal("nina", p.Detector);
        Assert.Equal(3.1, p.Quality!.Hfr);
        Assert.Equal(250, p.Quality.DetectedStars);
        Assert.Null(p.Quality.Fwhm);          // base NINA detection has no PSF fit
        Assert.Null(p.Quality.Eccentricity);
    }

    [Fact]
    public void A_failed_star_detection_reports_no_hfr_rather_than_nan()
    {
        var e = Untouched();
        e.StarDetectionAnalysis = new FakeAnalysis { HFR = double.NaN, HFRStDev = double.NaN, DetectedStars = 0 };

        var p = ImageSavedMapper.Map(e, "nina", default);
        Assert.NotNull(p.Quality);
        Assert.Null(p.Quality.Hfr);
        Assert.Equal(0, p.Quality.DetectedStars);
    }

    [Fact]
    public void The_planned_target_angle_and_the_achieved_rotator_angle_are_different_fields()
    {
        // Observed on the rig 2026-07-25: every target defined at position angle 0 while the rotator
        // actually sat at 109.218 deg. Reading targetRotation as "the camera's orientation" is wrong
        // by 109 degrees on that rig, which is why the achieved angle now ships alongside it.
        var meta = new ImageMetaData();
        meta.Target.PositionAngle = 0;
        meta.Rotator.MechanicalPosition = 109.218;
        meta.Rotator.Position = 109.218;

        var p = ImageSavedMapper.Map(new ImageSavedEventArgs { MetaData = meta }, detector: null, extras: default);

        Assert.Equal(0, p.TargetRotation);
        Assert.Equal(109.218, p.RotatorMechanical!.Value, 3);
        Assert.Equal(109.218, p.RotatorSky!.Value, 3);
    }

    [Fact]
    public void A_solved_frame_carries_the_wcs_parity_that_fixes_image_angle_handedness()
    {
        // NINA derives Flipped from the sign of the CD-matrix determinant (WorldCoordinateSystem),
        // not from any assumption about the optical train — so this is the authoritative answer to
        // "does image angle theta map to PA rot+theta or rot-theta" for a given rig, and it does not
        // depend on counting mirrors.
        var meta = new ImageMetaData { WorldCoordinateSystem = // crval1/2, crpix1/2, then the CD matrix. A positive determinant is the non-mirrored case.
            new WorldCoordinateSystem(10.0, 41.0, 100.0, 100.0, -0.0002, 0.0, 0.0, 0.0002) };
        meta.Rotator.Position = 180.094;

        var p = ImageSavedMapper.Map(new ImageSavedEventArgs { MetaData = meta }, detector: null, extras: default);

        Assert.NotNull(p.WcsRotation);
        Assert.NotNull(p.WcsFlipped);
        Assert.Equal(180.094, p.RotatorSky!.Value, 3);
    }

    [Fact]
    public void An_unsolved_frame_carries_no_wcs_rather_than_a_default()
    {
        // A rig solves at target start, not per sub, so most frames have no WCS at all. Reporting a
        // zero rotation or an unflipped parity there would be a fabricated measurement.
        var p = ImageSavedMapper.Map(Untouched(), detector: null, extras: default);
        Assert.Null(p.WcsRotation);
        Assert.Null(p.WcsFlipped);
    }

}
