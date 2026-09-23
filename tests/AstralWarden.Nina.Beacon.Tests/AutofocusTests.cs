using AstralWarden.Nina.Beacon.Contracts;
using AstralWarden.Nina.Beacon.Mapping;
using AstralWarden.Nina.Beacon.Watchers;
using NINA.Equipment.Equipment.MyFocuser;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Interfaces.ViewModel;
using OxyPlot;
using Xunit;

namespace AstralWarden.Nina.Beacon.Tests;

public class AfCurveFitterTests
{
    [Fact]
    public void Recovers_the_vertex_of_a_clean_parabola()
    {
        // y = 2 + (x - 15000)² / 1e6 sampled around the minimum.
        var points = Enumerable.Range(-4, 9)
            .Select(i => 15000.0 + i * 250)
            .Select(x => new AfMeasurePoint(x, 2 + Math.Pow(x - 15000, 2) / 1e6))
            .ToList();

        var fit = AfCurveFitter.Fit(points)!;

        Assert.Equal("quadratic", fit.Method);
        Assert.Equal(15000, fit.MinimumPosition, 0.5);
        Assert.Equal(2.0, fit.MinimumHfr, 0.01);
        Assert.True(fit.RSquared > 0.999);
    }

    [Fact]
    public void An_off_centre_sweep_reports_the_vertex_not_the_middle_of_the_sampled_range()
    {
        // A real sweep is not centred on the minimum: NINA steps out from wherever the focuser
        // happened to be, and the best-focus position usually lands off-centre. That is the whole
        // reason to fit a parabola — for a sweep sampled symmetrically about the minimum, the mean
        // of the sampled positions IS the vertex, so a symmetric fixture cannot tell a fit from an
        // average. Here the two are 500 steps apart.
        var positions = Enumerable.Range(0, 9).Select(i => 14500.0 + i * 250).ToList();
        var points = positions
            .Select(x => new AfMeasurePoint(x, 2 + Math.Pow(x - 15000, 2) / 1e6))
            .ToList();

        var fit = AfCurveFitter.Fit(points)!;

        Assert.Equal(15500, positions.Average(), 0.5); // the mean, for contrast
        Assert.Equal(15000, fit.MinimumPosition, 0.5);
        Assert.Equal(2.0, fit.MinimumHfr, 0.01);
        Assert.True(fit.RSquared > 0.999);
    }

    [Fact]
    public void A_long_travel_focuser_is_fitted_as_accurately_as_a_short_one()
    {
        // The fit shifts x by its mean "for conditioning (focuser positions are ~1e4-1e5; x⁴ would
        // otherwise reach 1e20)". That is a claim about real hardware, and it is the only reason the
        // shift exists — so it has to be checked where it bites. The other fixtures all sample near
        // 15 000, where the unshifted normal equations still happen to hold together; a focuser
        // reporting six-figure positions (a large-travel absolute focuser, or one whose driver
        // counts in microsteps) is ordinary and lands two orders of magnitude further out in x⁴.
        var points = Enumerable.Range(0, 9)
            .Select(i => 980_000.0 + i * 250)
            .Select(x => new AfMeasurePoint(x, 2 + Math.Pow(x - 981_000, 2) / 1e6))
            .ToList();

        var fit = AfCurveFitter.Fit(points)!;

        Assert.Equal(981_000, fit.MinimumPosition, 1.0);
        Assert.Equal(2.0, fit.MinimumHfr, 0.05);
        Assert.True(fit.RSquared > 0.999);
    }

    [Fact]
    public void Noisy_curve_reports_a_lower_r_squared()
    {
        var rng = new Random(42);
        var points = Enumerable.Range(-4, 9)
            .Select(i => 15000.0 + i * 250)
            .Select(x => new AfMeasurePoint(x, 2 + Math.Pow(x - 15000, 2) / 1e6 + rng.NextDouble() * 0.5))
            .ToList();

        var fit = AfCurveFitter.Fit(points)!;
        Assert.True(fit.RSquared < 0.99);
        Assert.True(fit.RSquared > 0.3);
    }

    [Fact]
    public void Too_few_points_or_no_minimum_yields_no_fit()
    {
        Assert.Null(AfCurveFitter.Fit([new AfMeasurePoint(1, 2), new AfMeasurePoint(2, 3)]));
        // Downward parabola (a < 0) is not a focus curve.
        var inverted = Enumerable.Range(0, 7)
            .Select(i => new AfMeasurePoint(i * 100, 10 - Math.Pow(i - 3, 2)))
            .ToList();
        Assert.Null(AfCurveFitter.Fit(inverted));
    }
}

public class AutofocusWatcherTests
{
    private static (AutofocusWatcher, List<(string Type, object Payload)>) Create(Func<DateTimeOffset>? clock = null)
    {
        var broadcasts = new List<(string, object)>();
        var watcher = new AutofocusWatcher(new FakeFocuserMediator(), (t, p) => { lock (broadcasts) broadcasts.Add((t, p)); }, clock);
        return (watcher, broadcasts);
    }

