# Beacon protocol v1

The Astral Warden Beacon (a NINA plugin) publishes a **one-way telemetry stream** on
`127.0.0.1:1999` as newline-delimited JSON over TCP. The Astral Warden agent connects and reads.
This document is the single source of truth for the contract; both sides keep hand-written DTOs
in sync with it (`src/AstralWarden.Nina.Beacon/Contracts/Messages.cs` and
`agent/src/AstralWarden.Agent.Core/Plugins/NinaBridge/BridgeMessages.cs`).

## Non-goals (by design, permanently)

- **No commands.** The socket carries telemetry out of NINA and nothing else. There is no way to
  make NINA do anything through it. Any future control path is cloud→agent, never this socket.
- **No client input.** A client connects and reads; it never sends a byte. There are no requests,
  responses, acknowledgements, subscriptions, or client heartbeats.
- **No RPC shapes.** No `method`/`params`/`id` fields, no request/response correlation of any kind.

## Connections

- **At most two clients at once.** Each client's queue lives in NINA's memory. The agent needs one
  connection; the second is room for a diagnostic tail. A third connection is closed immediately,
  before it is sent anything (not even `hello`), and never displaces a connected client.
- **One NINA instance per PC.** The first NINA instance to load the Beacon runs it for as long as
  that instance runs. A later instance logs a warning, shows a NINA notification and stays off for
  its whole session, so it can never take over the port and feed a reader another rig's data.

## Envelope

Every line is one JSON object:

```json
{"v":1,"type":"heartbeat","seq":42,"ts":"2026-07-21T03:14:00.123Z","payload":{}}
```

- `v` — protocol version. Bumped only on breaking changes.
- `type` — dotted lowercase message type (see catalog below).
- `seq` — **per-connection** monotonic counter, stamped when a message is queued for that client.
  The server drops messages rather than block NINA, so a gap in the seq stream is exactly the set
  of messages that client lost; `heartbeat` reports the server-wide drop count. Each client's queue
  is bounded both by count (2000 messages) and by memory (8 MB), dropping oldest first: messages
  range from a few hundred bytes to a few hundred KB, so a count alone wouldn't bound memory.
- `ts` — UTC ISO-8601 with milliseconds, stamped when the message was created.
- `payload` — type-specific body, camelCase. Consumers must tolerate unknown types and unknown
  fields (the stream iterates faster than the readers).

## Message catalog (v1)

### `hello` — first message on every connection
```json
{"schemaVersion":1,"beaconVersion":"0.1.0.0","ninaVersion":"3.3.0.1048","profileName":"Default","serverStartedAt":"2026-07-21T03:00:00.000Z"}
```
Null fields are omitted (`ninaVersion`/`profileName` when unavailable).

### `heartbeat` — every 5s
```json
{"uptimeSec":123.4,"clients":1,"droppedSinceLastHeartbeat":0,
 "devices":{"camera":true,"focuser":true,"mount":true,"safety":false}}
```
`devices` maps each watched device kind to its connected flag. Later phases add sequence
summaries; consumers must tolerate new fields.

### `bye` — best-effort on NINA shutdown
```json
{"reason":"shutdown"}
```

### `device.state` — pushed on change (≤1 per 10s per device; 5-min refresh when flat)
NINA pushes device info to the Beacon's registered consumers; no polling anywhere. Devices:
`camera, focuser, rotator, safety, flat, switch, weather, mount, filterwheel, guider`.
```json
{"device":"focuser","connected":true,"name":"ZWO EAF",
 "state":{"position":15400,"temperature":4.2,"isMoving":false,"tempComp":false}}
```
- `state` is null while disconnected; NaN driver values are omitted.
- Kind-specific `state` shapes are the records in `Contracts/Messages.cs`
  (camera: cooler/exposing/gain; safety: `isSafe`; mount: ra/dec/pier/tracking/park/
  hoursToMeridianFlip; weather: the full ObservingConditions set; guider: RMS arcsec;
  switch: `gauges` array of `{id,name,value}`).
- Current state is re-broadcast whenever a client connects, so a late joiner starts from truth.

### `device.connection` — immediate on connect/disconnect
```json
{"device":"camera","connected":false,"name":"QHY600M"}
```

