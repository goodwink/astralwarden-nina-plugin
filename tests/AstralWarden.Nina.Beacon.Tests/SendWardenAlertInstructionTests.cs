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
/// The user's own sequencer instruction. This one ships INSIDE NINA, in the user's sequence, so its
/// validation result is not ours to get wrong in either direction (ch.4: "the sequencer UI warns at
/// validation time when the Beacon isn't running or no agent is connected"):
///
///  • always-invalid marks a perfectly good sequence as broken inside capture software we did not
///    sell the user — our monitoring plugin degrading their imaging night;
///  • always-valid removes the only warning that the alert they just added would go nowhere.
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
        BeaconRuntime.ClientCount = null;
    }

    [Fact]
    public void With_the_beacon_running_and_an_agent_connected_it_validates_clean()
    {
        BeaconRuntime.Broadcast = (_, _) => { };
        BeaconRuntime.ClientCount = () => 1;

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
    public void With_no_agent_connected_it_says_the_alert_would_go_nowhere()
    {
        BeaconRuntime.Broadcast = (_, _) => { };
        BeaconRuntime.ClientCount = () => 0;

        var instruction = new SendWardenAlertInstruction();

        Assert.False(instruction.Validate());
        Assert.Contains(instruction.Issues, i => i.Contains("agent is not connected"));
    }

    [Fact]
    public void A_throwing_client_count_reports_clean_rather_than_faulting_the_sequencer_ui()
    {
        BeaconRuntime.Broadcast = (_, _) => { };
        BeaconRuntime.ClientCount = () => throw new InvalidOperationException("mid-teardown");

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
    public async Task With_the_beacon_gone_running_it_is_a_no_op_not_a_crash()
    {
        var instruction = new SendWardenAlertInstruction();

        Assert.Null(await Record.ExceptionAsync(() => instruction.Execute(null!, CancellationToken.None)));
    }
}
