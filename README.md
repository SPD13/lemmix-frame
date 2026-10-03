# Lemmix for Steam Frame

A native, standalone VR build of the Lemmix engine (NeoLemmix levels and styles) for the
Valve Steam Frame. It is a port of the web version, [LemmingsJS_VR](https://github.com/SPD13/LemmingsJS_VR)
(https://lemmix.spd13.us), whose every play function and menu it replicates. The web repo
sits in `web/` as a pinned submodule: it is the reference the port is tested against, and
the source of shared data (depth profiles, solutions, hotkey presets, config file formats).

- **Engine:** Godot 4.7 .NET (C#), OpenXR, Vulkan Mobile renderer.
- **Targets:** Linux ARM64, launched on the Frame in Steam Linux Runtime 4 ARM64 (tested in the
  3.0 "sniper" ARM64 image). An Android APK through Lepton is the fallback; it is not set up yet (needs the
  Android SDK and JDK 17).
- **Scope:** the Lemmix engine (the classic DOS engine is not part of it), VR only, play
  features only (the web version's piece editor, galleries, solver queue and LAN launcher
  stay web-only).
- **No game data ships:** the player downloads NeoLemmix, its styles and level packs inside
  the app, from https://www.neolemmix.com, as on the web version's setup page.

The plan, phases and decisions: `docs/plan.md`.

## Layout

```
web/                    LemmingsJS submodule, pinned (the oracle)
core/Lemmix.Core/       C# library with no Godot dependency: parsers, sim, game, panel, render buffers, indexes, storage
core/Lemmix.Core.Tests/ xunit v3 (an executable, so it also runs in the Frame's runtime image)
app/                    the Godot project
native/libopenmpt/      tracker music library, built for linux-arm64 (sniper SDK image) and macOS
oracle/                 Node dumpers running the web engine, the reference for the C# port
parity/matrix.json      every web feature to replicate, with its oracle and status (node parity/gen-matrix.js)
tools/                  build, test, export, deploy and report scripts
docs/                   plan, probe results, porting notes
```

## Setup (macOS, Apple Silicon)

- .NET SDK 8 or later; Node; Docker (colima here; `tools/docker.sh` works around its config).
- Godot 4.7.2 .NET (not in git): `tools/get-godot.sh` downloads the official release, checks it
  against its SHA-512 sums, and puts the macOS and linux-arm64 editors in `.tools/` and the
  Linux ARM64 export templates (`--android` adds Android's) in
  `~/Library/Application Support/Godot/export_templates/4.7.2.stable.mono/`.
- The assets the oracles need (`neolemmix/`, `levels/`) come from a working copy of the web
  repo with them installed: `WEB_ASSETS`, by default `../LemmingsJS`.
- `native/libopenmpt/build.sh` once.

## Commands

```
make verify          the gate: core tests on the Mac and in sniper arm64, the app starts headless
make export-linux    build/app/linux-arm64, what goes to the Frame
make probe-virtual   the off-device probe (docs/phase0-probe.md)
make matrix          regenerate parity/matrix.json after bumping web/
```

On the Frame (developer mode, ssh as `steamos@frame.local`):

```
tools/frame-deploy.sh [play|probe|benchmark]   upload, register the "lemmix" title, choose what it runs
tools/frame-pull-report.sh                     probe.json, perf.json, qa.json, controller-models.json
tools/frame-capture.sh                         a picture of the headset's view
```

How the Godot app is built, what the device showed and how to work on it:
**`docs/godot-implementation.md`**.

Godot on the Mac: tests pass `--xr-mode off` (with no OpenXR runtime the start can stall),
and `tools/godot-run.sh` stops the process once its marker is printed, since Godot 4.7 .NET
headless on macOS may hang at exit.

## Credits and licence

The Lemmix engine is a port of NeoLemmix's `LemGame.pas` by way of the web version, so it
carries NeoLemmix's licence, CC BY-NC 4.0: free, non-commercial, crediting Eric Langedijk
(Lemmix), Stephan Neupert and Namida Verasche. LemmingsJS by oklemenz and tomsoftware.
libopenmpt (BSD), Godot (MIT). Developed with Claude Code.
