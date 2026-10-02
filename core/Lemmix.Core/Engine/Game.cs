using Lemmix.Util;

namespace Lemmix.Engine;

// web/lemmix/js/game.js - the NeoLemmix simulation behind the surface the 3D layer drives: the
// game, its timer, lemming manager, skills, victory condition and command manager. One frame
// is one LemGame.Update at 17 frames a second. Time can be walked the NeoLemmix way (Rewind):
// a restart, a frame back or a loaded replay load the nearest saved state and simulate forward.

// The DOS GameTimer's surface, at NeoLemmix's frame rate. The web version ticks it with
// setInterval (the 3D page replaces that with a fixed-step accumulator in its frame loop);
// here the host calls Advance(ms) from its frame loop, which is that accumulator.
public sealed class GameTimer
{
    public const double FrameMs = 1000.0 / 17;
    public double TimePerFrameMs = FrameMs;
    double _speedFactor = 1;
    bool _running;
    double _accumulator;
    public int TickIndex;
    public readonly EventHandler<object?> OnGameTick = new();
    public readonly EventHandler<int> OnBeforeGameTick = new();

    public bool IsRunning() => _running;
    public double SpeedFactor
    {
        get => _speedFactor;
        set { _speedFactor = value; if (IsRunning()) { Suspend(); Continue(); } }
    }
    public void Suspend() { _running = false; _accumulator = 0; }
    public void Stop() { Suspend(); OnBeforeGameTick.Dispose(); OnGameTick.Dispose(); }
    public void Toggle() { if (IsRunning()) Suspend(); else Continue(); }
    public void Continue() { if (IsRunning()) return; _running = true; _accumulator = 0; }

    public void Tick()
    {
        OnBeforeGameTick.Trigger(TickIndex);
        TickIndex++;
        OnGameTick.Trigger(null);
    }

    // The interval's ticks owed after `ms` of wall time, run now (at most `maxTicks`, so a stall
    // does not turn into a burst). Returns the ticks run.
    public int Advance(double ms, int maxTicks = 64)
    {
        if (!_running) return 0;
        _accumulator += ms;
        double period = TimePerFrameMs / _speedFactor;
        int n = 0;
        while (_running && _accumulator >= period && n < maxTicks) { _accumulator -= period; Tick(); n++; }
        if (n == maxTicks) _accumulator = 0;
        return n;
    }

    public int GetGameTime() => TickIndex / 17;
    public int GetGameTicks() => TickIndex;
    public double TicksToSeconds(double t) => t / 17;
    public double SecondsToTicks(double s) => s * 17;
}

// getGameSkills(): skill ids are positions in the level's panel (0..9).
public sealed class GameSkills
{
    readonly LemGame _sim;
    public GameSkills(LemGame sim) { _sim = sim; }
    public readonly EventHandler<object?> OnCountChanged = new();
    public readonly EventHandler<object?> OnSelectionChanged = new();
    public List<string> Names => _sim.ActiveSkills;
    public int GetSelectedSkill() => _sim.SelectedSkill == null ? -1 : _sim.ActiveSkills.IndexOf(_sim.SelectedSkill);
    public bool SetSelectedSkill(int? i)
    {
        string? name = i is int k && k >= 0 && k < _sim.ActiveSkills.Count ? _sim.ActiveSkills[k] : null;
        if (name == null) return false;
        _sim.SetSelectedSkill(name);
        OnSelectionChanged.Trigger(null);
        return true;
    }
    public int GetSkill(int i) => i >= 0 && i < _sim.ActiveSkills.Count ? _sim.SkillCountOf(_sim.ActiveSkills[i]) : 0;
    public bool CanReduseSkill(int i)
    {
        string? name = i >= 0 && i < _sim.ActiveSkills.Count ? _sim.ActiveSkills[i] : null;
        return name != null && _sim.CheckSkillAvailable(Lem.SkillToAction[name]);
    }
    public bool ReduseSkill(int i) => true; // the simulation already took it
    public void Cheat() { foreach (string n in _sim.ActiveSkills) _sim.CurrSkillCount[Lem.SkillToAction[n]] = 99; }
}

