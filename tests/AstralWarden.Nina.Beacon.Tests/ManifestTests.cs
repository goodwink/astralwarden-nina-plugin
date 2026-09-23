using System.Reflection;
using Xunit;

namespace AstralWarden.Nina.Beacon.Tests;

/// <summary>
/// NINA builds the plugin's browser entry from these assembly attributes, so a placeholder
/// left in here is a wrong claim shown to users — and the version drives NINA's "is this folder
/// current" decision, which is why it must come from $(BeaconVersion) rather than a hand-edited
/// literal that drifts.
/// </summary>
public class ManifestTests
{
    private static readonly Assembly Plugin = typeof(Beacon).Assembly;

    private static string? Metadata(string key) => Plugin
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == key)?.Value;

    [Fact]
    public void Version_is_stamped_from_the_build_property()
    {
        var version = Plugin.GetName().Version;
        Assert.NotNull(version);
        // 1.0.0.0 is what the SDK stamps when nothing sets <Version> — i.e. the props wiring broke.
        Assert.NotEqual(new Version(1, 0, 0, 0), version);
        Assert.NotEqual(new Version(0, 0, 0, 0), version);
        // The same version must reach the file-version resource NINA and Explorer read.
        Assert.StartsWith(version.ToString(3),
            Plugin.GetCustomAttribute<AssemblyFileVersionAttribute>()!.Version);
    }

    [Fact]
    public void The_license_is_declared_and_resolvable()
    {
        Assert.Equal("MPL-2.0", Metadata("License"));
        Assert.Equal("https://www.mozilla.org/en-US/MPL/2.0/", Metadata("LicenseURL"));
    }

    [Theory]
    [InlineData("LicenseURL")]
    [InlineData("Repository")]
    [InlineData("Homepage")]
    [InlineData("ChangelogURL")]
    [InlineData("FeaturedImageURL")]
    [InlineData("ScreenshotURL")]
    [InlineData("AltScreenshotURL")]
    public void Every_url_is_either_absent_or_a_real_absolute_url(string key)
    {
        var value = Metadata(key);
        Assert.NotNull(value); // the key itself must exist, even when empty
        if (value.Length == 0) return; // deliberately unset (no screenshots exist yet) — fine

        Assert.True(Uri.TryCreate(value, UriKind.Absolute, out var uri), $"{key} is not absolute: {value}");
        Assert.Equal(Uri.UriSchemeHttps, uri!.Scheme);
    }

    [Fact]
    public void Identity_fields_are_filled_in()
    {
        Assert.Equal("Astral Warden Beacon", Plugin.GetCustomAttribute<AssemblyTitleAttribute>()!.Title);
        // ProductName must be the DISPLAY name: it is how tooling picks the plugin's own assembly
        // out of a folder full of dependency DLLs.
        Assert.Equal("Astral Warden Beacon", Plugin.GetCustomAttribute<AssemblyProductAttribute>()!.Product);
        foreach (var key in new[] { "Repository", "Homepage", "Tags", "LongDescription", "MinimumApplicationVersion" })
            Assert.False(string.IsNullOrWhiteSpace(Metadata(key)), $"{key} is empty");
    }

    [Fact]
    public void The_publisher_is_the_legal_entity()
    {
        // AssemblyCompany becomes the manifest's Author; the copyright holder is the same entity.
        Assert.Equal("Astral Warden, LLC", Plugin.GetCustomAttribute<AssemblyCompanyAttribute>()!.Company);
        Assert.EndsWith("Astral Warden, LLC", Plugin.GetCustomAttribute<AssemblyCopyrightAttribute>()!.Copyright);
    }

    // The limits below are NINA's manifest.schema.json (isbeorn/nina.plugin.manifests). The
    // manifest is generated from these attributes at release time, so a value past a limit makes
    // the release's manifest invalid — this catches it at build time instead.

    [Fact]
    public void Attributes_fit_the_manifest_schema()
    {
        Assert.True(Plugin.GetCustomAttribute<AssemblyTitleAttribute>()!.Title.Length <= 50, "Name > 50");
        Assert.True(Plugin.GetCustomAttribute<AssemblyCompanyAttribute>()!.Company.Length <= 256, "Author > 256");
        Assert.True(Plugin.GetCustomAttribute<AssemblyDescriptionAttribute>()!.Description.Length <= 256,
            "ShortDescription > 256");
        Assert.True(Metadata("LongDescription")!.Length <= 10000, "LongDescription > 10000");
        Assert.True(Guid.TryParse(Plugin.GetCustomAttribute<System.Runtime.InteropServices.GuidAttribute>()!.Value, out _),
            "Identifier is not a GUID");

        var tags = Metadata("Tags")!.Split(',');
        Assert.True(tags.Length <= 20, "more than 20 tags");
        Assert.All(tags, t => Assert.InRange(t.Length, 1, 30));
    }

    [Theory]
    [InlineData("MinimumApplicationVersion")]
    public void Versions_have_four_numeric_parts(string key)
    {
        // CreateManifest.ps1 splits on '.' into Major/Minor/Patch/Build; the schema requires all four
        // to be digits.
        Assert.Matches(@"^\d+\.\d+\.\d+\.\d+$", Metadata(key)!);
    }

    [Fact]
    public void File_version_has_four_numeric_parts()
    {
        // The release workflow requires the tag, which must be four numeric parts, to equal this.
        Assert.Matches(@"^\d+\.\d+\.\d+\.\d+$",
            Plugin.GetCustomAttribute<AssemblyFileVersionAttribute>()!.Version);
    }
}
