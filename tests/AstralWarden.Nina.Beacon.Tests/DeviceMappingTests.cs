using AstralWarden.Nina.Beacon.Contracts;
using AstralWarden.Nina.Beacon.Mapping;
using AstralWarden.Nina.Beacon.Watchers;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Equipment.MyFocuser;
using NINA.Equipment.Equipment.MySafetyMonitor;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.Equipment.MyWeatherData;
using Xunit;

namespace AstralWarden.Nina.Beacon.Tests;

public class DeviceMappingTests
{
    [Fact]
    public void Focuser_maps_position_temperature_and_motion()
    {
        var state = DeviceInfoMapper.Focuser(new FocuserInfo
        {
            Connected = true, Position = 15400, Temperature = 4.2, IsMoving = true, TempComp = false,
        });
        Assert.Equal(15400, state.Position);
        Assert.Equal(4.2, state.Temperature);
        Assert.True(state.IsMoving);
    }

    [Fact]
    public void NaN_driver_values_become_null_so_json_omits_them()
    {
        var state = DeviceInfoMapper.Focuser(new FocuserInfo { Position = 100, Temperature = double.NaN });
        Assert.Null(state.Temperature);
        Assert.DoesNotContain("temperature", BeaconJson.Serialize(state));

        var weather = DeviceInfoMapper.Weather(new WeatherDataInfo { Temperature = 10.5, Humidity = double.NaN });
        Assert.Equal(10.5, weather.Temperature);
        Assert.Null(weather.Humidity);
    }

    [Fact]
    public void Camera_maps_cooler_state_and_battery_only_when_present()
    {
        var state = DeviceInfoMapper.Camera(new CameraInfo
        {
            Temperature = -9.8, TemperatureSetPoint = -10, CoolerOn = true, CoolerPower = 62.5,
            Gain = 100, Offset = 30, HasBattery = false, Battery = -1,
        });
        Assert.Equal(-9.8, state.Temperature);
        Assert.Equal(-10, state.SetPoint);
        Assert.True(state.CoolerOn);
        Assert.Equal(62.5, state.CoolerPower);
        Assert.Null(state.Battery); // no battery → omitted, not -1
    }

    [Fact]
    public void Safety_and_mount_map_the_essentials()
    {
        Assert.False(DeviceInfoMapper.Safety(new SafetyMonitorInfo { IsSafe = false }).IsSafe);

        var mount = DeviceInfoMapper.Mount(new TelescopeInfo
        {
            RightAscension = 5.5, Declination = -20.25, TrackingEnabled = true,
            Slewing = false, AtPark = false, TimeToMeridianFlip = 1.75,
        });
        Assert.Equal(5.5, mount.Ra);
        Assert.Equal(-20.25, mount.Dec);
        Assert.True(mount.TrackingEnabled);
        Assert.Equal(1.75, mount.HoursToMeridianFlip);
    }
}

public class StateThrottleTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 21, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public void First_state_always_emits()
    {
        var throttle = new StateThrottle();
        Assert.True(throttle.ShouldEmit("a", T0));
    }

    [Fact]
    public void Rapid_changes_are_limited_to_one_per_interval()
    {
        var throttle = new StateThrottle();
        throttle.ShouldEmit("a", T0);
        Assert.False(throttle.ShouldEmit("b", T0.AddSeconds(1)));
        Assert.False(throttle.ShouldEmit("c", T0.AddSeconds(9)));
        Assert.True(throttle.ShouldEmit("d", T0.AddSeconds(10)));
    }

    [Fact]
    public void Flat_state_refreshes_slowly()
    {
        var throttle = new StateThrottle();
        throttle.ShouldEmit("a", T0);
        Assert.False(throttle.ShouldEmit("a", T0.AddMinutes(1)));
        Assert.True(throttle.ShouldEmit("a", T0.AddMinutes(5)));
    }

    [Fact]
    public void Connection_flip_forces_an_emit()
    {
        var throttle = new StateThrottle();
        throttle.ShouldEmit("a", T0);
        Assert.True(throttle.ConnectionFlipped(true));
        Assert.True(throttle.ShouldEmit("b", T0.AddSeconds(1), force: true));
        Assert.False(throttle.ConnectionFlipped(true)); // unchanged → no flip
    }

    [Fact]
    public void Pixel_scale_is_arcsec_per_pixel_at_the_current_binning()
    {
        // 206.265 x 3.76um / 1000mm = 0.7756"/px unbinned; bin 2 really does cover twice the sky per
        // pixel, so a consumer comparing pixels to arcsec would be out by that factor without it.
        Assert.Equal(0.7756, DeviceInfoMapper.PixelScale(3.76, 1000, 1)!.Value, 3);
        Assert.Equal(1.5511, DeviceInfoMapper.PixelScale(3.76, 1000, 2)!.Value, 3);
        // A short refractor spreads far less sky per pixel than a long one.
        Assert.True(DeviceInfoMapper.PixelScale(3.76, 2000, 1) < DeviceInfoMapper.PixelScale(3.76, 500, 1));
    }

    [Fact]
    public void Pixel_scale_is_null_rather_than_fabricated_when_an_input_is_missing()
    {
        // A profile with no focal length set is the common case on a fresh install; inventing a scale
        // there would silently corrupt every sky-unit conversion downstream.
        Assert.Null(DeviceInfoMapper.PixelScale(3.76, null, 1));
        Assert.Null(DeviceInfoMapper.PixelScale(null, 1000, 1));
        Assert.Null(DeviceInfoMapper.PixelScale(3.76, 0, 1));
        Assert.Null(DeviceInfoMapper.PixelScale(0, 1000, 1));
        Assert.Null(DeviceInfoMapper.PixelScale(3.76, 1000, 0));
    }
}
