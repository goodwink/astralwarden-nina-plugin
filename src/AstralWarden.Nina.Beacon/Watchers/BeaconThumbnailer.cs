using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AstralWarden.Nina.Beacon.Watchers;

/// <summary>
/// Encodes a saved sub's display image to a small JPEG for the agent's thumbnail pipeline.
///
/// Mirrors how ninaAPI produces its own thumbnail: it takes <c>ImageSavedEventArgs.Image</c> — the
/// BitmapSource NINA has ALREADY stretched for display — and scale+JPEG-encodes it directly, with no
/// manual auto-stretch (the raw-linear-looks-black problem is solved before the event fires). We do
/// the same, so nothing here needs NINA's Stretch/AutoStretch machinery or the image history.
///
/// Runs inline on NINA's ImageSaved thread (as ninaAPI does), so it is measured to stay cheap
/// (BeaconThumbnailerTests) and is total — a bad/unavailable image yields null, never a throw, so the
/// frame still ships without a thumbnail. The agent re-encodes to its own byte budget, so this only
/// needs to be a reasonable, small source; we cap the longest edge to keep the socket line small.
/// </summary>
public static class BeaconThumbnailer
{
    /// <summary>Longest-edge downscale before encoding — matches the agent encoder's target so its
    /// re-encode is near-passthrough (it never upscales).</summary>
    public const int LongestEdge = 700;
    public const int Quality = 85;

    /// <summary>Base64 JPEG of the downscaled image, or null if there's nothing usable to encode.</summary>
    public static string? EncodeBase64(BitmapSource? image)
    {
        var bytes = Encode(image);
        return bytes is null ? null : System.Convert.ToBase64String(bytes);
    }

    public static byte[]? Encode(BitmapSource? image)
    {
        try
        {
            if (image is null) return null;
            var w = image.PixelWidth;
            var h = image.PixelHeight;
            if (w <= 0 || h <= 0) return null;

            var scale = System.Math.Min(1.0, (double)LongestEdge / System.Math.Max(w, h));
            BitmapSource source = image;
            if (scale < 1.0)
            {
                var scaled = new TransformedBitmap(image, new ScaleTransform(scale, scale));
                if (scaled.CanFreeze) scaled.Freeze();
                source = scaled;
            }

            var encoder = new JpegBitmapEncoder { QualityLevel = Quality };
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return ms.ToArray();
        }
        catch
        {
            // A UI-affine or half-disposed BitmapSource must never fault the save pipeline — the
            // frame ships thumbnail-less, which the agent handles.
            return null;
        }
    }
}