// getVictoryCondition(): counts and the release rate, in DOS terms.
public sealed class Victory
{
    readonly LemGame _sim;
    public Victory(LemGame sim) { _sim = sim; }
    public int GetNeedCount() => _sim.Level.NeedCount;
    public int GetReleaseCount() => _sim.Level.ReleaseCount;
    public int GetSurvivorsCount() => _sim.LemmingsIn;
    public int GetSurvivorPercentage() => JsMath.Floor(_sim.LemmingsIn / (double)Math.Max(1, _sim.Level.ReleaseCount) * 100);
    public int GetLeftCount() => _sim.LemmingsToRelease;
    public int GetOutCount() => _sim.LemmingsOut;
    public int GetCurrentReleaseRate() => _sim.ReleaseRate;
    public int GetMinReleaseRate() => _sim.MinReleaseRate;
    public int GetMaxReleaseRate() => 99;
    // DOS commands change the rate in steps; NeoLemmix moves the interval.
    public bool ChangeReleaseRate(int delta)
    {
        int target = _sim.CurrSpawnInterval - Math.Sign(delta);
        if (!_sim.CheckIfLegalSI(target)) return false;
        _sim.AdjustSpawnInterval(target);
        return true;
    }
    public bool DoNuke() { _sim.Nuke(); return true; } // the manager already asked; Nuke() is idempotent
    public void DoFinalize() { }
}

// getLemmingManager(): the lemmings, and how a click picks one.
public sealed class LemmingManager
{
    readonly Game _game;
    public LemmingManager(Game game) { _game = game; }
    public List<Lemming> Lemmings => _game.Sim.Lemmings;
    public Lemming? GetLemming(int id) => id >= 0 && id < _game.Sim.Lemmings.Count ? _game.Sim.Lemmings[id] : null;
    // The lemming NeoLemmix would give the selected skill to, at this cursor position.
    public Lemming? GetLemmingAt(int x, int y)
    {
        var sim = _game.Sim;
        int action = sim.SelectedSkill != null ? Lem.SkillToAction[sim.SelectedSkill] : BA.NONE;
        var pick = sim.GetPriorityLemming(action, x, y);
        if (pick.Lemming != null) return pick.Lemming;
        return sim.GetPriorityLemming(BA.NONE, x, y).Lemming; // hover: any lemming under the cursor
    }
    // The lemming NeoLemmix marks at this cursor position - the one the selected skill would go to - or null.
    public Lemming? GetSelectedLemmingAt(int x, int y)
    {
        var sim = _game.Sim;
        int action = sim.SelectedSkill != null ? Lem.SkillToAction[sim.SelectedSkill] : BA.NONE;
        return sim.GetPriorityLemming(action, x, y).Lemming;
    }
    public bool DoLemmingAction(Lemming lem, int skillIndex)
    {
        string? name = skillIndex >= 0 && skillIndex < _game.Sim.ActiveSkills.Count ? _game.Sim.ActiveSkills[skillIndex] : null;
        return name != null && _game.Sim.AssignSkillTo(lem, name);
    }
    public void AddNewLemmings() { } // the simulation releases its own
    public bool IsNuking() => _game.Sim.UserSetNuking;
    public void DoNukeAllLemmings() => _game.Sim.Nuke();
    public int GetLemmingsOut() => _game.Sim.LemmingsOut;
}

// what the page's panel needs from the game to redraw (panel.js GamePanel implements it)
public interface IGamePanel
{
    void Render(bool force = false);
    void Dispose();
}

public sealed record ReplayMode(string Kind, int CutVersion, int RecordVersion);
public sealed record StateMark(int Frame, List<ReplayEntry> Recorded);
public sealed record RestoreInfo(int Frame, bool Paused);

