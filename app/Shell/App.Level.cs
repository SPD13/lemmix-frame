using System;
using System.Collections.Generic;
using Godot;
using Lemmix.App.Board;
using Lemmix.App.Session;
using Lemmix.App.Ui.Windows;
using Lemmix.Engine;
using Lemmix.Library;
using Lemmix.Render;
using Lemmix.Ui;

namespace Lemmix.App.Shell;

// web/3d/js/app.js's level flow: library.enter (the lock lifted, the recent list, the level
// loaded), resolveLevel, loadLevel's surrounding parts (the old level disposed, the status strip,
// the opening text, a replay or the stored solution engaged, a level arriving behind a window
// starting held, the board placed in front of the head), moveLevel (prev / next / restart), the
// onGameEnd handler (the clear recorded unless a solution was watched, the talismans, the closing
// text, the outcome on the strip, and 3 s later the next level after a win or the same one again -
// unless a replay or a step back since has made the level playable again), Watch Solution.
public sealed partial class App
{
    public const double END_ADVANCE_MS = 3000;

    double? _endAt;                 // endTimeout
    Game? _endGame;
    bool _endWon;
    bool _reload;                   // a switch that needs the level built again (doors)
    bool _solutionLit;

    /** The level built again at the next frame (toggleDoors, a session's ReloadRequested). */
    public void RequestReload() => _reload = true;

    /** library.enter: into a level - the lock the app starts under lifted; `solution`: with its
     *  stored solution replaying (the toolbar's Watch Solution, or the --solution argument). */
    public void EnterLevel(string levelId) => EnterLevel(levelId, false);

    public void EnterLevel(string levelId, bool solution)
    {
        Locked = false;
        LevelId = levelId;
        if (solution) _pendingSolution = true;
        LoadLevel();
    }

    /** moveLevel: the neighbouring level in the pack's play order, wrapping; 0: the same again. */
    public void MoveLevel(int delta)
    {
        if (LevelId == null) return; // no level yet: the library is up, choose there
        var next = Tree.Next(LevelId, delta);
        if (next != null) LevelId = next;
        LoadLevel();
    }

    /** A loaded replay (the replay files page): played from the start at normal speed. */
    public void LoadReplayText(string text, string kind = "file")
    {
        if (Session == null) return;
        Session.Game.LoadReplayFile(Replay.Parse(text), kind);
    }

    void DisposeSession()
    {
        CancelLoad();
        if (Session == null) return;
        Windows.Status.Badge.Set(false, Presenting);
        _solutionLit = false;
        Pointers.HideCursors();
        _endAt = null;
        _endGame = null;
        var s = Session;
        Session = null;
        FreeSession(s);
        Bar?.Dispose();
        Bar = null;
        if (BarView != null)
        {
            BarView.GetParent()?.RemoveChild(BarView);
            BarView.QueueFree();
            BarView = null;
        }
        _hoverTile = null;
    }

    void FreeSession(GameSession s)
    {
        s.Close();
        if (s.GetParent() == this) RemoveChild(s);
        s.QueueFree();
    }

    void ShowUnplayable(LevelDescription where)
    {
        string name = where.Title != null ? Lemmix.Store.Js.ToStr(where.Title) : where.Label;
        Windows.Status.Set(name: name, meta: where.PackName + " · " + where.Label, note: "needs the Lemmix engine", kind: "lost");
    }

    // a level's stored solution (res://Data/solutions, as the web's solutions/), or null
    public string? ReadSolution(string levelId)
    {
        var url = Solutions.Url(SolutionsRoot, levelId);
        if (url == null) return null;
        string path = Uri.UnescapeDataString(url);
        return Godot.FileAccess.FileExists(path) ? Godot.FileAccess.GetFileAsString(path) : null;
    }

    static string? ReadFile(string path)
    {
        try
        {
            if (path.StartsWith("res://", StringComparison.Ordinal) || path.StartsWith("user://", StringComparison.Ordinal))
                return Godot.FileAccess.FileExists(path) ? Godot.FileAccess.GetFileAsString(path) : null;
            return System.IO.File.Exists(path) ? System.IO.File.ReadAllText(path) : null;
        }
        catch (Exception) { return null; }
    }

