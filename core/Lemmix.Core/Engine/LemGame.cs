using Lemmix.Util;

namespace Lemmix.Engine;

public sealed record SoundCue(string Name, int? X, int? Y);

// One replay entry (TReplay): {type: "assignment", frame, skill, lemIndex, lemId, x, y, dx},
// {type: "spawn_interval", frame, interval, spawned}, {type: "nuke", frame}.
public sealed class ReplayEntry
{
    public required string Type;
    public int Frame;
    public string? Skill;
    public int LemIndex;
    public string? LemId;
    public int X, Y, Dx;
    public int Interval, Spawned;
}

public sealed class GadgetSavedState
{
    public int RemainingLemmings;
    public bool HoldActive, Triggered, SecondariesTreatAsBusy;
    public int TeleLem;
    public bool ZombieMode, NeutralMode;
    public int X, Y;
    public required string Effect;
    public required (int Frame, string State, bool Visible)[] Animations;
}

// TLemmingGameSavedState (lemgame.js saveState)
public sealed class SavedState
{
    public required GameScalars Scalars;
    public required Dictionary<int, int> CurrSkillCount, UsedSkillCount;
    public required HashSet<int> TalismansAchieved;
    public required List<Lemming> Lemmings;
    public required List<GadgetSavedState> Gadgets;
    public required ushort[] Physics;
    public byte[]? GroundImage;
    public sbyte[]? GroundMask;
    public Dictionary<string, object> Extra = new(); // the page's own (depth and relief maps), see Game
}

// SAVED_SCALARS: what a saved state carries of the game's own numbers and flags, in that order.
public struct GameScalars
{
    public int CurrentIteration, ClockFrame, TimePlay, LemmingsToRelease, LemmingsCloned,
        LemmingsOut, LemmingsIn, LemmingsRemoved, SpawnedDead, DelayEndFrames, ParticleFinishTimer,
        NextLemmingCountdown;
    public bool HatchesOpened;
    public int ButtonsRemain, CurrSpawnInterval;
    public bool UserSetNuking, ExploderAssignInProgress;
    public int IndexLemmingToBeNuked;
    public bool GameFinished;
    public int LemNextAction;
    public bool LemJumpToHoistAdvance;
}

// web/lemmix/js/lemgame.js LemGame - the NeoLemmix game mechanics, ported method for method
// from LemGame.pas (TLemmingGame). This file: state, the replay, saved states, setup. The
// rest of the methods are in LemGame.*.cs, in the JS file's order.
public sealed partial class LemGame
{
    public readonly Level Level;
    public readonly Masks Masks;
    public readonly int Width, Height;
    public ushort[] Physics;
    public readonly List<Gadget> Gadgets;
    public List<Lemming> Lemmings = new();           // LemmingList
    public List<SoundCue> Sounds = new();            // cues of the current frame
    public readonly uint[] TriggerMap;
    public readonly uint[] BlockerMap;
    public readonly byte[] ZombieMap;
    public int SimulationDepth;
    public int CurrentIteration;
    public int ClockFrame;
    public int TimePlay;
    public readonly bool HasTimeLimit;
    public int LemmingsToRelease;
    public int LemmingsCloned;
    public int LemmingsOut;
    public int LemmingsIn;
    public int LemmingsRemoved;
    public int SpawnedDead;
    public int DelayEndFrames;
    public int ParticleFinishTimer;
    public int NextLemmingCountdown = 20;
    public bool HatchesOpened;
    public int ButtonsRemain;
    public int CurrSpawnInterval;
    public int SpawnIntervalModifier;
    public bool UserSetNuking;
    public bool ExploderAssignInProgress;
    public int IndexLemmingToBeNuked;
    public bool GameFinished;
    public bool GameCheated;
    public bool DoneAssignmentThisFrame;
    public int LemNextAction = BA.NONE;
    public bool LemJumpToHoistAdvance;
    public Lemming? LastBlockerCheckLem;
    public string? SelectedSkill;                    // a skill name, or null
    public readonly List<string> ActiveSkills;
    public Dictionary<int, int> CurrSkillCount = new(); // by action
    public Dictionary<int, int> UsedSkillCount = new();
    public Dictionary<int, int> SkillLimits = new();
    public HashSet<int> TalismansAchieved = new();
    public List<uint> BrickColors = new();
    public uint? FixedColor;
    public bool Playing;
    // The replay: what the player did, one entry per action. It is the authority: a player's
    // action is recorded here and takes effect when the next update reaches CheckForReplayAction.
    public List<ReplayEntry> Recorded = new();
    public int RecordVersion;    // bumped whenever the replay's entries change
    public int CutVersion;       // bumped when a cut removes entries (the player took control)
    public bool ReplayInsert;    // actions add to the replay without cutting it
    public int SelectDx;         // directional select: -1, 0 or 1 (the panel's, sticky)
    public int HotkeyDx;         // the same while a direction hotkey is held; it wins
    public bool SelectWalkerOnly; // the Select Walker hotkey held: pick walkers only

