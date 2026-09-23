using System.Globalization;
using AstralWarden.Nina.Beacon.Contracts;
using Xunit;

namespace AstralWarden.Nina.Beacon.Tests;

public class BeaconJsonTests
{
    /// <summary>
    /// ':' in a custom date format string is the CULTURE'S time separator, not a literal.
    /// The Beacon runs on whatever locale the rig owner installed, and this stamps the `ts` on every
    /// envelope — so without InvariantCulture a whole rig's telemetry can be unparseable.
    /// </summary>
    [Fact]
    public void Timestamps_ignore_the_machine_locale()
    {
        var original = Thread.CurrentThread.CurrentCulture;
        try
        {
            var odd = (CultureInfo)CultureInfo.InvariantCulture.Clone();
            odd.DateTimeFormat.TimeSeparator = ".";
            odd.DateTimeFormat.DateSeparator = "/";
            Thread.CurrentThread.CurrentCulture = odd;

            var ts = BeaconJson.Timestamp(new DateTimeOffset(2026, 7, 22, 3, 14, 5, 123, TimeSpan.Zero));

            Assert.Equal("2026-07-22T03:14:05.123Z", ts);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = original;
        }
    }

    [Fact]
    public void Timestamps_are_normalised_to_utc()
    {
        var ts = BeaconJson.Timestamp(new DateTimeOffset(2026, 7, 22, 5, 14, 5, 0, TimeSpan.FromHours(2)));
        Assert.Equal("2026-07-22T03:14:05.000Z", ts);
    }
}