    /** loadLevel: the level state.levelId names, in place of the one on the board. */
    public void LoadLevel()
    {
        // a level still being prepared on the worker: this one waits for it (the worker's io is
        // its alone), then loads (PollLoad)
        if (_loading is { Session: null, Task.IsCompleted: false }) { _loadAgain = true; return; }
        LoadTimes.Start();
        DisposeSession();
        LoadTimes.Lap("dispose");
        _lobbyRoom = false; // the level's room takes the lobby's place
        Windows.SetLevelText(null);
        Windows.Status.Set(note: "loading…", kind: "");

        // resolveLevel: the level meant, else the first there is
        if (Tree.Root == null) { Windows.Status.Set(note: "no levels installed", kind: "lost"); return; }
        LevelId ??= Tree.FirstLevelId();
        var where = LevelId != null ? Tree.Describe(LevelId) : null;
        if (where == null)
        {
            LevelId = Tree.FirstLevelId();
            where = LevelId != null ? Tree.Describe(LevelId) : null;
        }
        if (where == null || LevelId == null) { Windows.Status.Set(note: "no levels found", kind: "lost"); return; }
        Library.SetCurrent(LevelId);
        if (where.Engine != "lemmix") { ShowUnplayable(where); return; }

        // ?solution: the stored solution, watched; ?nxrp: a replay file
        string? replay = null, kind = "file";
        if (_pendingSolution)
        {
            replay = ReadSolution(LevelId);
            if (replay != null) kind = "solution";
            else GD.PushWarning("[app] no stored solution for " + LevelId);
            _pendingSolution = false;
        }
        if (replay == null && _pendingNxrp != null)
        {
            replay = ReadFile(_pendingNxrp);
            if (replay == null) GD.PushWarning("[app] replay not found: " + _pendingNxrp);
        }
        _pendingNxrp = null;

        var options = new SessionOptions
        {
            Assets = SyncLoad ? Io : LoaderIo, EnvironmentAssets = Io, LevelId = LevelId, Switches = Fx.Switches(), DioramaRoot = DioramaRoot, Environment = Env,
            Audio = Audio, Speed = Speed, EnvironmentInBackground = Options.EnvironmentInBackground, Clock = Now,
            SpreadRestore = Options.SpreadRestore,
            ReplayText = replay, ReplayKind = kind,
        };
        if (SyncLoad)
        {
            GameSession s;
            try { s = GameSession.Load(options); }
            catch (Exception e) { LoadFailed(where, e); return; }
            Adopt(s);
            FinishLoad(s, where);
            return;
        }
        // in a headset: the level built on a worker while the frames go on (the head tracked, the
        // windows answering), then its board handed to the engine a few milliseconds a frame (PollLoad)
        var load = _loading = new PendingLoad { Options = options, Where = where };
        load.Task = System.Threading.Tasks.Task.Run(() => GameSession.Prepare(options));
    }

    // ---- a level's load spread over frames
    sealed class PendingLoad
    {
        public required SessionOptions Options;
        public required LevelDescription Where;
        public System.Threading.Tasks.Task<GameSession.Prepared>? Task;
        public GameSession? Session;        // attached, its terrain on its way to the engine
        public bool Abandoned;              // the level left before it was up
    }
    PendingLoad? _loading;
    bool _loadAgain;                        // another level asked for while one was being prepared
    public bool SyncLoad;                   // loads finish within LoadLevel (tests, the benchmark)
    public bool Loading => _loading != null;
    public const double TerrainBudgetMs = 4; // the terrain's share of a frame while a level comes up

    // once a frame: the worker's level attached when ready, then its terrain a slice at a time
    void PollLoad()
    {
        var load = _loading;
        if (load == null) return;
        if (load.Session == null)
        {
            if (!load.Task!.IsCompleted) return;
            if (_loadAgain || load.Abandoned)
            {
                _loading = null;
                bool again = _loadAgain;   // asked for after the worker began (abandoned or not)
                _loadAgain = false;
                if (again) LoadLevel();
                return;
            }
            if (load.Task.IsFaulted) { _loading = null; LoadFailed(load.Where, load.Task.Exception?.InnerException ?? load.Task.Exception!); return; }
            GameSession attached;
            try { attached = GameSession.Attach(load.Options, load.Task.Result, deferTerrain: true); }
            catch (Exception e) { _loading = null; LoadFailed(load.Where, e); return; }
            Adopt(attached);
            attached.Board.Visible = false;     // shown whole, once its terrain is in
            load.Session = attached;
            return;
        }
        var s = load.Session;
        s.Board.TerrainView.Sync(TerrainBudgetMs);
        if (s.Board.TerrainView.Pending) return;
        s.Board.Visible = true;
        _loading = null;
        LoadTimes.Lap("terrain-frames");
        FinishLoad(s, load.Where);
    }

