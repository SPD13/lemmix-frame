# Phase 0: off-device probe (2 Oct 2026)

`make probe-virtual`: `tools/probe-virtual` run in `registry.gitlab.steamos.cloud/steamrt/sniper/platform/arm64`
(Steam Runtime 3, arm64, .NET 8.0.26 self-contained), the runtime the Frame runs native Linux games in.

| Check | Result | Consequence |
|---|---|---|
| libopenmpt 0.8.9 through P/Invoke | Renders an .it module at ~1300× real time | Music through AudioStreamGenerator is cheap |
| Filesystem | Case-sensitive | Styles have `Objects/`, `Terrain/` folders: the core needs a case-insensitive path map (phase 2) |
| GC, ×8 play with rewind states (3.6 MB each) on a 90 Hz loop | p99 11.1 ms (the loop period), max 11.14 ms, 1.34 ms total GC pause in 10 s | SustainedLowLatency + concurrent GC is enough; pooling is still planned |
| HTTPS from sniper to neolemmix.com | All three downloads answer 200 with the zip signature: `NeoLemmix_V12.14.0.zip` 6.9 MB, `styles.zip` 92.5 MB, `LemmingsPlus_All_20201114.zip` 22.8 MB | Setup downloads directly. **Range is ignored**: no resume, so retry restarts the file |
| Godot export, Linux ARM64 | 147 MB (engine 67 MB + .NET runtime + pck); starts in sniper arm64, no OpenXR runtime → falls back, exits cleanly | Linux ARM64 is viable as the primary target |

Left for device session 1 (`-- --probe`, writes `probe.json`): renderer and GPU, OpenXR runtime,
interaction profiles, refresh rates, foveation support, stress-scene frame times.

Environment notes: no Android SDK or JDK 17 on the dev Mac yet, so the APK fallback is not built;
Godot 4.7.2 .NET on macOS stalls starting with OpenXR enabled and no runtime (tests use `--xr-mode off`).
