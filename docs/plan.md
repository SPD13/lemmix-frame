# Lemmix for Steam Frame: a native standalone VR app

## Context

The Lemmix engine (NeoLemmix-compatible sim) and its 3D/VR front end exist only as a web app: plain global-script JS, Three.js r147 and WebXR (`LemmingsJS/`, branch `solution`). The Steam Frame shipped on 18 Sep 2026. It runs SteamOS on ARM64 (Snapdragon 8 Gen 3, Adreno 750) with SteamVR's OpenXR runtime, and it has no official WebXR browser. The goal is a native Frame app that replicates every play function and menu of the web app. Agents build it with as little human work as possible, and the user's part is limited to quality and performance checks on the device.

Decisions taken with the user:
- **Route:** a Godot 4.6 native port, not a browser shell.
- **Scope:** the Lemmix engine only; the classic DOS engine is out.
- **VR only.** A Mac desktop build exists only for automated tests and is never shipped.
- **Play features only.** The piece editor, galleries, solver queue and LAN launcher server mode stay web-only.
- **Hardware:** the user has a Steam Frame and a PSVR2 on PC. There is no Steamworks partner account yet.
- **Dev machine:** an Apple M1 with Docker (native linux/arm64 containers) and .NET 8/10 SDKs. The installed Godot is 3.3.2, so 4.6 .NET must be installed.

### Options evaluated (summary for the user)

