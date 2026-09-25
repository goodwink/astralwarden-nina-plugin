# Manifest PR template

The body for the pull request that lists a release in NINA's plugin manager
([`isbeorn/nina.plugin.manifests`](https://github.com/isbeorn/nina.plugin.manifests)). The release
workflow stages the manifest on a branch of the maintainer's fork; the maintainer opens the PR by
hand with this body. Replace every `<...>`.

The manifest repo's maintainer reviews the tagged source and the exact zip, and asks about anything
that could affect NINA or the rig. Keep the safety section true to the release being submitted:
when a behaviour it describes changes, change it here too.

---

**Title:** `Add manifest for Astral Warden Beacon <version>`

## Summary

<First listing: one paragraph on what the plugin does. An update: one line, "Updates Astral Warden
Beacon from <previous> to <version>.">

<Update only: the user-visible changes, taken from this version's CHANGELOG.md section.>

- Repository: https://github.com/goodwink/astralwarden-nina-plugin
- Release: https://github.com/goodwink/astralwarden-nina-plugin/releases/tag/<version>
- License: MPL-2.0
- Publisher: Astral Warden, LLC
- Minimum NINA version: <MinimumApplicationVersion>
- Installer: `AstralWarden.Nina.Beacon.<version>.zip` (DLL + PDB only), SHA-256 `<checksum from the release's manifest.json>`
- Manifest generated from the assembly by the official `CreateManifest.ps1` in the tag's release
  workflow. `validate-latest-manifest.js` passed against the live release asset before this PR was
  opened.

## What it does and doesn't do

<Keep each point accurate for this version, with links pinned to the release tag.>

- **Loopback only, no connections of its own.** The listener binds `IPAddress.Loopback` on
  127.0.0.1:1999 ([BeaconServer.cs](https://github.com/goodwink/astralwarden-nina-plugin/blob/<version>/src/AstralWarden.Nina.Beacon/Server/BeaconServer.cs)).
  The plugin makes no outbound connection, local or remote.
- **One-way and read-only.** Clients connect and read. The socket accepts no requests or
  commands, and the plugin commands no device and changes no NINA state.
- **Bounded, and never blocks NINA.** At most two clients; each client's queue drops oldest past
  2000 messages or 8 MB. No work with no client connected. Handlers on NINA's threads are CPU-only
  over in-memory data and do no I/O.
- **Fault-isolated.** Mediator subscriptions retry until NINA's handlers are registered; each
  watcher is guarded so nothing throws into NINA; a busy port is retried in the background.
- **Stops cleanly.** Teardown returns every handler to NINA, waits (bounded) for background polls,
  and gives clients one shared 2 s window to receive the final message.
- **One NINA instance per PC.** The first instance to load the plugin is monitored; others notify
  and stay off.
- **One sequencer instruction,** "Send Astral Warden alert". It broadcasts on the local stream and
  completes immediately, never fails the sequence, and can't block a sequence from starting.

## Testing

- <N> tests run in CI on every push and before every release. <Name any new tests that cover this
  version's changes.>
- <How this version was run: real rigs and/or NINA's simulators, NINA and plugin versions.>

## Maintainer and AI-assisted development

I'm Kyle Goodwin, the accountable maintainer, publishing for Astral Warden, LLC. This plugin is
developed with material AI assistance (Anthropic's Claude). I have reviewed, tested and understand
all of the code in this release, verified its security, privacy, licensing and provenance, and can
explain, debug and maintain it myself.
