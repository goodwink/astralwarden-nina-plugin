namespace AstralWarden.Nina.Beacon.Contracts;

// The wire contract, v1. docs/protocol.md is the authoritative description; the agent keeps its
// own mirrored DTOs (BridgeMessages.cs). Payloads are camelCase on the wire via BeaconJson.

public static class BeaconProtocol
{
    public const int Version = 1;
    public const int DefaultPort = 1999;
}

/// <summary>First message on every new client connection.</summary>
public sealed record HelloPayload(
    int SchemaVersion,
    string BeaconVersion,
    string? NinaVersion,
    string? ProfileName,
    string ServerStartedAt);

/// <summary>Every 5s. Proof of life for NINA, the Beacon, and the socket.</summary>
public sealed record HeartbeatPayload(
    double UptimeSec,
    int Clients,
    long DroppedSinceLastHeartbeat,
    IReadOnlyDictionary<string, bool>? Devices = null,
    bool? SequenceRunning = null,
    string? Instruction = null);

/// <summary>Sent best-effort when NINA shuts the Beacon down.</summary>
public sealed record ByePayload(string Reason);

// ---- device.state / device.connection (phase 4) ----
// One shape for all devices: `device` names the kind, `state` is the kind-specific record below
// (null while disconnected). Only fields the agent actually cares about — surface stays small.

public sealed record DeviceStatePayload(string Device, bool Connected, string? Name, object? State);

public sealed record DeviceConnectionPayload(string Device, bool Connected, string? Name);

public sealed record FocuserState(int Position, double? Temperature, bool IsMoving, bool TempComp);

public sealed record CameraState(
    double? Temperature, double? SetPoint, bool CoolerOn, double? CoolerPower, bool DewHeaterOn,
    bool IsExposing, string? Activity, int Gain, int Offset, double? Battery,
    /// <summary>Image scale in arcsec/pixel at the CURRENT binning, or null when the profile has no
    /// focal length set. The one number needed to read a per-frame pixel measurement in sky units —
    /// without it a doubling offset of "7.6 px" cannot be compared with a guider's arcsec.</summary>
    double? PixelScale = null,
    /// <summary>When the running exposure is due to finish (NINA's <c>CameraInfo.ExposureEndTime</c>),
    /// as a wire timestamp. This is what makes a hung exposure detectable WITHOUT knowing anything
    /// about the rig: a 15-minute sub and an hour-long sub each declare their own deadline, so no
    /// threshold has to be guessed and no history has to be learned. Null when not exposing.</summary>
    string? ExposureEndTime = null,
    /// <summary>Seconds the last frame took to download (<c>CameraInfo.LastDownloadTime</c>), or null
    /// before the first one. The deadline above is the end of INTEGRATION; a big sensor on USB2 can
    /// spend a long time after that still working, and treating download as a hang would fire on
    /// every sub. This is what sizes the grace period from the camera's own measured behaviour.</summary>
    double? LastDownloadSec = null);

public sealed record RotatorState(double MechanicalPosition, double SkyPosition, bool IsMoving, bool Synced);

public sealed record SafetyState(bool IsSafe);

public sealed record FlatState(string? CoverState, bool LightOn, int Brightness);

public sealed record SwitchGauge(short Id, string? Name, double Value);

public sealed record SwitchState(IReadOnlyList<SwitchGauge> Gauges);

public sealed record WeatherState(
    double? Temperature, double? Humidity, double? Pressure, double? DewPoint, double? CloudCover,
    double? WindSpeed, double? WindGust, double? WindDirection, double? SkyQuality,
    double? SkyTemperature, double? SkyBrightness, double? StarFwhm, double? RainRate);

public sealed record MountState(
    double? Ra, double? Dec, double? Altitude, double? Azimuth, string? SideOfPier,
    bool TrackingEnabled, bool Slewing, bool AtPark, bool AtHome, bool IsPulseGuiding,
    double? HoursToMeridianFlip, double? SiderealTime);

public sealed record FilterWheelState(string? Filter, int? Position, bool IsMoving);

public sealed record GuiderRms(double? Ra, double? Dec, double? Total, double? PeakRa, double? PeakDec);

public sealed record GuiderState(double? PixelScale, GuiderRms? Rms);

// ---- image.saved / image.stars (phase 5) ----

/// <summary>One saved exposure: path, capture context, statistics, aggregate quality, and the
/// guiding RMS recorded during the exposure. Pointing (ra/dec) comes from image metadata — data
/// the agent has never had per frame.</summary>
public sealed record ImageSavedPayload(
    string? Path,
    string? ImageType,
    string? Filter,
    double? ExposureSec,
    string? ExposureStart,
    int? ExposureNumber,
    string? Target,
    double? TargetRa,
    double? TargetDec,
    /// <summary>The target's PLANNED position angle, from the target definition — 0 on a target set
    /// up without one, regardless of where the camera actually sits.</summary>
    double? TargetRotation,
    /// <summary>The rotator's ACHIEVED angle at capture. Mechanical is the raw encoder position; sky
    /// is NINA's sky-referenced angle, which only differs once the rotator has been synced by a plate
    /// solve — until then it echoes mechanical, and is not a true position angle.</summary>
    double? RotatorMechanical,
    double? RotatorSky,
    /// <summary>From the image's plate-solve WCS, when NINA attached one. `wcsRotation` is the solved
    /// image orientation and `wcsFlipped` is the PARITY — whether the sky-to-pixel mapping is mirrored,
    /// which NINA derives from the sign of the CD-matrix determinant rather than from any assumption
    /// about the optical train. Present only on frames that were actually solved (a rig solves at
    /// target start, not per sub), but parity is a property of the optical configuration, so one
    /// solve establishes it for the session.</summary>
    double? WcsRotation,
    bool? WcsFlipped,
    double? Ra,
    double? Dec,
    double? Airmass,
    string? PierSide,
    double? CameraTemp,
    double? CameraSetPoint,
    int? Gain,
    int? Offset,
    int? FocuserPosition,
    double? FocuserTemp,
    ImageStats? Stats,
    ImageQuality? Quality,
    ExposureGuiding? Guiding,
    string? Detector,
    // Base64 JPEG of the display image, downscaled in-process (light frames only). Large but only on
    // light frames (minutes apart); the agent re-encodes it to its own budget and uploads. Omitted
    // (null) for calibration frames and when the image can't be encoded.
    string? Thumbnail = null);

