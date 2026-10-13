# Lemmix for Steam Frame

A native, standalone VR build of the Lemmix engine (NeoLemmix levels and styles) for the
Valve Steam Frame. It is a port of the web version, [LemmingsJS_VR](https://github.com/SPD13/LemmingsJS_VR)
(https://lemmix.spd13.us), whose every play function and menu it replicates. The web repo
sits in `web/` as a pinned submodule: it is the reference the port is tested against, and
the source of shared data (depth profiles, solutions, hotkey presets, config file formats).

[![Lemmix for Steam Frame: trailer](https://img.youtube.com/vi/txD72wqSjrw/maxresdefault.jpg)](https://youtu.be/txD72wqSjrw)

*The trailer (1:26), on YouTube.*

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

## Install on your Steam Frame

Lemmix is not in the Steam store. The Frame runs it the way it runs games under development: as a
**devkit title**, which needs the headset's **Developer Mode**. The installer app on your computer finds the headset,
pairs with it, copies the game to it over the network and adds it to your Steam library. The same
app updates the game later. You only need the
computer for the install (about 5 minutes). After that the game runs on the headset alone.

**What you need**

- A Steam Frame on Wi-Fi, with its system up to date.
- A computer on the **same network**: Windows 10 or 11, macOS 12 or later, or Linux. Nothing needs
  installing on it: the installer app brings everything it uses.
- About 1 GB free on the Frame:
  - about 160 MB for the game (a 65 MB download);
  - about 430 MB for Steam Linux Runtime 4, which Steam downloads at the first launch if it isn't
    there yet;
  - about 300 MB for the NeoLemmix data, which the game downloads at its first start (about 120 MB
    of downloads).

### 1. Turn on Developer Mode on the Frame (once)

1. In the headset, open **Settings > System**.
2. Switch **Developer Mode** on. A **Developer** section appears in Settings.

Developer Mode does two things:
- It lets the Frame run titles that are not from the Steam store.
- It starts the SteamOS devkit service. With that service, a computer on your network can *ask*
  to pair with the headset. No computer gets access until you accept it in the headset (step 3).

Leave Developer Mode on while you use Lemmix.

The Frame's network name is usually `frame`, so its address is `frame.local`. The installer looks
for it on its own. If it can't find it, it asks for the address. You can then type the Frame's
**IP address**, which is in its Wi-Fi settings (the details of the connected network).

### 2. Download the installer app

| Computer | Download |
| --- | --- |
| Windows 10, 11 | [Lemmix-Frame-Installer-Windows.exe](https://github.com/SPD13/lemmix-frame/releases/latest/download/Lemmix-Frame-Installer-Windows.exe) |
| macOS 12 or later | [Lemmix-Frame-Installer-macOS.zip](https://github.com/SPD13/lemmix-frame/releases/latest/download/Lemmix-Frame-Installer-macOS.zip) |
| Linux (PC, Steam Deck) | [Lemmix-Frame-Installer-Linux-x86_64.tar.gz](https://github.com/SPD13/lemmix-frame/releases/latest/download/Lemmix-Frame-Installer-Linux-x86_64.tar.gz) |
| Linux (ARM64) | [Lemmix-Frame-Installer-Linux-arm64.tar.gz](https://github.com/SPD13/lemmix-frame/releases/latest/download/Lemmix-Frame-Installer-Linux-arm64.tar.gz) |

The links always give the latest version. The app always installs the latest release of the game,
so you don't need a new copy of the app for each release.

The app is not signed with a paid certificate, so Windows and macOS warn the first time it is opened:

- **Windows:** double-click `Lemmix-Frame-Installer-Windows.exe`. If Windows says "Windows protected
  your PC", choose **More info**, then **Run anyway**. If the browser warns that the file is not
  commonly downloaded, choose to keep it.
- **macOS:** open the `.zip` (Safari may already have done it). You can move **Lemmix Installer** to
  your Applications folder. Double-click it. If macOS says it cannot verify the app ("Not Opened"):
  1. Choose **Done**.
  2. Open **System Settings > Privacy & Security**.
  3. Scroll down to **Security**, choose **Open Anyway** next to the app's name, and confirm.

  When the app asks to **find devices on local networks**, choose **Allow**: that is how it reaches
  the Frame.
- **Linux:** unpack the download and run the app, for example in a terminal:
  `tar -xzf Lemmix-Frame-Installer-Linux-x86_64.tar.gz && ./Lemmix-Frame-Installer`. It opens in
  your web browser and quits when the page is closed.

### 3. Connect, pair and install

**Step 1 of the app, connect to the Frame.** The app looks for the Frame on the network and connects
to it. Four checks turn green:

1. **Frame found on the network.**
2. **Developer Mode is on.**
3. **Paired with this computer.** This needs you the first time only:
   1. Put the headset on, with Steam's menus showing (not a game).
   2. Choose **Pair with the Frame** in the app.
   3. The Frame shows a pairing request from a **development host**. It shows your computer's IP
      address and the key's name, `lemmix-installer@<your computer>`. **Accept it within 30
      seconds.**

   If you miss the request or decline it, choose **Pair with the Frame** again.
4. **Frame ready for Lemmix:** Steam is running, and there is enough free space.

If a check turns red, it says what is wrong and what to do. For example: wake the headset, turn
Developer Mode on, or type the Frame's IP address in **Frame address**. Fix it, then choose **Check
again**.

**Step 2 of the app, install.** The app shows what is on the Frame and the latest version on GitHub.
Choose **Install**:
1. The game is downloaded and sent to the Frame; a progress bar shows.
2. The app checks the download's checksum.
3. It installs the game in `~/devkit-game/lemmix` and registers it with Steam.

It ends with **Installed! Put the headset on and play.**

**What pairing does.** Accepting does two things on the Frame:
- it adds the installer's key to the authorized keys of its user, `steamos`;
- it switches its SSH server on.

The key stays on your computer:
- macOS and Linux: `~/.config/lemmix-frame/frame_rsa`;
- Windows: `%LOCALAPPDATA%\lemmix-frame\frame_rsa`.

The app and the scripts below share that key: a computer paired by one is paired for the other. Have
you already paired this computer with the SteamOS Devkit Client, or set up SSH on the Frame yourself?
Then the installer uses that access and skips pairing. Did you set a password in **Settings >
Developer > Set User Password**? Then you can also sign in with it (**Sign in with it instead**)
rather than pair in the headset. The app then pairs this computer for the next times.

#### Installing with the scripts instead

The scripts do the same as the app from a terminal, for older systems or scripted installs. They pair
the same way. They only need what the system has (`ssh` and `curl`).

**macOS or Linux:** open Terminal (on macOS: Applications > Utilities > Terminal), paste this line
and press Enter:

```
curl -fsSL https://github.com/SPD13/lemmix-frame/releases/latest/download/install.sh | bash
```

**Windows:** open PowerShell (Start menu, type `PowerShell`, Enter), paste this line and press Enter:

```
powershell -ExecutionPolicy Bypass -c "irm https://github.com/SPD13/lemmix-frame/releases/latest/download/install.ps1 | iex"
```

Would you rather read the scripts before running them? Download `install.sh` or `install.ps1` and
`frame-install.sh` from the [latest release](https://github.com/SPD13/lemmix-frame/releases/latest)
into one folder. Then run `bash install.sh`, or
`powershell -ExecutionPolicy Bypass -File install.ps1` on Windows. They take the options in the
table below.

The first time, the script pairs your computer with the Frame:
1. It shows: *"Press Enter, then put the headset on and accept"*.
2. Press Enter and put the headset on.
3. Accept the request within 30 seconds, as above.

The script then goes on alone:
1. The Frame downloads the latest release from GitHub and checks its checksum.
2. It installs the game and registers it with Steam.
3. The script ends with *"Done"*.

### 4. Play

1. In the headset, open your **Library**. The game is listed as **lemmix**, with the non-Steam
   and devkit titles. Start it.
2. At the first launch, Steam may first download Steam Linux Runtime 4 ARM64.
3. Steam's dashboard stays open over the game once it has started. Select **Resume Game** to go
   into the game. This is a standard Steam Frame limitation, not a fault in Lemmix: Steam treats
   every non-Steam and devkit title as a flat desktop game and keeps its dashboard up at launch.
   For the moment there is no workaround.
4. The game opens its **setup page**. It downloads NeoLemmix, its styles and the level packs from
   [neolemmix.com](https://www.neolemmix.com) (about 120 MB). No game data comes with Lemmix
   itself.

To add more levels, use the setup page's **web server for level uploads**. It shows an address to
open in a browser on a computer on the same Wi-Fi. From there you can upload level packs (folders
or zips).

### Update, reinstall, uninstall

To **update**, open the installer app again: it connects by itself. When a newer version is out,
step 2 shows **Update available** and a button **Update to &lt;version&gt;**. **Up to date** means
there is nothing to do. To **uninstall**, choose **Uninstall Lemmix from the Frame...** at the
bottom of step 2. Tick the box to also delete the game data it downloaded.

Your levels, settings, progress and replays are kept across updates: they live apart from the
game, in `~/.local/share/godot/app_userdata/Lemmix`. With the scripts, run the same line again to
update:

| | macOS / Linux | Windows (PowerShell) |
|---|---|---|
| Install / update | `curl -fsSL …/install.sh \| bash` | `powershell -ExecutionPolicy Bypass -c "irm …/install.ps1 \| iex"` |
| Give the address | `curl -fsSL …/install.sh \| bash -s -- --host 192.168.1.42` | `powershell -ExecutionPolicy Bypass -c "& ([scriptblock]::Create((irm …/install.ps1))) -Host 192.168.1.42"` |
| Uninstall | `curl -fsSL …/install.sh \| bash -s -- --uninstall` | `powershell -ExecutionPolicy Bypass -c "& ([scriptblock]::Create((irm …/install.ps1))) -Uninstall"` |
| Uninstall and delete the downloaded data | `… \| bash -s -- --uninstall --purge` | `… -Uninstall -Purge` |
| Install a package you downloaded | `bash install.sh --file lemmix-frame-linux-arm64.tar.gz` | `powershell -ExecutionPolicy Bypass -File install.ps1 -File lemmix-frame-linux-arm64.tar.gz` |

`…` stands for `https://github.com/SPD13/lemmix-frame/releases/latest/download`.

**Already have SSH on the Frame?** You can install without a computer-side script. Log in with
`ssh steamos@frame.local` and run:

```
curl -fsSL https://github.com/SPD13/lemmix-frame/releases/latest/download/frame-install.sh | bash
```

It also takes `--uninstall` and `--purge` (`… | bash -s -- --uninstall`).

### If something goes wrong

The installer app says what went wrong next to the check or step that failed, and what to do. It
keeps a log of what it did: `installer.log` in `%LOCALAPPDATA%\lemmix-frame-installer` (Windows),
`~/Library/Caches/lemmix-frame-installer` (macOS) or `~/.cache/lemmix-frame-installer` (Linux).
Attach it when you [report a problem](https://github.com/SPD13/lemmix-frame/issues).

- **macOS: the app "cannot be opened" or is "Not Opened":** **System Settings > Privacy & Security >
  Open Anyway** (see step 2). If it can't find the Frame, allow it in **System Settings > Privacy &
  Security > Local Network**.
- **"The Frame's identity has changed"** (installer app, after the Frame was reset): choose **Trust
  the Frame's new key**.

The scripts' messages:

- **"does not answer on port 32000":** the computer can't reach the Frame's devkit service.
  - Check that Developer Mode is on.
  - Check that the Frame is awake and on the same network as the computer. A guest Wi-Fi, or a
    router that keeps devices apart, blocks it, and so can a VPN on the computer.
  - Try the Frame's IP address with `--host` / `-Host`.
- **"pairing failed":** the request was declined, or not accepted within 30 seconds. Run the
  installer again. Make sure Steam's menus are showing in the headset, not a game.
- **"Steam is not running on the Frame":** the headset was asleep. Put it on and run the installer
  again. The game is copied already; this run only registers it.
- **"REMOTE HOST IDENTIFICATION HAS CHANGED"** (after the Frame was reset): forget its old identity
  with `ssh-keygen -R frame.local` (or the IP you used). This works on Windows too. Then run the
  installer again.
- **Windows says `ssh` is not recognized:** add Windows' OpenSSH client. It's in Settings > System
  > Optional features > Add a feature > OpenSSH Client.
- **lemmix is not in the library:** run the installer again. If it still isn't there, restart the
  headset.

**Removing the pairing:** this removes your computer's access, not the game. Delete the
installer's line from the Frame's `~/.ssh/authorized_keys`:

```
ssh -i ~/.config/lemmix-frame/frame_rsa steamos@frame.local "sed -i '/lemmix-installer@/d' ~/.ssh/authorized_keys"
```

**What goes where on the Frame:**
- `~/devkit-game/lemmix/`: the game. Its `VERSION` file holds the version and the commit.
- `~/devkit-game/lemmix-argv.json` and `lemmix-settings.json`: what Steam launches (the runtime
  is Steam Linux Runtime 4 ARM64).
- `~/.local/share/godot/app_userdata/Lemmix/`: the data the game downloaded, its settings,
  replays and logs.

## Layout

```
web/                    LemmingsJS submodule, pinned (the oracle)
core/Lemmix.Core/       C# library with no Godot dependency: parsers, sim, game, panel, render buffers, indexes, storage
core/Lemmix.Core.Tests/ xunit v3 (an executable, so it also runs in the Frame's runtime image)
app/                    the Godot project
native/libopenmpt/      tracker music library, built for linux-arm64 (sniper SDK image) and macOS
oracle/                 Node dumpers running the web engine, the reference for the C# port
parity/matrix.json      every web feature to replicate, with its oracle and status (node parity/gen-matrix.js)
installer/              the installer app for players (Go: Windows, macOS, Linux), built by tools/make-installer.sh
dist/                   the installer scripts (install.sh, install.ps1) and frame-install.sh, which runs on the Frame
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
make package         build/dist: the release package (.tar.gz + .sha256) and the three installers
make release         export, package, tag v<config/version> and publish the GitHub release
make probe-virtual   the off-device probe (docs/phase0-probe.md)
make matrix          regenerate parity/matrix.json after bumping web/
```

On the Frame (developer mode, ssh as `steamos@frame.local`; to get that access, run
`dist/install.sh` once, which pairs the computer as in the install section above):

```
tools/frame-deploy.sh [play|probe|benchmark]   upload, register the "lemmix" title, choose what it runs
tools/frame-pull-report.sh                     probe.json, perf.json, qa.json, controller-models.json
tools/frame-capture.sh                         a picture of the headset's view
```

### Distribution

- **`installer/`** is the installer app, in Go. It is built by `tools/make-installer.sh` into
  `build/installer/` for Windows, macOS and Linux:
  - It shows a web page (`installer/ui/`) in a native window: WebView2 on Windows, WebKit on macOS.
    On Linux it opens in the browser.
  - It finds the Frame (mDNS) and pairs like the scripts, with the same key. Its ssh client is its
    own, so the computer needs nothing installed.
  - It streams the release package to the Frame and checks its checksum. Then it runs that
    release's `frame-install.sh --file` over ssh. It falls back to the copy embedded at build time
    (`installer/embedded/`) for uninstalls and `--archive`.
  - To try a release before publishing: `--archive build/dist/lemmix-frame-linux-arm64.tar.gz`, or
    `LEMMIX_RELEASE_API=<a JSON in GitHub's releases/latest format>`.
  - It is a port of the VPX Steam Frame installer app; keep the two in step.
- **`dist/`** holds the installer scripts:
  - `install.sh` (macOS/Linux) and `install.ps1` (Windows) run on the computer. They find the
    Frame (the `_steamos-devkit._tcp` mDNS service), pair with it if needed, then run
    `frame-install.sh` over ssh.
  - **Pairing** is a `POST` of an `ssh-rsa` public key to `http://<frame>:32000/register`. That
    is the devkit service of Developer Mode, the same request the SteamOS Devkit Client sends.
    Steam asks the wearer to accept, and on acceptance the key is installed and sshd is enabled.
  - `frame-install.sh` runs on the Frame. It downloads and unpacks the package into
    `~/devkit-game/lemmix`, then registers the title with Steam through the `devkit-1` IPC
    (`create-shortcut`, as `~/devkit-utils` does). It needs neither the Devkit Client nor its
    `devkit-utils`.
- **The package** is `lemmix-frame-linux-arm64.tar.gz`: a `lemmix/` folder holding the export
  and a `VERSION` file (`config/version` and the commit).
- **The download location** is `releases/latest/download/` of this repo (the app reads
  `releases/latest` through GitHub's API). Change it in the three scripts and `installer/app.go`, or with `LEMMIX_RELEASE_URL` for a test. The repo must be public for players to
  download from it.
- **To release:**
  1. Bump `config/version` in `app/project.godot` and commit.
  2. Run `tools/release.sh` (`--draft` to check it first; `--no-export` to reuse the current
     export). It refuses a dirty tree or an existing tag. It builds the installer app too (needs
     Go: `brew install go`) and attaches it to the release.
- **To try a package before publishing,** serve `build/dist` and point the installer at it:
  `(cd build/dist && python3 -m http.server 8124)`, then
  `LEMMIX_RELEASE_URL=http://<this Mac's IP>:8124 bash build/dist/install.sh`.
- `tools/frame-deploy.sh` stays the development path: rsync, then
  `frame-install.sh --register --argv …`.

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