    // `masks` = the gfx/mask bitmaps (Masks.Load)
    public LemGame(Level level, Masks masks)
    {
        Level = level;
        Masks = masks;
        Width = level.Width;
        Height = level.Height;
        Physics = level.Physics;
        Gadgets = level.Gadgets;
        TriggerMap = new uint[Width * Height];
        BlockerMap = new uint[Width * Height];
        ZombieMap = new byte[Width * Height];
        HasTimeLimit = level.TimeLimitSeconds > 0;
        CurrSpawnInterval = level.SpawnInterval;
        ActiveSkills = level.Skills.Select(s => s.Name).ToList();
    }

    // ---- the replay

    // Take a parsed replay (Replay.Parse) as the game's own, from the start.
    public void LoadReplay(ParsedReplay replay)
    {
        var output = new List<ReplayEntry>();
        foreach (var a in replay.Assignments)
            output.Add(new ReplayEntry { Type = "assignment", Frame = a.Frame, Skill = a.Skill, LemIndex = a.LemIndex, LemId = a.LemId, X = a.X, Y = a.Y, Dx = a.Dx });
        foreach (var s in replay.SpawnIntervals)
            output.Add(new ReplayEntry { Type = "spawn_interval", Frame = s.Frame, Interval = s.Interval, Spawned = s.Spawned });
        foreach (var n in replay.Nukes) output.Add(new ReplayEntry { Type = "nuke", Frame = n.Frame });
        Recorded = output.OrderBy(e => e.Frame).ToList(); // stable, as Array.prototype.sort
        RecordVersion++;
    }

    // TReplay.Add: one entry of a kind per frame, the newer one wins.
    public void Record(ReplayEntry entry)
    {
        Recorded = Recorded.Where(r => !(r.Frame == entry.Frame && r.Type == entry.Type)).ToList();
        Recorded.Add(entry);
        RecordVersion++;
    }

    // The frame of the replay's last action, -1 when it has none.
    public int LastActionFrame
    {
        get
        {
            int last = -1;
            foreach (var r in Recorded) if (r.Frame > last) last = r.Frame;
            return last;
        }
    }

    // Replaying: the replay still has actions ahead of (or on) this frame.
    public bool Replaying => CurrentIteration <= LastActionFrame;

    // Is there a recorded entry of this kind on this frame?
    public bool HasRecorded(string type, int frame)
    {
        foreach (var r in Recorded) if (r.Type == type && r.Frame == frame) return true;
        return false;
    }

    // TReplay.Cut: the replay's future goes - assignments and nukes from `frame` on,
    // spawn-interval changes from the frame after (from `frame` too when the one on it
    // disagrees with the interval in force).
    public void CutReplay(int frame)
    {
        var onFrame = Recorded.FirstOrDefault(r => r.Type == "spawn_interval" && r.Frame == frame);
        int siFrom = onFrame != null && onFrame.Interval != CurrSpawnInterval ? frame : frame + 1;
        int before = Recorded.Count;
        Recorded = Recorded.Where(r => r.Type == "spawn_interval" ? r.Frame < siFrom : r.Frame < frame).ToList();
        if (Recorded.Count != before) { RecordVersion++; CutVersion++; }
    }

    // RegainControl: the player acts, so the replay is cut here - unless it is being added to.
    public void RegainControl(bool force = false)
    {
        if (ReplayInsert && !force) return;
        if (CurrentIteration > LastActionFrame) return;
        CutReplay(CurrentIteration);
    }

