using Lemmix.Util;

namespace Lemmix.Engine;

// result(): how the level ends. Talismans in the order they were achieved (Array.from(set)).
public sealed record GameResult(bool Success, int Saved, int Needed, int Count, bool TimeUp, List<int> Talismans);

// web/lemmix/js/lemgame.js lines 2087-2526, ported method for method: removeLemming and the
// talisman check, the frame (update and the checks it runs), the shadows' simulation, teleporting,
// gadget animation, and the player's controls (skills, nuke, spawn interval, result).
public sealed partial class LemGame
{
    public void RemoveLemming(Lemming L, int remMode, bool silent = false)
    {
        if (IsSimulating) return;
        if (L.IsZombie)
        {
            L.Removed = true;
            if (remMode == RM.NEUTRAL && !silent) CueSoundEffect(SFX.FALLOUT, L);
        }
        else if (!L.Removed)
        {
            LemmingsRemoved++;
            LemmingsOut--;
            L.Removed = true;
            switch (remMode)
            {
                case RM.SAVE: LemmingsIn++; break;
                case RM.NEUTRAL: if (!silent) CueSoundEffect(SFX.FALLOUT, L); break;
                case RM.ZOMBIE:
                    if (!silent) CueSoundEffect(SFX.ZOMBIE);
                    L.IsZombie = true;
                    L.Removed = false;
                    break;
            }
        }
        DoTalismanCheck();
    }

    public void DoTalismanCheck()
    {
        foreach (var t in Level.Talismans)
        {
            int saveReq = t.Save >= 0 ? t.Save : Level.NeedCount;
            if (LemmingsIn < saveReq) continue;
            if (t.TimeLimit >= 0 && CurrentIteration >= t.TimeLimit) continue;
            int total = 0, types = 0;
            bool ok = true;
            foreach (string name in LevelBuilder.Skills)
            {
                int used = SkillsUsed(name);
                // `name in t.limits && t.limits[name] >= 0 && used > t.limits[name]`
                foreach (var kv in t.Limits)
                {
                    if (kv.Key != name) continue;
                    if (kv.Value >= 0 && used > kv.Value) ok = false;
                    break;
                }
                total += used;
                if (used > 0) types++;
            }
            if (!ok) continue;
            if (t.SkillLimit >= 0 && total > t.SkillLimit) continue;
            if (t.SkillTypeLimit >= 0 && types > t.SkillTypeLimit) continue;
            TalismansAchieved.Add(t.Id);
        }
    }

    // ---- the frame

    // UpdateLemmings: one frame of the game.
    public void Update()
    {
        DoneAssignmentThisFrame = false;
        Sounds = new List<SoundCue>();
        if (GameFinished || StateIsUnplayable) return;
        CheckAdjustSpawnInterval();
        CheckForQueuedAction();
        CheckForReplayAction();
        IncrementIteration();
        CheckReleaseLemming();
        CheckLemmings();
        CheckUpdateNuking();
        UpdateGadgets();
        DrawAnimatedGadgets();
    }

    public void IncrementIteration()
    {
        CurrentIteration++;
        ClockFrame++;
        if (DelayEndFrames > 0) DelayEndFrames--;
        if (ParticleFinishTimer > 0) ParticleFinishTimer--;
        if (ClockFrame == 17)
        {
            ClockFrame = 0;
            if (TimePlay > -5999) TimePlay--;
            if (TimePlay == 0) CueSoundEffect(SFX.TIMEUP);
        }
        switch (CurrentIteration)
        {
            case 15:
                CueSoundEffect(Gadgets.Any(g => g.Effect == "WINDOW" && g.Presets.Zombie) ? SFX.ZOMBIE : SFX.LETSGO);
                break;
            case 35:
            {
                int count = 0, ax = 0, ay = 0;
                foreach (var g in Gadgets)
                {
                    if (g.EffectBase != "WINDOW") continue;
                    g.Triggered = true;
                    g.CurrentFrame = 1;
                    count++;
                    ax += g.X + (g.Width >> 1); ay += g.Y + (g.Height >> 1);
                }
                HatchesOpened = true;
                if (count != 0) CueSoundEffect(SFX.ENTRANCE, JsMath.ToInt32((double)ax / count), JsMath.ToInt32((double)ay / count));
                break;
            }
        }
    }