public sealed class Game
{
    public readonly Level Level;
    public readonly LemGame Sim;
    public readonly GameTimer GameTimer = new();
    public readonly GameSkills Skills;
    public readonly Victory VictoryCondition;
    public readonly CommandManager CommandManager;
    public readonly EventHandler<ClassicGameResult> OnGameEnd = new();
    public GameStateTypes FinalGameState = GameStateTypes.UNKNOWN;
    public readonly LemmingManager LemmingManager;
    public IGamePanel? Gui;
    public readonly EventHandler<RestoreInfo> OnRestore = new(); // the game jumped to another frame
    public Action? OnLoadReplayRequest;                            // the page opens a file picker
    public Lemming? CursorLemming;                                  // what the pointer is on, for the info strip
    public bool NukePrepared;
    public bool ClearPhysics;                                       // the level as its physics map
    public Action? OnOptionChanged;                                 // the page redraws what it shows for an option
    public bool ShowAthleteInfo;                                    // the hotkey held: the info strip spells the permanent skills
    public StateMark? StateMarkSaved;                               // the Save State hotkey
    // replay mode, as the page sees it: engaged by a loaded file, a solution, the panel's replay
    // button or Load State, and over the moment the player takes control (a cut), a cancel, or a new level
    public ReplayMode? Mode;
    public readonly SaveStates States = new();
    readonly Dictionary<int, Frame> _countdown = new();

    // `prepareLevel` runs before the sim starts: the web version paints the pickup icons from the
    // lemming sprites there (sprites.js generatePickupIcons), which only changes how pickups look.
    public Game(Level level, Masks masks, Action<Level>? prepareLevel = null)
    {
        Level = level;
        Sim = new LemGame(level, masks);
        prepareLevel?.Invoke(level);
        Skills = new GameSkills(Sim);
        VictoryCondition = new Victory(Sim);
        CommandManager = new CommandManager(this, GameTimer);
        LemmingManager = new LemmingManager(this);
        GameTimer.OnGameTick.On(_ => OnGameTimerTick());
        Sim.Start();
        States.Add(Sim); // frame 0: what a restart goes back to
    }

    public void Start() => GameTimer.Continue();
    public void Stop() => GameTimer.Stop();
    public void Dispose() { Stop(); Gui?.Dispose(); }
    public void QueueCommand(ICommand cmd) => CommandManager.QueueCommand(cmd);

    void OnGameTimerTick()
    {
        Sim.Update();
        // a state every ten seconds, the list thinned as it grows
        if (Sim.CurrentIteration > 0 && Sim.CurrentIteration % SaveStates.SaveEvery == 0 &&
            !States.States.Any(s => SaveStates.FrameOf(s) == Sim.CurrentIteration))
        {
            States.Add(Sim);
            States.Tidy(Sim.CurrentIteration);
        }
        CheckForGameOver();
        Gui?.Render();
    }

    // ---- walking time (the panel's replay, frame back and frame forward)

    public bool Replaying => Sim.Replaying;

    // Replay mode on: the record is being played back and the player has not taken over.
    public void EngageReplay(string? kind = null) => Mode = new ReplayMode(kind ?? "attempt", Sim.CutVersion, Sim.RecordVersion);
    public void DisengageReplay() => Mode = null;
    public bool ReplayEngaged => Mode != null && Sim.CutVersion == Mode.CutVersion;
    // The replay engaged is a level's stored solution, untouched since it was loaded: a win is
    // the solver's, never a clear of the player's.
    public bool WatchingSolution => ReplayEngaged && Mode!.Kind == "solution" && Sim.RecordVersion == Mode.RecordVersion;
    public bool ReplayInsert => Sim.ReplayInsert;
    public void ToggleReplayInsert() { Sim.ReplayInsert = !Sim.ReplayInsert; Gui?.Render(true); }
    public void ToggleClearPhysics()
    {
        ClearPhysics = !ClearPhysics;
        Gui?.Render(true);
        OnOptionChanged?.Invoke();
    }
    public void SetClearPhysics(bool on) { if (ClearPhysics != on) ToggleClearPhysics(); }
    public void SetSelectDx(int dx) { Sim.SelectDx = dx; Gui?.Render(true); }