public sealed record ImageStats(double Mean, double Median, int Min, int Max, double StDev, double Mad);

/// <summary>Aggregates from star detection. fwhm/eccentricity only when HocusFocus is the active
/// detector (read via reflection off its analysis subclass).</summary>
public sealed record ImageQuality(
    double? Hfr, double? HfrStDev, int? DetectedStars,
    double? Fwhm, double? FwhmMad, double? Eccentricity, double? EccentricityMad);

/// <summary>Guiding RMS recorded across this exposure (raw pixels; multiply by scale for arcsec).</summary>
public sealed record ExposureGuiding(double? Ra, double? Dec, double? Total, double? Scale, int? DataPoints);

/// <summary>Per-star detail for a light frame — THE differentiator. Stars are capped (top by
/// brightness); the 8×8 grid summary always covers every detected star and is what tilt/
/// curvature/aberration analysis consumes.</summary>
public sealed record ImageStarsPayload(
    string? Path,
    string? Detector,
    int Width,
    int Height,
    int StarCount,
    IReadOnlyList<StarPoint> Stars,
    IReadOnlyList<IReadOnlyList<GridCell?>> Grid);

/// <summary>theta = PSF elongation angle in radians (HocusFocus PSF fit). Direction is what
/// splits "eccentricity up" into tracking/flexure (same angle across the frame, guiding clean),
/// optics (radial/edge pattern), or seeing (random).</summary>
public sealed record StarPoint(double X, double Y, double Hfr, double? Fwhm, double? Ecc, double? Theta, double Brightness);

public sealed record GridCell(int Count, double? MedianHfr, double? MedianFwhm, double? MedianEcc);

// ---- autofocus (phase 6) ----
// The Beacon assembles the curve from the live per-point broadcasts (no report-file reads) and
// fits it itself, so the agent gets curve + fit quality even when nothing writes AF JSONs.

public sealed record AfPointPayload(double Position, double Hfr);

public sealed record AfMeasurePoint(double Position, double Hfr);

public sealed record AfFit(string Method, double MinimumPosition, double MinimumHfr, double RSquared);

public sealed record AfCompletePayload(
    bool Success,
    string? Filter,
    double? FinalPosition,
    double? Temperature,
    double DurationSec,
    IReadOnlyList<AfMeasurePoint> Points,
    AfFit? Fit);

// ---- sequence / mount / alerts (phase 7) ----

/// <summary>One entry in the advanced sequencer's currently-running set (outermost container →
/// innermost instruction, as `path`).
///
/// <c>Type</c> is the CLR type name (<c>item.GetType().Name</c>) and is what anything downstream must
/// match on. <c>Name</c> is the sequencer's DISPLAY name: it is user-editable and localized, so it
/// cannot identify an instruction. Both appear in real captures for the same concept — "TakeExposure"
/// and "Take Exposure", "Slew To Ra/Dec" — which is precisely the ambiguity <c>Type</c> removes.</summary>
public sealed record SequenceItemInfo(string Name, string Type, string Path, string Status, int Attempts);

public sealed record SequenceStatePayload(
    bool Running,
    IReadOnlyList<SequenceItemInfo> Items,
    string? Target,
    double? TargetRa,
    double? TargetDec);

/// <summary>Discrete mount lifecycle event: slewed, parked, unparked, homed, flip-before, flip-after.</summary>
public sealed record MountEventPayload(
    string Event,
    double? FromRa = null, double? FromDec = null,
    double? ToRa = null, double? ToDec = null);

/// <summary>A user-authored "Send Astral Warden alert" instruction fired inside the sequence.</summary>
public sealed record AlertPayload(string Title, string Severity, string? Message);

// ---- Target Scheduler broker messages (phase 8) ----
// In-process IMessageBroker subscription to Target Scheduler's five topics (verified against TS
// 5.10.3 source: PubSub/*Publisher.cs) — replaces the flaky ts/v0 HTTP API for per-target context.
// Full project/exposure-plan progress stats remain with the agent's ts-api plugin for now.

public sealed record TsWaitStartPayload(
    string? Project, string? Target, double? Ra, double? Dec, double? Rotation,
    int? SecondsUntilNextTarget);

public sealed record TsTargetStartPayload(
    bool NewTarget, string? Project, string? Target, double? Ra, double? Dec, double? Rotation,
    string? Filter, double? ExposureSec, string? Gain, string? Offset, string? Binning);

public sealed record TsTargetCompletePayload(
    string? Project, string? Target, double? Ra, double? Dec, double? Rotation);

public sealed record TsContainerStoppedPayload(string? StoppedAt);