    public void CheckReleaseLemming()
    {
        if (!HatchesOpened || UserSetNuking) return;
        if (NextLemmingCountdown > 0) NextLemmingCountdown--;
        if (NextLemmingCountdown != 0) return;
        NextLemmingCountdown = CurrSpawnInterval;
        if (LemmingsToRelease <= 0) return;
        var level = Level;
        int pos = level.ReleaseCount - level.Preplaced.Count - LemmingsToRelease;
        // `ix === undefined` past either end of spawnOrder
        if (pos < 0 || pos >= level.SpawnOrder.Count) return;
        int ix = level.SpawnOrder[pos];
        if (ix < 0) return;
        var g = Gadgets[ix];
        var L = new Lemming(Lemmings.Count);
        Lemmings.Add(L);
        L.Identifier = "N" + pos;
        Transition(L, BA.FALLING);
        if (L.Action == BA.FALLING) L.InitialFall = true;
        L.X = g.TriggerRect.X0;
        L.Y = g.TriggerRect.Y0;
        L.Dx = 1;
        if (g.FlipLemming) TurnAround(L);
        L.IsSlider = g.Presets.Slider; L.IsClimber = g.Presets.Climber; L.IsSwimmer = g.Presets.Swimmer;
        L.IsDisarmer = g.Presets.Disarmer; L.IsFloater = g.Presets.Floater;
        if (!L.IsFloater) L.IsGlider = g.Presets.Glider;
        if (g.Presets.Zombie) { SpawnedDead--; RemoveLemming(L, RM.ZOMBIE, true); }
        if (g.Presets.Neutral) L.IsNeutral = true;
        if (g.RemainingLemmings > 0)
        {
            g.RemainingLemmings--;
            if (g.RemainingLemmings == 0) CueSoundEffect(g.Meta.SoundExhaust, g.X, g.Y);
        }
        LemmingsToRelease--;
        LemmingsOut++;
    }

    public void CheckUpdateNuking()
    {
        if (!(UserSetNuking && ExploderAssignInProgress)) return;
        while (IndexLemmingToBeNuked < Lemmings.Count - 1 && Lemmings[IndexLemmingToBeNuked].Removed) IndexLemmingToBeNuked++;
        if (IndexLemmingToBeNuked > Lemmings.Count - 1) ExploderAssignInProgress = false;
        else
        {
            var L = Lemmings[IndexLemmingToBeNuked];
            if (L.ExplosionTimer == 0 && !(L.Action == BA.SPLATTING || L.Action == BA.EXPLODING)) L.ExplosionTimer = 84;
            IndexLemmingToBeNuked++;
        }
    }

    public bool CheckIfZombiesRemain()
    {
        foreach (var L in Lemmings) if (L.IsZombie && !L.Removed) return true;
        if (LemmingsToRelease > 0)
        {
            int p = Level.ReleaseCount - Level.Preplaced.Count - LemmingsToRelease;
            // `i >= 0` is false for undefined (past either end)
            if (p >= 0 && p < Level.SpawnOrder.Count)
            {
                int i = Level.SpawnOrder[p];
                if (i >= 0 && Gadgets[i].Presets.Zombie) return true;
            }
        }
        return false;
    }

    public bool CheckIfLegalSI(int si) => !(Level.SpawnLocked || si < Lem.MIN_SI || si > Level.SpawnInterval);

    // AdjustSpawnInterval: the interval in force changes (what a replay entry does).
    public void ApplySpawnInterval(int si)
    {
        if (si == CurrSpawnInterval || !CheckIfLegalSI(si)) return;
        CurrSpawnInterval = si;
    }

    // The player changes it: recorded, and in force at once (RecordSpawnInterval + CheckForReplayAction(True)).
    public void AdjustSpawnInterval(int si)
    {
        if (si == CurrSpawnInterval || !CheckIfLegalSI(si)) return;
        RegainControl();
        Record(new ReplayEntry { Type = "spawn_interval", Frame = CurrentIteration, Interval = si, Spawned = Lemmings.Count });
        CheckForReplayAction(true);
    }

