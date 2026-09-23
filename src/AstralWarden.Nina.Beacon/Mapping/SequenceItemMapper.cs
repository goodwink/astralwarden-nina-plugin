using AstralWarden.Nina.Beacon.Contracts;
using NINA.Astrometry;
using NINA.Sequencer.Container;
using NINA.Sequencer.SequenceItem;

namespace AstralWarden.Nina.Beacon.Mapping;

/// <summary>Maps the advanced sequencer's running items to the wire shape: instruction name plus
/// its container path, and the innermost target context if a DSO container encloses it.</summary>
public static class SequenceItemMapper
{
    /// <summary>
    /// How far up the container chain to walk. The graph belongs to NINA, and this runs on the
    /// 2-second poll thread inside NINA's process: a chain that loops back on itself (a container
    /// reachable from its own parent) walked unbounded pegs a core and grows the path list until
    /// the process dies — on a rig nobody can walk over to. Real sequences nest a handful deep, so
    /// the cap only ever bites on a graph that is already wrong.
    /// </summary>
    public const int MaxContainerDepth = 64;

    public static SequenceStatePayload Map(bool running, IReadOnlyCollection<ISequenceItem> items)
    {
        var infos = new List<SequenceItemInfo>(items.Count);
        string? target = null;
        double? targetRa = null, targetDec = null;

        foreach (var item in items)
        {
            var path = new List<string>();
            var parent = item.Parent;
            for (var depth = 0; parent is not null && depth < MaxContainerDepth; depth++)
            {
                if (!string.IsNullOrWhiteSpace(parent.Name)) path.Add(parent.Name);
                if (target is null && parent is IDeepSkyObjectContainer dso && dso.Target is { } t
                    && !string.IsNullOrWhiteSpace(t.TargetName))
                {
                    target = t.TargetName;
                    var coords = t.InputCoordinates?.Coordinates;
                    if (coords is not null)
                    {
                        targetRa = D(coords.RA);
                        targetDec = D(coords.Dec);
                    }
                }
                parent = parent.Parent;
            }
            path.Reverse();

            infos.Add(new SequenceItemInfo(
                Name: item.Name ?? item.GetType().Name,
                // The CLR type, which is stable and locale-proof. A running CoolCamera or
                // WarmCamera IS a deliberate temperature ramp by definition, and that is the only
                // signal NINA gives a plugin — TempChangeRunning is private on the concrete CameraVM,
                // the cool/warm methods are not evented, and AtTargetTemp is a stateless closeness
                // check. Display names cannot carry this: they are user-editable and localized.
                Type: item.GetType().Name,
                Path: string.Join(" > ", path),
                Status: item.Status.ToString(),
                Attempts: item.Attempts));
        }

        return new SequenceStatePayload(running, infos, target, targetRa, targetDec);
    }

    private static double? D(double v) => double.IsNaN(v) || double.IsInfinity(v) ? null : v;
}
