using System.Reflection;
using System.Runtime.InteropServices;

// NINA reads its plugin manifest from these assembly attributes.
[assembly: Guid("c56fe1c1-af2f-42a7-b4bb-e87504c38797")]
[assembly: AssemblyTitle("Astral Warden Beacon")]
[assembly: AssemblyDescription("Streams NINA telemetry to the local Astral Warden agent over a read-only localhost socket.")]
[assembly: AssemblyCompany("Astral Warden, LLC")]
// The plugin's DISPLAY name, not the assembly name: a NINA plugin folder is mostly third-party
// dependency DLLs, so tooling identifies the plugin's own assembly by ProductName == folder name
// (Hocus Focus and Target Scheduler both follow this). Using the dotted assembly name here made us
// the one plugin our own rig-inventory tooling reported as "unidentified".
[assembly: AssemblyProduct("Astral Warden Beacon")]
[assembly: AssemblyCopyright("Copyright © 2026 Astral Warden, LLC")]
[assembly: ComVisible(false)]

// AssemblyVersion/AssemblyFileVersion are deliberately absent: they are generated from
// $(BeaconVersion) in Directory.Build.props. Do not add them back here — bump the property.

// Plugin-manifest metadata (NINA plugin convention).
[assembly: AssemblyMetadata("MinimumApplicationVersion", "3.2.0.9001")]
// MPL-2.0: the NINA plugin ecosystem's near-universal norm, and the Beacon is the one piece of
// Astral Warden that runs inside someone else's application. The license covers this repository
// only; the Astral Warden agent and service are separate products.
[assembly: AssemblyMetadata("License", "MPL-2.0")]
[assembly: AssemblyMetadata("LicenseURL", "https://www.mozilla.org/en-US/MPL/2.0/")]
[assembly: AssemblyMetadata("Repository", "https://github.com/goodwink/astralwarden-nina-plugin")]
[assembly: AssemblyMetadata("Homepage", "https://astralwarden.com")]
[assembly: AssemblyMetadata("Tags", "telemetry,monitoring")]
[assembly: AssemblyMetadata("ChangelogURL", "https://github.com/goodwink/astralwarden-nina-plugin/blob/main/CHANGELOG.md")]
[assembly: AssemblyMetadata("FeaturedImageURL", "")]
[assembly: AssemblyMetadata("ScreenshotURL", "")]
[assembly: AssemblyMetadata("AltScreenshotURL", "")]
[assembly: AssemblyMetadata("LongDescription", @"Companion plugin for the Astral Warden monitoring agent.

Publishes a one-way stream of imaging telemetry (device state, image quality, autofocus runs,
sequence progress) over a localhost TCP socket for the Astral Warden agent running on the same
machine. Read-only by construction: the Beacon observes NINA through its public mediator
interfaces, never commands equipment, and accepts no input on the socket.")]
