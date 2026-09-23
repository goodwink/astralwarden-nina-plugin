using AstralWarden.Nina.Beacon.Watchers;
using NINA.Image.ImageAnalysis;
using Xunit;

namespace AstralWarden.Nina.Beacon.Tests;

public class HocusFocusProbeTests
{
    // Shapes mirror HF 4.0.0.10: per-star values nested in a PSF object, flat fields left at 0.
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

    private sealed class FlatStar : DetectedStar
    {
        public double FWHM { get; init; }
        public double Eccentricity { get; init; }
    }

    [Fact]
    public void Reads_the_nested_psf_fit_when_present()
    {
        var star = new PsfStar { PSF = new FakePsf { FWHMArcsecs = 2.5, Eccentricity = 0.3, ThetaRadians = 1.2 } };
        var accessor = HocusFocusProbe.StarAccessor([star])!;

        var (fwhm, ecc, theta) = accessor(star);
        Assert.Equal(2.5, fwhm);
        Assert.Equal(0.3, ecc);
        Assert.Equal(1.2, theta); // elongation angle — the tracking/optics/seeing discriminator
    }

    [Fact]
    public void A_star_whose_psf_fit_failed_yields_nulls()
    {
        var accessor = HocusFocusProbe.StarAccessor([new PsfStar { PSF = null }])!;
        var (fwhm, ecc, theta) = accessor(new PsfStar { PSF = null });
        Assert.Null(fwhm);
        Assert.Null(ecc);
        Assert.Null(theta);
    }

    [Fact]
    public void Flat_fields_are_used_when_nonzero_and_treated_as_absent_at_zero()
    {
        var accessor = HocusFocusProbe.StarAccessor([new FlatStar()])!;

        var populated = accessor(new FlatStar { FWHM = 3.1, Eccentricity = 0.4 });
        Assert.Equal(3.1, populated.Fwhm);
        Assert.Equal(0.4, populated.Ecc);
        Assert.Null(populated.Theta); // flat fields never carry an angle

        // The live-run bug: base-type flat fields exist but are never populated → 0 → must be null.
        var unpopulated = accessor(new FlatStar());
        Assert.Null(unpopulated.Fwhm);
        Assert.Null(unpopulated.Ecc);
    }

    [Fact]
    public void Stock_detector_stars_have_no_accessor()
    {
        Assert.Null(HocusFocusProbe.StarAccessor([new DetectedStar()]));
    }
}