    // CheckForReplayAction: what the replay says happens on this frame.
    public void CheckForReplayAction(bool spawnIntervalOnly = false)
    {
        int f = CurrentIteration;
        // each walk over the replay as it stood when it began (a copy kept for it, refilled)
        var now = _recordedNow;
        now.Clear(); now.AddRange(Recorded);
        foreach (var r in now) if (r.Type == "spawn_interval" && r.Frame == f) ApplySpawnInterval(r.Interval);
        if (spawnIntervalOnly) return;
        now.Clear(); now.AddRange(Recorded);
        foreach (var a in now)
        {
            if (a.Frame != f) continue;
            if (a.Type == "nuke") { UserSetNuking = true; ExploderAssignInProgress = true; continue; }
            if (a.Type != "assignment") continue;
            Lemming? L = null;
            string id = JsString.Upper(a.LemId ?? "");
            if (id != "") L = LemmingWithIdentifier(id);
            if (L == null && a.LemIndex >= 0 && a.LemIndex < Lemmings.Count) L = Lemmings[a.LemIndex];
            if (L == null || a.Skill == null || !Lem.SkillToAction.TryGetValue(a.Skill, out int action) || !Lem.Assignable.Contains(action)) continue;
            if (L.Removed || L.Teleporting || L.PortalWarpFrame > 0) continue;
            if (MayAssign(action, L) && CheckSkillAvailable(action))
            {
                if (DoSkillAssignment(L, action)) CueSoundEffect(SFX.ASSIGN_SKILL, L);
            }
        }
    }

    readonly List<ReplayEntry> _recordedNow = new();

    // the first lemming whose identifier is `id` (upper case), or null
    Lemming? LemmingWithIdentifier(string id)
    {
        foreach (var x in Lemmings) if (JsString.Upper(x.Identifier) == id) return x;
        return null;
    }

    // ---- saved states (TLemmingGameSavedState)

    GameScalars GetScalars() => new()
    {
        CurrentIteration = CurrentIteration, ClockFrame = ClockFrame, TimePlay = TimePlay, LemmingsToRelease = LemmingsToRelease,
        LemmingsCloned = LemmingsCloned, LemmingsOut = LemmingsOut, LemmingsIn = LemmingsIn, LemmingsRemoved = LemmingsRemoved,
        SpawnedDead = SpawnedDead, DelayEndFrames = DelayEndFrames, ParticleFinishTimer = ParticleFinishTimer,
        NextLemmingCountdown = NextLemmingCountdown, HatchesOpened = HatchesOpened, ButtonsRemain = ButtonsRemain,
        CurrSpawnInterval = CurrSpawnInterval, UserSetNuking = UserSetNuking, ExploderAssignInProgress = ExploderAssignInProgress,
        IndexLemmingToBeNuked = IndexLemmingToBeNuked, GameFinished = GameFinished, LemNextAction = LemNextAction,
        LemJumpToHoistAdvance = LemJumpToHoistAdvance,
    };

    void SetScalars(GameScalars s)
    {
        CurrentIteration = s.CurrentIteration; ClockFrame = s.ClockFrame; TimePlay = s.TimePlay; LemmingsToRelease = s.LemmingsToRelease;
        LemmingsCloned = s.LemmingsCloned; LemmingsOut = s.LemmingsOut; LemmingsIn = s.LemmingsIn; LemmingsRemoved = s.LemmingsRemoved;
        SpawnedDead = s.SpawnedDead; DelayEndFrames = s.DelayEndFrames; ParticleFinishTimer = s.ParticleFinishTimer;
        NextLemmingCountdown = s.NextLemmingCountdown; HatchesOpened = s.HatchesOpened; ButtonsRemain = s.ButtonsRemain;
        CurrSpawnInterval = s.CurrSpawnInterval; UserSetNuking = s.UserSetNuking; ExploderAssignInProgress = s.ExploderAssignInProgress;
        IndexLemmingToBeNuked = s.IndexLemmingToBeNuked; GameFinished = s.GameFinished; LemNextAction = s.LemNextAction;
        LemJumpToHoistAdvance = s.LemJumpToHoistAdvance;
    }