    // ---- the hotkeys' own functions (GameWindow.Form_KeyDown)

    // Select a skill by its NeoLemmix name, when the level has it.
    public bool SelectSkillByName(string name)
    {
        int i = Sim.ActiveSkills.IndexOf(JsString.Upper(name));
        if (i < 0) return false;
        QueueCommand(new CommandSelectSkill(i));
        return true;
    }

    // Next (+1) or previous (-1) skill on the panel, wrapping as NeoLemmix does.
    public void StepSkill(int dir)
    {
        var names = Sim.ActiveSkills;
        int sn = Skills.GetSelectedSkill();
        int to = -1;
        if (dir > 0)
        {
            if (sn >= 0 && sn < names.Count - 1) to = sn + 1;
            else if (sn > 0) to = 0;
        }
        else
        {
            if (sn > 0) to = sn - 1;
            else if (sn == 0 && names.Count > 1) to = names.Count - 1;
        }
        if (to >= 0) QueueCommand(new CommandSelectSkill(to));
    }

    // The release rate to its limit (spbFaster/spbSlower with RightClick).
    public void SetReleaseRateExtreme(int dir)
    {
        for (int i = 0; i < 200 && VictoryCondition.ChangeReleaseRate(dir); i++) { /* one step each */ }
        Gui?.Render(true);
    }

    // Cancel Replay: the player takes over even in replay-insert mode.
    public void CancelReplay() { Sim.RegainControl(true); DisengageReplay(); Gui?.Render(true); }

    static ReplayEntry CopyEntry(ReplayEntry r) => new()
    {
        Type = r.Type, Frame = r.Frame, Skill = r.Skill, LemIndex = r.LemIndex, LemId = r.LemId,
        X = r.X, Y = r.Y, Dx = r.Dx, Interval = r.Interval, Spawned = r.Spawned,
    };

    // Save State: this frame and the replay as it stands, for Load State.
    public void SaveStateMark() => StateMarkSaved = new StateMark(Sim.CurrentIteration, Sim.Recorded.Select(CopyEntry).ToList());

    // Load State: the replay as saved, the game at the saved frame, paused.
    public bool LoadStateMark()
    {
        if (StateMarkSaved == null) return false;
        Sim.Recorded = StateMarkSaved.Recorded.Select(CopyEntry).ToList();
        GotoFrame(StateMarkSaved.Frame, true);
        if (Sim.Recorded.Count > 0) EngageReplay("attempt");
        return true;
    }

    // Skip to Previous Assignment: the frame before the replay's last action at or before now.
    public void SkipToLastAction()
    {
        var sim = Sim;
        int last = sim.LastActionFrame;
        if (last == -1) return;
        int target = 0;
        if (sim.CurrentIteration > last) target = last;
        else for (int i = 0; i <= sim.CurrentIteration; i++) if (sim.Recorded.Any(r => r.Frame == i)) target = i;
        GotoFrame(Math.Max(target - 1, 0), true);
    }

    // Skip to Next Shrugger: ahead at hyperspeed until a builder, platformer or stacker runs out, then paused.
    public void SkipToNextShrugger()
    {
        var sim = Sim;
        static bool Busy(Lemming L) => !L.Removed && (L.Action == BA.BUILDING || L.Action == BA.PLATFORMING || L.Action == BA.STACKING);
        if (!sim.Lemmings.Any(Busy)) return;
        Rewind.RunUntil(sim, States, s => s.Lemmings.Any(L => !L.Removed && L.Action == BA.SHRUGGING), 17 * 60 * 10);
        AfterJump(true);
    }

    // The game at `frame`, the replay kept; paused when `pause`.
    public void GotoFrame(int frame, bool pause)
    {
        Rewind.GotoFrame(Sim, States, frame);
        AfterJump(pause);
    }