| Option | Verdict |
|---|---|
| Patched Chromium ARM64 with WebXR (saphid/chromium-webxr-steam-frame) bundling the site | Code unchanged, but the build is unofficial: unmerged OpenXR CLs, seccomp sandbox disabled, 9.5 h builds on an x86 Linux box, no foveation. It is not native and is risky for Steam review. Rejected |
| Wolvic/Gecko kiosk APK through Lepton (`3d/plans/steam-frame-wolvic-apk.md`) | Needs a 40 GB Gecko build, and Lepton's OpenXR runtime is uncertain. Superseded |
| Embed a JS engine (Jint/QuickJS/V8) and run `lemmix/js` unchanged | Saves only the sim (~4.8k lines), which is the easiest part to port. Interpreted speed is risky for re-simulation during rewind and ×8. Rejected |
| Custom C++/Rust OpenXR + Vulkan engine | Means rebuilding a UI toolkit, text, audio and the XR plumbing. Too much |
| **Godot 4.6 .NET (C#)** | Valve documents it for the Frame: Linux ARM64 and APK, OpenXR 1.1, Steam Frame Controller profile, foveation, headless CLI export. C# matches JS int32 semantics and keeps the class model, and `dotnet test` runs on the Mac and in linux-arm64 Docker. **Chosen** |

### Extension or new project? A new independent repo

Create `lemmix-frame/` as a sibling of `LemmingsJS/`, with LemmingsJS as a pinned git submodule (`web/`).
- **Nothing compiles across.** Browser JS cannot be a "different compile route" into Godot. What is shared is data (profiles, solutions, hotkey presets, config schemas) and the JS used as a test oracle, and a submodule gives exactly that, pinned to a commit.
- **Separate toolchain and release cadence.** Godot, .NET, Docker and Steam depots are a different toolchain, and a Steam release runs on its own cadence and version numbers.
- **The web repo publishes its whole committed tree to gh-pages** (`builder/`), so native code there would bloat the site.
- **The web repo stays untouched.** The oracle dumpers live in the new repo and load the submodule's JS.
- **Assets:** the gitignored assets (`neolemmix/`, `levels/`) are read from the working copy through `WEB_ASSETS=../LemmingsJS`.

## Architecture

```
lemmix-frame/
  web/                      submodule: LemmingsJS @ pinned SHA (oracle + shared data)
  core/Lemmix.Core/         C# net8 library, no Godot dependency
      Parse/      parser.js, styles.js (IO behind IFileSource, case-insensitive path map), .nxlv/.nxmi/.nxmo/.nxmt/.nxtm, .nxrp
      Sim/        pixels, level, lemgame (method for method), sprites, shadows, rewind, replay
      Game/       game.js + the classic pieces it needs (CommandManager, Command*, EventHandler, GameTimer → fixed step)
      Panel/      panel.js → 416×40 RGBA bitmap + hit cells; minimap bitmap; cursor selection
      Render/     depth.js classes, terrain.js chunk mesher, decals, portals, bridge sprite voxels, envgen → pure buffers
      Index/      levels/styles/music index builders (ordinal plus numeric-aware compare matching ICU output, checked by oracle)
      Store/      prefs/controls/progress in the web's 3 JSON schemas, favorites, recent, bar pose
  core/Lemmix.Core.Tests/   xUnit; oracle comparisons; run on Mac and in linux/arm64 Docker
  app/                      Godot 4.6 .NET project (Mobile renderer, OpenXR, OpenXR Vendors plugin)
      Xr/         session, local-floor, action map (Oculus Touch + Steam Frame Controller), focus-loss pause, recenter
      Board/      diorama scene from Core buffers: ArrayMesh chunks merged to 128 px, unlit vertex-colour material, MultiMesh lemmings
      Ui/         Panel3D (SubViewport + quad + ray→push_input), view-models, VR keyboard, one bundled OFL mono font
      Audio/      SFX via runtime wav/ogg/mp3 loaders + AudioStreamPlayer3D; music via libopenmpt P/Invoke → AudioStreamGenerator
      Test/       IPointerSource scripted input, pose replays, desktop debug camera (test builds only), screenshot runner
  native/libopenmpt/        Dockerfile (sniper arm64 SDK) + macOS build; NDK build for the APK fallback
  oracle/                   Node: dumpers using web/tools/lemmix-node.js (pngjs path is the reference), comparators
  parity/matrix.yaml        every inventory item: id, oracle type, status, web SHA; generated by script
  tools/                    frame-deploy.sh (ssh+rsync, devkit_rsa), frame-pull-report.sh, export.sh, steam/ (steamcmd vdf, later)
  .claude/skills/port-next/ the autonomous loop (like /solve-next)
```

C# porting rules, enforced by a checklist in `core/README.md` and by the oracles:
- **Numbers and rounding:**
  - `JsMath.Round` = `Floor(x + 0.5)`; never `Math.Round` (`pixels.js:113`, `level.js:196`).
  - Keep the `double` physics cutoff in its original operation order (`level.js:471`).
  - The xorshift RNG uses `uint` (`level.js:49-58`).
- **Collections and order:**
  - Stable sorts only (`lemgame.js:263`, `styles.js:244`).
  - Replay entries stay in insertion order (`lemgame.js:276-326`).
  - Loops over lemmings are index loops that re-read `Count` (`lemgame.js:2252, :2271`).
  - `CopyFrom` is generated from one field list (`lemgame.js:159-165`).
- **Text and globalization:**
  - `InvariantGlobalization`.
  - Ordinal or invariant string APIs; `[0-9]` instead of `\d`.
  - Strip the BOM in the parser.
- **GC:** concurrent GC with `SustainedLowLatency` during play, and pooled rewind buffers (~3.6 MB per state).

## Phases (each ends in an automated gate; agents loop with `/port-next`)

**No device is needed to start.** Phases 0–7 are built and verified on the Mac and in linux/arm64 Docker. Every device-dependent choice sits behind a setting or an abstraction, with a sensible default, so the first device session only adjusts it:

| Choice | Default | Changed at the device session if needed |
|---|---|---|
| Renderer | Mobile (Vulkan) | Compatibility is a project setting; unlit vertex colours look the same in both |
| Controller profile | Oculus Touch emulation | Add the Steam Frame Controller profile to the action map |
| Foveation, refresh rate | Off, 90 Hz | Settings in the OpenXR Vendors plugin |
| Mesh merge size, MultiMesh batching | Tuned on the Mac | Constants |

0. **Bootstrap, off-device.**
   - Install Godot 4.6 .NET plus export templates (`brew install --cask godot-mono`).
   - Create the repo, the submodule and the libopenmpt Docker build.
   - Run a "virtual probe" in the Steam Linux Runtime sniper arm64 container on the Mac. It covers what the device probe would have checked without a GPU:
     - Core tests;
     - libopenmpt through P/Invoke;
     - HttpClient TLS plus HEAD and range requests to the three neolemmix.com URLs (`setup.js:22-24`);
     - case-sensitive paths and the GC pause histogram;
     - a headless Linux ARM64 Godot export that launches.
   - Build the APK once to confirm the fallback compiles.
   - Prepare the device probe app now so it is ready for the first session. It is unattended, writes `probe.json` (pulled over ssh) and measures the renderers, the OpenXR profiles, local-floor, foveation on/off, a stress scene and panel legibility.
   - **Optional interim check:** a Windows OpenXR build on the PSVR2 through SteamVR. It exercises the same runtime family before the Frame arrives.
1. **Sim oracles (Node).** Per-frame 64-bit hashes of the canonical `saveState` (sorted keys, the sounds cues, `recorded.length`, plus an `isInteger` assert) over these inputs:
   - all 1076 levels with no input;
   - the 85 `.nxrp` solutions;
   - assignment fuzz (legal assignments only) saved as `.nxrp`;
   - command fuzz through `game.js` (pause, RR, nuke, frame step, gotoFrame, insert, cut, direction/walker filters);
   - a rewind self-check;
   - picking and the info-strip text at random cursor points, and `shadows.compute` lists.

   Also a canonical parse dump per level, with `../nx-render/*.png` as a second check. Hash files are committed; full JSON is written only at the first frame that differs.
2. **Core port.** Exit when all of these hold:
   - 1076 levels hash-equal over all frames;
   - 85/85 solutions;
   - 10k fuzz traces;
   - the rewind self-check;
   - green on the Mac and in linux/arm64 Docker.
3. **Render buffers.** Node oracles load `three.min.js` and run `terrain.js`, `depth.js`, `portals.js`, `bridge.js` and `envgen.js` through `vm` (with the `window.__lem3dObjectData` and profile-fetch stubs). They dump chunk positions, colours, UVs and indices, also after scripted dig/build steps. The panel bitmap has its own oracle (pixel-exact, using `tools/classic-node.js` loading plus a `DisplayImage` stub).

   Exit when `Lemmix.Core/Render` is float32-exact for every effect-switch combination on a level sample and the panel is pixel-exact. Then the Godot board scene renders them. Its gate is screenshots at fixed poses side by side with Three.js renders: SSIM is only a warning, and the contact sheet is reviewed by an agent.

   Covered here: 3D doors/exits/water, decals, replay markers, clear-physics overlay, skill shadows, cursor, minimap, the room environment (runtime envgen collage; optional import of the user's own pre-generated `3d/env` sets from a device folder, never shipped), and the in-VR 2D flat view.
4. **VR shell and interactions.**
   - Trigger clicks, trigger-drag moves the board in 3 axes, grip grabs, two grips scale (0.15×–8×), the beam swaps hands, and the bindable A/B/stick functions use the default table from `hotkeys.js:179`.
   - The toolbar and lock/park/move handles, the status strip, the confirm/notice modal, the tooltip, the REPLAY badge, volume and mute.
   - Pause when focus is lost, and recenter.

   Semantic unit tests cover the time-based behaviour: RR hold repeat, the two-press nuke, the frame-step repeat at 250/100 ms, the drag threshold. Scripted pose replays run on the Mac, and optionally a Monado simulated-driver run in Docker.
5. **Menus as VR panels.** Each panel's view-model is compared with a semantic oracle extracted from the web page through `__lem3d` (rows, labels, states, hit rectangles). Pixel comparison is not used because the fonts differ.
   - **World library:** the union of the desktop library and the VR catalog. Breadcrumb, pack rows (logo, author, counts), rank rows, tiles (cleared/best/★/▶ solution), search through the VR keyboard, recent, favorites, rescan, order and scrollbar.
   - **Setup:**
     - direct downloads of the NeoLemmix engine, the styles and Lemmings Plus, with resume, a zip-signature check, retry and progress;
     - install a zip from `~/Lemmix/import/`;
     - list and delete packs, storage usage, and installed engine/styles versions (this replaces the version warning);
     - config export/import in the web's JSON schemas, so progress moves between web and Frame;
     - credits.
   - **Solutions list:** search, pack filter, show filter, play solution.
   - **Controls dialog:** VR and keyboard tabs, 3 presets, show unassigned, find key, import/export.
   - **Key hints**, the **settings window** (8 effect switches plus recentre), the **level text window** (PRETEXT/POSTTEXT, talismans, result).
   - **Load/save replay** through `~/Lemmix/replays/` and an in-VR file list.
   - **Keyboard hotkeys:** a Bluetooth keyboard uses the same table.
6. **Audio.** SFX cue mapping (`app.js` `SFX_BY_CUE`) and spatial placement at the emitter. Music follows the level's `MUSIC` fallback list, then the `.nxmi` rotation, then the pack folder and finally `neolemmix/music`. Gate: a cue-sequence oracle per frame (reuses the sim hashes) and a libopenmpt render-checksum test.
7. **Persistence and the first-run flow.** All `lem3d-*` keys are mapped to `user://` JSON. On first run with nothing installed, the app opens Setup. The `?nxrp=`/`?level=` equivalents become command-line arguments, used by tests and the benchmark.
**Device session 1 (when the Frame is available, about 1 h).** Agents deploy over ssh.
- Run the probe app and the benchmark unattended.
- The user plays through the in-headset QA checklist panel, which records to a file: feel, comfort, legibility, controls, menus.
- Agents read `probe.json`, `perf.json` and the checklist, then set the defaults from the table above and fix the findings.

8. **Performance.**
   - Re-mesh only changed chunks on WorkerThreadPool after a rewind or `loadState`, and update textures per tile instead of re-uploading the whole level texture.
   - Turn on foveation (`XR_FB_foveation` / eye-tracked) and pick the refresh rate.
   - Add a benchmark mode: a scripted tour of the 10 heaviest levels × effect combinations at ×8 with rewinds. It writes `perf.json` (p50/p99 frame time, GC pauses) and pulls it over ssh.
   - Target: p99 under 11.1 ms (90 Hz), GC pauses under 2 ms.
   - **Device session 2:** the unattended benchmark again, plus final acceptance with the QA checklist.
9. **Packaging.** Linux ARM64 on the Steam Linux Runtime 3.0 (sniper) ARM64 as primary, and the APK as fallback. Steamworks app, depot and steamcmd scripts are prepared and wait for the partner account, which is a user step ($100 fee). Store text carries the credits: NeoLemmix is CC BY-NC 4.0, so the app must be free, and it credits Langedijk, Neupert, Verasche and oklemenz/LemmingsJS. Users download the assets in-app, and none ship.

## Automation

- **`/port-next` skill:** take the next `todo` row of `parity/matrix.yaml`, read the cited web source, port it, run its oracle and the full regression gate (`make verify`: `dotnet test` on the Mac and in Docker, plus Godot headless scene tests), then commit. When the gate fails, diagnose from the first differing frame dump.
- **Generated parity matrix:** built by a script from the hotkey function ids (`hotkeys.js`), the `case "…"` actions in `app.js`, the `lem3d-*` keys, the effect switches and the `__lem3d` panels. This stops features from being dropped silently.
- **Pin bumps:** bumping the web pin re-runs every oracle and reopens the rows that changed.
- **Device work is scripted:** `tools/frame-deploy.sh`, `frame-pull-report.sh` and the probe/benchmark builds. The user only puts on the headset and runs the QA checklist.

## Verification (end to end)

- `make verify`: Core oracles (sim, parse, render buffers, panel, indexes, UI view-models) are green on macOS arm64 and in linux/arm64 Docker.
- `godot --headless --path app -- --test`: scene tests, scripted pointer/pose replays, screenshot contact sheets.
- `tools/export.sh linux-arm64 && tools/frame-deploy.sh`, then on the Frame:
  - play one level of each Lemmings Plus difficulty and the Intro pack;
  - watch 3 solutions;
  - rewind, save/load state, replay insert, load/save `.nxrp`;
  - install everything from scratch in Setup;
  - export the config, import it into the web app, and confirm the progress is there;
  - read `perf.json` against the targets.

## Critical files (web side, read-only references)

- `LemmingsJS/lemmix/js/lemgame.js`, `level.js`, `pixels.js`, `styles.js`, `parser.js`, `game.js`, `panel.js`: the sim and engine to port.
- `LemmingsJS/tools/lemmix-node.js`, `nx-physics-test.js`, `nx-solve.js --verify`, `nx-fixtures.js`, `classic-node.js`: the oracle base.
- `LemmingsJS/3d/js/terrain.js`, `depth.js`, `portals.js`, `bridge.js`, `envgen.js`, `decals.js`, `replay-markers.js`, `minimap.js`, `cursor.js`: render buffers.
- `LemmingsJS/3d/js/app.js` (VR windows, actions, `__lem3d` at :6058), `vr.js`, `hotkeys.js`, `library.js`, `setup.js`, `solutions.js`, `config-store.js`, `audio.js`: UI, input, audio and persistence.
- `LemmingsJS/3d/plans/steam-frame-wolvic-apk.md`: add a one-line "superseded by lemmix-frame" note.

## User actions required

1. Developer mode on the Frame, plus pairing with the SteamOS Devkit Client (ssh key), just before device session 1.
2. Two headset sessions: bring-up (probe, feel, perf baseline) after phase 7, and acceptance after phase 8.
3. A Steamworks partner account before phase 9.
