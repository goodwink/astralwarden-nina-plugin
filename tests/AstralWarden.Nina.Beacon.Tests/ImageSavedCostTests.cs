using System.Diagnostics;
using AstralWarden.Nina.Beacon.Contracts;
using AstralWarden.Nina.Beacon.Mapping;
using AstralWarden.Nina.Beacon.Watchers;
using NINA.Image.ImageAnalysis;
using Xunit;
using Xunit.Abstractions;

namespace AstralWarden.Nina.Beacon.Tests;

/// <summary>
/// Whatever the ImageSaved handler does is time NINA doesn't spend on the next frame, so
/// this measures the expensive half — star mapping (the reflection accessor runs once per star for
/// the 8×8 grid, plus once per emitted star) and JSON serialization.
///
/// Measured 2026-07-22, i7-14700K, net8.0-windows Release, after warmup:
///   500 → 1.2 ms | 2 000 → 1.9 ms | 5 000 → 3.1 ms | 10 000 → 6.3 ms | 20 000 → 11 ms | 50 000 → 22 ms
/// Cost scales with TOTAL detected stars (the grid sees all of them), not with what we emit — the
/// wire payload is capped at 500 stars either way. 50 000 is the pessimistic bound: a fast
/// wide-field scope on a dense Milky Way field.
///
/// The handler stays INLINE, and the reason is not the old inherited "5 ms budget" (which the tail
/// blows through) — it is:
///  • A rig PC is single-thread slower, but by ~2–4×, not orders of magnitude. Worst case there is
///    ~50–90 ms, once per exposure.
///  • The two risk factors cannot co-occur: 50 000 detected stars requires a long, deep exposure.
///    Short-cadence frames — the only case where per-frame overhead could accumulate — detect few
///    stars. So the tail is self-limiting.
///  • The work is pure CPU over an in-memory list: no I/O, no network, no lock held (audited
///    across every handler), and bounded allocation. It cannot stall for seconds the way a blocking handler
///    could — which is the failure mode that would actually threaten imaging.
///  • Offloading has a real correctness cost for that ~50 ms: a deferred handler would hold
///    references to NINA's star/analysis objects past the event, and would need explicit sequencing
///    to keep image.saved ahead of image.stars.
/// If a rig calibration comes back worse than ~4×, the lever is to cap the GRID's star
/// input (sample down); that bounds the tail without deferring anything.
/// </summary>
public class ImageSavedCostTests
{
    private readonly ITestOutputHelper _output;

    public ImageSavedCostTests(ITestOutputHelper output) => _output = output;

    private sealed class FakePsf
    {
        public double FWHMArcsecs { get; init; }
        public double Eccentricity { get; init; }
        public double ThetaRadians { get; init; }
    }

    private sealed class PsfStar : DetectedStar
    {
        public FakePsf? PSF { get; init; }
    }

    private static List<DetectedStar> Field(int count)
    {
        var random = new Random(42);
        var stars = new List<DetectedStar>(count);
        for (var i = 0; i < count; i++)
        {
            stars.Add(new PsfStar
            {
                Position = new Accord.Point(random.Next(0, 6000), random.Next(0, 4000)),
                HFR = 2 + random.NextDouble(),
                MaxBrightness = random.Next(100, 60000),
                PSF = new FakePsf
                {
                    FWHMArcsecs = 2 + random.NextDouble(),
                    Eccentricity = random.NextDouble(),
                    ThetaRadians = random.NextDouble() * Math.PI,
                },
            });
        }
        return stars;
    }

    private static double MillisecondsPerFrame(int starCount, int iterations)
    {
        var stars = Field(starCount);
        var accessor = HocusFocusProbe.StarAccessor(stars);
        Assert.NotNull(accessor); // otherwise we'd be timing the cheap path by accident

        void Once()
        {
            var payload = StarListMapper.Map(stars, 6000, 4000, accessor, @"C:\img\a.fits", "hocusfocus");
            _ = BeaconJson.Serialize(payload);
        }

        Once(); // JIT + reflection cache warmup
        var stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++) Once();
        stopwatch.Stop();
        return stopwatch.Elapsed.TotalMilliseconds / iterations;
    }

    [Theory]
    [InlineData(500)]
    [InlineData(2000)]
    [InlineData(5000)]
    [InlineData(10_000)]
    [InlineData(20_000)]
    [InlineData(50_000)]
    public void Star_mapping_cost_is_recorded(int starCount)
    {
        var ms = MillisecondsPerFrame(starCount, iterations: 20);
        _output.WriteLine($"{starCount} stars: {ms:0.00} ms per frame");
    }

    /// <summary>
    /// The reference workload for calibrating a rig PC: a fixed, allocation-light, purely
    /// single-threaded CPU loop. Run it here and on the rig PC to measure that machine's real
    /// single-thread factor instead of guessing it, then multiply the numbers above.
    /// </summary>
    [Fact]
    public void Single_thread_calibration_reference()
    {
        static double Work()
        {
            var acc = 0.0;
            for (var i = 1; i < 20_000_000; i++) acc += Math.Sqrt(i) / i;
            return acc;
        }

        Work(); // warmup
        var stopwatch = Stopwatch.StartNew();
        var result = Work();
        stopwatch.Stop();
        _output.WriteLine($"calibration: {stopwatch.Elapsed.TotalMilliseconds:0} ms (checksum {result:0.000})");
    }

    /// <summary>
    /// A scaling-shape guard, not a stopwatch: at the pessimistic bound the measured cost is ~22 ms,
    /// so a threshold this loose never flakes on a loaded CI box, but an accidental O(n²) — a nested
    /// scan over the star list, or losing the OrderBy/Take partial-sort path — lands in seconds and
    /// trips it. That is the change that would actually invalidate the stays-inline decision.
    /// </summary>
    [Fact]
    public void Mapping_cost_stays_sub_quadratic_at_the_pessimistic_star_count()
    {
        var ms = MillisecondsPerFrame(50_000, iterations: 5);
        _output.WriteLine($"50000 stars: {ms:0.00} ms per frame");
        Assert.True(ms < 250, $"star mapping cost regressed to {ms:0.00} ms/frame at 50k stars — " +
                              "the scaling shape changed; re-run the ImageSaved cost analysis");
    }
}
