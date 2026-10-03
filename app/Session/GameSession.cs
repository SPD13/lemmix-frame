using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Godot;
using Lemmix.App.Audio;
using Lemmix.App.Board;
using Lemmix.Audio;
using Lemmix.Engine;
using Lemmix.Io;
using Lemmix.Render;

namespace Lemmix.App.Session;

// Where a level sits in levels/index.json: its file, its pack (the panel graphics, the music
// rotation and folder) and its place in the pack's play order.
public sealed class LevelLocation
{
    public required string Id;
    public required string Url;
    public string? PackDir;          // the pack's folder when it ships its own panel graphics (game.packDir)
    public string? PackName;
    public List<string>? MusicRotation;
    public string? MusicDir;
    public List<string>? MusicFiles;
    public int Ordinal = -1;         // LevelTree.levelsOf(pack).indexOf(level)

    // the pack node is the nearest "pack" above the level
    public static LevelLocation? Find(string indexJson, string id)
    {
        using var doc = JsonDocument.Parse(indexJson);
        var path = new List<JsonElement>();
        JsonElement? hit = null;
        bool Walk(JsonElement n)
        {
            path.Add(n);
            if (n.TryGetProperty("levels", out var lv) && lv.ValueKind == JsonValueKind.Array)
                foreach (var l in lv.EnumerateArray())
                    if (l.TryGetProperty("id", out var lid) && lid.GetString() == id) { hit = l; return true; }
            if (n.TryGetProperty("children", out var ch) && ch.ValueKind == JsonValueKind.Array)
                foreach (var c in ch.EnumerateArray()) if (Walk(c)) return true;
            path.RemoveAt(path.Count - 1);
            return false;
        }
        if (!Walk(doc.RootElement) || hit is not { } level) return null;
        JsonElement? pack = null;
        for (int i = path.Count - 1; i >= 0; i--)
            if (path[i].TryGetProperty("kind", out var k) && k.GetString() == "pack") { pack = path[i]; break; }
        static string? S(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        static List<string>? L(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : null;
        var loc = new LevelLocation { Id = id, Url = S(level, "url") ?? throw new InvalidOperationException("no url for " + id) };
        if (pack is { } p)
        {
            bool panel = p.TryGetProperty("panel", out var pv) && pv.ValueKind == JsonValueKind.True;
            loc.PackDir = panel ? S(p, "dir") : null;
            loc.PackName = S(p, "name");
            loc.MusicRotation = L(p, "musicRotation");
            loc.MusicDir = S(p, "musicDir");
            loc.MusicFiles = L(p, "musicFiles");
            int n = 0;
            void Count(JsonElement node)
            {
                if (node.TryGetProperty("levels", out var lv) && lv.ValueKind == JsonValueKind.Array)
                    foreach (var l in lv.EnumerateArray()) { if (loc.Ordinal < 0 && S(l, "id") == id) loc.Ordinal = n; n++; }
                if (node.TryGetProperty("children", out var ch) && ch.ValueKind == JsonValueKind.Array)
                    foreach (var c in ch.EnumerateArray()) Count(c);
            }
            Count(p);
        }
        return loc;
    }
}

// What the session is opened with.
public sealed class SessionOptions
{
    public required IFileSource Assets;             // the asset root: neolemmix/, levels/ (and 3d/env/ if made)
    public required string LevelId;
    public BoardSwitches Switches = new();
    public Node3D? DioramaRoot;                     // the board goes under it (VR placement scales it)
    public EnvironmentView? Environment;            // the room (a sibling of the diorama root); none: no room
    public GameAudio? Audio;
    public string ProfilesDir = "res://Data/profiles";
    public double Speed = 1;                        // ?speed=
    public bool EnvironmentInBackground = true;     // the room's pictures built on a worker
    public Lemmix.Ui.PanelAssets? PanelAssets;      // the panel's graphics, for the replay markers' pictures
    public Func<double>? Clock;                     // ms; Time.GetTicksUsec by default
    public string? ReplayText;                      // a NeoLemmix replay (.nxrp) to play from the start (?nxrp=, ?solution=1)
    public string ReplayKind = "file";              // "solution": a stored solution, watched (no clear recorded)
}

// web/3d/js/app.js loadLevel + animateBody, the session half: the Game for a level, the board
// built for it (BoardScene), the timer driven from the frame loop with the web's fixed-step
// accumulator, the terrain flushed and the scene synced each tick, sound cues played at their
// place, the level's music, a jump in time (OnRestore) put back into the scene, and what the
// input layer calls: a ray to a level point, an assignment, the hover, the switches. The level
// flow (auto-advance, confirm dialogs, the catalog) is the shell's: see the events.
public sealed partial class GameSession : Node
{
    public const double FF_SPEED = 4, SLOWMO_SPEED = 0.25; // NeoLemmix's fast forward and slow motion

    public readonly SessionOptions Options;
    public readonly LevelLocation Location;
    public readonly Level Level;
    public readonly Game Game;
    public readonly SpriteSet Sprites;
    public readonly BoardScene Board;
    public readonly EnvironmentView? Environment;
    public readonly List<string> MusicCandidates;
    public readonly Lemmix.Ui.GamePanel? Panel;
    readonly Func<double> _clock;

    // the shell's hooks
    public event Action<ClassicGameResult>? LevelEnded;      // onGameEnd: the verdict; the shell moves on (or not)
    public event Action<RestoreInfo>? Restored;              // refreshAfterRestore: cancel the end timer, clear the verdict, the minimap
    public event Action? ReloadRequested;                    // a switch that needs the level built again (doors)
    public event Action? PauseChanged;                       // syncPauseLabel
    public Camera3D? Eye;                                    // the camera the board is seen through (the headset's in a session)
    public Func<bool> Presenting = () => false;              // renderer.xr.isPresenting
    public bool GameCursorInUse;                             // NeoLemmix's cursor drawn: the ring is not (cursorReady)

    // the frame loop's state
    double _lastFrame = double.NaN;
    public double TickDebt { get; private set; }
    public double Alpha { get; private set; } = 1;
    readonly HashSet<string> _holders = new(StringComparer.Ordinal);
    bool _wasRunning;
    public bool Disposed { get; private set; }

    // the hover (applyHover / updateHoverRing)
    Lemming? _hovered;
    (int X, int Y)? _cursorSim;
    public Lemming? Hovered => _hovered;
    public (int X, int Y)? CursorSim => _cursorSim;

    static readonly Dictionary<(IFileSource, string), SpriteSet> SpriteSets = new();
    static readonly Dictionary<IFileSource, Masks> MasksByIo = new();

    GameSession(SessionOptions o, LevelLocation loc, Level level, Game game, SpriteSet sprites, Lemmix.Ui.GamePanel? panel,
        DepthProfile profile, EnvProfile envProfile)
    {
        Name = "session";
        Options = o; Location = loc; Level = level; Game = game; Sprites = sprites; Panel = panel;
        _clock = o.Clock ?? (() => Time.GetTicksUsec() / 1000.0);
        Environment = o.Environment;
        Board = new BoardScene(new BoardInputs
        {
            Level = level, Game = game, Sprites = sprites, Profile = profile, EnvProfile = envProfile,
            Switches = o.Switches.Clone(), BackdropMaterial = Environment?.BackdropMaterial,
        })
        { Now = _clock, ShadowsOn = o.Switches.Shadows };
        Board.LastTickMs = _clock();
        Board.OnCue = cue => PlayCue(cue.Name, cue.X, cue.Y);
        Board.FloorYInLevel = FloorYInLevel;
        (o.DioramaRoot ?? (Node)this).AddChild(Board);

        // the per-tick bridge, after the game's own handler
        game.GameTimer.OnGameTick.On(_ => Board.SyncScene(false));
        game.OnRestore.On(RefreshAfterRestore);
        game.OnOptionChanged = () => { if (!game.GameTimer.IsRunning()) Board.SyncScene(true); };
        game.OnGameEnd.On(r => LevelEnded?.Invoke(r));
        game.GameTimer.SpeedFactor = o.Speed;

        // music: the level's MUSIC line, the pack's rotation, the pack's folder then neolemmix/music
        var names = MusicResolver.Names(level.Info.Music, loc.MusicRotation, loc.Ordinal);
        var dirs = new List<MusicResolver.MusicDir>();
        if (!string.IsNullOrEmpty(loc.MusicDir)) dirs.Add(new MusicResolver.MusicDir(loc.MusicDir, loc.MusicFiles));
        dirs.Add(new MusicResolver.MusicDir(StyleManager.AssetDir + "music", MusicIndex(o.Assets)));
        MusicCandidates = MusicResolver.Candidates(names, dirs);
    }

    // neolemmix/music/index.json's files, or null (every extension tried)
    static List<string>? MusicIndex(IFileSource io)
    {
        string? text = io.Text(StyleManager.AssetDir + "music/index.json");
        if (text == null) return null;
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.TryGetProperty("files", out var f) && f.ValueKind == JsonValueKind.Array
                ? f.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : null;
        }
        catch (JsonException) { return null; }
    }

    static string? ReadProfile(string dir, string file)
    {
        string path = dir.TrimEnd('/') + "/" + file;
        if (path.StartsWith("res://") || path.StartsWith("user://"))
            return Godot.FileAccess.FileExists(path) ? Godot.FileAccess.GetFileAsString(path) : null;
        return System.IO.File.Exists(path) ? System.IO.File.ReadAllText(path) : null;
    }

    // loadLevel (the Lemmix engine): the level built from its styles, the sprite set its theme
    // names, the Game, the board, the room, the music; the game started (or held)
    public static GameSession Load(SessionOptions o)
    {
        var io = o.Assets;
        string index = io.Text("levels/index.json") ?? throw new InvalidOperationException("no levels/index.json");
        var loc = LevelLocation.Find(index, o.LevelId) ?? throw new InvalidOperationException("no level " + o.LevelId);
        var styles = new StyleManager(io);
        if (!MasksByIo.TryGetValue(io, out var masks)) MasksByIo[io] = masks = Masks.Load(io);
        LevelBuilder.DigitFont = masks.Countdown;
        var level = LevelBuilder.Build(LevelBuilder.ParseLevel(io.Text(loc.Url) ?? throw new InvalidOperationException("no file " + loc.Url)), styles, o.LevelId);
        string setName = level.Theme.Lemmings is { Length: > 0 } s ? s : "default";
        if (!SpriteSets.TryGetValue((io, setName), out var sprites)) SpriteSets[(io, setName)] = sprites = new SpriteSet(io).Load(setName);
        var game = new Game(level, masks, l => SpriteSet.GeneratePickupIcons(l, sprites, l.Theme));
        Lemmix.Ui.GamePanel? panel = null;
        if (o.PanelAssets != null && game.Gui == null)
            panel = Lemmix.Ui.GamePanel.SetGuiDisplay(game, new Lemmix.Ui.PixelCanvas(), o.PanelAssets, sprites);
        var gd = GroundData.FromLevel(level);
        var profile = DepthProfile.Merge(DepthProfile.FilesForGroundData(gd).Select(f => DepthProfile.Parse(ReadProfile(o.ProfilesDir, f))));
        var envProfile = EnvProfile.ForLevel(level, url => ReadProfile(o.ProfilesDir, url[(url.LastIndexOf('/') + 1)..]));
        var session = new GameSession(o, loc, level, game, sprites, panel, profile, envProfile);
        if (o.ReplayText != null) game.LoadReplay(Replay.Parse(o.ReplayText), o.ReplayKind);

        // the room: drawn after the board is up
        if (session.Environment != null)
        {
            session.Environment.SetMode(o.Switches.Environment);
            if (!session.Presenting()) session.Environment.PlaceDesktop();
            _ = session.Environment.SetLevel(EnvironmentLayout.LevelContext(level, o.LevelId, envProfile), io, o.EnvironmentInBackground);
        }
        // (as on the web, the sprites are drawn from the first tick on: the openings and the
        // slices are there from the start, the flat objects and the lemmings come with the tick)
        game.Start();
        return session;
    }

    // starts the level's music (state.music; the shell calls it again when sound comes back on)
    public void PlayMusic()
    {
        if (!Board.Switches.Music || Options.Audio == null) return;
        Options.Audio.PlayLevelMusic(MusicCandidates);
    }

    public override void _Ready() => PlayMusic();

    public override void _Process(double delta) => Step(_clock());

    // ------------------------------------------------------------ the frame loop
    // animateBody's session part: the fixed-step accumulator at 17 fps x speed, a stalled frame
    // capped at five ticks of debt, then the interpolation and the per-frame overlays
    public int Step(double now)
    {
        if (Disposed) return 0;
        using var _ = Perf.Time(Perf.S.Step);
        double dt = double.IsNaN(_lastFrame) ? 0 : now - _lastFrame;
        _lastFrame = now;
        var timer = Game.GameTimer;
        int ticks = 0;
        double alpha = 1;
        if (timer.IsRunning())
        {
            double tickMs = timer.TimePerFrameMs / timer.SpeedFactor;
            TickDebt = Math.Min(TickDebt + dt, tickMs * 5);
            while (TickDebt >= tickMs && timer.IsRunning())
            {
                using (Perf.Time(Perf.S.Sim)) timer.Tick();
                ticks++;
                TickDebt -= tickMs;
            }
            alpha = Math.Min(1, (now - Board.LastTickMs) / tickMs);
        }
        else TickDebt = 0;
        Alpha = alpha;
        Board.ApplyInterpolation(alpha);
        Board.Particles.UpdateScale();
        UpdateHoverRing();
        Board.CpmAnimate(now);
        Board.Markers.Hidden = Game.ClearPhysics;
        Board.Markers.UpdateFor(now);
        Board.TerrainView.Sync();
        if (Environment != null && Eye != null && Board.GetParent() is Node3D root)
        {
            using var env = Perf.Time(Perf.S.Env);
            var moved = Environment.Update(Eye.GlobalPosition, root, Presenting());
            if (moved is Vector3 m && !Presenting()) Eye.GlobalPosition = m;
        }
        return ticks;
    }

    // ------------------------------------------------------------ the clock
    public bool Running => Game.GameTimer.IsRunning();

    public void TogglePause() { Game.GameTimer.Toggle(); PauseChanged?.Invoke(); }

    // toggleSpeed: fast forward / slow motion on, or back to normal; a paused game starts
    public void ToggleSpeed(double speed)
    {
        var timer = Game.GameTimer;
        bool paused = !timer.IsRunning();
        timer.SpeedFactor = !paused && timer.SpeedFactor == speed ? 1 : speed;
        if (paused) { timer.Continue(); PauseChanged?.Invoke(); }
    }

    // holdSim / releaseSim: a window the player deals with holds the clock; the last to leave
    // puts it back the way the first found it
    public void Hold(string who)
    {
        if (_holders.Contains(who)) return;
        var timer = Game.GameTimer;
        if (_holders.Count == 0)
        {
            _wasRunning = timer.IsRunning();
            if (_wasRunning) timer.Suspend();
        }
        _holders.Add(who);
        PauseChanged?.Invoke();
    }

    public void Release(string who)
    {
        if (!_holders.Remove(who) || _holders.Count > 0) return;
        if (_wasRunning) Game.GameTimer.Continue();
        _wasRunning = false;
        PauseChanged?.Invoke();
    }

    public bool Held => _holders.Count > 0;

    // refreshAfterRestore: the game jumped to another frame; the scene's copies refreshed and the
    // frame drawn as a tick would
    void RefreshAfterRestore(RestoreInfo info)
    {
        if (Disposed) return;
        using var _ = Perf.Time(Perf.S.Restore);
        using (Perf.Time(Perf.S.Resync)) Board.Terrain.Resync();
        Board.Lemmings.ClearPrevPositions();
        Board.ResetSceneMemory();
        TickDebt = 0;
        Restored?.Invoke(info);
        Board.SyncScene(false);
        PauseChanged?.Invoke();
    }

    // ------------------------------------------------------------ sound
    // sfxPos: a sim coordinate as a world position (the lemmings' plane)
    public Vector3 SfxPos(double simX, double simY) =>
        BoardMaterials.WorldOf(Board) * new Vector3((float)simX, (float)simY, (float)BoardZ.LEMMING_Z);

    public Vector3? LastCuePos { get; private set; }
    public string? LastCue { get; private set; }

    void PlayCue(string name, int? x, int? y)
    {
        Vector3? pos = x != null ? SfxPos(x.Value, y ?? 0) : null;
        LastCue = name; LastCuePos = pos;
        Options.Audio?.PlayCue(name, pos, Eye?.GlobalPosition);
    }

    // ------------------------------------------------------------ the pointer
    // the pick plane: the level's rectangle, 40 px taller, on the lemmings' plane; a world ray to
    // the level point it lands on (rounded), or null
    public Vector2I? Pick(Vector3 origin, Vector3 direction)
    {
        var inv = BoardMaterials.WorldOf(Board).AffineInverse();
        var o = inv * origin;
        var d = inv.Basis * direction;
        if (Math.Abs(d.Z) < 1e-9) return null;
        double t = (BoardZ.LEMMING_Z - o.Z) / d.Z;
        if (t < 0) return null;
        double x = o.X + d.X * t, y = o.Y + d.Y * t;
        if (x < 0 || x > Level.Width || y < -20 || y > Level.Height + 20) return null;
        return new Vector2I((int)Math.Floor(x + 0.5), (int)Math.Floor(y + 0.5)); // Math.round
    }

    // applyHover's board part: the pointer on the board (sim coordinates) or off it
    public void SetPointer(Vector2I? sim)
    {
        if (sim is Vector2I p)
        {
            _cursorSim = (p.X, p.Y);
            var lem = Game.LemmingManager.GetLemmingAt(p.X, p.Y);
            if (lem != null && lem.Action != BA.NONE) _hovered = lem;
        }
        else _cursorSim = null;
        UpdateHoverRing();
    }

    // updateHoverRing: the ring follows the hovered lemming until it escapes the pointer; the
    // lemming NeoLemmix marks is the one the selected skill would go to; paused, the frame is
    // drawn again when that (or the shadow) changes
    void UpdateHoverRing()
    {
        var lem = _hovered;
        if (lem != null && (lem.Removed || lem.Action == BA.NONE)) lem = null;
        if (lem != null && _cursorSim is { } c)
        {
            double dx = lem.X - c.X, dy = (lem.Y - 5) - c.Y;
            if (dx * dx + dy * dy > BoardZ.HOVER_RING_RADIUS * BoardZ.HOVER_RING_RADIUS) lem = null;
        }
        if (_cursorSim == null) lem = null;
        _hovered = lem;
        Board.CursorSim = _cursorSim;
        if (Game.ClearPhysics) Board.Terrain.SetPhysicsPaint(Level.Physics, ClearPhysicsOverlay.HighlightBits(Level, _cursorSim));
        var marked = _cursorSim is { } m ? Game.LemmingManager.GetSelectedLemmingAt(m.X, m.Y) : null;
        bool markedChanged = marked != Game.CursorLemming;
        Game.CursorLemming = marked;
        var shadow = Board.ShadowChoice();
        var shadowKey = shadow is { } s ? (s.Lem.Index, s.Skill) : ((int, string)?)null;
        if (markedChanged || !Equals(shadowKey, _shadowKey))
        {
            _shadowKey = shadowKey;
            if (!Game.GameTimer.IsRunning()) Board.SyncScene(true);
        }
        Board.SetRing(lem, !GameCursorInUse);
    }
    (int, string)? _shadowKey;

    // actOnSimPick: the selected skill to the lemming at the point; paused, the frame runs now
    public bool AssignAt(int simX, int simY)
    {
        var lem = Game.LemmingManager.GetLemmingAt(simX, simY);
        if (lem == null) return false;
        Game.QueueCommand(new CommandLemmingsAction(lem.Id));
        // (the web also plays the DOS assign effect here; the Lemmix sim cues its own)
        Game.ForceOneFrame();
        return true;
    }

    // ------------------------------------------------------------ switches
    public BoardSwitches Switches => Board.Switches;
    public void SetEmboss(bool on) => Board.RebuildRelief(on);
    public void SetSmooth(bool on) => Board.SetSmooth(on);
    public void SetSmoothTerrain(bool on) => Board.SetSmoothTerrain(on);
    public void SetColorBlend(string level) => Board.SetColorBlend(level);
    public void SetShadows(bool on) { Board.Switches.Shadows = on; Board.ShadowsOn = on; if (!Running) Board.SyncScene(true); }
    public void SetEnvironment(string mode) { Board.Switches.Environment = mode; Environment?.SetMode(mode); }
    public void SetMusic(bool on)
    {
        Board.Switches.Music = on;
        if (on) PlayMusic(); else Options.Audio?.StopMusic();
    }
    // the openings carve the terrain as the level is built: the level is built again
    public void SetDoors(bool on) { Board.Switches.Doors = on; ReloadRequested?.Invoke(); }
    public void SetClearPhysics(bool on) => Game.SetClearPhysics(on);

    // the room's floor in the level's own pixels (y down from the top edge)
    double FloorYInLevel()
    {
        if (Environment == null) return double.PositiveInfinity;
        var g = BoardMaterials.WorldOf(Board);
        var probe = g.Origin;
        probe.Y = (float)Environment.FloorWorldY();
        return (g.AffineInverse() * probe).Y;
    }

    // disposeSession
    public void Close()
    {
        if (Disposed) return;
        Disposed = true;
        Environment?.ClearLevel();
        Game.Stop();
        Options.Audio?.StopAll();
        Board.GetParent()?.RemoveChild(Board);
        Board.QueueFree();
        Board.Terrain.Dispose();
        _hovered = null; _cursorSim = null;
    }

    public override void _ExitTree() => Close();
}
