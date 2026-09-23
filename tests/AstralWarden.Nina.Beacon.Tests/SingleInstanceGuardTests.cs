using AstralWarden.Nina.Beacon.Server;
using Xunit;

namespace AstralWarden.Nina.Beacon.Tests;

/// <summary>
/// One PC can run several NINA instances (one per camera or profile), but the agent monitors one
/// rig per PC, and the Beacon's socket is one fixed port. So the first NINA instance to load the
/// Beacon is the one monitored, for as long as it runs. A later instance must stay off for its
/// whole session. If it could take over when the first one closed, the agent would silently start
/// receiving a different rig's devices and frames under the same rig.
/// </summary>
public class SingleInstanceGuardTests
{
    // A unique name per test, so tests never contend with a real NINA or with each other.
    private static string Name() => $"Local\\AstralWardenBeaconTest-{Guid.NewGuid():N}";

    [Fact]
    public void The_first_claim_wins_and_a_second_is_refused()
    {
        var name = Name();
        using var first = SingleInstanceGuard.TryClaim(name);
        var second = SingleInstanceGuard.TryClaim(name);

        Assert.NotNull(first);
        Assert.Null(second);
    }

    [Fact]
    public void Once_the_winner_releases_a_new_instance_can_claim()
    {
        // NINA closed (or crashed) and was started again: the fresh instance is monitored.
        var name = Name();
        var first = SingleInstanceGuard.TryClaim(name);
        Assert.NotNull(first);
        first!.Dispose();

        using var next = SingleInstanceGuard.TryClaim(name);
        Assert.NotNull(next);
    }

    [Fact]
    public void A_refused_instance_holds_nothing_so_it_cannot_block_or_inherit_the_claim()
    {
        // B was refused while A ran. When A closes, the claim must not stay alive through anything
        // B kept, because the next NINA to start should be monitored.
        var name = Name();
        var a = SingleInstanceGuard.TryClaim(name);
        Assert.NotNull(a);
        Assert.Null(SingleInstanceGuard.TryClaim(name)); // B, refused

        a!.Dispose();

        using var c = SingleInstanceGuard.TryClaim(name);
        Assert.NotNull(c);
    }

    [Fact]
    public void Releasing_twice_is_harmless()
    {
        var guard = SingleInstanceGuard.TryClaim(Name());
        Assert.NotNull(guard);
        guard!.Dispose();
        guard.Dispose();
    }
}