    [Fact]
    public void A_successful_run_emits_start_points_and_complete_with_the_curve()
    {
        var (watcher, broadcasts) = Create();

        watcher.AutoFocusRunStarting();
        foreach (var i in Enumerable.Range(-3, 7))
            watcher.NewAutoFocusPoint(new DataPoint(15000 + i * 250, 2 + Math.Pow(i * 250, 2) / 1e6));
        watcher.UpdateEndAutoFocusRun(new AutoFocusInfo(4.5, 15010, "L", DateTime.UtcNow));

        Assert.Equal("af.start", broadcasts[0].Type);
        Assert.Equal(7, broadcasts.Count(b => b.Type == "af.point"));
        var complete = (AfCompletePayload)broadcasts.Single(b => b.Type == "af.complete").Payload;
        Assert.True(complete.Success);
        Assert.Equal("L", complete.Filter);
        Assert.Equal(15010, complete.FinalPosition);
        Assert.Equal(7, complete.Points.Count);
        Assert.NotNull(complete.Fit);
        Assert.Equal(15000, complete.Fit!.MinimumPosition, 1.0);
    }

    [Fact]
    public void A_new_start_closes_a_dangling_run_as_failed()
    {
        var (watcher, broadcasts) = Create();

        watcher.AutoFocusRunStarting();
        watcher.NewAutoFocusPoint(new DataPoint(15000, 2.5));
        watcher.AutoFocusRunStarting(); // engine gave up and retried — no end broadcast for run 1

        var completes = broadcasts.Where(b => b.Type == "af.complete").ToList();
        var failed = (AfCompletePayload)Assert.Single(completes).Payload;
        Assert.False(failed.Success);
        Assert.Single(failed.Points);
    }

    [Fact]
    public void A_silent_run_times_out_as_failed()
    {
        var now = new DateTimeOffset(2026, 7, 21, 3, 0, 0, TimeSpan.Zero);
        var (watcher, broadcasts) = Create(() => now);

        watcher.AutoFocusRunStarting();
        watcher.NewAutoFocusPoint(new DataPoint(15000, 2.5));
        now += TimeSpan.FromMinutes(20);
        watcher.UpdateDeviceInfo(new FocuserInfo()); // any tick past the timeout

        var failed = (AfCompletePayload)Assert.Single(broadcasts, b => b.Type == "af.complete").Payload;
        Assert.False(failed.Success);
    }

    [Fact]
    public void Points_without_a_start_broadcast_still_form_a_run()
    {
        var (watcher, broadcasts) = Create();

        watcher.NewAutoFocusPoint(new DataPoint(15000, 2.5));
        watcher.UpdateEndAutoFocusRun(new AutoFocusInfo(double.NaN, 15000, "", DateTime.UtcNow));

        var complete = (AfCompletePayload)broadcasts.Single(b => b.Type == "af.complete").Payload;
        Assert.True(complete.Success);
        Assert.Single(complete.Points);
        Assert.Null(complete.Temperature); // NaN → omitted
    }

    private sealed class FakeFocuserMediator : IFocuserMediator
    {
        public void RegisterConsumer(IFocuserConsumer consumer) { }
        public void RemoveConsumer(IFocuserConsumer consumer) { }

        // Everything below is unused by the watcher.
        public event Func<object, EventArgs, Task>? Connected { add { } remove { } }
        public event Func<object, EventArgs, Task>? Disconnected { add { } remove { } }
        public string Action(string actionName, string actionParameters) => throw new NotSupportedException();
        public void Broadcast(FocuserInfo deviceInfo) => throw new NotSupportedException();
        public Task<bool> Connect() => throw new NotSupportedException();
        public Task Disconnect() => throw new NotSupportedException();
        public NINA.Equipment.Interfaces.IDevice GetDevice() => throw new NotSupportedException();
        public FocuserInfo GetInfo() => throw new NotSupportedException();
        public Task<IList<string>> Rescan() => throw new NotSupportedException();
        public void SendCommandBlind(string command, bool raw) => throw new NotSupportedException();
        public bool SendCommandBool(string command, bool raw) => throw new NotSupportedException();
        public string SendCommandString(string command, bool raw) => throw new NotSupportedException();
        public void BroadcastAutoFocusRunStarting() => throw new NotSupportedException();
        public void BroadcastNewAutoFocusPoint(DataPoint dataPoint) => throw new NotSupportedException();
        public void BroadcastSuccessfulAutoFocusRun(AutoFocusInfo info) => throw new NotSupportedException();
        public void BroadcastUserFocused(FocuserInfo info) => throw new NotSupportedException();
        public Task<int> MoveFocuser(int position, CancellationToken ct) => throw new NotSupportedException();
        public Task<int> MoveFocuserByTemperatureRelative(double temperature, double slope, CancellationToken ct) => throw new NotSupportedException();
        public Task<int> MoveFocuserRelative(int position, CancellationToken ct) => throw new NotSupportedException();
        public void ToggleTempComp(bool tempComp) => throw new NotSupportedException();
        public void RegisterHandler(IFocuserVM handler) => throw new NotSupportedException();
    }
}
