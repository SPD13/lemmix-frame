# Lemmix for Steam Frame — the Godot implementation

The reference for working on the native app: how it is built, how its parts fit, what was learnt
on the device, and how to change it safely. Written on 2 Oct 2026 at commit `6c53a75`, after
device session 1. Read with `docs/plan.md` (decisions and phases), `core/README.md` (the C#
porting rules) and `docs/device-session-1.md` (the device checklist).

---

## 1. What this is

A native, standalone VR port of the Lemmix engine (NeoLemmix levels and styles) for the Valve
Steam Frame. It reproduces the play functions and menus of the web version (LemmingsJS, branch
`solution`, in `web/` as a pinned submodule), in Godot 4.7.2 .NET (C#), OpenXR, the Vulkan
Mobile renderer, exported for Linux ARM64.

- **Scope:** the Lemmix engine only (the classic DOS engine is out), VR only, play features only.
  The piece editor, galleries, solver queue and LAN launcher stay web-only.
- **No game data ships.** The player installs NeoLemmix, its styles and level packs in the app
  (downloads from neolemmix.com, or the upload server from a computer).
- **Correctness model:** the web code is the oracle. The core is ported method for method and
  checked bit-for-bit against dumps of the web engine (section 5). The app's look and behaviour
  follow the web's VR layer (`web/3d/js/app.js`, `vr.js`, …), except where the headset asked for
  a native departure (section 12).

### Status (2 Oct 2026)

| Area | State |
|---|---|
| Sim, level build, replays, rewind | Hash-identical to the web on all 1076 levels, the 85 solutions, fuzzed input, the rewind self-check |
| Render buffers (terrain, depth, portals, bridge voxels, decals, markers, envgen) | float32-identical to the web's for the sampled effect combinations |
| Panel, skill bar, minimap, cursor, sprites | Pixel-identical to the web's oracles |
| Indexes, settings store, hotkeys, library, config files | Identical to the web's oracles (natural sort included) |
| VR shell, windows, pages, audio, setup | Ported; semantic tests; pixel shots reviewed |
| Device | Runs on the Frame. Device session 1 done: input, controllers, setup page, popups fixed (section 8) |
| Parity matrix | 117 rows: 104 done, 12 wip, 1 n/a (`parity/matrix.json`) |
| Tests | Core 99 cases (70 methods; 1 skipped without `LEMMIX_DOWNLOAD_TEST`), green on the Mac and in sniper arm64; app 80, green; the export starts in sniper and on the Frame |

---

## 2. Repository layout

```
lemmix-frame/
  web/                      LemmingsJS submodule, pinned (a5b7b4f): the oracle and shared data
  core/Lemmix.Core/         C# net8.0 library, no Godot: everything that can be checked against the web
  core/Lemmix.Core.Tests/   xunit v3 executable (runs on the Mac and inside the Frame's runtime image)
  app/                      the Godot project (Lemmix.csproj, project.godot, openxr_action_map.tres)
  app/Data/                 web data the app ships: depth profiles, solutions, credits (tools/sync-web-data.sh)
  native/libopenmpt/        tracker music library builds (linux-arm64 in the sniper SDK image, macOS)
  oracle/                   Node dumpers running the web engine; out/ holds the committed reference dumps
  parity/                   matrix.json (every web feature, its oracle and status) + gen-matrix.js
  tools/                    build, test, export, render, deploy and device scripts (section 10)
  docs/                     plan, device session, probe, this document
  .tools/                   (not in git) Godot editors for macOS and linux-arm64
  build/                    (not in git) exports, shots, reports
```

Core folders: `Util` (JS number/string semantics), `Parse`, `Engine` (the sim), `Io`, `Oracle`
(state hashing), `Index` (levels/styles/music indexes), `Setup` (installer, downloads, upload
server), `Audio` (libopenmpt, music and sound resolution), `Render` (pure-data render buffers),
`Ui` (panel, minimap, cursor bitmaps), `Store` (localStorage and the web's JSON files), `Input`
(hotkeys), `Library` (tree, progress, favorites, recent, search, solutions).

App folders: `Main.cs`, `Shell/` (the app node and its partials), `Xr/` (input, VR manager,
controller models), `Board/` (the diorama), `Session/` (one level), `Ui/` (Canvas2D, Panel3D,
`Windows/` and `Pages/`), `Audio/`, `Test/` (in-engine tests, shots, probe).

---

## 3. Toolchain and environment

| Item | Version / place | Notes |
|---|---|---|
| Godot | 4.7.2 stable .NET, `.tools/Godot_mono.app` and `.tools/Godot_v4.7.2-stable_mono_linux_arm64` | `tools/godot.sh` runs the Mac editor; export templates in `~/Library/Application Support/Godot/export_templates/4.7.2.stable.mono/` |
| .NET | SDK 8+, `net8.0`; `global.json` selects the Microsoft.Testing.Platform runner | core sets `InvariantGlobalization`, unchecked int arithmetic |
| xunit | v3, an executable test project | so it runs self-contained inside the Frame's runtime container |
| Node | oracles in `oracle/` (puppeteer-core for page shots) | assets from `WEB_ASSETS` (default `../LemmingsJS`) |
| Docker | colima on the Mac (aarch64) | **use `tools/docker.sh`** (clean `DOCKER_CONFIG`: the user's config has a broken `credsStore`); bind mounts do not work (only another project's folder is mounted), so files go in and out as **tar streams** |
| Frame runtime images | `registry.gitlab.steamos.cloud/steamrt/sniper/{sdk,platform}/arm64` | the Frame itself launches devkit titles in **Steam Linux Runtime 4 ARM64** (section 8); sniper is the test image |
| Render box | `tools/render-box/Dockerfile.trixie` → `lemmix-render-box:trixie` | Debian trixie, Xvfb, Mesa 25 lavapipe: the Vulkan Mobile renderer in software. An LLVM 15 lavapipe crashed, hence trixie |

### Godot gotchas on the Mac

- **Headless Godot .NET hangs at exit on macOS.** Never wait on its exit: `tools/godot-run.sh
  <seconds> "<marker>" <args>` watches the output for a marker line and kills it.
- Tests and tools pass `--xr-mode off`; the no-runtime path can stall the start otherwise.
- **Never use a `[ModuleInitializer]` that touches Godot types.** It runs as the assembly loads,
  before Godot's interop is up; the exported build segfaulted on one. Register in a static
  constructor of a Node class instead (see `Shots.RegisterPageShots`).
- New resources (an imported `.png`) need an import before a run sees them:
  `tools/godot-run.sh 300 "import done" --headless --import --path app` (the marker never shows;
  the timeout ends it once the `.import` file is written). Export imports by itself.
- `Godot.HttpClient` and `Engine` clash with `System.Net.Http.HttpClient` and `Lemmix.Engine`:
  qualify them.

---

## 4. Architecture

```
            web/ (LemmingsJS, pinned)  ──oracle dumps──▶  oracle/out/*.json(.gz)
                                                                │ compared by
core/Lemmix.Core (no Godot) ◀───────────── core/Lemmix.Core.Tests
  Engine: LemGame (sim), Game (commands, timer, rewind), Level/LevelBuilder, Replay, Styles, Sprites
  Render: TerrainMesher, Depth, Portals, BridgeGeometry, Decals, ReplayMarkers, SkillBar, EnvGen  → plain buffers
  Ui: GamePanel, Minimap, CursorImages                                                           → RGBA bitmaps
  Index / Store / Input / Library / Setup / Audio                                                → data, files, HTTP
        │ used by
app/ (Godot)
  Main ─▶ Shell/App (Node3D, partial class) ─┬─ Xr: XrInput (OpenXR / scripted) → VrManager → IVrHooks (App.Input)
                                             ├─ Session/GameSession: one level (Game + views), stepped per frame
                                             ├─ Board/*: TerrainView, SpritePool, OverlaysView, EnvironmentView, SkillBarView
                                             ├─ Ui/Windows: VrWindows (toolbar, modal, catalog, settings, level text, strip, tooltip)
                                             ├─ Ui/Pages: VrPages (setup, solutions, controls, key hints, replays, QA) + VR keyboard
                                             ├─ Audio/GameAudio, Shell/PointerView, Xr/ControllerModels
                                             └─ App.Upload (LevelServer), DeviceCapture, Benchmark, Perf
```

The rule that keeps this testable: **anything that has a web counterpart with deterministic
output lives in the core and has an oracle**. The app turns core buffers into Godot resources and
wires input, windows and the XR session; it is tested with in-engine tests and reviewed shots.

---

## 5. The core and its oracles

### 5.1 Porting rules

`core/README.md` is the checklist; the essentials:

- **Numbers:** `JsMath.Round` (floor(x+0.5)), never `Math.Round`; `JsMath.ToInt32/ToUint32` for
  `|0`, `>>>0`; doubles in the JS operation order; the xorshift RNG in `uint`; `V8Math` for
  transcendental functions (fdlibm as V8 has it, FMA on arm64 matched); `ClampU8` for
  `Uint8ClampedArray` (ties to even).
- **Collections:** stable sorts only (`OrderBy`); replay entries in insertion order; loops over
  lemmings are index loops re-reading `Count`; `CopyFrom` covers every field.
- **Text:** invariant globalization, ordinal comparisons, `[0-9]` not `\d`, strip the BOM
  (`JsString.Trim` removes U+FEFF), JS case mappings (`JsCase`), JSON as V8 parses and prints it
  (`JsJson`/`JsValue`).
- **Sort order of the indexes:** `localeCompare(…, {numeric: true, sensitivity: "base"})` is
  reproduced by `NaturalCompare` (an ASCII order table derived from Node, digit runs by value, a
  Latin base-letter table), checked by its own oracle.
- **Files:** the Frame's filesystem is **case-sensitive** and the styles are not consistent
  (`Objects/` vs `objects/`): every read goes through `DiskFileSource`/`TreeSource`, which resolve
  paths ignoring case.

### 5.2 Oracles

`oracle/*.js` load the pinned web code in Node (the same files the browser runs) and dump
reference data to `oracle/out/` (committed; large ones gzipped). The C# tests regenerate the same
data and compare.

| Oracle | What it fixes | Test |
|---|---|---|
| `sim.js`, `fuzz.js` | per-frame state hashes (100-frame blocks) for every level with no input, the solutions, fuzzed assignments and commands | `Oracle/SimTests` |
| `build.js` | the level build (graphics, physics map, gadgets) per level | `Oracle/LevelBuildTests` |
| `probe.js` | picking and the info strip at random points, skill shadows | `Oracle/ProbeTests` |
| (rewind self-check) | going back gives the frame as first played | `Oracle/RewindTests` |
| `terrain.js`, `portals.js`, `bridge.js`, `markers.js`, `envgen.js` | render buffers (positions, colours, UVs, indices) incl. after digs and rewinds | `Render/*Tests` |
| `panel.js`, `skillbar.js`, `minimap.js`, `cursor.js`, `sprites.js` | panel and bar pixels, hit cells, sprite frames | `Ui/*Tests`, `Render/SkillBarTests` |
| `indexes.js` | levels/styles/music index.json | `Index/IndexTests` |
| `settings.js` | store, preferences, hotkeys, library, search, config files | `Store`, `Input`, `Library` tests |
| `webshots.js`, `pageshots.js`, `boardshots.js` | the web's windows, pages and board as pictures (for side-by-side review) | shots (section 9) |

`StateHash` is a 64-bit two-lane FNV/xorshift hash fed 32-bit words in the web's order
(`oracle/lib/hash.js` ↔ `Oracle/StateHash.cs`); a mismatch is reported at the first differing
frame, and the full JSON of that frame is dumped for diagnosis.

**Bumping the web pin:** `git -C web fetch … && git -C web checkout <sha>`, regenerate the
oracles that the change touches (`node oracle/<x>.js`), run the core tests, port the change, then
`make matrix` to refresh `parity/matrix.json` (it still records `239ebc5`; the pin is `a5b7b4f`).

---

## 6. The Godot app

### 6.1 Start-up and modes (`app/Main.cs`)

`Main` checks whether OpenXR initialised (Godot starts it from `xr/openxr/enabled`), sets
`UseXR`, then adds what the command line asks for (user arguments after `--`):

| Argument | What runs |
|---|---|
| (none) | `Shell/App`: the game |
| `--test [filter]` | `Test/TestRunner`: every `[AppTest]` method (name contains the filter) |
| `--shot <name> <file.png>` | `Test/Shots`: a named scene rendered to a PNG |
| `--probe` | `Test/Probe`: device report, writes `user://probe.json` |
| `--benchmark` | `Shell/Benchmark` with the app: writes `user://perf.json` |
| `--smoke` | quits after 0.5 s printing `smoke ok` |

`AppOptions.FromCommandLine` maps the web's URL parameters: `--level=<id>`, `--nxrp=<file>`,
`--assets=<dir>`, `--environment=none`, speed, solution.

### 6.2 `Shell/App` (one node, partial class)

| File | Responsibility |
|---|---|
| `App.cs` | build (`_Ready`: store, prefs, effects, audio, world environment, rig, diorama root, environment, VrManager, windows, pointers, pages, upload server); the frame loop; IVrWindowsHost; diorama placement; window placement (`WindowPose`); quit |
| `App.Level.cs` | level flow: enter, load, end, next/prev, solutions, GC regions per level |
| `App.Input.cs` | the ray (`Cast`: the question, a page, the shell's extra entries, the windows, the skill bar, the board), hover and press (`OnSelectPick`), beam length |
| `App.Hotkeys.cs` | keyboard and controller functions (`HotkeyDispatch` from the core decides; this applies) |
| `App.Pages.cs` | building the pages over live data; setup/library refresh after installs |
| `App.Upload.cs` | the level upload server: switch, URLs, events queued to the frame, settings files on the frame |
| `ShellEffects.cs` | the 8 render switches (read at start, written on toggle) |
| `ShellLibrary.cs` | the catalog's view of the core library |
| `PointerView.cs` | beams, impact dot, NeoLemmix cursor on the board, floor grid; fallback grip box and aim sphere |
| `Benchmark.cs`, `Perf.cs` | unattended performance run; per-section timers |
| `DeviceCapture.cs` | a picture of the headset's view on request over ssh |
| `KeyCodes.cs` | Godot keys → `KeyboardEvent.code` names (a Bluetooth keyboard uses the web's table) |

**The frame (`App.Frame(now)`)**, in order: upload-server events → pending level reload →
post-load collection → scripted head → presenting edge → session step (sim ticks, scene sync) and
the skill bar → windows layout → `Vr.Update` (input, grabs, sticks, hover) → windows placed on the
first real pose → pointers → level end. `_Process` calls `Frame(Now())`; tests drive `Frame`
themselves with `AppOptions.Manual`.

**Thread rule:** Godot objects are touched on the main thread only. Background work (installs,
downloads, the upload server's HTTP threads) posts closures: pages through `VrPage.Post`, the
server through `App._uploadEvents` (drained at the start of `Frame`; a server call that needs an
answer waits on a `TaskCompletionSource`, `App.OnFrame`).

### 6.3 XR rig and input

Built in code in `App.BuildRig`: `XROrigin3D` → `XRCamera3D`, and per hand three
`XRController3D` nodes on the poses `aim_pose`, `grip_pose`, `palm_pose`. Without a runtime (Mac,
tests) a `ScriptedXrInput` stands in, with a fixed head.

`Xr/XrInput.cs` `OpenXrInput.Poll()` fills a `HandState` per hand: `Connected`, `Aim`, `Grip`,
`Trigger`, `Squeeze`, `Lower`/`Upper` (A/X, B/Y), `StickClick`, `Stick` (y flipped to WebXR's
sign).

- **Aim and grip from the palm (Frame).** SteamVR 2.17 on the Frame leaves the controller's aim
  and grip action poses untracked for this app, while the palm pose tracks (section 8). When aim
  or grip have no tracking data, they are derived from the palm with the driver's component
  offsets: `aim = palm · handmodel⁻¹ · openxr_aim` (`OpenXrInput.FrameOffsets`, numbers from
  `/opt/steamvr/drivers/frame_controller/resources/rendermodels/frame_controller_<hand>/*.json`,
  left hand mirrored in x). Other controllers without aim fall back to the palm as is.
- **Action map** (`app/openxr_action_map.tres`): generic controller, Oculus Touch, Pico 4, hand
  interaction, and the **Valve Frame controller** (`/interaction_profiles/valve/frame_controller_valve`,
  extension `XR_VALVE_frame_controller_interaction`): trigger, squeeze, thumbstick (+click/touch),
  right A/B → `ax_button`/`by_button`, right menu; the left has a D-pad instead of face buttons:
  D-pad down → `ax_button` (lower), D-pad up → `by_button` (upper), view → menu.
- **Diagnostics:** `[xr]` log lines — session state changes; each hand's profile, every pose's
  tracking/confidence/origin, trigger and stick, on any change and every 3 s for the first two
  minutes.

`Xr/VrManager.cs` is the web's `vr.js` VRManager: the pointing hand (right first; a trigger on
the other takes over), trigger = click, trigger-drag past `VR_DRAG_THRESHOLD` (2 cm) moves the
board in 3 axes, grip grabs, both grips scale (0.15×–8×), sticks pan/tilt/dolly, face buttons go
to the hotkey table by hand role, focus loss holds the sim, recenter. Its hooks (`IVrHooks`) are
answered by `App.Input`. Key constants: `VR_PIXEL_SCALE` 2.5 mm per game pixel, GUI 0.6 m wide at
y −0.3, z −0.75; modal 0.42 m; catalog 0.62 m; tool buttons 4.5 cm.

### 6.4 Windows and pages

**Painting.** The web paints its VR windows on HTML canvases; the port has `Ui/Canvas2D` — the
subset of the canvas 2D API they use (paths, round rects, arcs, text with the web's fonts mapped
to the bundled Noto Sans Mono, gradients, images, global alpha) — painted into a `SubViewport` and
shown on a quad by `Ui/Panel3D` (W×H canvas pixels, a width in metres; `Hit()` turns a ray into
canvas pixels). Window code is a line-by-line port of the web's paint calls; window tests compare
the traced call list with the web's (`Test/WindowFixture`).

**Windows** (`Ui/Windows/VrWindows`): the toolbar (`VrToolbar`, icon buttons painted by
`BarIcons`), the question/notice (`VrModal`, via `AskConfirm`/`AskNotice`), the world catalog
(`VrCatalog`), settings (`VrSettings`), level text (`VrLevelText`), status strip and REPLAY badge
(`VrStatusStrip`), tooltip (`VrTooltip`), and the bar's placement (`VrWindowPlacement`: lock to
head / float / park below the board). `VrWindows.Pick` gives the ray's target, `Act` the press,
`ApplyHover` the hover. **`IconButtons`** (hover, tooltip, beam length) is built from the
toolbar's own `Buttons` array plus the windows' buttons — add a toolbar button to `VrToolbar` and
it is covered.

**Pages** (`Ui/Pages/VrPages`): the web's desktop pages in the headset, one at a time — setup,
solutions, controls dialog, key hints, replay files, QA checklist — plus the VR keyboard. A page
is a `VrPage`: CSS-like units (`U(css)` = css × `S` canvas px), widgets (`Button`, `Checkbox`,
`Select`, popups), regions for hit-testing, a scrolled body, background work through `Post`.

**The lobby (native, `Ui/Windows/VrLobby`).** The app's title screen, up in a session while no
level is on the board: NeoLemmix's main menu (`GameMenuScreen.pas`, numbers from
`data/title.nxmi`) as a 1.5 m screen 1.4 m off in the windows' frame - centred on the line the
windows open along, upright (square to the floor) as the windows are - built from the installed
`neolemmix/gfx/menu/` by `core/.../Ui/TitleArt.cs`: `background.png` tiled, `logo.png`, a footer
in `menu_font.png` (what the sign under the beam does; the app's version), and the scroller (the
reel turned by the two worker lemmings, `TitleScroller`, 12 px taller than NeoLemmix's so the text clears its dashed edges; lines in `VrLobby.ScrollerLines`). Three
signs float 12 cm in front, the headset's own: **PLAY** (`sign_play.png`) opens the world
catalog (setup when nothing is installed), **VR SETTINGS** (`sign_config.png`, its music note
swapped for a drawn headset) opens the VR window as the bar's VR button does, **QUIT**
(`sign_quit.png`) ends the app at once. Their key caps (F1, F3, Esc) are taken off
(`TitleArt.RemoveKeyCap`); under the beam a sign glows as NeoLemmix's does under the mouse
(`MakeClickableImageAuto`'s glow) and steps forward. While a window or a page is up the screen is
veiled and the signs put away. The catalog is never locked any more: its close (and Escape) go
back to the lobby. The toolbar's exit (the door) also comes back here, after asking. Without the menu art the screen and signs are drawn plainly. Picks are
`lobbyplay`, `lobbyvr`, `lobbyquit` (`App.ActOnLobby`); shots `vr-lobby`, `vr-lobby-vr`.

**Placement (native).** Windows and pages open from the head's position **facing the play
space's default forward** (its −Z, turned by the yaw correction as the board is), not along the
gaze: `App.WindowPose`/`FrontOf`, used by both hosts' `PlaceWindows` and on recenter.

### 6.5 Render order (everything in the transparent pass sorts by priority)

| Priority | What | Depth |
|---|---|---|
| −128…21 | board materials (clamped web render orders), opaque pass first | tested |
| 0…4 | skill bar parts (web orders 50…54 minus 50), minimap 1 | — |
| 0 | the lobby's screen (opaque), its signs, scroller and veil (transparent, behind the windows) | tested |
| 54 / 55 | pages / page buttons and the VR keyboard (`GUI_ORDER_PAGE[_BTN]`) | off |
| 55 | toolbar icons, status strip, volume (`GUI_ORDER_BAR_TOOL`) | off |
| 56 | modal, catalog, settings, level text (`GUI_ORDER_MODAL`) | off |
| 57 | modal buttons; 58 tooltip | off |
| **59** | **controller models** (`ControllerOnTop`) | squeezed to the near plane |
| 60 | beam, impact dot, board cursor (`VR_MARK_ORDER`) | off |

Opaque materials always draw before transparent ones whatever their priority; anything that must
order against the windows has to be in the transparent pass (write `ALPHA`, or a transparent
`StandardMaterial3D`).

### 6.6 The board

`Session/GameSession` owns one level: the core `Game`, and the views it syncs every frame.

- `Board/TerrainView`: the chunk meshes `TerrainMesher` builds (dirty chunks re-meshed, on the
  thread pool when many), the level and decal textures.
- `Board/BoardMaterials` + `RawColor`: three.js r147's unmanaged colour reproduced —
  `ALBEDO = srgb_to_linear(texel × vertexColor)` — so the board's colours match the web's.
- `Board/SpritePool`: one MeshInstance3D per captured sprite draw (the web's BillboardPool); only
  changed meshes/materials/transforms call into Godot.
- `Board/OverlaysView`: clear-physics overlay, replay markers and labels, skill shadows.
- `Board/EnvironmentView`: the room around the board (EnvGen pictures, rings of floor/walls);
  `Board/SceneryView` in their place when the gallery has a scenery (6.11).
- `Board/SkillBarView`: the skill bar as relief meshes from `Render/SkillBar`.

### 6.7 Audio

`Audio/GameAudio`: sound cues from `neolemmix/sound` (wav/ogg/mp3 loaders), positioned at the
emitter in a session; music by `Audio/MusicResolver` (the level's MUSIC list, the pack's rotation,
the pack folder, `neolemmix/music`) played through libopenmpt (`Audio/OpenMpt`, P/Invoke into
`libopenmpt.so` shipped in the export's data folder) into an `AudioStreamGenerator`.

### 6.8 Persistence and data on the device

| What | Where (on the Frame: `~/.local/share/godot/app_userdata/Lemmix/`) |
|---|---|
| Settings (the web's localStorage keys `lem3d-*` and others) | `lem3d-store.json` (`Core/Store/LocalStore`: debounced atomic saves, flushed on quit/pause; a file that does not parse is kept as `.bad`) |
| Installed assets | `assets/` (`neolemmix/`, `levels/` + `index.json`s, `units.json`) |
| Import / export folders | `import/`, `export/` |
| Replays | `replays/` |
| Device reports | `probe.json`, `perf.json`, `qa.json`, `controller-models.json`, `capture-<n>.png` |
| Godot log | `logs/godot.log` (+ dated copies) |
| Native-only keys | `lemmix-frame-upload-server` (`on`/`off`) |

### 6.9 Setup, installs and the upload server

- `Core/Setup/Installer`: units (engine, styles, level dirs) recorded in `units.json`, zip kind
  detection, level-zip mapping, replace/uninstall, index rebuild (levels, styles, music).
- `Core/Setup/Downloads`: the three official zips from neolemmix.com (resume, zip check, retry).
- `Ui/Pages/VrSetupPage`: big "Get NeoLemmix / Get Style Packages / Get Lemmings Plus Packs"
  buttons, installed level dirs with delete, the upload-server card, credits. Scale 2.1
  (native; the web's is 1.5). No zip buttons, no configuration card (both moved to the browser).
- `Core/Setup/LevelServer` (HttpListener; started from the setup page's checkbox, remembered):
  port 8642 or the next free one; answers **local-network addresses only**; serves
  `Setup/upload.html` (embedded resource). API: `GET /api/list`, `PUT /api/file` (via a hidden part
  file), `DELETE /api/entry`, `POST /api/plan|install` (installs an uploaded zip through the
  Installer and removes it), `POST /api/rescan`, `GET|POST /api/config?kind=controls|prefs|progress`
  (the web's three settings files; the app exports/imports on the frame; progress merges).
  Paths are kept inside `assets/levels`; hidden names and `index.json` are refused.

### 6.10 Controllers (`Xr/ControllerModels.cs`)

- **Models:** the runtime's own, as Valve asks for the Frame — `XR_EXT_render_model` +
  `XR_EXT_interaction_render_model` through Godot's `OpenXRRenderModelManager` (one per hand
  under the origin; project setting `xr/openxr/extensions/render_model=true`). Moving parts
  (trigger, buttons, stick) are animated by Godot from the runtime's node poses.
- **Drawn on top** (`ControllerOnTop`): each part's material becomes a shader material that
  squeezes its clip depth into [0.999, 1] (reverse Z), keeps its texture/colour/roughness/metallic,
  writes depth (its parts still hide one another), and draws at priority 59.
- **Lights:** two directional lights with a cull mask on the controllers' own layers (19 left,
  20 right); the rest of the scene is unshaded.
- **Sticker:** the Lemmix logo's character, cut out of the app icon with macOS Vision
  (`app/Xr/sticker.png`). On a part named like a sticker (sticker/badge/decal/logo/label/emblem)
  it becomes that part's texture; otherwise a `Decal` on the outer side of the controller's head,
  at the largest flat patch found by casting rays at the model's triangles in the grip frame
  (binned in y/z), upright with the grip's +Y.
- **Report:** `controller-models.json` — per hand the top-level path, every node/mesh/material,
  the grip in the model's frame and where the sticker went.
- **Fallback:** without render models, `PointerView` draws a green box at the grip and a sphere at
  the aim (hidden for a hand whose model is shown).

### 6.11 Scenery: a gallery's room to the horizon (native only)

A gallery (the level's theme style) may have a **scenery**: pictures made offline from the style's
own pieces by `tools/scenery-gen`, kept under the asset root at `3d/env/<style>/scenery/`
(`scenery.json` + PNGs; never in git, like the web's `3d/env/<style>/`). When it has one,
`EnvironmentView` builds none of envgen's ring pictures and `SceneryView` shows instead:

- a ground disc (115 m) with a seamless tile (`ground.png`, 1.28 m, trilinear so no mip seams);
- one open drum per strip round the player's place, near to far: `rubble` 3.8 m, `pillars`
  6.5 m, `overhang` 9.5 m (hung from 5 m up, its top melting into the sky), `outcrops` 14 m,
  `ridge` 25 m, `far` 44 m, `horizon` 78 m;
- a sky sphere (120 m) painted by elevation: zenith, high, horizon, below.

Every strip has **2048 texels round**, so a texel spans the same angle near or far (about the
board's own pixel seen at 0.9 m): one pixel-art size, like a 2D game's parallax layers. The
strips are drawn calm straight ahead (u = 0.5, where the board is): nothing near, the far ridges
low, so the board stands against the haze.

One unshaded shader (`SceneryView.ShaderCode`) paints everything: the texel graded (`grade`,
`desat` per strip), then mixed toward the sky's colour *in the direction it is seen* by
`SceneryLook.Haze` (1 - e^(-d / distance_m), capped, plus a mist thickening toward the ground
far away), so the far strips melt into the horizon. Alpha test, no blending, nothing sorted;
an ordered dither of one step of the 10-bit buffer keeps the dark gradients from banding. The
haze, colours and grades are the manifest's, read at run time: tune `scenery.json` without
making the pictures again. The rings are pushed out together (`SceneryLayout.Scale`) when a wide
level makes the room's first ring wider than the nearest strip.

Make one: `cd tools/scenery-gen && WEB_ASSETS=../../../LemmingsJS dotnet run -c Release -- gen
<style>` (about 4 s; also writes `preview.png`, the view from the player's place with the haze).
The recipe (`Layers.Recipe`) and the piece sorting (`Pieces.cs`: mass, spire, tuft, hang,
rubble, by measurement and colour; steel, bridges, signs and grey pieces left out) are the same
for every style; it was tuned on `orig_dirt`. Copy to the Frame with `tools/frame-scenery.sh
<style>` (`--remove <style>` takes it off). Look round in the render box with the `vr-scene` shot
and `SHOT_LOOK="yaw,pitch"`, `RENDER_EXTRA="3d/env/<style>/scenery"`.

The room's modes are `none`, `fog` (native: envgen's haze alone, as galleries looked before
sceneries) and `full` (the scenery, else the rings). **How to make a scenery for another
gallery:** `docs/scenery.md`.

**Revert:** delete `3d/env/<style>/scenery/` (the room is the rings again for that gallery),
run with `--scenery=off`, or revert the commits on branch `scenery`.

---

## 7. Native departures from the web (deliberate)

| Departure | Why |
|---|---|
| Frame controller models with the Lemmix sticker, drawn on top; no green box/sphere | Valve's requirement; device session 1 |
| Aim/grip derived from the palm pose on the Frame | SteamVR leaves them untracked (section 8) |
| Exit button (door icon) at the left end of the toolbar: with a level, asks "Back to the lobby?" and puts the level away (`App.ExitToLobby`); the lobby's QUIT ends the app | a native app needs a way out |
| Windows and pages open along the default forward, not the gaze | device session 1 |
| Setup page: larger scale, big Get buttons, no zip buttons, no config card, minimal text | device session 1 |
| Level upload server + browser page (files, folders, zips, settings backup) | levels cannot ship |
| `CommandSelectSkill(0)` selects the first skill; a release-rate click changes the rate once | web bugs, fixed in both (web `a5b7b4f`) |
| Preferences imported from a computer apply at the next start | effects are read at start, as the web's reload |
| A lobby (NeoLemmix's title screen: PLAY, VR SETTINGS, QUIT) at start instead of a locked catalog; the catalog closes back to it | a title screen for the native app (6.4) |
| A gallery with a scenery (`3d/env/<style>/scenery/`) shows it instead of envgen's rings; `--scenery=off` restores the rings | a room going to a far, hazy horizon (6.11) |

---

## 8. The Steam Frame: what the device showed (session 1, 2 Oct 2026)

| Item | Finding |
|---|---|
| OS / user | SteamOS (holo), aarch64, kernel 6.18; ssh `steamos@frame.local` with the user's key |
| GPU / driver | Qualcomm Adreno 750, Mesa Turnip, Vulkan 1.4.359; Godot Mobile renderer works |
| OpenXR runtime | SteamVR/OpenXR 2.17.10, OpenXR 1.0.54 (Godot falls back to 1.0) |
| Refresh / target | 72 Hz reported (the only rate exposed), render target 1728×1728 per eye, foveation supported (`XR_FB_foveation`, `XR_META_foveation_eye_tracked`) |
| Runtime for devkit titles | **Steam Linux Runtime 4 ARM64** (`SteamLinuxRuntime_4-arm64`); sniper is also installed. The build (tested in sniper) runs in SLR 4 |
| Probe stress scene | p50 13.89 ms (vsync-locked at 72 Hz), p99 14.41 ms, GC 0 |
| Controller profile | without a Frame profile SteamVR binds `khr/generic_controller`; with it, `valve/frame_controller_valve` |
| **Poses** | SteamVR delivers the **palm pose** (`/pose/openxr_handmodel`) but leaves **aim and grip** (`/pose/tip`, `/pose/grip`) untracked for this app — even with bindings identical to commercial games'. Hence the palm-derived poses (6.3). Worth reporting to Valve and re-checking after SteamVR updates |
| Buttons | delivered (trigger, A/B, D-pad, stick) |
| Render models | load and animate; `XR_EXT_render_model` and `XR_EXT_interaction_render_model` present |
| Steam Input | also exposes the controllers as a virtual Xbox pad (`SteamVirtualGamepadInfo`); no effect on OpenXR input observed |
| Vulkan loader noise | "Failed to find vkGetInstanceProcAddr in layer VALVE_rpo / fdm_injection", missing overlay layers: harmless |

**Diagnosis tools that worked:** `--verbose` (Godot lists extensions, bindings, profile changes);
SteamVR logs in `~/.local/share/Steam/logs/` (`xrclient_Lemmix.txt`, `vrserver.txt`,
`controller.txt`); SteamVR's generated bindings in `~/.config/openvr/config/openxr/steam.app.<id>_*_binding.json`;
an `override.cfg` next to the executable to flip a project setting without rebuilding (e.g.
`[xr] openxr/extensions/render_model=false`); `tools/frame-capture.sh` for the headset's view.

---

## 9. Testing

| Suite | Run | Contents |
|---|---|---|
| Core | `make core` (Mac), `make core-linux` (sniper arm64, case-sensitive FS) | xunit v3: oracle comparisons, installer, upload server over HTTP, store, hotkeys, library, audio. `LINUX_ASSETS=1` streams the assets so the oracle tests run on Linux too; `LEMMIX_DOWNLOAD_TEST=1` downloads the real zips |
| App | `make app-test` (`-- --test [filter]`) | `[AppTest]` static methods in `app/Test/*Tests.cs`, run inside Godot headless: VR manager with scripted poses, windows, pages (with fake backends, `PageFixture`), the shell end to end (`ShellTests.Rig`: a real App with scripted input and a manual clock), board, audio, controllers, upload server, XR input maths |
| Smoke | `make app-smoke`, `make linux-smoke` (exported build in sniper) | the app starts; the export starts |
| Shots | `tools/render.sh build/shots --shot <name> /out/<file>.png` | the render box (Vulkan Mobile, Xvfb): `board*`, `terrain`, `skillbar`, `vr-scene`, `vr-lobby`, `vr-lobby-vr`, `vr-catalog`, `win-*`, `windows-scene`, `windows-over-ui`, `page-*` (e.g. `page-setup`, `page-setup-levels`, `page-setup-upload`), `controllers`, `controllers-over-scene`, `icon-quit[-hover]`. `RENDER_ASSETS=1` for shots that need levels |
| Gate | `make verify` | core, core-linux, app-smoke, app-test, linux-smoke |

A test fails by throwing (`Check.True/Equal/Near`). An app test that needs the scene tree adds its
nodes to `SceneTree.Root` and frees them in `finally`. A test that waits on work posted to the
frame pumps `rig.Frame()` (the test owns the main thread; Godot's deferred calls do not run
during a test).

---

## 10. Build, deploy, device workflow

```
make export-linux                           build/app/linux-arm64 (Lemmix.arm64, Lemmix.pck, data_*: ~152 MB)
tools/frame-deploy.sh [play|probe|benchmark] rsync to ~/devkit-game/lemmix, register the title with Steam, set its argv
FRAME_GODOT_ARGS='"--verbose"' tools/frame-deploy.sh play   engine arguments too
tools/frame-pull-report.sh                  *.json reports → build/frame/
tools/frame-capture.sh                      the headset's view now → build/frame/captures/
```

- Defaults: `FRAME_HOST=frame.local`, `FRAME_USER=steamos`, ssh's own keys (or `FRAME_KEY`).
- Registration uses the Devkit Client's own scripts on the device
  (`python3 ~/devkit-utils/steam-client-create-shortcut --parms '{…}'`, compat tool
  `SteamLinuxRuntime_4-arm64`). The title is **lemmix** in the library; its command line is
  `~/devkit-game/lemmix-argv.json`, read at each launch — so play/probe/benchmark switch without
  re-uploading.
- **Clean device builds.** Build what goes to the headset from a commit, not from a working tree
  another agent is editing: `git worktree add ../lemmix-frame-device <sha>` (once), then
  `git -C ../lemmix-frame-device checkout -f --detach <sha>`, `dotnet build`, `tools/export.sh`,
  `tools/frame-deploy.sh` from that worktree (`.tools` and `native/libopenmpt/out` symlinked in).
- Headless smoke on the device itself:
  `ssh steamos@frame.local 'cd ~/devkit-game/lemmix && ~/.local/share/Steam/steamapps/common/SteamLinuxRuntime_4-arm64/run -- ./Lemmix.arm64 --headless --xr-mode off -- --smoke'`.
- Do not `pkill -f Lemmix.arm64` over ssh: the pattern matches the ssh command itself. Use
  `pgrep -x`/`pkill -x Lemmix.arm64`.

---

## 11. Performance

- **Measuring:** `--benchmark` tours the ten largest levels in the default and the heaviest look at
  ×8 with skills and a 170-frame rewind every 5 s; `perf.json` gives frame times, frames over
  budget, rewinds, draw calls/primitives, GC pauses and collections, garbage, memory, and per
  section of the frame (`Perf.S`: Sim, Objects, Lemmings, Pools, Mesh, TexUpload, MeshSwap, Step,
  Env, Bar, Shell, Rewind, Resync, Restore, Bench) its time and garbage, also inside slow frames.
- **GC strategy:** concurrent GC, `SustainedLowLatency` during play, a full compacting collection
  after each level load, then a no-GC region (192 MB, falling back to 128/64) until it is used up.
- **Allocation discipline:** pooled saved states (8 reserved at load), no per-frame garbage in the
  panel, skill bar, sprites, captures, audio buffers; numeric keys instead of strings for frame
  caches; Godot wrappers disposed; scene objects touched only when they change.
- **Rewind:** the zombie-map clear limited to what was written; resync in halves; dirty chunks
  meshed on the thread pool (row bands for a lone big chunk); the refresh spread over 3 frames
  with the old frame shown.
- **Results on the Mac (headless, loaded host):** default look p99 8.3–10.1 ms, 0 GC pauses;
  heavy look p99 up to 18.8 ms; worst rewind frame ≤ 21.9 ms (the 170-frame re-simulation is
  atomic: 10–21 ms here, 3–6 ms on a quiet Mac).
- **Still to measure on the Frame (benchmark not yet run there):** GPU cost of the heavy look's soft
  colour blend (13.1 M vertices on To_Infinity vs 0.44 M default), draw calls (one per sprite,
  ~465 terrain chunks), the 1.9 MB level texture re-upload per digging tick, native memory (RSS
  2–3.3 GB after a heavy level in the container).
- **Target:** the Frame runs at 72 Hz here (13.9 ms); the plan's 90 Hz target is 11.1 ms.

---

## 12. Known differences and limitations

- Translucent blending: three.js blended sRGB values, Godot blends linear — slight differences on
  translucent pieces.
- Fonts: the bundled Noto Sans Mono stands for the web's monospace fonts (window text is compared
  by paint calls, not pixels).
- Dark colours may band slightly with a 10-bit buffer.
- GSM-encoded WAVs are silent (as in the browser); the AdLib music fallback was dropped.
- The room may show fog only on some levels (to check on the device).
- `render.flat_view` (the 2D view inside VR) is n/a: the headset always shows the diorama.
- The APK fallback is not set up.

---

## 13. Open items

1. **Parity rows still wip** (`parity/matrix.json`): game command fuzz oracle; effect switches'
   remaining integration; VR session details; controls dialog, key hints, level text, library,
   replay files, settings, setup, solutions, VR keyboard integration checks (mostly ported, rows
   awaiting their last checks or device confirmation). Regenerate the matrix for the `a5b7b4f` pin.
2. **Device session 1, remaining steps:** play one level per difficulty, replay tools, windows
   tour, comfort; **the benchmark on the Frame** (`frame-deploy.sh benchmark`), the QA checklist
   (`qa.json`); then set defaults (foveation, refresh rate if more become available, mesh batching).
3. **Controllers:** confirm the sticker's place on the real model (a decal on the head's outer side
   today); report the untracked aim/grip poses to Valve and drop the palm fallback when fixed.
4. **The Frame button names in the UI:** Valve's review asks on-screen glyphs to match Frame names
   (or Xbox ones); the controls dialog and key hints still use the web's A/X, B/Y wording.
5. **The web commit `a5b7b4f` is local to the user's LemmingsJS** — not pushed; a fresh clone of
   this repo cannot fetch its submodule pin until it is.
6. Packaging and Steam (depots, store page) wait for a Steamworks partner account. NeoLemmix is
   CC BY-NC 4.0: the app must be free and credit its authors (the setup page's credits).
7. The Mac's disk is nearly full (~3.5 GB free); exports and Docker images need room.

---

## 14. How to…

**Add a toolbar button.** Draw its icon in `BarIcons` (64×64, `BarToolIcon` frame) and map its
name in `BarIcons.ByName`; create it with `Make("<name>")` in `VrToolbar` and put it in
`LeftTools` or `RightTools` (it joins `Buttons`, hence `IconButtons`: hover, tooltip, beam
length); add its tip in `VrWindows.TipText` and its action in `VrWindows.Act` (use `AskConfirm`
for anything that cannot be undone); give `IVrWindowsHost` a default-implemented member if the
app must do something (test hosts keep compiling); add a `WindowsTests` case and a shot.

**Add a card to a page.** Write a `float XCard(float x, float y, float w, bool paint)` that
measures when `paint` is false and paints when true (the page calls it twice), add it to
`PaintPage` with `Card(...)`, give widgets region ids handled in `OnPress`; data through the
page's backend interface (add members with default implementations so the fakes in
`PageFixture` keep compiling); a `page-*` shot in `PageShots` and a `PagesTests` case.

**Port a web feature.** Find its row in `parity/matrix.json`; if it has deterministic output, put
the logic in the core and write or extend an oracle in `oracle/` first, then the C# twin and its
test; the app part goes through the core's data. Mark the row done when `make verify` is green.

**Add an upload-server endpoint.** A route in `LevelServer.Serve`, the action as a public method
(tests call it over HTTP in `LevelServerTests`), anything touching the app through a delegate set
in `App.Upload` and run with `OnFrame`; the page in `upload.html` (plain HTML/JS, no external
resources: the computer may have no internet).

**Investigate something in the headset.** Deploy with `FRAME_GODOT_ARGS='"--verbose"'`, read
`~/.local/share/godot/app_userdata/Lemmix/logs/godot.log` and the SteamVR logs over ssh, take a
picture with `tools/frame-capture.sh`, and try settings without a rebuild through `override.cfg`.
