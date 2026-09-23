# Changelog — Astral Warden Beacon

Versions are set by `<BeaconVersion>` in `Directory.Build.props`; NINA compares the stamped
`AssemblyVersion` to decide whether an installed plugin folder is current, so every build that
reaches a rig gets its own version. Versions are four parts (`Major.Minor.Patch.Build`), and each
release is the tag of the same name.

## Unreleased

- **The alert instruction no longer blocks a sequence from starting when the agent isn't connected.**
  NINA shows any validation issue as a "start anyway?" prompt when a sequence starts, so an agent
  that was restarting or not yet installed could hold up a night. Validation now fails only if the
  Beacon itself failed to start.
- **Copying the alert instruction keeps its on-error behaviour and attempt count.** Duplicating it,
  or loading it from a template, used to reset both to NINA's defaults.
- **Only one NINA instance per PC runs the Beacon.** The first instance to load it is monitored. A
  later instance shows a notification and stays off for its session. Before, a second instance
  took over the socket when the first closed, so the agent could receive another rig's data.
- **Memory is bounded.** At most two clients can connect (a third is closed at once), and each
  client's backlog drops oldest past 8 MB as well as past 2000 messages.
- **No work when nobody is listening.** With no client connected, a saved frame costs nothing: no
  thumbnail encode, star mapping or serialization on NINA's save thread.
- **Faster, cleaner shutdown.** Connected clients share one two-second window to receive `bye`,
  instead of two seconds each, so NINA closes sooner. Teardown also waits (bounded) for background
  polls to finish, so none is still reading NINA's sequencer afterwards.
- A device watcher whose registration with NINA failed no longer leaves a handler behind.
- **Removed:** `appm.model`. The plugin no longer polls Astro-Physics APPM's local HTTP API.
  Nothing read the data, and the Beacon now opens no connection of its own.

## 1.3.2.1

First public release, and the first listed in NINA's plugin manager. The version jumps to 1.x to
mark it as the first release for general use.

- **Fixed:** if NINA disabled or shut down the plugin while it was still subscribing to NINA's
  events at startup, some of its event handlers could stay attached to NINA's mediators and fire
  into a stopped plugin on the next mount, image-saved or sequence event. Teardown now waits for an
  in-progress subscription and then removes every handler.
- Published under MPL-2.0 by Astral Warden, LLC, from
  [github.com/goodwink/astralwarden-nina-plugin](https://github.com/goodwink/astralwarden-nina-plugin).
- Built and released by CI from a tag. The release carries the plugin zip and its NINA manifest.

## 1.3.2.0

Tagged but never released: its release run stopped at the test step, which caught the teardown
bug fixed in 1.3.2.1. Otherwise the same as 0.3.2.

## 0.3.2

- **`device.state` for the camera carries `exposureEndTime` and `lastDownloadSec`.** The first is
  NINA's `CameraInfo.ExposureEndTime` — when the running exposure is due to finish — which lets the
  cloud detect a hung exposure without knowing anything about the rig: a 15-minute sub and an
  hour-long sub each declare their own deadline. The second is the camera's own last download time,
  which sizes the allowance for readout from measured behaviour instead of a constant that would be
  wrong for a big sensor on a slow link.

  Reported only while exposing, because NINA leaves the previous exposure's end time in place
  afterwards and an idle camera would otherwise look permanently overdue. The end time is converted
  from NINA's local `DateTime` to UTC — shipping it unconverted would be wrong by the rig's UTC
  offset, which is exactly the size of error that reads as "hung" several time zones from UTC.

## 0.3.1

- **`sequence.state` items carry `type`** — the CLR type name (`item.GetType().Name`) beside the
  existing display `name`. Display names are user-editable and localized, so they cannot identify an
  instruction: real captures from one rig contain both `TakeExposure` and `Take Exposure`, and a
  user-renamed `Cool Camera +`. The agent matches `CoolCamera`/`WarmCamera` on `type` to emit
  `nina.camera.ramping`, which tells the cloud a temperature ramp is deliberate rather than a cooler
  failing.

  **Needs agent 0.3.1 too.** Older agents have no ramp tracking at all, so an upgrade of only one side
  loses the signal either way — nothing breaks, the feature is just absent.

## 0.3.0

- **Thumbnails.** `image.saved` now carries a downscaled JPEG (base64, light frames only), encoded
  in-process from NINA's already-stretched display image — no dependency on the Advanced API. The
  agent re-encodes it to its own budget and uploads it. Encoded inline on the ImageSaved thread
  (~14 ms dev / ~48 ms rig for a 24 MP frame), the same way ninaAPI produces its own thumbnail;
  total, so a bad image just ships thumbnail-less.

## 0.2.0

First build intended for a real rig. Hardening pass over 0.1.0:

- A busy port 1999 no longer kills the plugin at load. The listener binds if it can, otherwise logs
  and retries every 30s in the background; a listener that stops accepting is rebuilt. Accept
  failures back off instead of spinning.
- Background loops (heartbeat, sequence poll, APPM poll) survive an unexpected exception: each tick
  is guarded, a repeating fault is logged once, and only shutdown ends a loop.
- Nothing can throw into NINA: the alert instruction, the device and autofocus watchers, and the
  Target Scheduler subscription are all guarded, and teardown disposes every watcher even if one
  fails.
- Timestamps are locale-independent. `:` in a .NET date format is the culture's time separator, so
  on a rig whose locale differs, every timestamp on the stream was previously malformed.
- Absence is reported as `null` rather than as NINA's sentinels — `NaN`, `-1` for unknown counts
  (gain/offset/exposure number), empty strings, and `pierUnknown`. `0` is preserved.
- Stars with a non-finite position or HFR are dropped instead of being serialized as `"NaN"`.

## 0.1.0

First build. Publishes a one-way telemetry stream to the local Astral Warden agent over
`127.0.0.1:1999` — device state, image quality (incl. per-star PSF data when Hocus Focus is the
active star detector), autofocus runs, sequence state, Target Scheduler activity — plus a
**Send Astral Warden alert** sequencer instruction. Read-only by construction: the Beacon observes
NINA through its public mediators, commands nothing, and reads nothing from the socket.
