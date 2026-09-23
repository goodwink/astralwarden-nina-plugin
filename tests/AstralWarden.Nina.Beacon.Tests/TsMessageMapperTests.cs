using AstralWarden.Nina.Beacon.Contracts;
using AstralWarden.Nina.Beacon.Mapping;
using NINA.Astrometry;
using Xunit;

namespace AstralWarden.Nina.Beacon.Tests;

public class TsMessageMapperTests
{
    private static Coordinates Coords(double raHours, double decDeg) =>
        new(Angle.ByHours(raHours), Angle.ByDegree(decDeg), Epoch.J2000);

    [Fact]
    public void Wait_start_maps_next_target_and_countdown()
    {
        var headers = new Dictionary<string, object>
        {
            ["SecondsUntilNextTarget"] = 754,
            ["ProjectName"] = "Broadband",
            ["TargetName"] = "M31",
            ["Coordinates"] = Coords(0.712, 41.27),
            ["Rotation"] = 15.0,
        };

        var (type, payload) = TsMessageMapper.Map(TsMessageMapper.WaitStart, DateTime.Now, headers)!.Value;

        Assert.Equal("ts.waitstart", type);
        var p = (TsWaitStartPayload)payload;
        Assert.Equal("M31", p.Target);
        Assert.Equal("Broadband", p.Project);
        Assert.Equal(754, p.SecondsUntilNextTarget);
        Assert.Equal(0.712, p.Ra!.Value, 3);
        Assert.Equal(41.27, p.Dec!.Value, 2);
    }

    [Fact]
    public void Target_start_and_new_target_start_share_a_shape_with_a_flag()
    {
        var headers = new Dictionary<string, object>
        {
            ["ProjectName"] = "Broadband",
            ["TargetName"] = "M31",
            ["Coordinates"] = Coords(0.712, 41.27),
            ["Rotation"] = 15.0,
            ["ExposureFilterName"] = "Ha",
            ["ExposureLength"] = 600.0,
            ["ExposureGain"] = "100",
            ["ExposureOffset"] = "(camera)",
            ["ExposureBinning"] = "1x1",
        };

        var normal = (TsTargetStartPayload)TsMessageMapper.Map(TsMessageMapper.TargetStart, "M31", headers)!.Value.Payload;
        var fresh = (TsTargetStartPayload)TsMessageMapper.Map(TsMessageMapper.NewTargetStart, "M31", headers)!.Value.Payload;

        Assert.False(normal.NewTarget);
        Assert.True(fresh.NewTarget);
        Assert.Equal("Ha", normal.Filter);
        Assert.Equal(600.0, normal.ExposureSec);
        Assert.Equal("(camera)", normal.Offset);
    }

    [Fact]
    public void Target_complete_falls_back_to_content_for_the_name()
    {
        var payload = (TsTargetCompletePayload)TsMessageMapper.Map(
            TsMessageMapper.TargetComplete, "M31",
            new Dictionary<string, object> { ["ProjectName"] = "Broadband" })!.Value.Payload;

        Assert.Equal("M31", payload.Target); // TS omits the TargetName header on this topic
        Assert.Equal("Broadband", payload.Project);
    }

    [Fact]
    public void Container_stopped_carries_the_stop_time()
    {
        var payload = (TsContainerStoppedPayload)TsMessageMapper.Map(
            TsMessageMapper.ContainerStopped, "Container Stopped",
            new Dictionary<string, object> { ["StoppedAt"] = new DateTime(2026, 7, 21, 3, 0, 0, DateTimeKind.Utc) })!.Value.Payload;

        Assert.Equal("2026-07-21T03:00:00.000Z", payload.StoppedAt);
    }

    [Fact]
    public void Unknown_topic_and_missing_headers_are_harmless()
    {
        Assert.Null(TsMessageMapper.Map("SomeOtherPlugin-Topic", null, null));
        var (_, payload) = TsMessageMapper.Map(TsMessageMapper.WaitStart, null, null)!.Value;
        var p = (TsWaitStartPayload)payload;
        Assert.Null(p.Target);
        Assert.Null(p.Ra);
    }
}
