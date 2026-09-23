# Astral Warden Beacon

A [N.I.N.A.](https://nighttime-imaging.eu/) 3.x plugin. It publishes a **one-way telemetry
stream** of what NINA is doing on `127.0.0.1:1999`, as newline-delimited JSON over TCP. The
[Astral Warden](https://astralwarden.com) agent running on the same PC reads this stream to monitor
the rig remotely. The stream format is fully documented in [`docs/protocol.md`](docs/protocol.md),
so any local tool can consume it; you don't need Astral Warden to use it.

## What it does not do

The plugin runs inside NINA on a PC that is usually somewhere you can't walk over to. So it is
built never to get in NINA's way:

- **Loopback only.** The listener binds `IPAddress.Loopback`
  ([`BeaconServer.cs`](src/AstralWarden.Nina.Beacon/Server/BeaconServer.cs)). Nothing off the PC
  can connect to it.
- **Read-only.** The Beacon observes NINA through its public mediators and events. It commands no
  device and changes no NINA state. Clients connect and read; the socket accepts no requests,
  acknowledgements or commands of any kind.
- **Never blocks NINA.** Each client has a queue that drops its oldest messages once it holds
  2000 messages or 8 MB, and at most two clients can connect. A slow or stuck reader loses messages
  (visible as gaps in `seq`) rather than slowing NINA or growing its memory. The work done on
  NINA's own threads is CPU-only over in-memory data and does no I/O, and with no client connected
  it is skipped entirely.
- **Stops cleanly.** On disable or shutdown the Beacon removes every handler it attached to NINA,
  waits (briefly, with a bound) for its background work to finish, and gives connected clients one
  shared two-second window to receive the final `bye`.
- **One NINA instance per PC.** If you run several NINA instances, the first one to load the Beacon
  is monitored. The others show a notification and stay off; they never take over.
- **Fails quietly.** Every watcher is isolated. If a port is busy, a device driver throws, or an
  optional plugin is missing, that one feed goes quiet and is retried; nothing is thrown into
  NINA.
- **Sends nothing off the PC.** The Beacon makes no network connection of its own, local or
  remote. Its only socket is the loopback listener that clients connect to.

## What it streams

| | via |
|---|---|
| hello / 5s heartbeat (uptime, drops, device summary, sequence state) | server + watchers |
| Pushed device state for 10 device kinds (no polling) | `I*Consumer` registrations |
| `image.saved` with per-frame pointing, guiding RMS during the exposure, quality, thumbnail | `IImageSaveMediator.ImageSaved` |
| `image.stars`: per-star list (≤500) + 8×8 median grid (tilt/curvature signal) | standard `StarDetectionAnalysis`; Hocus Focus PSF extras when it is the active detector |
| Autofocus curve (`af.start/point/complete`) + a quadratic fit with R² | `IFocuserConsumer` |
| Sequence running chain, current instruction and target | `ISequenceMediator` (2s change poll) |
| Mount lifecycle (slew, park, home, meridian flip) | `ITelescopeMediator` events |
| Target Scheduler wait / target start / complete context | NINA `IMessageBroker` (Target Scheduler ≥ 4.8) |
| `alert.custom`, from the **Send Astral Warden alert** sequencer instruction | `ISequenceItem` export |

Everything degrades gracefully. Without Hocus Focus you get stock quality only; without Target
Scheduler that feed is silent.

## Install

From NINA: **Plugins → Available → Astral Warden Beacon → Install**, then restart NINA.

Manually: download `AstralWarden.Nina.Beacon.<version>.zip` from
[Releases](https://github.com/goodwink/astralwarden-nina-plugin/releases) and extract it into
`%localappdata%\NINA\Plugins\3.0.0\Astral Warden Beacon\`. Then restart NINA.

Requires NINA 3.2.0.9001 or later.

## Build & test

```powershell
dotnet build AstralWarden.Nina.Beacon.sln
dotnet test  AstralWarden.Nina.Beacon.sln
```

Targets `net8.0-windows` and references the `NINA.Plugin` 3.2.0.9001 NuGet package. No NINA
install is needed to build.

### Dev loop

```powershell
# NINA must be closed (the installed DLL is locked while it runs)
powershell -File scripts\install-dev.ps1
# then start NINA → Options > Plugins → "Astral Warden Beacon"
```

There's no hot reload: every iteration is build → install → restart NINA. To watch the raw
stream without the agent:

```powershell
$c = New-Object Net.Sockets.TcpClient("127.0.0.1", 1999)
$r = New-Object IO.StreamReader($c.GetStream())
while ($true) { $r.ReadLine() }
```

## Rules for contributors

- **Don't rename exported types.** NINA saves sequences with each instruction's fully-qualified type
  name. `AstralWarden.Nina.Beacon.Instructions.SendWardenAlertInstruction` is frozen: renaming it
  or its namespace would turn the instruction in everyone's saved sequences into "unknown".
- **Never change the plugin GUID** in `Properties/AssemblyInfo.cs`. NINA uses it to identify the
  plugin across installs and upgrades.
- **Protocol changes are additive.** Add fields or message types; don't repurpose existing ones.
  Update `docs/protocol.md` in the same change.

## Versions and releases

The version lives in one place: `<BeaconVersion>` in `Directory.Build.props`, written as four parts
(`Major.Minor.Patch.Build`). It is stamped onto `AssemblyVersion`/`AssemblyFileVersion` at build
time. Every build that reaches a rig gets its own version, because NINA compares `AssemblyVersion`
to decide whether an installed plugin folder is current. Changes are listed in
[`CHANGELOG.md`](CHANGELOG.md).

To release:

1. Bump `<BeaconVersion>` and add its `CHANGELOG.md` section; commit.
2. Push a tag equal to the version, e.g. `git tag 1.2.3.0 && git push origin 1.2.3.0`.
3. [`release.yml`](.github/workflows/release.yml) then:
   - runs the tests;
   - builds the plugin;
   - fails if the tag doesn't match the built DLL's version;
   - zips the DLL and PDB;
   - generates the NINA manifest with the official `CreateManifest.ps1`;
   - publishes a GitHub Release carrying the zip and the manifest;
   - pushes the manifest to a branch of the maintainer's fork of
     [`nina.plugin.manifests`](https://github.com/isbeorn/nina.plugin.manifests).
4. The maintainer opens the manifest pull request by hand.

A published release asset is never replaced, because the manifest pins its checksum. A fix ships as
a new version.

## Maintainer & AI-assisted development

Maintained by Kyle Goodwin for Astral Warden, LLC. Kyle is the accountable human maintainer:
he has reviewed, tested and can explain, debug and maintain all of this code.

This plugin was developed with material AI assistance (Anthropic's Claude). Every change was
reviewed by the maintainer, and the plugin has been verified on real imaging rigs as well as NINA's
simulators.

## License

Copyright © 2026 Astral Warden, LLC. Licensed under the [Mozilla Public License 2.0](LICENSE).
The license covers this repository only. The Astral Warden agent and service are separate products.
