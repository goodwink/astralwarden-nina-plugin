using AstralWarden.Nina.Beacon.Contracts;
using AstralWarden.Nina.Beacon.Mapping;
using AstralWarden.Nina.Beacon.Server;
using NINA.Equipment.Interfaces.Mediator;
using NINA.WPF.Base.Interfaces.Mediator;

namespace AstralWarden.Nina.Beacon.Watchers;

/// <summary>
/// image.saved + image.stars from IImageSaveMediator.ImageSaved — the standard event that carries
/// full StarDetectionAnalysis (with HocusFocus's per-star richness when it's the active detector).
/// Also camera.downloadtimeout. Reads e.Image (the display-stretched BitmapSource) only to encode a
/// thumbnail for light frames — the measured-cheap, total BeaconThumbnailer, on the same thread as
/// ninaAPI's own thumbnail. Never blocks the save pipeline: handlers map + Broadcast (non-blocking)
/// inside try/catch.
/// </summary>
public sealed class ImageWatcher : IDisposable
{
    private readonly IImageSaveMediator _imageSave;
    private readonly ICameraMediator _camera;
    private readonly BeaconServer _server;
    private readonly RetrySubscription _saveEvents;
    private readonly RetrySubscription _cameraEvents;

    public ImageWatcher(IImageSaveMediator imageSave, ICameraMediator camera, BeaconServer server,
        Action<string>? log = null)
    {
        _imageSave = imageSave;
        _camera = camera;
        _server = server;
        // Both mediators forward event add/remove to handlers registered after plugin composition
        // (eager subscription NREs). One retry group per mediator so a failure on one can't
        // double-subscribe the other.
        _saveEvents = new RetrySubscription("image-save events",
            subscribe: () => _imageSave.ImageSaved += OnImageSaved,
            unsubscribe: () => _imageSave.ImageSaved -= OnImageSaved,
            log);
        _cameraEvents = new RetrySubscription("camera events",
            subscribe: () => _camera.DownloadTimeout += OnDownloadTimeout,
            unsubscribe: () => _camera.DownloadTimeout -= OnDownloadTimeout,
            log);
    }

    private void OnImageSaved(object? sender, ImageSavedEventArgs e)
    {
        try
        {
            // Everything below exists only to be sent. With no reader connected, spend nothing on
            // NINA's save thread. There is no late-joiner replay for frames, so nothing is lost.
            if (_server.ClientCount == 0) return;

            // Mapping lives in ImageSavedMapper (pure, directly tested); this handler only supplies
            // the two things that need live NINA state: the reflection-derived detector/extras and
            // the camera's sensor dimensions.
            var analysis = e.StarDetectionAnalysis;
            var detector = analysis is null ? null : HocusFocusProbe.DetectorId(analysis);
            var extras = analysis is null
                ? default
                : ToExtras(HocusFocusProbe.AnalysisExtras(analysis));

            // Encode the thumbnail inline off e.Image (already display-stretched by NINA) for light
            // frames only — calibration frames get no thumbnail. Cost measured (~14 ms dev); total,
            // so a bad image just yields a null thumb, never a throw into the save pipeline.
            var isLight = ImageSavedMapper.EmitsStars(e.MetaData?.Image?.ImageType);
            var thumbnail = isLight ? BeaconThumbnailer.EncodeBase64(e.Image) : null;

            _server.Broadcast("image.saved", ImageSavedMapper.Map(e, detector, extras, thumbnail));

            if (isLight && analysis?.StarList is { Count: > 0 } stars)
            {
                var (width, height) = SensorDims();
                _server.Broadcast("image.stars", StarListMapper.Map(
                    stars, width, height, HocusFocusProbe.StarAccessor(stars),
                    e.PathToImage?.LocalPath, detector));
            }
        }
        catch
        {
            // never let telemetry propagate into NINA's save pipeline
        }
    }

    private static ImageSavedMapper.AnalysisExtras ToExtras(
        (double? Fwhm, double? FwhmMad, double? Ecc, double? EccMad) x) =>
        new(x.Fwhm, x.FwhmMad, x.Ecc, x.EccMad);

    private Task OnDownloadTimeout(object sender, EventArgs e)
    {
        try { _server.Broadcast("camera.downloadtimeout", new { }); } catch { }
        return Task.CompletedTask;
    }

    /// <summary>Star positions are in (binned) image coordinates; derive dims from the camera.</summary>
    private (int? Width, int? Height) SensorDims()
    {
        try
        {
            var info = _camera.GetInfo();
            if (info is not { Connected: true } || info.XSize <= 0 || info.YSize <= 0) return (null, null);
            var binX = Math.Max(1, (int)info.BinX);
            var binY = Math.Max(1, (int)info.BinY);
            return (info.XSize / binX, info.YSize / binY);
        }
        catch
        {
            // mutation-gate: ignore — equivalent mutant. default((int?, int?)) IS (null, null), so
            // no test can separate this from the return-default operator. The behaviour that matters
            // (a throwing camera driver costs the field size, not the star message) is covered by
            // ImageWatcherTests.A_camera_driver_that_throws_costs_the_sensor_dimensions_not_the_star_message.
            return (null, null);
        }
    }

    public void Dispose()
    {
        _saveEvents.Dispose();
        _cameraEvents.Dispose();
    }
}
