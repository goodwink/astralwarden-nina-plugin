using System.Reflection;
using AstralWarden.Nina.Beacon.Mapping;
using NINA.Astrometry;
using NINA.Core.Model;
using NINA.Sequencer.Container;
using NINA.Sequencer.SequenceItem;
using NSubstitute;
using Xunit;

namespace AstralWarden.Nina.Beacon.Tests;

/// <summary>
/// The `sequence.state` item mapping. What it owes its consumers, from the mapper's own contract
/// ("instruction name plus its container path, and the innermost target context if a DSO container
/// encloses it"), ch.4's envelope invariants, and the fact that it runs on the 2-second poll thread
/// inside NINA:
///
///  • the path reads outermost → innermost, which is how a person reads a sequence tree;
///  • `type` is the CLR type name, not the display name — display names are user-editable and
///    localized, and the cloud's cooler-ramp detector matches on the type (ch.4 landmine 7);
///  • the target is the INNERMOST enclosing DSO container's, because that is the one actually being
///    imaged when targets are nested;
///  • absence is null, never a sentinel — NaN coordinates must not reach the wire as numbers, while
///    a real 0 must survive (ch.4 envelope invariants);
///  • it must always finish. It walks an object graph NINA owns, on the thread that polls every
///    2 seconds, and it appends to a list as it goes.
/// </summary>
public class SequenceItemMapperTests
{
    private sealed class TakeExposure : SequenceItem
    {
        public override Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) =>
            Task.CompletedTask;

