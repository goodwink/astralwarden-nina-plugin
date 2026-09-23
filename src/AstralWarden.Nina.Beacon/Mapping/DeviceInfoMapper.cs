using AstralWarden.Nina.Beacon.Contracts;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Equipment.MyFilterWheel;
using NINA.Equipment.Equipment.MyFlatDevice;
using NINA.Equipment.Equipment.MyFocuser;
using NINA.Equipment.Equipment.MyGuider;
using NINA.Equipment.Equipment.MyRotator;
using NINA.Equipment.Equipment.MySafetyMonitor;
using NINA.Equipment.Equipment.MySwitch;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.Equipment.MyWeatherData;

namespace AstralWarden.Nina.Beacon.Mapping;

/// <summary>Pure NINA-Info → wire-state mapping (the unit-tested layer). NaN means "driver has no
/// value" and maps to null so the JSON omits it.</summary>
public static class DeviceInfoMapper
{
    public static FocuserState Focuser(FocuserInfo i) =>
        new(i.Position, D(i.Temperature), i.IsMoving, i.TempComp);

    public static CameraState Camera(CameraInfo i, double? focalLengthMm = null) =>
        new(D(i.Temperature), D(i.TemperatureSetPoint), i.CoolerOn, D(i.CoolerPower),
            i.DewHeaterOn, i.IsExposing, i.CameraState.ToString(), i.Gain, i.Offset,
            i.HasBattery ? i.Battery : null,
            PixelScale(D(i.PixelSize), focalLengthMm, i.BinX),
            // Only meaningful while exposing: NINA leaves the last exposure's end time in place after
            // it finishes, so reporting it unconditionally would show a deadline that has passed on an
            // idle camera and read as permanently overdue.
            i.IsExposing ? ExposureEnd(i.ExposureEndTime) : null,
            i.LastDownloadTime > 0 ? i.LastDownloadTime : null);

    /// <summary>The exposure deadline as a UTC wire timestamp. NINA holds it as a local
    /// <see cref="DateTime"/> with <c>Kind.Unspecified</c>, so it is treated as local and converted —
    /// shipping it unconverted would be wrong by the rig's UTC offset, which is exactly the size of
    /// error that reads as "hung" on a rig several time zones from UTC.</summary>
    private static string? ExposureEnd(DateTime end) =>
        end == default ? null : BeaconJson.Timestamp(DateTime.SpecifyKind(end, DateTimeKind.Local).ToUniversalTime());

    /// <summary>Image scale in arcsec/pixel: 206.265 × pixel size (µm) × binning ÷ focal length (mm).
    /// Binning is included because a bin-2 frame really does cover twice the sky per pixel, and a
    /// consumer comparing pixels to arcsec would otherwise be wrong by that factor on every binned
    /// frame. Null unless both inputs are present and positive — a fabricated scale would silently
    /// corrupt every sky-unit conversion downstream.</summary>
    public static double? PixelScale(double? pixelSizeUm, double? focalLengthMm, int binning)
    {
        if (pixelSizeUm is not { } px || focalLengthMm is not { } fl) return null;
        if (!(px > 0) || !(fl > 0) || binning < 1) return null;
        return Math.Round(206.265 * px * binning / fl, 4);
    }

    public static RotatorState Rotator(RotatorInfo i) =>
        new(i.MechanicalPosition, i.Position, i.IsMoving, i.Synced);

    public static SafetyState Safety(SafetyMonitorInfo i) => new(i.IsSafe);

    public static FlatState Flat(FlatDeviceInfo i) =>
        new(i.CoverState.ToString(), i.LightOn, i.Brightness);

    public static SwitchState Switches(SwitchInfo i)
    {
        var gauges = new List<SwitchGauge>();
        if (i.ReadonlySwitches is not null)
            foreach (var s in i.ReadonlySwitches) gauges.Add(new SwitchGauge(s.Id, s.Name, s.Value));
        if (i.WritableSwitches is not null)
            foreach (var s in i.WritableSwitches) gauges.Add(new SwitchGauge(s.Id, s.Name, s.Value));
        return new SwitchState(gauges);
    }

    public static WeatherState Weather(WeatherDataInfo i) =>
        new(D(i.Temperature), D(i.Humidity), D(i.Pressure), D(i.DewPoint), D(i.CloudCover),
            D(i.WindSpeed), D(i.WindGust), D(i.WindDirection), D(i.SkyQuality),
            D(i.SkyTemperature), D(i.SkyBrightness), D(i.StarFWHM), D(i.RainRate));

    public static MountState Mount(TelescopeInfo i) =>
        new(D(i.RightAscension), D(i.Declination), D(i.Altitude), D(i.Azimuth),
            i.SideOfPier.ToString(), i.TrackingEnabled, i.Slewing, i.AtPark, i.AtHome,
            i.IsPulseGuiding, D(i.TimeToMeridianFlip), D(i.SiderealTime));

    public static FilterWheelState FilterWheel(FilterWheelInfo i) =>
        new(i.SelectedFilter?.Name, i.SelectedFilter?.Position, i.IsMoving);

    public static GuiderState Guider(GuiderInfo i) =>
        new(D(i.PixelScale), i.RMSError is null
            ? null
            : new GuiderRms(D(i.RMSError.RA?.Arcseconds ?? double.NaN),
                D(i.RMSError.Dec?.Arcseconds ?? double.NaN),
                D(i.RMSError.Total?.Arcseconds ?? double.NaN),
                D(i.RMSError.PeakRA?.Arcseconds ?? double.NaN),
                D(i.RMSError.PeakDec?.Arcseconds ?? double.NaN)));

    private static double? D(double v) => double.IsNaN(v) || double.IsInfinity(v) ? null : v;
}
