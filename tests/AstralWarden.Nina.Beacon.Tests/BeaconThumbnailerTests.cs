using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AstralWarden.Nina.Beacon.Watchers;
using Xunit;
using Xunit.Abstractions;

namespace AstralWarden.Nina.Beacon.Tests;

/// <summary>
/// The thumbnail is encoded inline on NINA's ImageSaved thread (as ninaAPI does), so it must
/// stay cheap (see ImageSavedCostTests for the handler budget). This builds a full-sensor BitmapSource and measures the
/// downscale+JPEG encode. It also confirms the output is a decodable JPEG and that a null/degenerate
/// image yields null rather than throwing.
/// </summary>
public class BeaconThumbnailerTests
{
    private readonly ITestOutputHelper _output;
    public BeaconThumbnailerTests(ITestOutputHelper output) => _output = output;

    // A ~24 MP frame (6072×4042, a common CMOS sensor). Freeze it so it behaves like NINA's frozen
    // display BitmapSource — accessible off any thread, which is what makes the inline encode safe.
    private static BitmapSource FullSensorImage()
    {
        const int w = 6072, h = 4042;
        var stride = w * 3;
        var pixels = new byte[h * stride];
        var rand = new System.Random(1);
        rand.NextBytes(pixels); // worst case for JPEG: noise doesn't compress
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Rgb24, null, pixels, stride);
        bmp.Freeze();
        return bmp;
    }

    [Fact]
    public void Encodes_a_downscaled_decodable_jpeg()
    {
        var bytes = BeaconThumbnailer.Encode(FullSensorImage());
        Assert.NotNull(bytes);

        // It decodes as an image and its longest edge is the configured cap (not the source's 6072).
        using var ms = new System.IO.MemoryStream(bytes!);
        var decoded = new JpegBitmapDecoder(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        Assert.Equal(BeaconThumbnailer.LongestEdge, System.Math.Max(decoded.PixelWidth, decoded.PixelHeight));
        _output.WriteLine($"encoded {bytes!.Length / 1024} KB at {decoded.PixelWidth}x{decoded.PixelHeight}");
    }

    /// <summary>
    /// A scaling-shape tripwire, not a stopwatch — same shape as the sibling star-mapping guard in
    /// ImageSavedCostTests.
    ///
    /// Measured ~14 ms on the dev box for a 24 MP noise frame (worst case; real stretched subs
    /// compress better). Runs inline on NINA's ImageSaved thread alongside the ~4 ms star mapping;
    /// on the rig it scales by the ~3.3× single-thread factor to ~48 ms — negligible against
    /// minutes-apart light frames.
    ///
    /// The bound was 60 ms, which is 4× the dev measurement and NOT enough headroom for a shared CI
    /// runner: it went red at 74.1 ms and green on the rerun, which teaches people to re-run red
    /// rather than read it. What this test can honestly catch is an order-of-magnitude regression —
    /// encoding the full-resolution frame, or an added per-pixel pass — so the bound is now ~20× the
    /// dev measurement. Losing the downscale itself is caught deterministically by
    /// <see cref="Encodes_a_downscaled_decodable_jpeg"/>, which asserts the output dimensions and
    /// does not look at the clock at all; the real budget lives in the printed measurement.
    /// </summary>
    [Fact]
    public void Inline_encode_does_not_regress_by_an_order_of_magnitude()
    {
        var image = FullSensorImage();
        BeaconThumbnailer.Encode(image); // JIT + warm

        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 10; i++) BeaconThumbnailer.Encode(image);
        sw.Stop();
        var ms = sw.Elapsed.TotalMilliseconds / 10;
        _output.WriteLine($"downscale+JPEG encode of a 24 MP frame: {ms:0.0} ms");

        Assert.True(ms < 300, $"inline thumbnail encode regressed to {ms:0.0} ms/frame — that is not " +
                              "CI jitter; re-check that the downscale still happens before the encode");
    }

    /// <summary>
    /// LongestEdge is a CAP, not a target: the doc-comment pairs it with "the agent re-encodes to
    /// its own byte budget ... it never upscales". A frame that already fits must therefore come
    /// back at its own size — not upscaled, and not dropped.
    ///
    /// Every other test here feeds a 24 MP sensor, so the whole already-small branch was unexercised
    /// while a light frame smaller than 700 px is ordinary: a bin-4 sub off a small sensor, a
    /// cropped ROI, or the guide camera. Those rigs would have shipped no thumbnail at all.
    /// </summary>
    [Fact]
    public void A_frame_that_already_fits_the_cap_is_encoded_at_its_own_size()
    {
        const int w = 320, h = 240;
        var stride = w * 3;
        var pixels = new byte[h * stride];
        new System.Random(2).NextBytes(pixels);
        var small = BitmapSource.Create(w, h, 96, 96, PixelFormats.Rgb24, null, pixels, stride);
        small.Freeze();

        var bytes = BeaconThumbnailer.Encode(small);

        Assert.NotNull(bytes);
        using var ms = new System.IO.MemoryStream(bytes!);
        var decoded = new JpegBitmapDecoder(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        Assert.Equal(w, decoded.PixelWidth);
        Assert.Equal(h, decoded.PixelHeight);
    }

    [Fact]
    public void A_null_or_empty_image_yields_null_not_a_throw()
    {
        Assert.Null(BeaconThumbnailer.Encode(null));
        Assert.Null(BeaconThumbnailer.EncodeBase64(null));
    }
}
