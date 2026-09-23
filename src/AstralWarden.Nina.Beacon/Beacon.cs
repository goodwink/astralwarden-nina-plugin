using System.ComponentModel.Composition;
using AstralWarden.Nina.Beacon.Contracts;
using AstralWarden.Nina.Beacon.Instructions;
using AstralWarden.Nina.Beacon.Server;
using AstralWarden.Nina.Beacon.Watchers;
using NINA.Sequencer.Interfaces.Mediator;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Plugin;
using NINA.Plugin.Interfaces;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;

namespace AstralWarden.Nina.Beacon;

/// <summary>
/// Astral Warden Beacon — publishes a one-way telemetry stream for the local Astral Warden agent.
/// Read-only by construction: observes NINA through its public mediators, commands nothing, and
/// accepts no input on the socket.
/// </summary>
[Export(typeof(IPluginManifest))]
public class Beacon : PluginBase
{
    private readonly IProfileService _profileService;
    private readonly BeaconServer _server;
    private readonly HeartbeatWatcher _heartbeat;
    private readonly List<IDisposable> _watchers = new();
    private readonly string _startedAt = BeaconJson.Timestamp(DateTimeOffset.UtcNow);

    [ImportingConstructor]
    public Beacon(
        IProfileService profileService,
        ICameraMediator cameraMediator,
        IFocuserMediator focuserMediator,
        IRotatorMediator rotatorMediator,
        ISafetyMonitorMediator safetyMediator,
        IFlatDeviceMediator flatMediator,
        ISwitchMediator switchMediator,
        IWeatherDataMediator weatherMediator,
        ITelescopeMediator telescopeMediator,
        IFilterWheelMediator filterWheelMediator,
        IGuiderMediator guiderMediator,
        IImageSaveMediator imageSaveMediator,
        ISequenceMediator sequenceMediator,
        IMessageBroker messageBroker)
    {
        _profileService = profileService;

        Action<string> log = message => Logger.Info($"Beacon: {message}");
        _server = new BeaconServer(helloFactory: BuildHello, log: log);

        // Fault isolation: a throwing watcher costs that one feed, never the whole plugin (the
        // first live install died to a single NRE in a constructor-time event subscription).
        var deviceWatchers = new List<IDisposable>();
        void AddDevice(string name, Func<IDisposable> create) { if (Try(name, create) is { } w) deviceWatchers.Add(w); }
        void Add(string name, Func<IDisposable> create) { if (Try(name, create) is { } w) _watchers.Add(w); }

        // Device push: one consumer per device kind; NINA broadcasts state to us, we throttle and
        // forward. This replaces the agent's Advanced-API REST polling entirely.
        AddDevice("camera", () => new CameraWatcher(cameraMediator, _server,
            () => _profileService.ActiveProfile?.TelescopeSettings?.FocalLength));
        AddDevice("focuser", () => new FocuserWatcher(focuserMediator, _server));
        AddDevice("rotator", () => new RotatorWatcher(rotatorMediator, _server));
        AddDevice("safety", () => new SafetyWatcher(safetyMediator, _server));
        AddDevice("flat", () => new FlatWatcher(flatMediator, _server));
        AddDevice("switch", () => new SwitchWatcher(switchMediator, _server));
        AddDevice("weather", () => new WeatherWatcher(weatherMediator, _server));
        AddDevice("mount", () => new MountWatcher(telescopeMediator, _server));
        AddDevice("filterwheel", () => new FilterWheelWatcher(filterWheelMediator, _server));
        AddDevice("guider", () => new GuiderWatcher(guiderMediator, _server));
        _watchers.AddRange(deviceWatchers);

        Add("image", () => new ImageWatcher(imageSaveMediator, cameraMediator, _server, log));
        Add("autofocus", () => new AutofocusWatcher(focuserMediator, _server.Broadcast));
        var sequenceWatcher = Try("sequence", () => new SequenceWatcher(sequenceMediator, _server, log));
        if (sequenceWatcher is not null) _watchers.Add(sequenceWatcher);
        Add("mount-events", () => new MountEventWatcher(telescopeMediator, _server, log));
        Add("target-scheduler", () => new TargetSchedulerWatcher(messageBroker, _server));
        Add("appm", () => new Optional.AppmPoller(_server, log: log));

        // Sequence instructions are MEF-instantiated by NINA, so they reach the server statically.
        BeaconRuntime.Broadcast = _server.Broadcast;
        BeaconRuntime.ClientCount = () => _server.ClientCount;

        // Never throws — a busy port leaves the plugin loaded and retrying, not dead at composition.
        _server.Start();
        _heartbeat = new HeartbeatWatcher(_server, () => DeviceSummary(deviceWatchers),
            () => sequenceWatcher?.Summary ?? (false, null), log);

        Logger.Info(_server.IsListening
            ? $"Astral Warden Beacon {BeaconVersion()} started on 127.0.0.1:{_server.Port}"
            : $"Astral Warden Beacon {BeaconVersion()} started; port {_server.Port} is busy, retrying in the background");
    }

    private static T? Try<T>(string name, Func<T> create) where T : class
    {
        try
        {
            return create();
        }
        catch (Exception ex)
        {
            Logger.Error($"Beacon: {name} watcher failed to start and is disabled: {ex}");
            return null;
        }
    }

    private static IReadOnlyDictionary<string, bool> DeviceSummary(List<IDisposable> watchers)
    {
        var summary = new Dictionary<string, bool>();
        foreach (var watcher in watchers)
        {
            // Every device watcher derives DeviceWatcherBase<TInfo>; reflection-free duck access
            // via the shared members would need a non-generic interface — keep one instead.
            if (watcher is IDeviceSummary s) summary[s.Device] = s.IsConnected;
        }
        return summary;
    }

    private HelloPayload BuildHello() => new(
        SchemaVersion: BeaconProtocol.Version,
        BeaconVersion: BeaconVersion(),
        NinaVersion: TryGetNinaVersion(),
        ProfileName: _profileService.ActiveProfile?.Name,
        ServerStartedAt: _startedAt);

    private static string BeaconVersion() =>
        typeof(Beacon).Assembly.GetName().Version?.ToString() ?? "unknown";

    private static string? TryGetNinaVersion()
    {
        try { return CoreUtil.Version; }
        catch { return null; }
    }

    public override async Task Teardown()
    {
        BeaconRuntime.Broadcast = null;
        BeaconRuntime.ClientCount = null;
        // Teardown runs while NINA is shutting down, so mediators may already be gone: one watcher
        // throwing on Dispose must not strand the rest still registered.
        Quietly(_heartbeat.Dispose);
        foreach (var watcher in _watchers) Quietly(watcher.Dispose);
        try { await _server.ShutdownAsync("shutdown"); }
        catch (Exception ex) { Logger.Error($"Beacon: server shutdown failed: {ex}"); }
        Logger.Info("Astral Warden Beacon stopped");
        await base.Teardown();
    }

    private static void Quietly(Action dispose)
    {
        try { dispose(); }
        catch (Exception ex) { Logger.Error($"Beacon: watcher dispose failed: {ex}"); }
    }
}