    public void CheckAdjustSpawnInterval()
    {
        if (SpawnIntervalModifier == 0) return;
        AdjustSpawnInterval(CurrSpawnInterval + SpawnIntervalModifier);
    }

    public void CheckForQueuedAction()
    {
        // for..of: visits lemmings pushed during the loop, keeps the old list if it is replaced
        var list = Lemmings;
        for (int n = 0; n < list.Count; n++)
        {
            var L = list[n];
            if (L.QueueAction == BA.NONE) continue;
            if (L.Removed || L.CannotReceiveSkills || L.Teleporting) { L.QueueAction = BA.NONE; L.QueueFrame = 0; continue; }
            int skill = L.QueueAction;
            if (MayAssign(skill, L) && CheckSkillAvailable(skill))
            {
                // into the replay, applied by CheckForReplayAction right after (CheckForQueuedAction)
                if (!HasRecorded("assignment", CurrentIteration))
                {
                    Record(new ReplayEntry
                    {
                        Type = "assignment", Frame = CurrentIteration, Skill = Lem.ActionToSkill.TryGetValue(skill, out var s) ? s : null,
                        LemIndex = L.Index, LemId = L.Identifier, X = L.X, Y = L.Y, Dx = L.Dx,
                    });
                }
                L.QueueAction = BA.NONE; L.QueueFrame = 0;
            }
            else
            {
                L.QueueFrame++;
                if (L.QueueFrame > 0) { L.QueueAction = BA.NONE; L.QueueFrame = 0; } // SkillQFrames default 0
            }
        }
    }

    public void CheckLemmings()
    {
        ClearZombieMap();
        // for..of: visits lemmings pushed during the loop (cloners), keeps the old list if it is replaced
        var list = Lemmings;
        for (int n = 0; n < list.Count; n++)
        {
            var L = list[n];
            bool cont = true;
            if (L.ParticleTimer >= 0) L.ParticleTimer--;
            if (L.Removed) continue;
            if (L.Teleporting) cont = CheckLemTeleporting(L);
            if (cont && L.PortalWarpFrame > 0) cont = CheckLemPortalWarping(L);
            if (cont && L.ExplosionTimer != 0) cont = !UpdateExplosionTimer(L);
            if (cont) cont = HandleLemming(L);
            if (cont) cont = CheckLevelBoundaries(L);
            if (cont) CheckTriggerArea(L, false);
        }
        var list2 = Lemmings;
        for (int n = 0; n < list2.Count; n++)
        {
            var L = list2[n];
            if ((ReadZombieMap(L.X, L.Y) & 1) != 0 && L.Action != BA.EXITING && !L.IsZombie) RemoveLemming(L, RM.ZOMBIE);
        }
    }

    // SimulateTransition: a transition with nothing drawn or heard (for a shadow).
    public void SimulateTransitionLem(Lemming L, int action)
    {
        SimulationDepth++;
        if (action == BA.STACKING) L.StackLow = !HasPixelAt(L.X + L.Dx, L.Y);
        Transition(L, action);
        SimulationDepth--;
    }

    // SimulateLem: the lemming advanced one frame with nothing drawn or heard;
    // returns the positions it passed (with `doCheckObjects`), for the shadows.
    public List<int[]> SimulateLem(Lemming L, bool doCheckObjects = false)
    {
        SimulationDepth++;
        bool handle = HandleLemming(L);
        if (handle) handle = CheckLevelBoundaries(L);
        List<int[]> pos = new();
        if (handle && doCheckObjects)
        {
            pos = GetGadgetCheckPositions(L);
            for (int i = 0; i < pos.Count; i++)
            {
                int px = pos[i][0], py = pos[i][1];
                if (LemNextAction != BA.NONE && px == L.X && py == L.Y)
                {
                    Transition(L, LemNextAction);
                    LemNextAction = BA.NONE;
                }
                if ((HasTriggerAt(px, py, "TRAP") && FindGadgetId(px, py, "TRAP") != Lem.NO_OBJECT && !L.IsDisarmer)
                    || HasTriggerAt(px, py, "EXIT") || (HasTriggerAt(px, py, "WATER") && !L.IsSwimmer)
                    || HasTriggerAt(px, py, "FIRE") || HasTriggerAt(px, py, "ADDSKILL") || HasTriggerAt(px, py, "REMOVESKILLS")
                    || (HasTriggerAt(px, py, "TELEPORT") && FindGadgetId(px, py, "TELEPORT") != Lem.NO_OBJECT)
                    || (HasTriggerAt(px, py, "PORTAL") && FindGadgetId(px, py, "PORTAL") != Lem.NO_OBJECT))
                {
                    L.Action = BA.EXPLODING;
                    SimulationDepth--;
                    return pos;
                }
                if (HasTriggerAt(px, py, "WATER") && L.IsSwimmer) LemNextAction = BA.SWIMMING;
                if (HasTriggerAt(px, py, "TRAP") && HasPixelAt(px, py) && L.IsDisarmer) LemNextAction = BA.FIXING;
                if (L.X == px && L.Y == py) break;
            }
            if (HasTriggerAt(L.X, L.Y, "FORCELEFT", L)) HandleForceField(L, -1);
            else if (HasTriggerAt(L.X, L.Y, "FORCERIGHT", L)) HandleForceField(L, 1);
        }
        SimulationDepth--;
        return pos;
    }