    // the loading banner: up, where the windows open, while a level is on its way; its spinner turning
    void ShowLoading(double seconds)
    {
        bool show = Loading && Presenting;
        if (show && !LoadingBanner.Root.Visible && WindowPose is Transform3D head)
        {
            var (pos, quat, _) = VrWindowPlacement.PlaceWindows(head.Origin, head.Basis.GetRotationQuaternion(), 0);
            LoadingBanner.Show(true, new Transform3D(new Basis(quat), pos));
        }
        else if (!show) LoadingBanner.Show(false);
        LoadingBanner.Spin(seconds);
    }

    // a load under way dropped (DisposeSession: the level left, or another one loading)
    void CancelLoad()
    {
        var load = _loading;
        if (load == null) return;
        if (load.Session != null)
        {
            FreeSession(load.Session);
            _loading = null;
        }
        else load.Abandoned = true;         // its worker finishes; PollLoad lets it go
    }

    void LoadFailed(LevelDescription where, Exception e)
    {
        GD.PushError("[app] level " + LevelId + ": " + e);
        Windows.Status.Set(name: where.Label, meta: where.PackName, note: "FAILED TO LOAD", kind: "lost");
    }

    // the session's node in the shell (it is stepped by the shell's frame, not its own)
    void Adopt(GameSession s)
    {
        AddChild(s);
        s.SetProcess(false); // the shell's frame loop steps it
        s.Eye = Head as Camera3D;
        s.Presenting = () => Vr.Presenting;
        s.GameCursorInUse = Cursor?.Ok == true;
        s.LevelEnded += r => OnGameEnd(s, r);
        s.Restored += _ => OnRestored(s);
        s.ReloadRequested += RequestReload;
        s.Game.OnLoadReplayRequest = OpenReplayFiles;
    }

    // the loaded level made current: its skills bar, its texts, its place
    void FinishLoad(GameSession s, LevelDescription where)
    {
        Session = s;

        // the skills bar, in the bar's root (its panel is the game's GUI)
        try
        {
            Bar = new SkillBar(s.Game, PanelAssets.Load(Io, s.Location.PackDir), s.Sprites, () => Now());
            Bar.SetFlatSkills(Fx.FlatSkills);
            Bar.OnMinimapCenter = CenterViewOn;
            BarView = new SkillBarView(Bar);
            Windows.Toolbar.GuiRoot.AddChild(BarView);
            _hoverTile = BarView.GetNodeOrNull<Node3D>("HoverTile");
        }
        catch (Exception e) { GD.PushError("[app] skills bar: " + e.Message); Bar = null; }
        LoadTimes.Lap("skillbar");

        // a window the player is dealing with holds whatever game is current: this one too
        foreach (var who in _holders) s.Hold(who);

        SyncSolutionButton(true);
        var level = s.Level;
        string name = level.Name.Trim() is { Length: > 0 } n ? n : "(unnamed level)";
        string meta = where.PackName + " · " + where.Label + " · save " + level.NeedCount + "/" + level.ReleaseCount;
        Windows.SetLevelText(level.Pretext);
        Windows.Status.Set(name: name, meta: meta, note: "", kind: "");
        LayoutGuiPanel();
        if (Presenting) PlaceDiorama(HeadNow());
        _collectIn = 2; // once the old board is freed (QueueFree: the end of this frame)
        LoadTimes.Print(LevelId);
    }

