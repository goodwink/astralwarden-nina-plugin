using AstralWarden.Nina.Beacon.Mapping;
using AstralWarden.Nina.Beacon.Server;
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
using NINA.Equipment.Interfaces.Mediator;
using OxyPlot;

namespace AstralWarden.Nina.Beacon.Watchers;

// One small consumer per device, mirroring the per-device Watcher pattern the Advanced API uses.
// Each registers with its mediator; NINA then pushes UpdateDeviceInfo — no polling anywhere.

public sealed class CameraWatcher : DeviceWatcherBase<CameraInfo>, ICameraConsumer
{
    private readonly ICameraMediator _mediator;
    // Focal length lives in the PROFILE, not on the camera, so the image scale can only be assembled
    // where both are in reach. Read per update rather than cached: swapping to a reducer or a
    // different OTA changes the profile mid-session and the scale with it.
    private readonly Func<double?> _focalLengthMm;

    public CameraWatcher(ICameraMediator mediator, BeaconServer server, Func<double?>? focalLengthMm = null)
        : base(server, "camera")
    {
        _mediator = mediator;
        _focalLengthMm = focalLengthMm ?? (() => null);
        _mediator.RegisterConsumer(this);
    }
    public void UpdateDeviceInfo(CameraInfo deviceInfo) => Update(deviceInfo);
    protected override object? Map(CameraInfo info) => DeviceInfoMapper.Camera(info, _focalLengthMm());
    public override void Dispose() { _mediator.RemoveConsumer(this); base.Dispose(); }
}

public sealed class FocuserWatcher : DeviceWatcherBase<FocuserInfo>, IFocuserConsumer
{
    private readonly IFocuserMediator _mediator;
    public FocuserWatcher(IFocuserMediator mediator, BeaconServer server) : base(server, "focuser")
    {
        _mediator = mediator;
        _mediator.RegisterConsumer(this);
    }
    public void UpdateDeviceInfo(FocuserInfo deviceInfo) => Update(deviceInfo);
    protected override object? Map(FocuserInfo info) => DeviceInfoMapper.Focuser(info);

    // Autofocus hooks arrive on this same consumer; the AF watcher (phase 6) plugs in here.
    public void AutoFocusRunStarting() { }
    public void NewAutoFocusPoint(DataPoint dataPoint) { }
    public void UpdateEndAutoFocusRun(AutoFocusInfo info) { }
    public void UpdateUserFocused(FocuserInfo info) { }

    public override void Dispose() { _mediator.RemoveConsumer(this); base.Dispose(); }
}

public sealed class RotatorWatcher : DeviceWatcherBase<RotatorInfo>, IRotatorConsumer
{
    private readonly IRotatorMediator _mediator;
    public RotatorWatcher(IRotatorMediator mediator, BeaconServer server) : base(server, "rotator")
    {
        _mediator = mediator;
        _mediator.RegisterConsumer(this);
    }
    public void UpdateDeviceInfo(RotatorInfo deviceInfo) => Update(deviceInfo);
    protected override object? Map(RotatorInfo info) => DeviceInfoMapper.Rotator(info);
    public override void Dispose() { _mediator.RemoveConsumer(this); base.Dispose(); }
}

public sealed class SafetyWatcher : DeviceWatcherBase<SafetyMonitorInfo>, ISafetyMonitorConsumer
{
    private readonly ISafetyMonitorMediator _mediator;
    public SafetyWatcher(ISafetyMonitorMediator mediator, BeaconServer server) : base(server, "safety")
    {
        _mediator = mediator;
        _mediator.RegisterConsumer(this);
    }
    public void UpdateDeviceInfo(SafetyMonitorInfo deviceInfo) => Update(deviceInfo);
    protected override object? Map(SafetyMonitorInfo info) => DeviceInfoMapper.Safety(info);
    public override void Dispose() { _mediator.RemoveConsumer(this); base.Dispose(); }
}