    public bool CheckLemTeleporting(Lemming L)
    {
        int id = Gadgets.FindIndex(g => g.TeleLem == L.Index);
        if (id < 0) return false;
        var g = Gadgets[id];
        if (g.Effect != "RECEIVER") return false;
        if (g.Meta.KeyFrame == 0 && g.CurrentFrame < g.FrameCount - 1) return false;
        if (g.Meta.KeyFrame > 0 && g.CurrentFrame < g.Meta.KeyFrame - 1) return false;
        L.Teleporting = false;
        g.TeleLem = -1;
        HandlePostTeleport(L);
        return true;
    }

    public void HandlePostTeleport(Lemming L)
    {
        // brickColors[i]: undefined past the end, which the picture stores as 0 (`color >>> 0`)
        uint Brick(int i) => i >= 0 && i < BrickColors.Count ? BrickColors[i] : 0u;
        CheckTriggerArea(L, true);
        if (L.Action == BA.BLOCKING)
        {
            if (CheckForOverlappingField(L)) Transition(L, BA.WALKING);
            else { L.HasBlockerField = true; SetBlockerMap(); }
        }
        if ((L.Action == BA.BUILDING || L.Action == BA.PLATFORMING) && L.PhysicsFrame >= 9) L.ConstructivePositionFreeze = true;
        if (L.Action == BA.BUILDING && (L.BricksLeft < 12 || L.PhysicsFrame >= 9))
        {
            if (L.PhysicsFrame < 9) L.BricksLeft++;
            for (int i = 0; i <= 3; i++) AddConstructivePixel(L.X + i * L.Dx, L.Y, Brick(12 - L.BricksLeft));
            if (L.PhysicsFrame < 9) L.BricksLeft--;
        }
        else if (L.Action == BA.PLATFORMING && (L.BricksLeft < 12 || L.PhysicsFrame >= 9))
        {
            if (L.PhysicsFrame < 9) L.BricksLeft++;
            AddConstructivePixel(L.X, L.Y, Brick(12 - L.BricksLeft));
            if (L.PhysicsFrame < 9) L.BricksLeft--;
        }
    }

    public void MoveLemToReceivePoint(Lemming L, int gadgetId)
    {
        var g = Gadgets[gadgetId];
        var g2 = Gadgets[g.ReceiverId];
        if (g.FlipLemming) TurnAround(L);
        L.X = g2.TriggerRect.X0;
        L.Y = g2.TriggerRect.Y0;
    }

