using System.Collections.Generic;
using System.Collections.Immutable;
using AstralWarden.Nina.Beacon.Server;
using AstralWarden.Nina.Beacon.Watchers;
using NINA.Image.ImageAnalysis;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.WPF.Base.Interfaces.Mediator;
using NSubstitute;
using Xunit;

namespace AstralWarden.Nina.Beacon.Tests;

/// <summary>
/// The ImageSaved handler — the hottest wiring in the plugin, running on NINA's save thread. What
/// ch.4's message table promises about the two messages it produces:
///
///  • `image.saved` per saved exposure, always;
///  • `image.stars` "per light frame", carrying "≤500 brightest stars with PSF geometry + the 8×8
///    median grid" and pairing to its frame by `path`.
///
/// A frame the detector found nothing in has no star geometry to carry. Shipping one anyway makes
/// the bridge compute star reductions and an 8×8 grid over an empty list and ship them as if they
/// were measurements — a quality record for a frame nobody measured.
/// </summary>
public class ImageWatcherTests
{
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(300);

    private sealed class FakeAnalysis : IStarDetectionAnalysis
    {
        public double HFR { get; set; }
        public double HFRStDev { get; set; }
        public int DetectedStars { get; set; }
        public List<DetectedStar> StarList { get; set; } = new();
#pragma warning disable CS0067 // required by the interface; nothing here raises it
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
#pragma warning restore CS0067
    }

    private static ImageSavedEventArgs Frame(string imageType, List<DetectedStar>? stars)
    {
        var args = new ImageSavedEventArgs
        {
            PathToImage = new Uri(@"C:\images\M42\LIGHT_001.fits"),
            MetaData = new ImageMetaData { Image = { ImageType = imageType, ExposureTime = 300 } },
        };
        if (stars is not null) args.StarDetectionAnalysis = new FakeAnalysis { StarList = stars, DetectedStars = stars.Count };
        return args;
    }

    private static async Task<(BeaconServer Server, WireReader Reader, IImageSaveMediator Save)> WiredAsync()
    {
        var server = new BeaconServer(port: 0);
        server.Start();
        var reader = new WireReader();
        await reader.ConnectAsync(server);
        var save = Substitute.For<IImageSaveMediator>();
        return (server, reader, save);
    }

