using AstralWarden.Nina.Beacon.Contracts;
using AstralWarden.Nina.Beacon.Instructions;
using Xunit;

namespace AstralWarden.Nina.Beacon.Tests;

/// <summary>
/// BeaconRuntime is process-wide static, so anything that reads or writes it runs alone.
/// </summary>
[CollectionDefinition("BeaconRuntime", DisableParallelization = true)]
public class BeaconRuntimeCollection { }

/// <summary>
/// The user's own sequencer instruction. This one ships INSIDE NINA, in the user's sequence, and
/// NINA turns ANY validation issue into a modal "start anyway?" prompt, defaulting to Cancel, when
/// the sequence starts. So an issue is only raised when the plugin itself is broken (the Beacon
/// failed to start). An agent that simply isn't connected right now (restarting, updating, not
/// installed yet) is a passing state, and must never stand between the user and their night.
///
/// And validation runs on the sequencer's UI path, so it must never throw at NINA.
/// </summary>
[Collection("BeaconRuntime")]
public class SendWardenAlertInstructionTests : IDisposable
{
    public SendWardenAlertInstructionTests() => Clear();

    public void Dispose() => Clear();

    private static void Clear()
    {
        BeaconRuntime.Broadcast = null;
    }

    [Fact]
    public void With_the_beacon_running_it_validates_clean()
    {
        BeaconRuntime.Broadcast = (_, _) => { };

        var instruction = new SendWardenAlertInstruction();

        Assert.True(instruction.Validate());
        Assert.Empty(instruction.Issues);
    }

    [Fact]
    public void With_the_beacon_not_running_it_says_so()
    {
        var instruction = new SendWardenAlertInstruction();

        Assert.False(instruction.Validate());
        Assert.Contains(instruction.Issues, i => i.Contains("Beacon is not running"));
    }

    [Fact]
    public void With_no_agent_connected_it_still_validates_so_the_sequence_can_start()
    {
        // The Beacon is up but nobody is listening: validation must not ask whether anyone is.
        // A failed check here would put NINA's "start anyway?" modal in front of the user's night.
        using var server = new AstralWarden.Nina.Beacon.Server.BeaconServer(port: 0);
        BeaconRuntime.Broadcast = server.Broadcast;
        Assert.Equal(0, server.ClientCount);

        var instruction = new SendWardenAlertInstruction();

        Assert.True(instruction.Validate());
        Assert.Empty(instruction.Issues);
    }

    [Fact]
    public async Task Running_it_broadcasts_the_users_title_severity_and_message()
    {
        var sent = new List<(string Type, object Payload)>();
        BeaconRuntime.Broadcast = (t, p) => sent.Add((t, p));

        var instruction = new SendWardenAlertInstruction
        {
            Title = "  Roof check  ",
            Severity = " ERROR ",
            Message = "  cover did not open  ",
        };
        await instruction.Execute(null!, CancellationToken.None);

        var (type, payload) = Assert.Single(sent);
        Assert.Equal("alert.custom", type);
        var alert = Assert.IsType<AlertPayload>(payload);
        Assert.Equal("Roof check", alert.Title);
        Assert.Equal("error", alert.Severity);
        Assert.Equal("cover did not open", alert.Message);
    }

    [Fact]
    public async Task Every_severity_the_dropdown_offers_reaches_the_wire_unchanged()
    {
        // The severities are written down twice: as an x:Array in Templates.xaml (what the user can
        // pick) and as the `is "warn" or "error"` guard in the instruction (what survives to the
        // wire). Nothing joins them, so a value could be offered in the sequencer and then silently
        // rewritten to "info" — an alert the user marked as an error arriving as a notice.
        // Constructing the dictionary also exercises InitializeComponent: without it NINA has no
        // DataTemplate for our instruction and renders it as a bare type name in the user's sequence.
        var offered = (System.Collections.IEnumerable)new Templates()["AstralWardenSeverities"];
        var severities = offered.Cast<string>().ToArray();

        Assert.Equal(new[] { "info", "warn", "error" }, severities);

        foreach (var severity in severities)
        {
            var sent = new List<(string Type, object Payload)>();
            BeaconRuntime.Broadcast = (t, p) => sent.Add((t, p));

            var instruction = new SendWardenAlertInstruction { Severity = severity };
            await instruction.Execute(null!, CancellationToken.None);

            Assert.Equal(severity, ((AlertPayload)Assert.Single(sent).Payload).Severity);
        }
    }

    [Fact]
    public async Task An_unrecognised_severity_becomes_info_rather_than_reaching_the_wire_raw()
    {
        // ch.4 gives the severity vocabulary as info|warn|error; the cloud's alert fan-out reads it.
        var sent = new List<(string Type, object Payload)>();
        BeaconRuntime.Broadcast = (t, p) => sent.Add((t, p));

        var instruction = new SendWardenAlertInstruction { Severity = "catastrophic" };
        await instruction.Execute(null!, CancellationToken.None);

        Assert.Equal("info", ((AlertPayload)Assert.Single(sent).Payload).Severity);
    }

    [Fact]
    public async Task A_broadcast_that_throws_never_fails_the_users_sequence()
    {
        // Aborting a night's imaging over a monitoring detail is the worst thing this plugin could do.
        BeaconRuntime.Broadcast = (_, _) => throw new InvalidOperationException("socket gone");

        var instruction = new SendWardenAlertInstruction();

        Assert.Null(await Record.ExceptionAsync(() => instruction.Execute(null!, CancellationToken.None)));
    }

    [Fact]
    public void A_copy_keeps_every_setting_the_user_can_make()
    {
        // NINA clones an instruction whenever the user duplicates it, drags it from a template, or
        // loads a template into a sequence. NINA's own instructions copy the shared metadata,
        // including the on-error behaviour and attempt count the user sets in the sequencer, so a
        // copy that dropped them would quietly change how the user's sequence handles a failure.
        var original = new SendWardenAlertInstruction
        {
            Title = "Roof check",
            Severity = "error",
            Message = "cover did not open",
            Attempts = 3,
            ErrorBehavior = NINA.Sequencer.Utility.InstructionErrorBehavior.SkipInstructionSetOnError,
        };

        var copy = Assert.IsType<SendWardenAlertInstruction>(original.Clone());

        Assert.NotSame(original, copy);
        Assert.Equal("Roof check", copy.Title);
        Assert.Equal("error", copy.Severity);
        Assert.Equal("cover did not open", copy.Message);
        Assert.Equal(3, copy.Attempts);
        Assert.Equal(NINA.Sequencer.Utility.InstructionErrorBehavior.SkipInstructionSetOnError, copy.ErrorBehavior);
        Assert.Equal(original.Name, copy.Name);
        Assert.Equal(original.Category, copy.Category);
        Assert.Equal(original.Description, copy.Description);
        Assert.Same(original.Icon, copy.Icon);
    }

    [Fact]
    public async Task With_the_beacon_gone_running_it_is_a_no_op_not_a_crash()
    {
        var instruction = new SendWardenAlertInstruction();

        Assert.Null(await Record.ExceptionAsync(() => instruction.Execute(null!, CancellationToken.None)));
    }
}
