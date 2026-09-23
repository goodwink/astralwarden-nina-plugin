# Changelog — Astral Warden Beacon

Versions are set by `<BeaconVersion>` in `Directory.Build.props`; NINA compares the stamped
`AssemblyVersion` to decide whether an installed plugin folder is current, so every build that
reaches a rig gets its own version. Versions are four parts (`Major.Minor.Patch.Build`), and each
release is the tag of the same name.

## Unreleased

## 1.3.2.0

First public release, and the first listed in NINA's plugin manager. Functionally the same as
0.3.2. The version jumps to 1.x to mark it as the first release for general use.

- Published under MPL-2.0 by Astral Warden, LLC, from
  [github.com/goodwink/astralwarden-nina-plugin](https://github.com/goodwink/astralwarden-nina-plugin).
- Built and released by CI from a tag. The release carries the plugin zip and its NINA manifest.

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