    // Which animation-trigger conditions hold for a gadget (TGadget.GetAnimFlagState).
    public bool AnimFlag(Gadget g, string cond)
    {
        string b = g.EffectBase;
        switch (cond)
        {
            case "unconditional": return true;
            case "ready":
                if (!(b is "TRAP" or "TELEPORT" or "RECEIVER" or "PICKUP" or "LOCKEXIT" or "BUTTON" or "WINDOW" or "TRAPONCE" or "ANIMATION" or "ANIMONCE")) return true;
                if (g.SecondariesTreatAsBusy || g.Effect == "NONE") return false;
                switch (b)
                {
                    case "EXIT": return g.RemainingLemmings != 0;
                    case "TRAP": case "TELEPORT": case "ANIMATION": return g.CurrentFrame == 0;
                    case "LOCKEXIT": return g.CurrentFrame == 0 && g.RemainingLemmings != 0;
                    case "BUTTON": case "TRAPONCE": case "ANIMONCE": return g.CurrentFrame == 1;
                    case "PICKUP": return g.CurrentFrame % 2 != 0;
                    case "RECEIVER": return g.CurrentFrame == 0 && !g.HoldActive;
                    case "WINDOW": return g.CurrentFrame == 0 && g.RemainingLemmings != 0;
                }
                return true;
            case "busy":
                if (!(b is "TRAP" or "TELEPORT" or "RECEIVER" or "LOCKEXIT" or "BUTTON" or "WINDOW" or "TRAPONCE" or "ANIMATION" or "ANIMONCE")) return false;
                if (g.SecondariesTreatAsBusy) return true;
                switch (b)
                {
                    case "TRAP": case "ANIMATION": case "TELEPORT": return g.CurrentFrame > 0;
                    case "TRAPONCE": case "LOCKEXIT": case "BUTTON": case "WINDOW": case "ANIMONCE": return g.CurrentFrame > 1;
                    case "RECEIVER": return g.CurrentFrame > 0 || g.HoldActive;
                }
                return false;
            case "disabled":
                if (!(b is "EXIT" or "TRAP" or "PICKUP" or "LOCKEXIT" or "BUTTON" or "WINDOW" or "TRAPONCE" or "ANIMONCE")) return false;
                if (g.Effect == "NONE") return true;
                switch (b)
                {
                    case "EXIT": return g.RemainingLemmings == 0;
                    case "PICKUP": return g.CurrentFrame % 2 == 0;
                    case "BUTTON": case "TRAPONCE": case "ANIMONCE": return g.CurrentFrame == 0;
                    case "LOCKEXIT": return g.CurrentFrame == 1 || g.RemainingLemmings == 0;
                    case "WINDOW": return g.RemainingLemmings == 0;
                }
                return false;
            case "exhausted":
                switch (b)
                {
                    case "PICKUP": return g.CurrentFrame % 2 == 0;
                    case "BUTTON": case "TRAPONCE": case "ANIMONCE": return g.CurrentFrame == 0;
                    case "EXIT": case "LOCKEXIT": case "WINDOW": return g.RemainingLemmings == 0;
                }
                return false;
        }
        return false;
    }

    public void UpdateGadgets()
    {
        for (int i = Gadgets.Count - 1; i >= 0; i--)
        {
            var g = Gadgets[i];
            if ((g.Triggered || Lem.AlwaysAnimate.Contains(g.EffectBase)) && g.Effect != "PICKUP") g.CurrentFrame = g.CurrentFrame + 1;
            if (g.Effect == "TELEPORT" && g.ReceiverId >= 0)
            {
                var g2 = Gadgets[g.ReceiverId];
                if ((g.CurrentFrame >= g.FrameCount && g.Meta.KeyFrame == 0) || (g.CurrentFrame == g.Meta.KeyFrame && g.Meta.KeyFrame != 0))
                {
                    if (g.TeleLem >= 0)
                    {
                        MoveLemToReceivePoint(Lemmings[g.TeleLem], i);
                        g2.TeleLem = g.TeleLem;
                        g2.Triggered = true;
                        g2.ZombieMode = g.ZombieMode; g2.NeutralMode = g.NeutralMode;
                        g.TeleLem = -1;
                    }
                }
                g.SecondariesTreatAsBusy = g2.Triggered;
            }
            if (g.CurrentFrame >= g.FrameCount)
            {
                g.CurrentFrame = 0;
                g.Triggered = false;
                g.HoldActive = false;
                g.ZombieMode = false; g.NeutralMode = false;
            }
            // secondary animations (TGadgetAnimationInstance.UpdateOneFrame)
            foreach (var a in g.Animations)
            {
                if (a.Primary) continue;
                for (int t = a.Meta.Triggers.Count - 1; t >= 0; t--)
                {
                    var trig = a.Meta.Triggers[t];
                    if (AnimFlag(g, trig.Condition)) { a.State = trig.State; a.Visible = trig.Visible; break; }
                }
                if (a.State != "pause" && (a.State != "looptozero" || a.Frame > 0)) a.Frame = (a.Frame + 1) % a.Meta.FrameCount;
                switch (a.State)
                {
                    case "looptozero": if (a.Frame == 0) a.State = "pause"; break;
                    case "stop": a.Frame = 0; a.State = "pause"; break;
                    case "matchphysics": a.Frame = g.CurrentFrame; break;
                }
            }
        }
    }

