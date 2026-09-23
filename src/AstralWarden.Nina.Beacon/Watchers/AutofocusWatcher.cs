using AstralWarden.Nina.Beacon.Contracts;
using AstralWarden.Nina.Beacon.Mapping;
using NINA.Equipment.Equipment.MyFocuser;
using NINA.Equipment.Interfaces.Mediator;
using OxyPlot;

namespace AstralWarden.Nina.Beacon.Watchers;

/// <summary>
/// Autofocus lifecycle via IFocuserConsumer: af.start on run start, af.point per measured point,
/// af.complete with the full assembled curve + our own quadratic fit when the run ends. The
/// mediator only broadcasts run-end for SUCCESSFUL runs, so a still-open run is closed as failed
/// when the next one starts or after <see cref="RunTimeout"/> of silence.
/// (Takes a broadcast delegate rather than the server so the state machine is directly testable.)
/// </summary>
public sealed class AutofocusWatcher : IFocuserConsumer
{
    public static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(15);

    private readonly IFocuserMediator _mediator;
    private readonly Action<string, object> _broadcast;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _gate = new();
    private List<AfMeasurePoint>? _points;
    private DateTimeOffset _startedAt;
    private DateTimeOffset _lastActivity;

    public AutofocusWatcher(IFocuserMediator mediator, Action<string, object> broadcast,
        Func<DateTimeOffset>? clock = null)
    {
        _mediator = mediator;
        _broadcast = broadcast;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _mediator.RegisterConsumer(this);
    }

    public void AutoFocusRunStarting()
    {
        try
        {
            AfCompletePayload? abandoned;
            lock (_gate)
            {
                abandoned = TakeAbandonedRunLocked();
                _points = new List<AfMeasurePoint>();
                _startedAt = _lastActivity = _clock();
            }
            if (abandoned is not null) _broadcast("af.complete", abandoned);
            _broadcast("af.start", new { });
        }
        catch { /* never propagate into NINA's broadcast */ }
    }

    public void NewAutoFocusPoint(DataPoint dataPoint)
    {
        try
        {
            var point = new AfMeasurePoint(dataPoint.X, Math.Round(dataPoint.Y, 4));
            AfCompletePayload? abandoned;
            lock (_gate)
            {
                abandoned = TakeTimedOutRunLocked();
                _points ??= new List<AfMeasurePoint>(); // engine skipped the start broadcast
                if (_points.Count == 0) _startedAt = _clock();
                _points.Add(point);
                _lastActivity = _clock();
            }
            if (abandoned is not null) _broadcast("af.complete", abandoned);
            _broadcast("af.point", new AfPointPayload(point.Position, point.Hfr));
        }
        catch { }
    }

    public void UpdateEndAutoFocusRun(AutoFocusInfo info)
    {
        try
        {
            AfCompletePayload payload;
            lock (_gate)
            {
                var points = _points ?? new List<AfMeasurePoint>();
                payload = new AfCompletePayload(
                    Success: true,
                    Filter: string.IsNullOrWhiteSpace(info.Filter) ? null : info.Filter,
                    FinalPosition: info.Position,
                    Temperature: double.IsNaN(info.Temperature) ? null : info.Temperature,
                    DurationSec: Math.Round((_clock() - (_points is null ? _clock() : _startedAt)).TotalSeconds, 1),
                    Points: points,
                    Fit: AfCurveFitter.Fit(points));
                _points = null;
            }
            _broadcast("af.complete", payload);
        }
        catch { }
    }

    public void UpdateUserFocused(FocuserInfo info) { }
    public void UpdateDeviceInfo(FocuserInfo deviceInfo)
    {
        // Focuser state is FocuserWatcher's job; we only use the push as a timeout tick.
        try
        {
            AfCompletePayload? abandoned;
            lock (_gate) abandoned = TakeTimedOutRunLocked();
            if (abandoned is not null) _broadcast("af.complete", abandoned);
        }
        catch { }
    }

    /// <summary>Closes an open run as failed if it has gone quiet past the timeout, returning what
    /// to publish. Broadcasting is left to the caller so nothing is ever sent while holding the
    /// lock — these hooks run on NINA's threads.</summary>
    private AfCompletePayload? TakeTimedOutRunLocked() =>
        _points is not null && _clock() - _lastActivity > RunTimeout ? TakeAbandonedRunLocked() : null;

    private AfCompletePayload? TakeAbandonedRunLocked()
    {
        if (_points is null) return null;
        var payload = new AfCompletePayload(
            Success: false, Filter: null, FinalPosition: null, Temperature: null,
            DurationSec: Math.Round((_lastActivity - _startedAt).TotalSeconds, 1),
            Points: _points, Fit: AfCurveFitter.Fit(_points));
        _points = null;
        return payload;
    }

    public void Dispose() => _mediator.RemoveConsumer(this);
}
