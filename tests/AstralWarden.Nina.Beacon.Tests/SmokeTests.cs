using Xunit;

namespace AstralWarden.Nina.Beacon.Tests;

public class SmokeTests
{
    [Fact]
    public void Beacon_assembly_loads()
    {
        Assert.NotNull(typeof(Beacon).Assembly.GetName().Version);
    }
}