public sealed class FlatWatcher : DeviceWatcherBase<FlatDeviceInfo>, IFlatDeviceConsumer
{
    private readonly IFlatDeviceMediator _mediator;
    public FlatWatcher(IFlatDeviceMediator mediator, BeaconServer server) : base(server, "flat")
    {
        _mediator = mediator;
        _mediator.RegisterConsumer(this);
    }
    public void UpdateDeviceInfo(FlatDeviceInfo deviceInfo) => Update(deviceInfo);
    protected override object? Map(FlatDeviceInfo info) => DeviceInfoMapper.Flat(info);
    public override void Dispose() { _mediator.RemoveConsumer(this); base.Dispose(); }
}

public sealed class SwitchWatcher : DeviceWatcherBase<SwitchInfo>, ISwitchConsumer
{
    private readonly ISwitchMediator _mediator;
    public SwitchWatcher(ISwitchMediator mediator, BeaconServer server) : base(server, "switch")
    {
        _mediator = mediator;
        _mediator.RegisterConsumer(this);
    }
    public void UpdateDeviceInfo(SwitchInfo deviceInfo) => Update(deviceInfo);
    protected override object? Map(SwitchInfo info) => DeviceInfoMapper.Switches(info);
    public override void Dispose() { _mediator.RemoveConsumer(this); base.Dispose(); }
}

public sealed class WeatherWatcher : DeviceWatcherBase<WeatherDataInfo>, IWeatherDataConsumer
{
    private readonly IWeatherDataMediator _mediator;
    public WeatherWatcher(IWeatherDataMediator mediator, BeaconServer server) : base(server, "weather")
    {
        _mediator = mediator;
        _mediator.RegisterConsumer(this);
    }
    public void UpdateDeviceInfo(WeatherDataInfo deviceInfo) => Update(deviceInfo);
    protected override object? Map(WeatherDataInfo info) => DeviceInfoMapper.Weather(info);
    public override void Dispose() { _mediator.RemoveConsumer(this); base.Dispose(); }
}

public sealed class MountWatcher : DeviceWatcherBase<TelescopeInfo>, ITelescopeConsumer
{
    private readonly ITelescopeMediator _mediator;
    public MountWatcher(ITelescopeMediator mediator, BeaconServer server) : base(server, "mount")
    {
        _mediator = mediator;
        _mediator.RegisterConsumer(this);
    }
    public void UpdateDeviceInfo(TelescopeInfo deviceInfo) => Update(deviceInfo);
    protected override object? Map(TelescopeInfo info) => DeviceInfoMapper.Mount(info);
    public override void Dispose() { _mediator.RemoveConsumer(this); base.Dispose(); }
}

public sealed class FilterWheelWatcher : DeviceWatcherBase<FilterWheelInfo>, IFilterWheelConsumer
{
    private readonly IFilterWheelMediator _mediator;
    public FilterWheelWatcher(IFilterWheelMediator mediator, BeaconServer server) : base(server, "filterwheel")
    {
        _mediator = mediator;
        _mediator.RegisterConsumer(this);
    }
    public void UpdateDeviceInfo(FilterWheelInfo deviceInfo) => Update(deviceInfo);
    protected override object? Map(FilterWheelInfo info) => DeviceInfoMapper.FilterWheel(info);
    public override void Dispose() { _mediator.RemoveConsumer(this); base.Dispose(); }
}

public sealed class GuiderWatcher : DeviceWatcherBase<GuiderInfo>, IGuiderConsumer
{
    private readonly IGuiderMediator _mediator;
    public GuiderWatcher(IGuiderMediator mediator, BeaconServer server) : base(server, "guider")
    {
        _mediator = mediator;
        _mediator.RegisterConsumer(this);
    }
    public void UpdateDeviceInfo(GuiderInfo deviceInfo) => Update(deviceInfo);
    protected override object? Map(GuiderInfo info) => DeviceInfoMapper.Guider(info);
    public override void Dispose() { _mediator.RemoveConsumer(this); base.Dispose(); }
}
