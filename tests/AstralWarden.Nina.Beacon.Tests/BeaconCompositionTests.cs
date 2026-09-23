using System.Reflection;
using AstralWarden.Nina.Beacon.Instructions;
using AstralWarden.Nina.Beacon.Optional;
using AstralWarden.Nina.Beacon.Server;
using AstralWarden.Nina.Beacon.Watchers;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Plugin.Interfaces;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Interfaces.Mediator;
using NINA.WPF.Base.Interfaces.Mediator;
using NSubstitute;
using Xunit;

namespace AstralWarden.Nina.Beacon.Tests;

/// <summary>
/// The plugin's composition root — the one place that decides which telemetry the Beacon actually
/// produces. Nothing downstream can tell that a feed is missing: the heartbeat keeps reporting, the
/// socket stays up, and the owner simply never sees mount pointing, or the autofocus curve, or the
/// Target Scheduler feed again. So the set of feeds is asserted directly, against ch.4's message
/// table (ten device kinds, plus image, autofocus, sequence, mount events, Target Scheduler, APPM).
///
/// The static hand-off to the sequencer instruction is here too: NINA instantiates
/// SendWardenAlertInstruction itself, so BeaconRuntime is the only route from a user's sequence to
/// the socket. Unset, the user's "Send Astral Warden alert" step is a silent no-op — it validates,
/// it runs, and no alert is ever sent.
/// </summary>
[Collection("BeaconRuntime")]
public class BeaconCompositionTests
{
    private static Beacon Compose() => new(
        Substitute.For<IProfileService>(),
        Substitute.For<ICameraMediator>(),
        Substitute.For<IFocuserMediator>(),
        Substitute.For<IRotatorMediator>(),
        Substitute.For<ISafetyMonitorMediator>(),
        Substitute.For<IFlatDeviceMediator>(),
        Substitute.For<ISwitchMediator>(),
        Substitute.For<IWeatherDataMediator>(),
        Substitute.For<ITelescopeMediator>(),
        Substitute.For<IFilterWheelMediator>(),
        Substitute.For<IGuiderMediator>(),
        Substitute.For<IImageSaveMediator>(),
        Substitute.For<ISequenceMediator>(),
        Substitute.For<IMessageBroker>());

