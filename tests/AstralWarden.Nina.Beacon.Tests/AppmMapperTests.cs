using System.Text.Json;
using AstralWarden.Nina.Beacon.Mapping;
using Xunit;

namespace AstralWarden.Nina.Beacon.Tests;

public class AppmMapperTests
{
    [Fact]
    public void Maps_points_and_derives_rms_from_residuals()
    {
        var doc = JsonDocument.Parse("""
            {"MappingPoints":[
              {"Num":1,"HourAngle":-2.5,"Dec":40.0,"RaDelta":3.0,"DecDelta":4.0,"Side":"East","Status":"Solved"},
              {"Num":2,"HourAngle":1.5,"Dec":10.0,"RaDelta":-3.0,"DecDelta":-4.0,"Side":"West","Status":"Solved"}
            ]}
            """);

        var model = AppmMapper.Map(doc.RootElement, "Complete");

        Assert.Equal(2, model.PointCount);
        Assert.Equal("Complete", model.RunStatus);
        Assert.Equal(3.0, model.RaRms);
        Assert.Equal(4.0, model.DecRms);
        Assert.Equal(5.0, model.TotalRms);
        Assert.Equal(-2.5, model.Points[0].Ha);
        Assert.Equal("East", model.Points[0].Side);
    }

    [Fact]
    public void Accepts_a_bare_array_and_case_insensitive_fields()
    {
        var doc = JsonDocument.Parse("""[{"hourangle":1.0,"declination":20.0,"radelta":1.0,"decdelta":1.0}]""");
        var model = AppmMapper.Map(doc.RootElement, null);
        Assert.Equal(1, model.PointCount);
        Assert.Equal(20.0, model.Points[0].Dec);
        Assert.NotNull(model.TotalRms);
    }

    [Fact]
    public void Unsolved_points_are_kept_but_excluded_from_rms()
    {
        var doc = JsonDocument.Parse("""
            [{"HourAngle":1.0,"Dec":20.0,"Status":"Pending"},
             {"HourAngle":2.0,"Dec":30.0,"RaDelta":2.0,"DecDelta":2.0,"Status":"Solved"}]
            """);
        var model = AppmMapper.Map(doc.RootElement, "Running");
        Assert.Equal(2, model.PointCount);
        Assert.Equal(2.0, model.RaRms);
    }

    [Fact]
    public void Garbage_shapes_yield_an_empty_model()
    {
        var model = AppmMapper.Map(JsonDocument.Parse("""{"foo":"bar"}""").RootElement, null);
        Assert.Equal(0, model.PointCount);
        Assert.Null(model.TotalRms);
    }
}
