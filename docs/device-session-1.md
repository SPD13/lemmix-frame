# Device session 1: first run on the Steam Frame

About an hour with the headset. Everything up to here was built and checked on the Mac: the
sim, the level build, the board, the windows, the audio and the installer are verified against
the web version. This session checks what only the Frame can show: the runtime, the controllers,
how the board and windows look and feel, and how fast it runs.

## Before (once, about 10 minutes, no headset needed except for pairing)

1. **Developer mode on the Frame:** Settings > System > Developer Mode, on.
2. **The SteamOS Devkit Client** on a computer on the same network: the Mac, or the Windows PC.
   It comes with the Steamworks SDK tools and from Valve's SteamOS devkit page.
3. **Pair:** on the Frame, Settings > Developer > Pair new host. In the Devkit Client's
   Devkits tab, Register next to the Frame, then confirm on the headset.
4. Tell Claude:
   - the Frame's address (its hostname, `frame` by default, or IP);
   - which computer paired.

   Pairing put a key at `~/.config/steamos-devkit/devkit_rsa` on that computer (Windows:
   `%LOCALAPPDATA%\steamos-devkit\devkit_rsa`). From the Mac, `tools/frame-deploy.sh` uses that
   key to copy new builds without going through the Client again. If the PC paired, copy the key
   to the same path on the Mac.

## The build

Claude makes `build/app/linux-arm64/` (`make export-linux`). Upload it once through the Devkit
Client's **Title Upload**:

| Field | Value |
|---|---|
| Name | Lemmix |
| Local folder | `build/app/linux-arm64` |
| Start command | `Lemmix.arm64` |
| Runtime | Steam Linux Runtime 3.0 ARM64 (Sniper) |

It then appears under Library > Non-Steam > Devkit Game. Later builds:
`FRAME_HOST=… FRAME_USER=… tools/frame-deploy.sh`. The user is the one the Client shows for the
Frame; Claude checks it with you at the start.

## In the headset (about 45 minutes)

1. **Probe, unattended, 1 minute.**
   - Start Lemmix with the start command `Lemmix.arm64 -- --probe`, or run
     `tools/frame-deploy.sh --probe`.
   - Look ahead and don't move.
   - It writes `probe.json`: the renderer and GPU, the OpenXR runtime and its extensions, the
     controller profiles, refresh rates, foveation support, and frame times on a stress scene.
2. **First run.** Start Lemmix normally.
   - The setup window opens. Install NeoLemmix, the styles and Lemmings Plus (about 120 MB from
     neolemmix.com).
   - Check that the progress and messages read like the web's setup page.
   - **Upload from a computer:** tick "web server for level uploads" in the setup page and type the
     address it shows into a browser on a computer on the same Wi-Fi. Upload a level pack's
     folder, upload a pack's zip and press install, then delete a folder. Check that the headset's
     library follows each time. Turn it off and check that the page no longer answers.
3. **Play.** One level of each Lemmings Plus difficulty and one of the Intro pack. Check:
   - the controllers: the Frame's own models, drawn from the runtime (no green box at the grip),
     their trigger, buttons and stick moving with your fingers, and the Lemmix sticker on the
     outer side of each head, upright and not smeared (its spot is worked out from the model; say
     if it should sit elsewhere);
   - the beam, a trigger on a lemming, and the skill bar presses (left/right/middle, as on the web);
   - grip drag, two-grip scale, the thumbsticks (pan, tilt, dolly);
   - the toolbar: pause, restart, prev/next, worlds, solution, mute/volume, lock/park/move.
4. **Replay tools:**
   - watch a solution (markers on the board);
   - rewind and frame steps;
   - save/load state;
   - replay insert;
   - save a replay, then load it back.
5. **Windows:** the catalog (scroll with the stick, search with the VR keyboard), settings
   (the 8 effects), the level text, the controls dialog, solutions list, setup page.
6. **Feel and comfort:** board size and distance, window legibility, beam precision, anything
   that strains the eyes.
7. **Benchmark, unattended, about 3 minutes.**
   - Start with `Lemmix.arm64 -- --benchmark`.
   - It tours the heaviest levels with every effect at ×8 with rewinds, then writes `perf.json`.
8. **QA checklist.**
   - In the settings window, "QA checklist" lists the checks above with pass/fail and a note.
   - It saves `qa.json`.

## After

`tools/frame-pull-report.sh` fetches `probe.json`, `perf.json`, `qa.json` and
`controller-models.json` (the controller models' parts and where the sticker went) into
`build/frame/`. Claude reads them and sets the device defaults:
- the renderer;
- the controller profile;
- foveation and refresh rate;
- mesh batching.

Claude also fixes what failed, and the next session is the acceptance pass.