    private static async Task WaitForSubscriptionAsync(object substitute)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (substitute.ReceivedCalls().Any(c => c.GetMethodInfo().Name == "add_ImageSaved")) return;
            await Task.Delay(5);
        }
        Assert.Fail("ImageSaved was never subscribed");
    }

    [Fact]
    public async Task A_light_frame_with_detected_stars_ships_both_messages_paired_by_path()
    {
        var (server, reader, save) = await WiredAsync();
        using var watcher = new ImageWatcher(save, Substitute.For<ICameraMediator>(), server);
        await WaitForSubscriptionAsync(save);

        var stars = new List<DetectedStar>
        {
            new() { Position = new Accord.Point(100, 200), HFR = 2.4, MaxBrightness = 20000 },
        };
        save.ImageSaved += Raise.Event<EventHandler<ImageSavedEventArgs>>(this, Frame("LIGHT", stars));
        await Task.Delay(Settle);

        Assert.Equal(new[] { "image.saved", "image.stars" }, reader.Types);
        var paths = reader.Envelopes
            .Select(e => e.GetProperty("payload").GetProperty("path").GetString())
            .Distinct()
            .ToArray();
        Assert.Single(paths);

        reader.Dispose();
        server.Dispose();
    }

    private sealed class CountingAnalysis : IStarDetectionAnalysis
    {
        private readonly List<DetectedStar> _stars;
        public CountingAnalysis(List<DetectedStar> stars) => _stars = stars;
        public int StarListReads;
        public double HFR { get; set; }
        public double HFRStDev { get; set; }
        public int DetectedStars { get; set; }
        public List<DetectedStar> StarList
        {
            get { Interlocked.Increment(ref StarListReads); return _stars; }
            set { }
        }
#pragma warning disable CS0067 // required by the interface; nothing here raises it
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
#pragma warning restore CS0067
    }

    [Fact]
    public async Task With_no_one_connected_a_saved_frame_costs_nina_nothing()
    {
        // The handler runs on NINA's image-save thread. Its work (thumbnail encode, star mapping,
        // serialization) exists only to be sent; with no reader connected (the agent not installed
        // yet, or restarting) it would be spent on every frame for nothing.
        var server = new BeaconServer(port: 0);
        server.Start();
        var save = Substitute.For<IImageSaveMediator>();
        var camera = Substitute.For<ICameraMediator>();
        using var watcher = new ImageWatcher(save, camera, server);
        await WaitForSubscriptionAsync(save);

        var stars = new List<DetectedStar>
        {
            new() { Position = new Accord.Point(100, 200), HFR = 2.4, MaxBrightness = 20000 },
        };
        var analysis = new CountingAnalysis(stars) { DetectedStars = 1 };
        var frame = Frame("LIGHT", null);
        frame.StarDetectionAnalysis = analysis;

        save.ImageSaved += Raise.Event<EventHandler<ImageSavedEventArgs>>(this, frame);
        await Task.Delay(Settle);

        Assert.Equal(0, Volatile.Read(ref analysis.StarListReads));
        Assert.DoesNotContain(camera.ReceivedCalls(), c => c.GetMethodInfo().Name == "GetInfo");

        // ...and it is a gate, not a switch: once a reader connects, frames flow again.
        var reader = new WireReader();
        await reader.ConnectAsync(server);
        save.ImageSaved += Raise.Event<EventHandler<ImageSavedEventArgs>>(this, frame);
        await Task.Delay(Settle);
        Assert.Contains("image.saved", reader.Types);

        reader.Dispose();
        server.Dispose();
    }

    [Fact]
    public async Task A_camera_driver_that_throws_costs_the_sensor_dimensions_not_the_star_message()
    {
        // The camera is consulted only to size the 8×8 grid, and ASCOM drivers throw: a device that
        // has just dropped off the USB bus faults GetInfo. Fault isolation says one brittle source is
        // isolated rather than cascading, and the cascade available here is total — this handler
        // runs on NINA's ImageSaved thread, so losing it loses ALL image telemetry for the night.
        // StarListMapper already falls back to the stars' own extent when the dimensions are
        // unknown, so the frame still carries its geometry; only the field size is lost.
        var (server, reader, save) = await WiredAsync();
        var camera = Substitute.For<ICameraMediator>();
        camera.GetInfo().Returns(_ => throw new InvalidOperationException("driver disconnected"));
        using var watcher = new ImageWatcher(save, camera, server);
        await WaitForSubscriptionAsync(save);

        var stars = new List<DetectedStar>
        {
            new() { Position = new Accord.Point(100, 200), HFR = 2.4, MaxBrightness = 20000 },
        };
        save.ImageSaved += Raise.Event<EventHandler<ImageSavedEventArgs>>(this, Frame("LIGHT", stars));

        var starMessage = await reader.WaitForAsync("image.stars");
        Assert.Equal(new[] { "image.saved", "image.stars" }, reader.Types);
        Assert.Equal(1, starMessage.GetProperty("payload").GetProperty("stars").GetArrayLength());

        reader.Dispose();
        server.Dispose();
    }

    [Fact]
    public async Task A_light_frame_the_detector_found_nothing_in_ships_no_star_message()
    {
        var (server, reader, save) = await WiredAsync();
        using var watcher = new ImageWatcher(save, Substitute.For<ICameraMediator>(), server);
        await WaitForSubscriptionAsync(save);

        save.ImageSaved += Raise.Event<EventHandler<ImageSavedEventArgs>>(
            this, Frame("LIGHT", new List<DetectedStar>()));
        await Task.Delay(Settle);

        Assert.Equal(new[] { "image.saved" }, reader.Types);

        reader.Dispose();
        server.Dispose();
    }

    [Fact]
    public async Task A_calibration_frame_ships_no_star_message()
    {
        // ch.4: image.stars is per LIGHT frame. Star geometry off a flat or a dark is not a
        // measurement of the sky.
        var (server, reader, save) = await WiredAsync();
        using var watcher = new ImageWatcher(save, Substitute.For<ICameraMediator>(), server);
        await WaitForSubscriptionAsync(save);

        var stars = new List<DetectedStar>
        {
            new() { Position = new Accord.Point(10, 20), HFR = 9, MaxBrightness = 5000 },
        };
        save.ImageSaved += Raise.Event<EventHandler<ImageSavedEventArgs>>(this, Frame("FLAT", stars));
        await Task.Delay(Settle);

        Assert.Equal(new[] { "image.saved" }, reader.Types);

        reader.Dispose();
        server.Dispose();
    }
}