### `image.saved` — per saved exposure
```json
{"path":"C:\\imgs\\M31_L_0042.fits","imageType":"LIGHT","filter":"L","exposureSec":300,
 "exposureStart":"2026-07-21T03:05:00.000Z","exposureNumber":42,
 "target":"M31","targetRa":0.712,"targetDec":41.27,"targetRotation":15.0,
 "ra":0.713,"dec":41.26,"airmass":1.18,"pierSide":"pierWest",
 "cameraTemp":-9.9,"cameraSetPoint":-10,"gain":100,"offset":30,
 "focuserPosition":15400,"focuserTemp":4.2,
 "stats":{"mean":845.2,"median":840,"min":120,"max":65535,"stDev":210.4,"mad":38},
 "quality":{"hfr":2.31,"hfrStDev":0.4,"detectedStars":812,"fwhm":3.05,"eccentricity":0.42},
 "guiding":{"ra":0.42,"dec":0.38,"total":0.57,"scale":1.05,"dataPoints":150},
 "detector":"hocusfocus","thumbnail":"<base64 JPEG>"}
```
- `ra`/`dec` = mount pointing from image metadata (RA hours, Dec degrees) — per-frame pointing the
  agent never had. `guiding` = RMS recorded across THIS exposure (raw px; × `scale` for arcsec).
- `thumbnail` = base64 JPEG of NINA's display image, downscaled in-process (≤700 px longest edge),
  **light frames only**; absent for calibration frames and when the image can't be encoded. It is the
  one large field on the wire (tens of KB) but only on light frames (minutes apart). Encoded off
  `ImageSavedEventArgs.Image` — already auto-stretched by NINA — so no raw-linear black frames; the
  agent re-encodes it to its own size/quality budget before upload.
- `quality.fwhm/eccentricity` (+ `fwhmMad`/`eccentricityMad`) only when `detector` is
  `hocusfocus`; stock detector yields hfr/starCount only.
- **Absence is always `null`, never a sentinel.** NINA's metadata defaults encode "not known" three
  different ways — `NaN` for unread analog values (temperatures, airmass, exposure time), `-1` for
  unknown counts (`gain`, `offset`, `exposureNumber`), and `""` for unset text (`imageType`,
  `target`) — plus `pierUnknown` for an unknown pier side. All of them map to `null` on the wire, so
  a consumer never has to know NINA's sentinel vocabulary and a `-1` gain can't quietly become a
  gain bucket. `0` is preserved: 0 °C and gain 0 are real values.
- `guiding` is omitted entirely when the exposure wasn't guided (`dataPoints == 0`) rather than
  reported as zero RMS.

### `image.stars` — per light frame, after its image.saved (same `path`)
```json
{"path":"...","detector":"hocusfocus","width":4144,"height":2822,"starCount":812,
 "stars":[{"x":101.5,"y":220.1,"hfr":2.31,"fwhm":3.0,"ecc":0.4,"brightness":50123.0}],
 "grid":[[{"count":12,"medianHfr":2.2,"medianFwhm":2.9,"medianEcc":0.38},null, "…8 cells"], "…8 rows"]}
```
- `stars` capped at the brightest 500; `grid` (8×8 row-major, null = empty cell) always covers
  every detected star — the tilt/curvature/aberration signal.
- Per-star fwhm/ecc/theta come from HocusFocus's nested per-star PSF fit (verified vs HF
  4.0.0.10: `HocusFocusDetectedStar.PSF.FWHMArcsecs/Eccentricity/ThetaRadians` — fwhm is in
  ARCSEC, matching HF's aggregate; theta is the elongation angle in radians). Absent with the
  stock detector, for stars whose PSF fit failed, and flat-field zeros are treated as
  unpopulated. `brightness` is HF's normalized 0–1 MaxBrightness, not ADU.
- theta is the attribution discriminator: elongation at a coherent angle across the whole frame
  with clean guiding = differential flexure / unguided drift; an edge- or corner-loaded pattern =
  tilt/collimation; incoherent angles = seeing.

### `af.start` / `af.point` / `af.complete` — per autofocus run
The Beacon assembles the curve from NINA's live per-point broadcasts (IFocuserConsumer) — no
report-file reads — and fits it itself, so fit quality exists even where nothing writes AF JSONs.
```json
{"position":15250,"hfr":2.41}
```
```json
{"success":true,"filter":"L","finalPosition":15010,"temperature":4.5,"durationSec":142.5,
 "points":[{"position":14250,"hfr":3.9},{"position":14500,"hfr":3.1}],
 "fit":{"method":"quadratic","minimumPosition":15004.5,"minimumHfr":2.02,"rSquared":0.9931}}
```
- NINA only broadcasts run-end for successful runs: a dangling run is closed with
  `success:false` when the next run starts or after 15 min of silence (its partial points kept).