    static readonly int[] AnimObjMov = { 0, 1, 2, 2, 2, 2, 2, 1, 0, -1, -2, -2, -2, -2, -2, -1 };

    public void DrawAnimatedGadgets()
    {
        foreach (var g in Gadgets)
        {
            if (g.Effect != "BACKGROUND" || g.Speed == 0) continue;
            int factor = JsMath.Floor(2.0 * g.Speed * (CurrentIteration + 1.0) / 17) - JsMath.Floor(2.0 * g.Speed * CurrentIteration / 17);
            int mx = JsMath.Trunc((double)(AnimObjMov[g.AngleSegment] * factor) / 2);
            int my = JsMath.Trunc((double)(AnimObjMov[(g.AngleSegment + 12) % 16] * factor) / 2);
            g.X += mx; g.Y += my;
            int f = Width + g.Width;
            g.X = ((g.X + g.Width + f) % f) - g.Width;
            f = Height + g.Height;
            g.Y = ((g.Y + g.Height + f) % f) - g.Height;
            if (g.Object != null) { g.Object.X = g.X; g.Object.Y = g.Y; }
        }
    }

    // ---- the player's controls

    public void SetSelectedSkill(string? name)
    {
        if (name != null && ActiveSkills.IndexOf(name) >= 0) SelectedSkill = name;
    }

    // Assign the selected skill to the lemming the cursor at (x, y) picks.
    public bool AssignSkillAt(int x, int y, string? skillName)
    {
        string? key = string.IsNullOrEmpty(skillName) ? SelectedSkill : skillName; // skillName || this.selectedSkill
        if (key == null || !Lem.SkillToAction.TryGetValue(key, out int action)) return false;
        var lemming = GetPriorityLemming(action, x, y).Lemming;
        return AssignSkillTo(lemming, Lem.ActionToSkill[action]);
    }

    // AssignNewSkill for the player: the assignment is written into the replay at this frame
    // (cutting whatever the replay had from here on) and takes effect in the next update.
    // True when it was recorded.
    public bool AssignSkillTo(Lemming? L, string? skillName)
    {
        if (skillName == null || !Lem.SkillToAction.TryGetValue(skillName, out int action) || L == null || L.Removed) return false;
        if (!MayAssign(action, L) || !CheckSkillAvailable(action)) return false;
        // ProcessSkillAssignment: replay-insert mode keeps a frame's own assignment
        if (ReplayInsert && HasRecorded("assignment", CurrentIteration)) return false;
        RegainControl();
        Record(new ReplayEntry
        {
            Type = "assignment", Frame = CurrentIteration, Skill = skillName,
            LemIndex = L.Index, LemId = L.Identifier, X = L.X, Y = L.Y, Dx = L.Dx,
        });
        return true;
    }

    // RecordNuke: into the replay, in force from the next update.
    public void Nuke()
    {
        if (UserSetNuking || HasRecorded("nuke", CurrentIteration)) return;
        RegainControl();
        Record(new ReplayEntry { Type = "nuke", Frame = CurrentIteration });
    }

    public void SetSpawnIntervalModifier(int m) { SpawnIntervalModifier = m; }

    // Panel release rate, 1..99 (103 - SI).
    public int ReleaseRate => 103 - CurrSpawnInterval;
    public int MinReleaseRate => 103 - Level.SpawnInterval;

    // How the level ends, or null while playing.
    public GameResult? Result()
    {
        if (!GameFinished && !StateIsUnplayable) return null;
        // Array.from(set): insertion order. A HashSet<int> that is only added to (and copied
        // whole by LoadState) enumerates in insertion order.
        return new GameResult(LemmingsIn >= Level.NeedCount, LemmingsIn, Level.NeedCount,
            Level.ReleaseCount, IsOutOfTime, TalismansAchieved.ToList());
    }
}
