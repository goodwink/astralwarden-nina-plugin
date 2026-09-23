using AstralWarden.Nina.Beacon.Mapping;
using NINA.Image.ImageAnalysis;
using Xunit;

namespace AstralWarden.Nina.Beacon.Tests;

public class StarListMapperTests
{
    private static DetectedStar Star(double x, double y, double hfr, double brightness = 1000) =>
        new() { Position = new Accord.Point((float)x, (float)y), HFR = hfr, MaxBrightness = brightness };

    /// <summary>
    /// HFR, position and brightness come straight off the detector with no upstream NaN
    /// guard (unlike the PSF extras, which are nulled by the probe). The serializer is configured
    /// to write NaN as the *string* "NaN" rather than throw — a deliberate never-throw-into-NINA
    /// choice — so an unguarded value ships a string in a numeric field instead of failing loudly.
    /// </summary>
    [Fact]
    public void Non_finite_stars_are_dropped_rather_than_serialised_as_NaN()
    {
        var stars = new List<DetectedStar>
        {
            Star(100, 200, 2.0, brightness: 5000),
            new() { Position = new Accord.Point(float.NaN, 100), HFR = 2.0, MaxBrightness = 9000 },
            new() { Position = new Accord.Point(300, float.NaN), HFR = 2.0, MaxBrightness = 9000 },
            new() { Position = new Accord.Point(400, 400), HFR = double.NaN, MaxBrightness = 9000 },
            new() { Position = new Accord.Point(500, 500), HFR = double.PositiveInfinity, MaxBrightness = 9000 },
        };

        var payload = StarListMapper.Map(stars, 4000, 3000, null, null, "hocusfocus");

        var star = Assert.Single(payload.Stars);
        Assert.Equal(100, star.X);
        Assert.Equal(5, payload.StarCount); // the raw count stays honest about what was detected

        // No cell median may be poisoned by the dropped stars.
        foreach (var cell in payload.Grid.SelectMany(row => row).Where(c => c is not null))
        {
            Assert.False(cell!.MedianHfr is { } v && (double.IsNaN(v) || double.IsInfinity(v)));
        }
    }

    [Fact]
    public void Sensor_dimension_fallback_survives_a_non_finite_position()
    {
        // Width/height unknown → dims are derived from the star extent; a NaN there used to make
        // (int)Math.Ceiling(NaN) = int.MinValue and hand the grid a negative width.
        var payload = StarListMapper.Map(
            [new DetectedStar { Position = new Accord.Point(float.NaN, float.NaN), HFR = 2.0 }],
            null, null, null, null, null);

        Assert.True(payload.Width >= 1);
        Assert.True(payload.Height >= 1);
        Assert.Empty(payload.Stars);
    }

    [Fact]
    public void Maps_stars_with_positions_and_hfr()
    {
        var payload = StarListMapper.Map(
            [Star(100, 200, 2.345)], 4000, 3000, null, @"C:\img\a.fits", "nina");

        Assert.Equal(4000, payload.Width);
        Assert.Equal(1, payload.StarCount);
        var star = Assert.Single(payload.Stars);
        Assert.Equal(100, star.X);
        Assert.Equal(200, star.Y);
        Assert.Equal(2.345, star.Hfr);
        Assert.Null(star.Fwhm); // no extras accessor → stock-detector shape
    }

    [Fact]
    public void Caps_the_raw_list_at_the_brightest_500_but_grids_everything()
    {
        var stars = Enumerable.Range(0, 800)
            .Select(i => Star(i % 4000, i / 4000 * 100, 2.0, brightness: i))
            .ToList();

        var payload = StarListMapper.Map(stars, 4000, 3000, null, null, null);

        Assert.Equal(500, payload.Stars.Count);
        Assert.Equal(800, payload.StarCount);
        // Brightest kept: the dimmest retained star is brighter than every dropped one.
        Assert.True(payload.Stars.Min(s => s.Brightness) >= 300);
        // Grid still covers all 800.
        var gridCount = payload.Grid.SelectMany(r => r).Where(c => c is not null).Sum(c => c!.Count);
        Assert.Equal(800, gridCount);
    }

    [Fact]
    public void Grid_cells_hold_per_cell_medians()
    {
        // Two clusters: sharp stars top-left, bloated stars bottom-right (classic tilt shape).
        var stars = new List<DetectedStar>
        {
            Star(10, 10, 1.8), Star(20, 20, 2.0), Star(30, 30, 2.2),   // cell (0,0) median 2.0
            Star(3990, 2990, 4.0), Star(3980, 2980, 4.4),               // cell (7,7) median 4.2
        };

        var payload = StarListMapper.Map(stars, 4000, 3000, null, null, null);

        Assert.Equal(2.0, payload.Grid[0][0]!.MedianHfr);
        Assert.Equal(3, payload.Grid[0][0]!.Count);
        Assert.Equal(4.2, payload.Grid[7][7]!.MedianHfr);
        Assert.Null(payload.Grid[3][3]); // empty cell
    }

    [Fact]
    public void Extras_accessor_populates_fwhm_eccentricity_and_theta()
    {
        var payload = StarListMapper.Map(
            [Star(10, 10, 2.0)], 100, 100, _ => (3.1, 0.45, 1.571), null, "hocusfocus");

        var star = Assert.Single(payload.Stars);
        Assert.Equal(3.1, star.Fwhm);
        Assert.Equal(0.45, star.Ecc);
        Assert.Equal(1.571, star.Theta);
        Assert.Equal(3.1, payload.Grid[0][0]!.MedianFwhm);
        Assert.Equal(0.45, payload.Grid[0][0]!.MedianEcc);
    }

    [Fact]
    public void Falls_back_to_star_extent_when_sensor_dims_unknown()
    {
        var payload = StarListMapper.Map(
            [Star(10, 10, 2.0), Star(990, 490, 2.0)], null, null, null, null, null);

        Assert.True(payload.Width >= 990);
        Assert.True(payload.Height >= 490);
        Assert.NotNull(payload.Grid[0][0]);
        Assert.NotNull(payload.Grid[7][7]);
    }

    [Fact]
    public void Empty_star_list_produces_an_empty_grid_without_throwing()
    {
        var payload = StarListMapper.Map([], 4000, 3000, null, null, null);
        Assert.Empty(payload.Stars);
        Assert.Equal(0, payload.StarCount);
        Assert.All(payload.Grid.SelectMany(r => r), c => Assert.Null(c));
    }
}