    private static IReadOnlyList<Type> WatcherTypes(Beacon beacon) =>
        ((List<IDisposable>)typeof(Beacon)
            .GetField("_watchers", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(beacon)!)
        .Select(w => w.GetType())
        .ToList();

    [Fact]
    public async Task Every_documented_feed_is_registered()
    {
        var beacon = Compose();
        try
        {
            var expected = new[]
            {
                // The ten device kinds of ch.4's device.state row.
                typeof(CameraWatcher), typeof(FocuserWatcher), typeof(RotatorWatcher),
                typeof(SafetyWatcher), typeof(FlatWatcher), typeof(SwitchWatcher),
                typeof(WeatherWatcher), typeof(MountWatcher), typeof(FilterWheelWatcher),
                typeof(GuiderWatcher),
                // and the rest of the message table.
                typeof(ImageWatcher),           // image.saved / image.stars / camera.downloadtimeout
                typeof(AutofocusWatcher),       // af.start / af.point / af.complete
                typeof(SequenceWatcher),        // sequence.state
                typeof(MountEventWatcher),      // mount.event
                typeof(TargetSchedulerWatcher), // ts.*
                typeof(AppmPoller),             // appm.model
            };

            var registered = WatcherTypes(beacon);

            Assert.Equal(expected.OrderBy(t => t.Name), registered.OrderBy(t => t.Name));
        }
        finally
        {
            await beacon.Teardown();
        }
    }

    private static int ClientConnectedSubscribers(object server) =>
        ((Delegate?)typeof(BeaconServer)
            .GetField("ClientConnected", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(server))
        ?.GetInvocationList().Length ?? 0;

    [Fact]
    public async Task Every_late_joiner_replay_is_hooked_up_and_unhooked_at_teardown()
    {
        // ch.4: "Late joiners start from truth" — current device state (ten kinds), the last
        // sequence.state, the last ts.targetstart and the cached appm.model are re-broadcast when a
        // client connects. That is thirteen handlers on the server's ClientConnected, and it is the
        // one place the wiring is observable from outside: a feed that was never registered has no
        // handler, and a feed left registered after teardown fires into a disposed server on the
        // next agent reconnect, for as long as NINA runs.
        var beacon = Compose();
        var server = typeof(Beacon)
            .GetField("_server", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(beacon)!;

        Assert.Equal(13, ClientConnectedSubscribers(server));

        await beacon.Teardown();

        Assert.Equal(0, ClientConnectedSubscribers(server));
    }

    [Fact]
    public async Task Every_device_watcher_registers_with_its_mediator_and_hands_itself_back()
    {
        // Two lines per device kind, twenty in all, and both fail silently. Without
        // RegisterConsumer NINA never pushes that device's state, so the kind simply stops
        // existing — the heartbeat still lists it, the socket stays up, and nobody is told. Without
        // RemoveConsumer the consumer stays on NINA's mediator after the plugin is torn down and
        // keeps being pushed into, calling a disposed BeaconServer for as long as NINA runs, from
        // inside software the user did not get from us.
        //
        // The feed list above proves the ten watcher objects are constructed; it says nothing about
        // whether any of them is wired to anything.
        var camera = Substitute.For<ICameraMediator>();
        var focuser = Substitute.For<IFocuserMediator>();
        var rotator = Substitute.For<IRotatorMediator>();
        var safety = Substitute.For<ISafetyMonitorMediator>();
        var flat = Substitute.For<IFlatDeviceMediator>();
        var switches = Substitute.For<ISwitchMediator>();
        var weather = Substitute.For<IWeatherDataMediator>();
        var telescope = Substitute.For<ITelescopeMediator>();
        var filterWheel = Substitute.For<IFilterWheelMediator>();
        var guider = Substitute.For<IGuiderMediator>();

        var beacon = new Beacon(Substitute.For<IProfileService>(), camera, focuser, rotator, safety,
            flat, switches, weather, telescope, filterWheel, guider,
            Substitute.For<IImageSaveMediator>(), Substitute.For<ISequenceMediator>(),
            Substitute.For<IMessageBroker>());

        camera.Received(1).RegisterConsumer(Arg.Is<ICameraConsumer>(c => c is CameraWatcher));
        focuser.Received(1).RegisterConsumer(Arg.Is<IFocuserConsumer>(c => c is FocuserWatcher));
        rotator.Received(1).RegisterConsumer(Arg.Is<IRotatorConsumer>(c => c is RotatorWatcher));
        safety.Received(1).RegisterConsumer(Arg.Is<ISafetyMonitorConsumer>(c => c is SafetyWatcher));
        flat.Received(1).RegisterConsumer(Arg.Is<IFlatDeviceConsumer>(c => c is FlatWatcher));
        switches.Received(1).RegisterConsumer(Arg.Is<ISwitchConsumer>(c => c is SwitchWatcher));
        weather.Received(1).RegisterConsumer(Arg.Is<IWeatherDataConsumer>(c => c is WeatherWatcher));
        telescope.Received(1).RegisterConsumer(Arg.Is<ITelescopeConsumer>(c => c is MountWatcher));
        filterWheel.Received(1).RegisterConsumer(Arg.Is<IFilterWheelConsumer>(c => c is FilterWheelWatcher));
        guider.Received(1).RegisterConsumer(Arg.Is<IGuiderConsumer>(c => c is GuiderWatcher));
        // The autofocus watcher rides the focuser mediator as a second consumer, not a device kind.
        focuser.Received(1).RegisterConsumer(Arg.Is<IFocuserConsumer>(c => c is AutofocusWatcher));

        await beacon.Teardown();

        camera.Received(1).RemoveConsumer(Arg.Is<ICameraConsumer>(c => c is CameraWatcher));
        focuser.Received(1).RemoveConsumer(Arg.Is<IFocuserConsumer>(c => c is FocuserWatcher));
        rotator.Received(1).RemoveConsumer(Arg.Is<IRotatorConsumer>(c => c is RotatorWatcher));
        safety.Received(1).RemoveConsumer(Arg.Is<ISafetyMonitorConsumer>(c => c is SafetyWatcher));
        flat.Received(1).RemoveConsumer(Arg.Is<IFlatDeviceConsumer>(c => c is FlatWatcher));
        switches.Received(1).RemoveConsumer(Arg.Is<ISwitchConsumer>(c => c is SwitchWatcher));
        weather.Received(1).RemoveConsumer(Arg.Is<IWeatherDataConsumer>(c => c is WeatherWatcher));
        telescope.Received(1).RemoveConsumer(Arg.Is<ITelescopeConsumer>(c => c is MountWatcher));
        filterWheel.Received(1).RemoveConsumer(Arg.Is<IFilterWheelConsumer>(c => c is FilterWheelWatcher));
        guider.Received(1).RemoveConsumer(Arg.Is<IGuiderConsumer>(c => c is GuiderWatcher));
        focuser.Received(1).RemoveConsumer(Arg.Is<IFocuserConsumer>(c => c is AutofocusWatcher));
    }

    private static T HeartbeatSupplier<T>(Beacon beacon, string field) where T : Delegate
    {
        var heartbeat = typeof(Beacon)
            .GetField("_heartbeat", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(beacon)!;
        return (T)typeof(HeartbeatWatcher)
            .GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(heartbeat)!;
    }

    [Fact]
    public async Task The_heartbeat_is_wired_to_report_all_ten_device_kinds()
    {
        // ch.4: the heartbeat carries "per-device connected flags". The heartbeat is NOT in
        // _watchers — it is constructed separately, with two closures over the composition root —
        // so the feed assertion above says nothing about it. Those flags are how the fleet view
        // answers "is the camera connected" on a rig that is idle and therefore emitting nothing
        // else, and they degrade silently: an empty or null map reads exactly like a rig with no
        // gear attached.
        var beacon = Compose();
        try
        {
            var devices = HeartbeatSupplier<Func<IReadOnlyDictionary<string, bool>?>>(beacon, "_devices")();

            Assert.NotNull(devices);
            Assert.Equal(
                new[] { "camera", "filterwheel", "flat", "focuser", "guider", "mount", "rotator",
                        "safety", "switch", "weather" },
                devices!.Keys.OrderBy(k => k, StringComparer.Ordinal));

            // The other closure: the sequence summary, which must answer before any poll has run.
            var summary = HeartbeatSupplier<Func<(bool Running, string? Instruction)>>(beacon, "_sequence")();
            Assert.False(summary.Running);
            Assert.Null(summary.Instruction);
        }
        finally
        {
            await beacon.Teardown();
        }
    }

    [Fact]
    public async Task The_sequencer_instruction_can_reach_the_socket_while_the_beacon_is_up()
    {
        // BeaconRuntime is the only path from a MEF-instantiated sequence item to the server.
        BeaconRuntime.Broadcast = null;
        BeaconRuntime.ClientCount = null;

        var beacon = Compose();
        try
        {
            Assert.NotNull(BeaconRuntime.Broadcast);
            Assert.NotNull(BeaconRuntime.ClientCount);
            // The instruction validates clean only when the Beacon is running AND an agent is
            // connected; with the Beacon up and nobody listening it must say so rather than
            // pretending the alert would arrive.
            Assert.Equal(0, BeaconRuntime.ClientCount!());

            var instruction = new SendWardenAlertInstruction { Title = "test", Severity = "warn" };
            await instruction.Execute(null!, CancellationToken.None);
        }
        finally
        {
            await beacon.Teardown();
        }

        // Cleared on teardown, so an instruction left in a sequence after the plugin unloads is a
        // no-op rather than a call into a disposed server.
        Assert.Null(BeaconRuntime.Broadcast);
        Assert.Null(BeaconRuntime.ClientCount);
    }
}
