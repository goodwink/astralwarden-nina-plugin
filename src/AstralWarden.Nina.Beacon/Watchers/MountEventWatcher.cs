using AstralWarden.Nina.Beacon.Contracts;
using AstralWarden.Nina.Beacon.Server;
using NINA.Equipment.Interfaces.Mediator;

namespace AstralWarden.Nina.Beacon.Watchers;

/// <summary>Discrete mount lifecycle events (slew, park, home, meridian flip) — the excursion
/// markers guiding/HFR analysis correlates against.</summary>
public sealed class MountEventWatcher : IDisposable
{
    private readonly ITelescopeMediator _mediator;
    private readonly BeaconServer _server;
    private readonly RetrySubscription _events;

    public MountEventWatcher(ITelescopeMediator mediator, BeaconServer server, Action<string>? log = null)
    {
        _mediator = mediator;
        _server = server;
        // Slewed/Parked/Unparked/Homed forward to the telescope VM (registers after plugin
        // composition → NRE if subscribed eagerly); the flip events are field-like but ride the
        // same retry group for one atomic subscribe of this mediator.
        _events = new RetrySubscription("mount events",
            subscribe: () =>
            {
                _mediator.Slewed += OnSlewed;
                _mediator.Parked += OnParked;
                _mediator.Unparked += OnUnparked;
                _mediator.Homed += OnHomed;
                _mediator.BeforeMeridianFlip += OnBeforeFlip;
                _mediator.AfterMeridianFlip += OnAfterFlip;
            },
            unsubscribe: () =>
            {
                _mediator.Slewed -= OnSlewed;
                _mediator.Parked -= OnParked;
                _mediator.Unparked -= OnUnparked;
                _mediator.Homed -= OnHomed;
                _mediator.BeforeMeridianFlip -= OnBeforeFlip;
                _mediator.AfterMeridianFlip -= OnAfterFlip;
            },
            log);
    }

    private Task OnSlewed(object sender, MountSlewedEventArgs e)
    {
        Emit(new MountEventPayload("slewed",
            FromRa: D(e.From?.RA), FromDec: D(e.From?.Dec),
            ToRa: D(e.To?.RA), ToDec: D(e.To?.Dec)));
        return Task.CompletedTask;
    }

    private Task OnParked(object sender, EventArgs e) { Emit(new MountEventPayload("parked")); return Task.CompletedTask; }
    private Task OnUnparked(object sender, EventArgs e) { Emit(new MountEventPayload("unparked")); return Task.CompletedTask; }
    private Task OnHomed(object sender, EventArgs e) { Emit(new MountEventPayload("homed")); return Task.CompletedTask; }
    private Task OnBeforeFlip(object sender, BeforeMeridianFlipEventArgs e) { Emit(new MountEventPayload("flip-before")); return Task.CompletedTask; }
    private Task OnAfterFlip(object sender, AfterMeridianFlipEventArgs e) { Emit(new MountEventPayload("flip-after")); return Task.CompletedTask; }

    private void Emit(MountEventPayload payload)
    {
        try { _server.Broadcast("mount.event", payload); } catch { }
    }

    private static double? D(double? v) =>
        v is null || double.IsNaN(v.Value) || double.IsInfinity(v.Value) ? null : v;

    public void Dispose() => _events.Dispose();
}