    // Everything a later LoadState needs to put this frame back. With `physicsOnly` the
    // picture and the ground mask are left out (a solver needs the physics map alone).
    // `reuse`: a state no longer kept (SaveStates' spares), whose big arrays are filled in again
    // instead of allocated - the same values either way.
    public SavedState SaveState(bool physicsOnly = false, SavedState? reuse = null) => new()
    {
        Scalars = GetScalars(),
        CurrSkillCount = new Dictionary<int, int>(CurrSkillCount),
        UsedSkillCount = new Dictionary<int, int>(UsedSkillCount),
        TalismansAchieved = new HashSet<int>(TalismansAchieved),
        Lemmings = Lemmings.Select(L => L.Clone()).ToList(),
        Gadgets = Gadgets.Select(g => new GadgetSavedState
        {
            RemainingLemmings = g.RemainingLemmings, HoldActive = g.HoldActive, Triggered = g.Triggered,
            SecondariesTreatAsBusy = g.SecondariesTreatAsBusy, TeleLem = g.TeleLem, ZombieMode = g.ZombieMode,
            NeutralMode = g.NeutralMode, X = g.X, Y = g.Y, Effect = g.Effect, // a disarmed trap is "NONE"
            Animations = g.Animations.Select(a => (a.Frame, a.State, a.Visible)).ToArray(),
        }).ToList(),
        Physics = CopyInto(Level.Physics, reuse?.Physics)!,
        GroundImage = physicsOnly ? null : CopyInto(Level.GroundImage, reuse?.GroundImage),
        GroundMask = physicsOnly ? null : CopyInto(Level.GroundMask.GroundMask, reuse?.GroundMask),
        Extra = reuse?.Extra ?? new(),
    };

    // a copy of `src`, in `into` when it is there and the same size
    public static T[] CopyInto<T>(T[] src, T[]? into)
    {
        if (into == null || into.Length != src.Length) return (T[])src.Clone();
        Array.Copy(src, into, src.Length);
        return into;
    }

    // LoadSavedState: back to that frame. The replay, the selected skill and the player's
    // settings stay; the terrain goes back into the arrays the renderer holds references to.
    public void LoadState(SavedState s)
    {
        SetScalars(s.Scalars);
        CurrSkillCount = new Dictionary<int, int>(s.CurrSkillCount);
        UsedSkillCount = new Dictionary<int, int>(s.UsedSkillCount);
        TalismansAchieved = new HashSet<int>(s.TalismansAchieved);
        Lemmings = s.Lemmings.Select((L, i) => { var c = L.Clone(); c.Index = i; return c; }).ToList();
        for (int i = 0; i < s.Gadgets.Count; i++)
        {
            var gs = s.Gadgets[i];
            if (i >= Gadgets.Count) break;
            var g = Gadgets[i];
            g.RemainingLemmings = gs.RemainingLemmings; g.HoldActive = gs.HoldActive; g.Triggered = gs.Triggered;
            g.SecondariesTreatAsBusy = gs.SecondariesTreatAsBusy; g.TeleLem = gs.TeleLem; g.ZombieMode = gs.ZombieMode;
            g.NeutralMode = gs.NeutralMode;
            g.Effect = gs.Effect;
            g.X = gs.X; g.Y = gs.Y;
            if (g.Object != null) { g.Object.X = g.X; g.Object.Y = g.Y; }
            for (int j = 0; j < gs.Animations.Length && j < g.Animations.Count; j++)
            {
                var t = g.Animations[j];
                (t.Frame, t.State, t.Visible) = gs.Animations[j];
            }
        }
        s.Physics.CopyTo(Level.Physics, 0);
        s.GroundImage?.CopyTo(Level.GroundImage, 0);
        s.GroundMask?.CopyTo(Level.GroundMask.GroundMask, 0);
        ClearZombieMap(all: true);
        SetBlockerMap();
        SpawnIntervalModifier = 0;
        Sounds = new List<SoundCue>();
        LastBlockerCheckLem = null;
        DoneAssignmentThisFrame = false;
    }

    // ---- setup