- `fit` is the Beacon's own least-squares parabola (null under 3 points or no minimum); NINA's
  richer fits (hyperbolic etc.) are not exposed in-process.

### `camera.downloadtimeout` — on event (empty payload)
Note: `image.savefailed` needs the 3.3-only `ImageSaveFailed` mediator event — deferred until the
NINA.Plugin package reference moves past 3.2.0.9001.

### `sequence.state` — immediate on sequence start/finish, then on change (2s poll while running)
```json
{"running":true,
 "items":[{"name":"Take Exposure","path":"Sequence > M31 Panel 1 > Imaging","status":"RUNNING","attempts":1}],
 "target":"M31 Panel 1","targetRa":0.712,"targetDec":41.27}
```
`items` is the advanced sequencer's currently-running chain; `target` comes from the innermost
enclosing DSO container. Re-broadcast on client connect. Heartbeat also carries
`sequenceRunning` + `instruction` (innermost running item name).

### `mount.event` — on event
```json
{"event":"slewed","fromRa":0.5,"fromDec":40.1,"toRa":0.712,"toDec":41.27}
```
Events: `slewed` (with from/to), `parked`, `unparked`, `homed`, `flip-before`, `flip-after`.

### `alert.custom` — when a "Send Astral Warden alert" instruction executes
```json
{"title":"Clouds rolled in","severity":"warn","message":"Sky quality dropped below threshold"}
```
The Beacon ships a sequencer instruction (category "Astral Warden") with title / severity
(info|warn|error) / message fields; the agent forwards it as a standard event
(`nina.custom_alert`) into the alerting pipeline. Validation fails only when the Beacon itself
failed to start. NINA turns any validation issue into a "start anyway?" prompt when a sequence
starts, so an agent that isn't connected at that moment is not treated as one.

### `ts.waitstart` / `ts.targetstart` / `ts.targetcomplete` / `ts.containerstopped`
In-process Target Scheduler feed via NINA's official IMessageBroker — no HTTP API, no SQLite
touching. Topics + header shapes verified against TS 5.10.3 source (`PubSub/*Publisher.cs`);
silent when TS isn't installed. Last `ts.targetstart` re-broadcasts to late joiners.
```json
{"project":"Broadband","target":"M31","ra":0.712,"dec":41.27,"rotation":15.0,"secondsUntilNextTarget":754}
```
```json
{"newTarget":true,"project":"Broadband","target":"M31","ra":0.712,"dec":41.27,"rotation":15.0,
 "filter":"Ha","exposureSec":600,"gain":"100","offset":"(camera)","binning":"1x1"}
```
- `ts.targetstart.newTarget` distinguishes TargetScheduler-NewTargetStart from -TargetStart.
- `gain`/`offset` are strings because TS sends "(camera)" when deferring to camera defaults.
- What the broker can NOT provide (stays on the agent's ts-api plugin): project/target/exposure-
  plan **progress stats** (completion %, acquired/accepted counts, time-below-accept). Upstream
  proposal candidates for tcpalmer: an exposure-plan-progress topic on target start, and a
  per-image-graded topic.

## Deferred / v2 candidates

- `image.savefailed` — needs NINA 3.3's `ImageSaveFailed` mediator event (package bump).
- `exposure.start` — no clean public capture-start hook; `device.state` camera `isExposing`/
  `activity` covers it coarsely.
- `hello` plugin inventory (installed plugin names/versions) — needs a supported enumeration hook.
- HocusFocus tilt/aberration model per AF run — only via HF assembly reference or its per-region
  report files; revisit if grid-based tilt from `image.stars` proves insufficient.
- TS progress stats via broker — upstream proposal to tcpalmer (see ts section).
- `appm.model` (Astro-Physics APPM pointing-model points) — removed in 1.3.3.0 because nothing
  consumed it. If restored, poll APPM's local API only while an Astro-Physics mount is connected.
  The live APCC model stays out of reach either way: APCC's only known API is a raw serial-command
  passthrough that can command the mount.
