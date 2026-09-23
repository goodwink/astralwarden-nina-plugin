using System.IO;
using System.Net.Sockets;
using System.Text.Json;
using AstralWarden.Nina.Beacon.Server;
using AstralWarden.Nina.Beacon.Watchers;
using NINA.Core.Model;
using NINA.Sequencer.Interfaces.Mediator;
using NINA.Sequencer.SequenceItem;
using NSubstitute;
using Xunit;

namespace AstralWarden.Nina.Beacon.Tests;

/// <summary>
/// The sequence poll. ch.4 states the cadence as a contract: `sequence.state` goes out "on start/
/// finish, then on change (2s poll while running)". Both halves of that matter and they fail in
/// opposite directions:
///
///  • broadcasting every tick instead of on change is ~30× the socket traffic all night, and the
///    per-client queue is bounded drop-oldest — so the flood pushes real messages (image.saved,
///    af.complete) out of the queue and lights up the heartbeat's drop counter;
///  • swallowing a genuine change means the cloud never learns the sequence moved, which is the
///    shape of the false `stuck_instruction` alerts of 2026-08-12.
///
/// Driven through the real socket, because "was it broadcast" is only answerable on the wire.
/// </summary>
public class SequenceWatcherTests
{
    private sealed class Instruction : SequenceItem
    {
        public override Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) =>
            Task.CompletedTask;