    // A level's load leaves its garbage (the old board, the build's scratch) for the collector:
    // collected in full, the large object heap compacted, at the level's start rather than as a
    // full collection somewhere in play. Then play starts in a no-GC region: the collector waits
    // until that much has been allocated (more than a few minutes of the default look's play), so
    // play has no collections - the region ends with one when it is used up, and the next level
    // starts a new one. (Starting a region collects in full: only ever here, in the load.)
    public static readonly long[] NoGcRegionSizes = { 192L << 20, 128L << 20, 64L << 20 };
    int _collectIn;
    void CollectAfterLoad()
    {
        if (_collectIn == 0 || --_collectIn > 0) return;
        if (System.Runtime.GCSettings.LatencyMode == System.Runtime.GCLatencyMode.NoGCRegion)
            try { GC.EndNoGCRegion(); } catch (InvalidOperationException) { }
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        var gcClock = System.Diagnostics.Stopwatch.StartNew();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        double gcMs = gcClock.Elapsed.TotalMilliseconds;
        foreach (long size in NoGcRegionSizes)
        {
            try { if (GC.TryStartNoGCRegion(size)) break; }
            catch (ArgumentOutOfRangeException) { }   // more than this runtime allows: a smaller one
            catch (InvalidOperationException) { break; }
        }
        GD.Print($"[load] after the load: full collection {gcMs:0} ms, no-GC region {gcClock.Elapsed.TotalMilliseconds - gcMs:0} ms");
    }

    // ------------------------------------------------------------ the end of a level
    void OnGameEnd(GameSession s, ClassicGameResult result)
    {
        if (s != Session || LevelId == null) return;
        var game = s.Game;
        bool won = result.State == GameStateTypes.SUCCEEDED;
        bool watched = game.WatchingSolution; // a solution's win is the solver's, not the player's
        string best = "";
        if (won && !watched)
        {
            // the clock counts down; how long it took is the elapsed time
            int seconds = game.GameTimer.GetGameTime();
            bool record = Progress.Record(LevelId, seconds, (double)result.Survivors);
            best = " — " + LevelProgress.Format(seconds) + (record ? " (best)" : "");
        }
        // the talismans won, kept; the closing text in the opening text's place
        if (!watched)
        {
            var got = new List<object?>();
            foreach (var t in s.Level.Talismans) if (game.Sim.TalismansAchieved.Contains(t.Id)) got.Add((double)t.Id);
            if (got.Count > 0) Talismans.RecordWin(Store, LevelId, got);
        }
        if (won && s.Level.Posttext.Count > 0) Windows.SetLevelText(s.Level.Posttext);
        Windows.Status.Set(note: (watched ? "SOLUTION " : "") + (won ? "COMPLETE" + best : "FAILED"), kind: won ? "won" : "lost");
        _endAt = Now() + END_ADVANCE_MS;
        _endGame = game;
        _endWon = won;
    }

    // refreshAfterRestore's flow part: the level is playable again
    void OnRestored(GameSession s)
    {
        if (s != Session) return;
        _endAt = null;
        _endGame = null;
        Windows.SetLevelText(s.Level.Pretext);
        Windows.Status.Set(note: "", kind: "");
    }

    void CheckLevelEnd(double now)
    {
        if (_endAt is not double at || now < at) return;
        _endAt = null;
        var game = _endGame;
        _endGame = null;
        // a replay or a step back since the end keeps the level (it is playable again)
        if (Session != null && Session.Game == game && game!.FinalGameState != GameStateTypes.UNKNOWN) MoveLevel(_endWon ? 1 : 0);
    }

    /** The time left before the level moves on (tests), or null. */
    public double? EndAdvanceAt => _endAt;

    // ------------------------------------------------------------ the stored solutions
    public bool CanWatchSolution => Session != null && LevelId != null && Solutions.Has(LevelId);

    /**
     * Watch Solution: the level's stored solution loaded as the replay and played from the start
     * at normal speed, the REPLAY badge on; a win watched this way records no clear.
     */
    public void WatchSolution()
    {
        if (Session == null || LevelId == null) return;
        var text = ReadSolution(LevelId);
        if (text == null) { Windows.Status.Set(note: "no solution", kind: ""); return; }
        ParsedReplay parsed;
        try { parsed = Replay.Parse(text); }
        catch (Exception e) { GD.PushWarning("[app] solution: " + e.Message); return; }
        Session.Game.LoadReplayFile(parsed, "solution");
        Windows.Status.Set(note: "SOLUTION", kind: "");
        SyncSolutionButton(true);
    }

    /** syncSolutionButton: the bar's solution button lit while its solution is the replay engaged. */
    void SyncSolutionButton(bool force)
    {
        bool has = CanWatchSolution;
        bool lit = has && Session!.Game.WatchingSolution;
        if (lit != _solutionLit || force)
        {
            _solutionLit = lit;
            Windows.Toolbar.Solution.SetState(on: lit);
        }
    }
}