    public void Start()
    {
        var level = Level;
        GameFinished = false;
        LemmingsToRelease = level.ReleaseCount;
        LemmingsCloned = 0;
        TimePlay = HasTimeLimit ? level.TimeLimitSeconds : 0;
        LemmingsOut = 0;
        SpawnedDead = level.ZombieCount;
        LemmingsIn = 0;
        LemmingsRemoved = 0;
        DelayEndFrames = 0;
        CurrentIteration = 0;
        ClockFrame = 0;
        HatchesOpened = false;
        SpawnIntervalModifier = 0;
        UserSetNuking = false;
        ExploderAssignInProgress = false;
        IndexLemmingToBeNuked = 0;
        ParticleFinishTimer = 0;
        Lemmings = new List<Lemming>();
        CurrSpawnInterval = level.SpawnInterval;
        foreach (string name in LevelBuilder.Skills) CurrSkillCount[Lem.SkillToAction[name]] = 0;
        foreach (var s in level.Skills) CurrSkillCount[Lem.SkillToAction[s.Name]] = s.Count;
        foreach (string name in LevelBuilder.Skills) UsedSkillCount[Lem.SkillToAction[name]] = 0;
        NextLemmingCountdown = 20;
        ButtonsRemain = 0;
        foreach (var g in Gadgets)
        {
            g.Triggered = false; g.TeleLem = -1; g.HoldActive = false;
            g.ZombieMode = false; g.NeutralMode = false; g.SecondariesTreatAsBusy = false;
            if (g.Effect == "BUTTON") ButtonsRemain++;
        }
        InitializeBrickColors(StyleManager.ThemeColor(level.Theme, "MASK"));
        InitializeAllTriggerMaps();
        SetGadgetMap();
        AddPreplacedLemmings();
        SetBlockerMap();
        DrawAnimatedGadgets();
        SelectedSkill = ActiveSkills.Count > 0 ? ActiveSkills[0] : null;
        Playing = true;
    }

    void InitializeBrickColors(int rgb)
    {
        int r = (rgb >> 16) & 255, g = (rgb >> 8) & 255, b = rgb & 255;
        BrickColors = new List<uint>();
        for (int i = 0; i < 12; i++)
        {
            int C(int v) => Math.Min(Math.Max(v + (i - 6) * 4, 0), 255);
            // ABGR word, the layout the level picture keeps
            BrickColors.Add(unchecked((uint)(0xff << 24 | C(b) << 16 | C(g) << 8 | C(r))));
        }
    }

    void AddPreplacedLemmings()
    {
        foreach (var pre in Level.Preplaced)
        {
            var L = new Lemming(Lemmings.Count);
            Lemmings.Add(L);
            L.Identifier = "P" + pre.X + "." + pre.Y;
            L.X = pre.X; L.Y = pre.Y; L.Dx = pre.Dx;
            L.IsSlider = pre.Slider; L.IsClimber = pre.Climber; L.IsSwimmer = pre.Swimmer;
            L.IsFloater = pre.Floater; L.IsGlider = pre.Glider; L.IsDisarmer = pre.Disarmer;
            L.IsNeutral = pre.Neutral;
            if (!HasPixelAt(L.X, L.Y)) Transition(L, BA.FALLING);
            else if (pre.Blocker && !CheckForOverlappingField(L)) Transition(L, BA.BLOCKING);
            else Transition(L, BA.WALKING);
            if (L.Action == BA.FALLING) L.InitialFall = true;
            if (pre.Zombie) { RemoveLemming(L, RM.ZOMBIE, true); SpawnedDead--; }
            LemmingsToRelease--;
            LemmingsOut++;
        }
    }

    // ---- shared by every part (lemgame.js isSimulating, cueSoundEffect)

    public bool IsSimulating => SimulationDepth > 0;

    // cueSoundEffect(name, pos): one cue of a name per frame; pos a lemming, a point or none.
    public void CueSoundEffect(string? name) => Cue(name, null, null);
    public void CueSoundEffect(string? name, Lemming L) => Cue(name, L.X, L.Y);
    public void CueSoundEffect(string? name, int x, int y) => Cue(name, x, y);

    void Cue(string? name, int? x, int? y)
    {
        if (IsSimulating || string.IsNullOrEmpty(name)) return;
        foreach (var s in Sounds) if (s.Name == name) return;
        Sounds.Add(new SoundCue(name, x, y));
    }
}