        public override object Clone() => new Instruction();
    }

    private static readonly TimeSpan FastPoll = TimeSpan.FromMilliseconds(20);

    /// <summary>~25 polls at the interval above: long enough that "every tick" is unmistakable.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(500);

    /// <summary>A connected client that keeps reading, so nothing is left half-read between waits.</summary>
    private sealed class Reader : IDisposable
    {
        private readonly TcpClient _client = new();
        private readonly List<string> _types = new();

        public async Task ConnectAsync(BeaconServer server)
        {
            await _client.ConnectAsync("127.0.0.1", server.Port);
            while (server.ClientCount == 0) await Task.Delay(5);
            _ = Task.Run(async () =>
            {
                using var reader = new StreamReader(_client.GetStream());
                try
                {
                    string? line;
                    while ((line = await reader.ReadLineAsync()) is not null)
                    {
                        var type = JsonDocument.Parse(line).RootElement.GetProperty("type").GetString()!;
                        lock (_types) _types.Add(type);
                    }
                }
                catch { /* closed */ }
            });
        }

        public string[] Received { get { lock (_types) return _types.ToArray(); } }

        public void Dispose() => _client.Dispose();
    }

    [Fact]
    public async Task A_sequence_that_has_not_changed_is_broadcast_once_not_once_per_poll()
    {
        using var server = new BeaconServer(port: 0);
        server.Start();
        using var reader = new Reader();
        await reader.ConnectAsync(server);

        IReadOnlyCollection<ISequenceItem> items = new[] { new Instruction { Name = "Take Exposure" } };
        var mediator = Substitute.For<ISequenceMediator>();
        mediator.Initialized.Returns(true);
        mediator.IsAdvancedSequenceRunning().Returns(true);
        mediator.GetAdvancedSequencerCurrentRunningItems().Returns(_ => items);

        using var watcher = new SequenceWatcher(mediator, server, pollInterval: FastPoll);
        await Task.Delay(Settle);

        Assert.Equal(new[] { "sequence.state" }, reader.Received);
    }

    [Fact]
    public async Task A_change_of_running_instruction_is_broadcast()
    {
        using var server = new BeaconServer(port: 0);
        server.Start();
        using var reader = new Reader();
        await reader.ConnectAsync(server);

        IReadOnlyCollection<ISequenceItem> items = new[] { new Instruction { Name = "Take Exposure" } };
        var mediator = Substitute.For<ISequenceMediator>();
        mediator.Initialized.Returns(true);
        mediator.IsAdvancedSequenceRunning().Returns(true);
        mediator.GetAdvancedSequencerCurrentRunningItems().Returns(_ => items);

        using var watcher = new SequenceWatcher(mediator, server, pollInterval: FastPoll);
        await Task.Delay(Settle);
        Assert.Single(reader.Received);

        items = new[] { new Instruction { Name = "Switch Filter" } };
        await Task.Delay(Settle);

        Assert.Equal(new[] { "sequence.state", "sequence.state" }, reader.Received);
        Assert.Equal("Switch Filter", watcher.Summary.Instruction);
    }

    [Fact]
    public async Task A_sequence_waiting_inside_a_container_reports_no_instruction_not_the_last_one()
    {
        // ch.4, from the 2026-08-12 incident: NINA only ever adds LEAF items to its running set
        // (SequenceItem.Run guards AddRunningItem with !(this is ISequenceContainer)), so a sequence
        // parked in an "once safe" container reports an EMPTY item list while
        // IsAdvancedSequenceRunning() stays true. Reading "the newest start edge" as "running now"
        // raised stuck_instruction on both rigs against a Park Scope that had finished in seconds.
        // The heartbeat's summary is where that fact is stated, so it must say running-but-idle —
        // not keep naming the instruction that has already completed.
        using var server = new BeaconServer(port: 0);
        server.Start();
        using var reader = new Reader();
        await reader.ConnectAsync(server);

        IReadOnlyCollection<ISequenceItem> items = new[] { new Instruction { Name = "Park Scope" } };
        var mediator = Substitute.For<ISequenceMediator>();
        mediator.Initialized.Returns(true);
        mediator.IsAdvancedSequenceRunning().Returns(true);
        mediator.GetAdvancedSequencerCurrentRunningItems().Returns(_ => items);

        using var watcher = new SequenceWatcher(mediator, server, pollInterval: FastPoll);
        await Task.Delay(Settle);
        Assert.Equal("Park Scope", watcher.Summary.Instruction);

        items = Array.Empty<ISequenceItem>(); // Park Scope finished; the container is waiting
        await Task.Delay(Settle);

        Assert.True(watcher.Summary.Running);
        Assert.Null(watcher.Summary.Instruction);
    }

    [Fact]
    public void Before_the_first_poll_the_summary_answers_rather_than_throwing()
    {
        // The heartbeat starts alongside the watchers and beats on its own clock, so it can ask for
        // the summary before any poll has produced one. An exception here is thrown on the heartbeat
        // loop, which is the Beacon's liveness signal.
        using var server = new BeaconServer(port: 0);
        var mediator = Substitute.For<ISequenceMediator>();
        using var watcher = new SequenceWatcher(mediator, server, pollInterval: TimeSpan.FromMinutes(10));

        Assert.False(watcher.Summary.Running);
        Assert.Null(watcher.Summary.Instruction);
    }

    [Theory]
    [InlineData("SequenceStarting")]
    [InlineData("SequenceFinished")]
    public async Task The_handlers_nina_awaits_return_a_task_it_can_await(string eventName)
    {
        // These two are Func<object, EventArgs, Task> on NINA's mediator and NINA awaits what they
        // return. A null Task is a NullReferenceException raised inside NINA's own sequence start /
        // finish broadcast — our monitoring plugin faulting the capture software mid-sequence, which
        // is the one thing the Beacon must never do. Raise.Event discards the result, so the handler
        // is taken off the mediator and invoked the way NINA invokes it.
        using var server = new BeaconServer(port: 0);
        server.Start();
        var mediator = Substitute.For<ISequenceMediator>();
        mediator.Initialized.Returns(true);
        mediator.IsAdvancedSequenceRunning().Returns(true);
        mediator.GetAdvancedSequencerCurrentRunningItems()
            .Returns(_ => new[] { new Instruction { Name = "Take Exposure" } });

        using var watcher = new SequenceWatcher(mediator, server, pollInterval: TimeSpan.FromMinutes(10));

        var handler = await WaitForHandlerAsync(mediator, $"add_{eventName}");
        var returned = handler.DynamicInvoke(this, EventArgs.Empty);

        var task = Assert.IsAssignableFrom<Task>(returned);
        await task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task<Delegate> WaitForHandlerAsync(object substitute, string addMethod)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var call = substitute.ReceivedCalls().FirstOrDefault(c => c.GetMethodInfo().Name == addMethod);
            if (call?.GetArguments()[0] is Delegate handler) return handler;
            await Task.Delay(5);
        }
        Assert.Fail($"{addMethod} was never subscribed");
        throw new InvalidOperationException();
    }

    [Fact]
    public async Task A_sequence_that_stops_running_is_broadcast_as_stopped()
    {
        // The not-running state is itself a change, and the cloud needs it: it is what ends
        // `nina.sequence.running`.
        using var server = new BeaconServer(port: 0);
        server.Start();
        using var reader = new Reader();
        await reader.ConnectAsync(server);

        var running = true;
        IReadOnlyCollection<ISequenceItem> items = new[] { new Instruction { Name = "Take Exposure" } };
        var mediator = Substitute.For<ISequenceMediator>();
        mediator.Initialized.Returns(true);
        mediator.IsAdvancedSequenceRunning().Returns(_ => running);
        mediator.GetAdvancedSequencerCurrentRunningItems().Returns(_ => items);

        using var watcher = new SequenceWatcher(mediator, server, pollInterval: FastPoll);
        await Task.Delay(Settle);
        Assert.True(watcher.Summary.Running);

        running = false;
        await Task.Delay(Settle);

        Assert.Equal(new[] { "sequence.state", "sequence.state" }, reader.Received);
        Assert.False(watcher.Summary.Running);
        Assert.Null(watcher.Summary.Instruction);
    }

    [Fact]
    public async Task Once_disposed_the_poll_is_no_longer_touching_ninas_sequencer()
    {
        // NINA tears plugins down on exit and on disable. A poll tick still running after teardown
        // is our code walking NINA's sequencer while NINA dismantles it.
        using var server = new BeaconServer(port: 0);
        var inTick = new ManualResetEventSlim();
        var tickFinished = 0;
        var mediator = Substitute.For<ISequenceMediator>();
        mediator.Initialized.Returns(true);
        mediator.IsAdvancedSequenceRunning().Returns(_ =>
        {
            inTick.Set();
            Thread.Sleep(300); // a slow read of NINA's state, in flight when teardown lands
            Interlocked.Exchange(ref tickFinished, 1);
            return false;
        });

        var watcher = new SequenceWatcher(mediator, server, pollInterval: FastPoll);
        Assert.True(inTick.Wait(TimeSpan.FromSeconds(5)), "the poll never ran");
        watcher.Dispose();

        Assert.Equal(1, Volatile.Read(ref tickFinished));
        await Task.CompletedTask;
    }
}