    // The replay button: the level from the start, paused, the attempt replaying.
    public void RestartReplay() { GotoFrame(0, true); if (Sim.Recorded.Count > 0 && !WatchingSolution) EngageReplay("attempt"); }

    // One (or 17, or 85) frames back, paused.
    public void BackFrames(int n) => GotoFrame(Sim.CurrentIteration - n, true);

    // One frame forward is a tick while paused; more is a skip ahead at whatever speed the game was at.
    public void ForwardFrames(int n)
    {
        if (n <= 1) { ForceOneFrame(); return; }
        Rewind.GotoFrame(Sim, States, Sim.CurrentIteration + n);
        AfterJump(false);
    }

    // A paused game runs one frame: a click's assignment shows (ForceUpdateOneFrame).
    public void ForceOneFrame() { if (!GameTimer.IsRunning()) GameTimer.Tick(); }

    // A loaded replay file plays from the start at normal speed; `kind` says what it is ("solution", or a "file").
    public void LoadReplayFile(ParsedReplay parsed, string? kind = null)
    {
        Sim.LoadReplay(parsed);
        GameTimer.SpeedFactor = 1;
        Rewind.GotoFrame(Sim, States, 0);
        EngageReplay(kind ?? "file");
        AfterJump(false);
        if (!GameTimer.IsRunning()) GameTimer.Continue();
    }

    public void RequestLoadReplay() => OnLoadReplayRequest?.Invoke();

    void AfterJump(bool pause)
    {
        var sim = Sim;
        // the command log and the timer count frames the way the sim does
        GameTimer.TickIndex = sim.CurrentIteration;
        foreach (int k in CommandManager.LoggedCommands.Keys.Where(k => k >= sim.CurrentIteration).ToList()) CommandManager.LoggedCommands.Remove(k);
        FinalGameState = GameStateTypes.UNKNOWN; // re-latched by CheckForGameOver if still over
        NukePrepared = false;
        if (pause) GameTimer.Suspend();
        Gui?.Render(true);
        OnRestore.Trigger(new RestoreInfo(sim.CurrentIteration, !GameTimer.IsRunning()));
    }

    public GameStateTypes GetGameState()
    {
        if (FinalGameState != GameStateTypes.UNKNOWN) return FinalGameState;
        var sim = Sim;
        bool won = sim.LemmingsIn >= sim.Level.NeedCount;
        if (sim.GameFinished || sim.StateIsUnplayable)
            return won ? GameStateTypes.SUCCEEDED : (sim.IsOutOfTime ? GameStateTypes.FAILED_OUT_OF_TIME : GameStateTypes.FAILED_LESS_LEMMINGS);
        if (sim.IsOutOfTime && !sim.UserSetNuking) sim.Nuke(); // NeoLemmix nukes when the clock runs out
        return GameStateTypes.RUNNING;
    }

    public void CheckForGameOver()
    {
        if (FinalGameState != GameStateTypes.UNKNOWN) return;
        var state = GetGameState();
        if (state != GameStateTypes.RUNNING && state != GameStateTypes.UNKNOWN)
        {
            FinalGameState = state;
            OnGameEnd.Trigger(new ClassicGameResult(this));
        }
    }

    // Sound cues of the last frame.
    public List<SoundCue> Sounds => Sim.Sounds;

    // The 4x5 countdown digit `n` as a frame, cached.
    public Frame? CountdownFrame(int n)
    {
        var font = LevelBuilder.DigitFont;
        if (font == null) return null;
        if (!_countdown.TryGetValue(n, out var f))
        {
            var bmp = font.Crop(Math.Min(9, Math.Max(0, n)) * 4, 0, 4, 5);
            f = Frame.FromBitmap(bmp, 0, 0);
            _countdown[n] = f;
        }
        return f;
    }

    // A NeoLemmix replay as this game's own, before it starts.
    public void LoadReplay(ParsedReplay replay, string? kind = null) { Sim.LoadReplay(replay); EngageReplay(kind ?? "file"); }
}