        public override object Clone() => new TakeExposure();
    }

    private static ISequenceContainer Container(string? name, ISequenceContainer? parent = null)
    {
        var container = Substitute.For<ISequenceContainer>();
        container.Name.Returns(name);
        container.Parent.Returns(parent);
        return container;
    }

    private static IDeepSkyObjectContainer TargetContainer(
        string name, string targetName, Coordinates? coordinates, ISequenceContainer? parent = null)
    {
        var target = new InputTarget(Angle.ByDegree(52), Angle.ByDegree(-1), null)
        {
            TargetName = targetName,
        };
        if (coordinates is not null) PlaceCoordinates(target, coordinates);

        var container = Substitute.For<IDeepSkyObjectContainer>();
        container.Name.Returns(name);
        container.Parent.Returns(parent);
        container.Target.Returns(target);
        return container;
    }

    // InputTarget's InputCoordinates setter walks into NINA's astrometry (MoonInfo → NOVAS), which
    // P/Invokes a native library only a real NINA install has. The mapper just reads the value back,
    // so place it directly rather than dragging that dependency into the suite.
    private static void PlaceCoordinates(InputTarget target, Coordinates coordinates)
    {
        var field = typeof(InputTarget)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(f => f.FieldType == typeof(InputCoordinates));
        field.SetValue(target, new InputCoordinates(coordinates));
    }

    private static ISequenceItem Leaf(ISequenceContainer parent)
    {
        var item = new TakeExposure();
        item.AttachNewParent(parent);
        return item;
    }

    [Fact]
    public void The_path_names_the_enclosing_containers_outermost_first()
    {
        var root = Container("Sequence");
        var area = Container("Targets", root);
        var target = Container("M 31", area);

        var state = SequenceItemMapper.Map(true, new[] { Leaf(target) });

        Assert.Equal("Sequence > Targets > M 31", Assert.Single(state.Items).Path);
    }

    [Fact]
    public void A_container_with_no_name_is_left_out_of_the_path()
    {
        // NINA's synthetic wrapper containers (and anything the user blanked) have no useful name;
        // "Sequence >  > M 31" would be worse than omitting them.
        var root = Container("Sequence");
        var anonymous = Container("   ", root);
        var target = Container("M 31", anonymous);

        var state = SequenceItemMapper.Map(true, new[] { Leaf(target) });

        Assert.Equal("Sequence > M 31", Assert.Single(state.Items).Path);
    }

    [Fact]
    public void The_target_is_the_innermost_enclosing_dso_container()
    {
        // A DSO container can sit inside another (a template dropped into a target container). The
        // one being imaged is the innermost — reporting the outer one would attribute every frame
        // of the night to the wrong object.
        var outer = TargetContainer("outer", "NGC 7000",
            new Coordinates(20.9, 44.5, Epoch.J2000, Coordinates.RAType.Hours));
        var inner = TargetContainer("inner", "M 31",
            new Coordinates(0.7, 41.2, Epoch.J2000, Coordinates.RAType.Hours), outer);

        var state = SequenceItemMapper.Map(true, new[] { Leaf(inner) });

        Assert.Equal("M 31", state.Target);
        Assert.Equal(0.7, state.TargetRa!.Value, 3);
        Assert.Equal(41.2, state.TargetDec!.Value, 3);
    }

    [Fact]
    public void No_enclosing_dso_container_means_no_target()
    {
        var state = SequenceItemMapper.Map(true, new[] { Leaf(Container("Start of sequence")) });

        var item = Assert.Single(state.Items);
        Assert.Equal("Start of sequence", item.Path);
        Assert.Null(state.Target);
        Assert.Null(state.TargetRa);
        Assert.Null(state.TargetDec);
    }

    [Fact]
    public void Unreadable_coordinates_are_null_while_a_real_zero_survives()
    {
        // ch.4: absence is always null, never a sentinel. A NaN would serialize as a number the
        // cloud would then plot; RA 0 / Dec 0 is a real place in the sky.
        var nan = TargetContainer("t", "M 31",
            new Coordinates(double.NaN, double.NaN, Epoch.J2000, Coordinates.RAType.Hours));
        var nanState = SequenceItemMapper.Map(true, new[] { Leaf(nan) });
        Assert.Equal("M 31", nanState.Target);
        Assert.Null(nanState.TargetRa);
        Assert.Null(nanState.TargetDec);

        var origin = TargetContainer("t", "origin",
            new Coordinates(0, 0, Epoch.J2000, Coordinates.RAType.Hours));
        var originState = SequenceItemMapper.Map(true, new[] { Leaf(origin) });
        Assert.Equal(0.0, originState.TargetRa);
        Assert.Equal(0.0, originState.TargetDec);
    }

    [Fact]
    public void The_reported_type_is_the_clr_type_and_the_name_is_the_display_name()
    {
        // ch.4 landmine 7: display names are user-editable and localized, so the type is what
        // downstream detectors match on. Both ship; they are not the same field.
        var item = new TakeExposure { Name = "Grab a sub" };
        item.AttachNewParent(Container("Sequence"));

        var mapped = Assert.Single(SequenceItemMapper.Map(true, new[] { (ISequenceItem)item }).Items);

        Assert.Equal("TakeExposure", mapped.Type);
        Assert.Equal("Grab a sub", mapped.Name);
    }

    [Fact]
    public void An_unnamed_instruction_falls_back_to_its_type_name()
    {
        var item = new TakeExposure { Name = null! };
        item.AttachNewParent(Container("Sequence"));

        var mapped = Assert.Single(SequenceItemMapper.Map(true, new[] { (ISequenceItem)item }).Items);

        Assert.Equal("TakeExposure", mapped.Name);
    }

    [Fact]
    public void Not_running_maps_to_an_empty_state_rather_than_a_throw()
    {
        var state = SequenceItemMapper.Map(false, Array.Empty<ISequenceItem>());

        Assert.False(state.Running);
        Assert.Empty(state.Items);
        Assert.Null(state.Target);
    }

    [Fact]
    public async Task A_parent_chain_that_never_ends_still_returns()
    {
        // The container graph belongs to NINA, not to us, and this runs on the 2-second poll thread
        // inside NINA's process on a rig nobody can walk over to, where it must never interfere with imaging.
        // A chain that loops back on itself must cost a bounded walk, not a pegged core and a list
        // that grows until the process dies.
        var loop = Substitute.For<ISequenceContainer>();
        loop.Name.Returns("looping container");
        loop.Parent.Returns(loop);

        var mapped = Task.Run(() => SequenceItemMapper.Map(true, new[] { Leaf(loop) }));

        var finished = await Task.WhenAny(mapped, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(mapped, finished);
        var item = Assert.Single((await mapped).Items);
        Assert.True(item.Path.Length < 10_000, $"path grew to {item.Path.Length} characters");
    }

    [Fact]
    public void A_deep_chain_is_walked_to_a_bounded_depth()
    {
        // The same guard, stated as a number so it is visible in the payload: the path stops at the
        // cap instead of growing with whatever NINA hands us.
        ISequenceContainer chain = Container("c");
        for (var i = 0; i < SequenceItemMapper.MaxContainerDepth * 2; i++) chain = Container("c", chain);

        var item = Assert.Single(SequenceItemMapper.Map(true, new[] { Leaf(chain) }).Items);

        Assert.Equal(SequenceItemMapper.MaxContainerDepth, item.Path.Split(" > ").Length);
    }
}
