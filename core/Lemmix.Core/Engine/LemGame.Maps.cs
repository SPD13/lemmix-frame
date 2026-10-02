namespace Lemmix.Engine;

// web/lemmix/js/lemgame.js lines 466-1345: physics map access, trigger/blocker/zombie maps,
// transitions, skills and assignment, the priority lemming, trigger areas and the gadget
// handlers, the masks and the constructive skills (hasPixelAt .. findGroundPixel).
// isSimulating and cueSoundEffect are in LemGame.cs.
public sealed partial class LemGame
{
    // ---- physics map access

    public bool HasPixelAt(int x, int y)
    {
        return y >= 0 && y < Height && x >= 0 && x < Width && (Physics[x + y * Width] & PM.SOLID) != 0;
    }

    public bool HasSteelAt(int x, int y)
    {
        return y >= 0 && y < Height && x >= 0 && x < Width && (Physics[x + y * Width] & PM.STEEL) != 0;
    }

    public int PixelBits(int x, int y)
    {
        return y >= 0 && y < Height && x >= 0 && x < Width ? Physics[x + y * Width] : 0;
    }

    public void RemovePixelAt(int x, int y)
    {
        if (y >= 0 && y < Height && x >= 0 && x < Width)
        {
            int i = x + y * Width;
            Physics[i] = (ushort)(Physics[i] & ~PM.TERRAIN);
        }
    }

    // RenderInterface.RemoveTerrain: the picture loses what the physics map lost.
    public void RemoveTerrain(int x0, int y0, int w, int h)
    {
        if (IsSimulating) return;
        var mask = Level.GroundMask.GroundMask;
        for (int y = Math.Max(0, y0); y < Math.Min(Height, y0 + h); y++)
        {
            for (int x = Math.Max(0, x0); x < Math.Min(Width, x0 + w); x++)
            {
                int i = x + y * Width;
                if ((Physics[i] & PM.SOLID) == 0 && mask[i] != 0) Level.ClearGroundAt(x, y);
            }
        }
    }

    public void AddConstructivePixel(int x, int y, uint color)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height) return;
        int i = x + y * Width;
        Physics[i] = (ushort)(Physics[i] | PM.SOLID);
        if (!IsSimulating) Level.SetGroundAt(x, y, color);
    }

    // ---- trigger maps

    public void InitializeAllTriggerMaps()
    {
        Array.Clear(TriggerMap);
        Array.Clear(BlockerMap);
        Array.Clear(ZombieMap);
    }

    public void WriteTriggerMap(int bit, TriggerArea r)
    {
        for (int y = Math.Max(0, r.Y0); y < Math.Min(Height, r.Y1); y++)
        {
            for (int x = Math.Max(0, r.X0); x < Math.Min(Width, r.X1); x++) TriggerMap[x + y * Width] |= unchecked((uint)bit);
        }
    }

    public void SetGadgetMap()
    {
        foreach (var g in Gadgets)
        {
            if (!Lem.EffectTrigger.TryGetValue(g.Effect, out int bit) || bit == 0) continue;
            WriteTriggerMap(bit, g.TriggerRect);
            if (g.Effect == "LOCKEXIT" && ButtonsRemain == 0) g.CurrentFrame = 0;
        }
    }

    public bool ReadTriggerMap(int x, int y, int bit)
    {
        return x >= 0 && x < Width && y >= 0 && y < Height && (TriggerMap[x + y * Width] & unchecked((uint)bit)) != 0;
    }

    public void WriteBlockerMap(int x, int y, int lemIndex, int effect)
    {
        if (x >= 0 && x < Width && y >= 0 && y < Height) BlockerMap[x + y * Width] = unchecked((uint)((lemIndex << 8) | effect));
    }

    public int ReadBlockerMap(int x, int y, Lemming? L = null)
    {
        if (x < 0 || x >= Width || y < 0 || y >= Height) return Lem.BM_NONE;
        uint v = BlockerMap[x + y * Width];
        int result = (int)(v & 0xff);
        int idx = (int)((v >> 8) & 0xffff);
        // JS: this.lemmings[idx] is undefined past the end
        LastBlockerCheckLem = result != Lem.BM_NONE ? (idx < Lemmings.Count ? Lemmings[idx] : null) : null;
        if (LastBlockerCheckLem != null)
        {
            var B = LastBlockerCheckLem;
            if (result != Lem.BM_NONE && L != null && L.Action == BA.BUILDING)
            {
                int checkPosX = B.Dx == L.Dx ? L.X + 2 * L.Dx : L.X + 3 * L.Dx;
                if (L.Y >= B.Y - 1 && L.Y <= B.Y + 3 && B.X == checkPosX) return Lem.BM_NONE;
            }
            if (IsSimulating && (result == Lem.BM_FORCERIGHT || result == Lem.BM_FORCELEFT))
            {
                if (!HasPixelAt(B.X, B.Y)) return Lem.BM_NONE;
            }
        }
        return result;
    }

    public void SetBlockerMap()
    {
        Array.Clear(BlockerMap);
        for (int i = 0; i < Lemmings.Count; i++)
        {
            var L = Lemmings[i];
            if (!L.HasBlockerField || L.Removed) continue;
            int x = L.X - 6;
            if (L.Dx == 1) x++;
            for (int step = 0; step < 12; step++)
            {
                int effect = step <= 3 ? Lem.BM_FORCELEFT : step <= 7 ? Lem.BM_BLOCKER : Lem.BM_FORCERIGHT;
                for (int y = L.Y - 6; y <= L.Y + 4; y++) WriteBlockerMap(x + step, y, i, effect);
            }
        }
    }

    public int ReadZombieMap(int x, int y)
    {
        return x >= 0 && x < Width && y >= 0 && y < Height ? ZombieMap[x + y * Width] : 0;
    }

    public void WriteZombieMap(int x, int y, int v)
    {
        if (x >= 0 && x < Width && y >= 0 && y < Height)
        {
            int i = x + y * Width;
            ZombieMap[i] = unchecked((byte)(ZombieMap[i] | v));
        }
    }

    public void SetZombieField(Lemming L)
    {
        for (int x = L.X - 5; x <= L.X + 5; x++) for (int y = L.Y - 6; y <= L.Y + 4; y++) WriteZombieMap(x, y, 1);
        for (int y = L.Y - 6; y <= L.Y + 4; y++) WriteZombieMap(L.X + L.Dx * 6, y, 1);
    }

    public bool CheckForOverlappingField(Lemming L)
    {
        int x = L.X - 6;
        if (L.Dx == 1) x++;
        return HasTriggerAt(x, L.Y - 6, "BLOCKER") || HasTriggerAt(x + 11, L.Y - 6, "BLOCKER")
            || HasTriggerAt(x, L.Y + 4, "BLOCKER") || HasTriggerAt(x + 11, L.Y + 4, "BLOCKER");
    }

    // HasTriggerAt, by the TTriggerTypes name.
    public bool HasTriggerAt(int x, int y, string type, Lemming? L = null)
    {
        LastBlockerCheckLem = null;
        switch (type)
        {
            case "EXIT": return ReadTriggerMap(x, y, TR.EXIT) || (ButtonsRemain == 0 && ReadTriggerMap(x, y, TR.LOCKEDEXIT));
            case "FORCELEFT": return ReadBlockerMap(x, y, L) == Lem.BM_FORCELEFT || ReadTriggerMap(x, y, TR.FORCELEFT);
            case "FORCERIGHT": return ReadBlockerMap(x, y, L) == Lem.BM_FORCERIGHT || ReadTriggerMap(x, y, TR.FORCERIGHT);
            case "TRAP": return ReadTriggerMap(x, y, TR.TRAP);
            case "ANIM": return ReadTriggerMap(x, y, TR.ANIM);
            case "WATER": return ReadTriggerMap(x, y, TR.WATER);
            case "FIRE": return ReadTriggerMap(x, y, TR.FIRE);
            case "OWLEFT": return (PixelBits(x, y) & PM.ONEWAYLEFT) != 0;
            case "OWRIGHT": return (PixelBits(x, y) & PM.ONEWAYRIGHT) != 0;
            case "OWDOWN": return (PixelBits(x, y) & PM.ONEWAYDOWN) != 0;
            case "OWUP": return (PixelBits(x, y) & PM.ONEWAYUP) != 0;
            case "STEEL": return (PixelBits(x, y) & PM.STEEL) != 0;
            case "BLOCKER": { int b = ReadBlockerMap(x, y); return b == Lem.BM_BLOCKER || b == Lem.BM_FORCERIGHT || b == Lem.BM_FORCELEFT; }
            case "TELEPORT": return ReadTriggerMap(x, y, TR.TELEPORT);
            case "PICKUP": return ReadTriggerMap(x, y, TR.PICKUP);
            case "BUTTON": return ReadTriggerMap(x, y, TR.BUTTON);
            case "UPDRAFT": return ReadTriggerMap(x, y, TR.UPDRAFT);
            case "FLIPPER": return ReadTriggerMap(x, y, TR.FLIPPER);
            case "NOSPLAT": return ReadTriggerMap(x, y, TR.NOSPLAT);
            case "SPLAT": return ReadTriggerMap(x, y, TR.SPLAT);
            case "PORTAL": return ReadTriggerMap(x, y, TR.PORTAL);
            case "NEUTRALIZER": return ReadTriggerMap(x, y, TR.NEUTRALIZER);
            case "DENEUTRALIZER": return ReadTriggerMap(x, y, TR.DENEUTRALIZER);
            case "ADDSKILL": return ReadTriggerMap(x, y, TR.ADDSKILL);
            case "REMOVESKILLS": return ReadTriggerMap(x, y, TR.REMOVESKILLS);
            case "ZOMBIE": return (ReadZombieMap(x, y) & 1) != 0;
        }
        return false;
    }

    public bool HasIndestructibleAt(int x, int y, int direction, int skill)
    {
        return HasTriggerAt(x, y, "STEEL")
            || (HasTriggerAt(x, y, "OWUP") && skill is BA.BASHING or BA.MINING or BA.DIGGING)
            || (HasTriggerAt(x, y, "OWDOWN") && skill is BA.BASHING or BA.FENCING or BA.LASERING)
            || (HasTriggerAt(x, y, "OWLEFT") && direction == 1 && skill is BA.BASHING or BA.FENCING or BA.MINING or BA.LASERING)
            || (HasTriggerAt(x, y, "OWRIGHT") && direction == -1 && skill is BA.BASHING or BA.FENCING or BA.MINING or BA.LASERING);
    }

    // FindGadgetID: the last-listed usable gadget of this trigger type at (x, y).
    public int FindGadgetId(int x, int y, string triggerType)
    {
        // JS: an unknown type gives undefined, and `n & undefined` is 0
        int bit = triggerType switch
        {
            "TRAP" => TR.TRAP, "ANIM" => TR.ANIM, "TELEPORT" => TR.TELEPORT, "PICKUP" => TR.PICKUP, "BUTTON" => TR.BUTTON,
            "EXIT" => TR.EXIT | TR.LOCKEDEXIT, "FLIPPER" => TR.FLIPPER, "PORTAL" => TR.PORTAL, "ADDSKILL" => TR.ADDSKILL,
            _ => 0,
        };
        for (int id = Gadgets.Count - 1; id >= 0; id--)
        {
            var g = Gadgets[id];
            bool found = false;
            if ((Lem.EffectTrigger.GetValueOrDefault(g.Effect) & bit) != 0)
            {
                var r = g.TriggerRect;
                if (x >= r.X0 && x < r.X1 && y >= r.Y0 && y < r.Y1) found = true;
            }
            if (g.Effect == "LOCKEXIT" && ButtonsRemain != 0) found = false;
            if ((g.Effect == "EXIT" || g.Effect == "LOCKEXIT") && g.RemainingLemmings == 0) found = false;
            if (g.Triggered) found = false;
            if ((g.Effect == "BUTTON" || g.Effect == "TRAPONCE" || g.Effect == "ANIMONCE") && g.CurrentFrame == 0) found = false;
            if (g.Effect == "PICKUP" && g.CurrentFrame % 2 == 0) found = false;
            if (g.Effect == "TELEPORT" && g.ReceiverId >= 0 &&
                (Gadgets[g.ReceiverId].Triggered || Gadgets[g.ReceiverId].HoldActive)) found = false;
            if (found) return id;
        }
        return Lem.NO_OBJECT;
    }

    // ---- transitions

    public void TurnAround(Lemming L) { L.Dx = -L.Dx; }

    public void Transition(Lemming L, int newAction, bool doTurn = false)
    {
        if (doTurn) TurnAround(L);
        if (newAction == BA.TOWALKING) newAction = BA.WALKING;
        if (L.HasBlockerField && !(newAction == BA.OHNOING || newAction == BA.STONING))
        {
            L.HasBlockerField = false;
            SetBlockerMap();
        }
        if (!HasPixelAt(L.X, L.Y) && newAction == BA.WALKING) newAction = BA.FALLING;
        if (L.Action == newAction) return;
        if (newAction == BA.FALLING)
        {
            if (L.Action != BA.SWIMMING)
            {
                L.Fallen = 1;
                if (L.Action == BA.WALKING || L.Action == BA.BASHING) L.Fallen = 3;
                else if (L.Action == BA.MINING || L.Action == BA.DIGGING) L.Fallen = 0;
                else if (L.Action == BA.BLOCKING || L.Action == BA.JUMPING || L.Action == BA.LASERING) L.Fallen = -1;
            }
            L.TrueFallen = L.Fallen;
        }
        if (((newAction == BA.SHIMMYING || newAction == BA.JUMPING) && L.Action == BA.CLIMBING) ||
            (newAction == BA.JUMPING && L.Action == BA.SLIDING))
        {
            TurnAround(L);
            L.X += L.Dx;
            if (newAction == BA.SHIMMYING && HasPixelAt(L.X, L.Y - 8)) L.Y++;
        }
        if (newAction == BA.SHIMMYING && L.Action == BA.SLIDING) { L.Y += 2; if (HasPixelAt(L.X, L.Y - 8)) L.Y++; }
        if (newAction == BA.SHIMMYING && L.Action == BA.DEHOISTING) { L.Y += 2; if (HasPixelAt(L.X, L.Y - 9 + 1)) L.Y++; }
        if (newAction == BA.SHIMMYING && L.Action == BA.JUMPING)
        {
            for (int i = -1; i <= 3; i++)
            {
                if (HasPixelAt(L.X, L.Y - 9 - i) && !HasPixelAt(L.X, L.Y - 8 - i)) { L.Y -= i; break; }
            }
        }
        if (newAction == BA.DEHOISTING) L.DehoistPinY = L.Y;
        if (newAction == BA.SLIDING) L.DehoistPinY = -1;

        L.Action = newAction;
        L.Frame = 0;
        L.PhysicsFrame = 0;
        L.EndOfAnimation = false;
        L.BricksLeft = 0;
        bool oldIsStartingAction = L.IsStartingAction;
        L.IsStartingAction = true;
        L.InitialFall = false;
        L.MaxFrame = -1;
        L.MaxPhysicsFrame = Lem.AnimFrameCount[newAction] - 1;

        switch (L.Action)
        {
            case BA.ASCENDING: L.Ascended = 0; break;
            case BA.HOISTING: L.IsStartingAction = oldIsStartingAction; break;
            case BA.SPLATTING: L.ExplosionTimer = 0; CueSoundEffect(SFX.SPLAT, L); break;
            case BA.BLOCKING: L.HasBlockerField = true; SetBlockerMap(); break;
            case BA.EXITING:
                if (!IsOutOfTime) L.ExplosionTimer = 0;
                CueSoundEffect(SFX.YIPPEE, L);
                break;
            case BA.VAPORIZING: L.ExplosionTimer = 0; break;
            case BA.BUILDING: L.BricksLeft = 12; L.ConstructivePositionFreeze = false; break;
            case BA.PLATFORMING: L.BricksLeft = 12; L.ConstructivePositionFreeze = false; break;
            case BA.STACKING: L.BricksLeft = 8; break;
            case BA.OHNOING:
            case BA.STONING:
                CueSoundEffect(SFX.OHNO, L);
                L.IsSlider = L.IsClimber = L.IsSwimmer = L.IsFloater = L.IsGlider = L.IsDisarmer = false;
                L.HasBeenOhnoer = true;
                break;
            case BA.EXPLODING:
            case BA.STONEFINISH: CueSoundEffect(SFX.EXPLOSION, L); break;
            case BA.SWIMMING:
            {
                int i = 0;
                while (i < 4 && HasTriggerAt(L.X, L.Y - i - 1, "WATER") && !HasPixelAt(L.X, L.Y - i - 1)) i++;
                L.Y -= i;
                break;
            }
            case BA.FIXING: L.DisarmingFrames = 42; break;
            case BA.JUMPING: L.JumpProgress = 0; break;
            case BA.LASERING: L.LaserRemainTime = 10; break;
        }
    }

    // The JS returns a boolean (checkLemmings: `cont = !this.updateExplosionTimer(L)`), so bool here.
    public bool UpdateExplosionTimer(Lemming L)
    {
        L.ExplosionTimer--;
        if (L.ExplosionTimer == 0)
        {
            if (L.Action is BA.VAPORIZING or BA.DROWNING or BA.FLOATING or BA.GLIDING or BA.FALLING or BA.SWIMMING or BA.REACHING or BA.SHIMMYING or BA.JUMPING)
            {
                Transition(L, L.TimerToStone ? BA.STONEFINISH : BA.EXPLODING);
            }
            else
            {
                Transition(L, L.TimerToStone ? BA.STONING : BA.OHNOING);
            }
            return true;
        }
        return false;
    }

    public bool IsOutOfTime => HasTimeLimit && (TimePlay < 0 || (TimePlay == 0 && ClockFrame > 0));

    public bool StateIsUnplayable =>
        LemmingsOut == 0 && (LemmingsToRelease == 0 || UserSetNuking)
        && DelayEndFrames == 0 && ParticleFinishTimer == 0
        && !(UserSetNuking && CheckIfZombiesRemain());

    // ---- skills

    public bool CheckSkillAvailable(int action)
    {
        if (!Lem.ActionToSkill.TryGetValue(action, out string? name) || string.IsNullOrEmpty(name)) return false;
        return ActiveSkills.Contains(name) && CurrSkillCount.GetValueOrDefault(action) > 0;
    }

    public void UpdateSkillCount(int action, int amount = -1)
    {
        // JS: `undefined < 100` is false, so a missing entry stays missing
        if (CurrSkillCount.TryGetValue(action, out int cur) && cur < 100)
            CurrSkillCount[action] = Math.Max(Math.Min(cur + amount, 99), 0);
        if (amount < 0) UsedSkillCount[action] = UsedSkillCount.GetValueOrDefault(action) + -amount;
    }

    public int SkillCountOf(string name)
    {
        return Lem.SkillToAction.TryGetValue(name, out int action) ? CurrSkillCount.GetValueOrDefault(action) : 0;
    }

    public int SkillsUsed(string name)
    {
        return Lem.SkillToAction.TryGetValue(name, out int action) ? UsedSkillCount.GetValueOrDefault(action) : 0;
    }

    static bool IsDying(int a) =>
        a is BA.OHNOING or BA.STONING or BA.EXPLODING or BA.STONEFINISH or BA.DROWNING or BA.VAPORIZING or BA.SPLATTING or BA.EXITING;

    public bool MayAssign(int action, Lemming L)
    {
        int a = L.Action;
        switch (action)
        {
            case BA.TOWALKING:
                return a is BA.WALKING or BA.SHRUGGING or BA.BLOCKING or BA.PLATFORMING or BA.BUILDING or BA.STACKING or BA.BASHING
                    or BA.FENCING or BA.MINING or BA.DIGGING or BA.REACHING or BA.SHIMMYING or BA.LASERING;
            case BA.SLIDING: return !IsDying(a) && !L.IsSlider;
            case BA.CLIMBING: return !IsDying(a) && !L.IsClimber;
            case BA.FLOATING:
            case BA.GLIDING: return !IsDying(a) && !(L.IsFloater || L.IsGlider);
            case BA.SWIMMING:
                return !(a is BA.OHNOING or BA.STONING or BA.EXPLODING or BA.STONEFINISH or BA.VAPORIZING or BA.SPLATTING or BA.EXITING) && !L.IsSwimmer;
            case BA.FIXING: return !IsDying(a) && !L.IsDisarmer;
            case BA.BLOCKING:
                return a is BA.WALKING or BA.SHRUGGING or BA.PLATFORMING or BA.BUILDING or BA.STACKING or BA.BASHING or BA.FENCING
                    or BA.MINING or BA.DIGGING or BA.LASERING && !CheckForOverlappingField(L);
            case BA.EXPLODING:
            case BA.STONING:
                return !(a is BA.OHNOING or BA.STONING or BA.DROWNING or BA.EXPLODING or BA.STONEFINISH or BA.VAPORIZING or BA.SPLATTING or BA.EXITING);
            case BA.BUILDING:
                return a is BA.WALKING or BA.SHRUGGING or BA.PLATFORMING or BA.STACKING or BA.LASERING or BA.BASHING or BA.FENCING or BA.MINING or BA.DIGGING;
            case BA.PLATFORMING:
            {
                bool r = false;
                for (int n = 0; n <= 5; n++) r = r || !HasPixelAt(L.X + n * L.Dx, L.Y);
                return r && a is BA.WALKING or BA.SHRUGGING or BA.BUILDING or BA.STACKING or BA.BASHING or BA.FENCING or BA.MINING
                    or BA.DIGGING or BA.LASERING && LemCanPlatform(L);
            }
            case BA.STACKING:
                return a is BA.WALKING or BA.SHRUGGING or BA.PLATFORMING or BA.BUILDING or BA.BASHING or BA.FENCING or BA.MINING or BA.DIGGING or BA.LASERING;
            case BA.BASHING:
                return a is BA.WALKING or BA.SHRUGGING or BA.PLATFORMING or BA.BUILDING or BA.STACKING or BA.FENCING or BA.MINING or BA.DIGGING or BA.LASERING;
            case BA.FENCING:
                return a is BA.WALKING or BA.SHRUGGING or BA.PLATFORMING or BA.BUILDING or BA.STACKING or BA.BASHING or BA.MINING or BA.DIGGING or BA.LASERING;
            case BA.MINING:
                return a is BA.WALKING or BA.SHRUGGING or BA.PLATFORMING or BA.BUILDING or BA.STACKING or BA.BASHING or BA.FENCING
                    or BA.DIGGING or BA.LASERING && !HasIndestructibleAt(L.X, L.Y, L.Dx, BA.MINING);
            case BA.DIGGING:
                return a is BA.WALKING or BA.SHRUGGING or BA.PLATFORMING or BA.BUILDING or BA.STACKING or BA.BASHING or BA.FENCING
                    or BA.MINING or BA.LASERING && !HasIndestructibleAt(L.X, L.Y, L.Dx, BA.DIGGING);
            case BA.CLONING:
                return a is BA.WALKING or BA.SHRUGGING or BA.PLATFORMING or BA.BUILDING or BA.STACKING or BA.BASHING or BA.FENCING
                    or BA.MINING or BA.DIGGING or BA.ASCENDING or BA.FALLING or BA.FLOATING or BA.SWIMMING or BA.GLIDING or BA.FIXING
                    or BA.REACHING or BA.SHIMMYING or BA.JUMPING or BA.LASERING;
            case BA.SHIMMYING: return MayAssignShimmier(L);
            case BA.JUMPING:
                return a is BA.WALKING or BA.DIGGING or BA.BUILDING or BA.BASHING or BA.MINING or BA.SHRUGGING or BA.PLATFORMING
                    or BA.STACKING or BA.FENCING or BA.CLIMBING or BA.SLIDING or BA.LASERING;
            case BA.LASERING:
                return a is BA.WALKING or BA.SHRUGGING or BA.PLATFORMING or BA.BUILDING or BA.STACKING or BA.BASHING or BA.FENCING or BA.MINING or BA.DIGGING;
        }
        return false;
    }

    public bool MayAssignShimmier(Lemming L)
    {
        bool result = L.Action is BA.WALKING or BA.SHRUGGING or BA.PLATFORMING or BA.BUILDING or BA.STACKING or BA.BASHING
            or BA.FENCING or BA.MINING or BA.DIGGING or BA.LASERING;
        if (L.Action == BA.CLIMBING)
        {
            var copy = new Lemming(L.Index); copy.Assign(L); copy.IsPhysicsSimulation = true;
            var saved = (ushort[])Physics.Clone();
            SimulateLem(copy, false);
            saved.CopyTo(Physics, 0);
            if ((copy.Action == BA.FALLING && copy.Dx == -L.Dx) || copy.Action == BA.SLIDING)
            {
                if (HasPixelAt(copy.X, copy.Y - 9) || HasPixelAt(copy.X, copy.Y - 8)) result = true;
            }
        }
        else if (L.Action == BA.SLIDING || L.Action == BA.DEHOISTING)
        {
            var copy = new Lemming(L.Index); copy.Assign(L); copy.IsPhysicsSimulation = true;
            int old = copy.Action;
            var saved = (ushort[])Physics.Clone();
            SimulateLem(copy, false);
            saved.CopyTo(Physics, 0);
            if (copy.Action != old && copy.Dx == L.Dx && (old != BA.DEHOISTING || copy.Action != BA.SLIDING))
            {
                result = L.Y > Height + 4 ? !HasPixelAt(L.X, Height - 1) : true;
            }
        }
        else if (L.Action == BA.JUMPING)
        {
            for (int i = -1; i <= 3; i++)
            {
                if (HasPixelAt(L.X, L.Y - 9 - i) && !HasPixelAt(L.X, L.Y - 8 - i)) { result = true; break; }
            }
        }
        return result;
    }

    // DoSkillAssignment: the skill goes to this lemming now (from the replay, always).
    public bool DoSkillAssignment(Lemming L, int newSkill)
    {
        if (!CheckSkillAvailable(newSkill)) return false;
        if (DoneAssignmentThisFrame) return false;
        UpdateSkillCount(newSkill);
        L.QueueAction = BA.NONE; L.QueueFrame = 0;
        if (newSkill == BA.STACKING) L.StackLow = !HasPixelAt(L.X + L.Dx, L.Y);
        if (newSkill == BA.TOWALKING && L.Action == BA.BUILDING && HasPixelAt(L.X, L.Y - 1) && !HasPixelAt(L.X + L.Dx, L.Y)) L.Y--;
        if (newSkill == BA.TOWALKING && L.Action == BA.WALKING)
        {
            TurnAround(L);
            if ((HasTriggerAt(L.X, L.Y, "FORCERIGHT", L) && L.Dx == -1) || (HasTriggerAt(L.X, L.Y, "FORCELEFT", L) && L.Dx == 1))
            {
                if (HasPixelAt(L.X, L.Y)) { L.WalkerPositionAdjusted = true; L.X -= L.Dx; }
            }
        }
        if (newSkill == BA.SLIDING) L.IsSlider = true;
        else if (newSkill == BA.CLIMBING) L.IsClimber = true;
        else if (newSkill == BA.FLOATING) L.IsFloater = true;
        else if (newSkill == BA.GLIDING) L.IsGlider = true;
        else if (newSkill == BA.FIXING) L.IsDisarmer = true;
        else if (newSkill == BA.SWIMMING)
        {
            L.IsSwimmer = true;
            if (L.Action == BA.DROWNING) Transition(L, BA.SWIMMING);
        }
        else if (newSkill == BA.EXPLODING)
        {
            L.ExplosionTimer = 1; L.TimerToStone = false; L.HideCountdown = true;
        }
        else if (newSkill == BA.STONING)
        {
            L.ExplosionTimer = 1; L.TimerToStone = true; L.HideCountdown = true;
        }
        else if (newSkill == BA.CLONING)
        {
            LemmingsCloned++;
            GenerateClonedLem(L);
        }
        else if (newSkill == BA.SHIMMYING)
        {
            Transition(L, L.Action is BA.CLIMBING or BA.SLIDING or BA.JUMPING or BA.DEHOISTING ? BA.SHIMMYING : BA.REACHING);
        }
        else Transition(L, newSkill);
        DoneAssignmentThisFrame = true;
        return true;
    }

    public void GenerateClonedLem(Lemming L)
    {
        var N = new Lemming(Lemmings.Count);
        N.Assign(L);
        N.Identifier = "C" + CurrentIteration;
        Lemmings.Add(N);
        TurnAround(N);
        LemmingsOut++;
        if (N.Action == BA.MINING)
        {
            if (N.PhysicsFrame == 2) ApplyMinerMask(N, 1, 0, 0);
            else if (N.PhysicsFrame >= 3 && N.PhysicsFrame < 15) ApplyMinerMask(N, 1, -2 * N.Dx, -1);
        }
        else if ((N.Action == BA.BUILDING || N.Action == BA.PLATFORMING) && N.PhysicsFrame >= 9) LayBrick(N);
    }

    // The direction filter in force: a held direction hotkey, else the panel's choice.
    public int EffectiveSelectDx => HotkeyDx != 0 ? HotkeyDx : SelectDx;

    // GetPriorityLemming: the lemming a click at (x, y) means, for the selected skill (or any
    // skill when `action` is NONE), and how many lemmings sit under the cursor.
    public (Lemming? Lemming, int Count) GetPriorityLemming(int action, int mx, int my)
    {
        const int NonPerm = 0, Perm = 1, NonWalk = 2, Walk = 3;
        Lemming? priority = null;
        int curValue = 10, count = 0;
        int newSkill = action;
        // JS: SKILL_TO_ACTION of an unknown name is undefined, which mayAssign rejects (-1 here)
        if (newSkill == BA.NONE)
            newSkill = !string.IsNullOrEmpty(SelectedSkill) ? (Lem.SkillToAction.TryGetValue(SelectedSkill, out int sa) ? sa : -1) : BA.EXPLODING;
        bool InCursor(Lemming L) => mx >= L.X - 8 && mx < L.X + 5 && my >= L.Y - 10 && my < L.Y + 3;
        int Dist(Lemming L) { int a = 2 * (L.X - 8) - 2 * mx + 13, b = 2 * (L.Y - 10) - 2 * my + 13; return a * a + b * b; }
        static bool InBox(Lemming L, int box)
        {
            switch (box)
            {
                case Perm: return L.HasPermanentSkills;
                case NonPerm:
                    return L.Action is BA.BASHING or BA.FENCING or BA.MINING or BA.DIGGING or BA.BUILDING or BA.PLATFORMING or BA.STACKING
                        or BA.BLOCKING or BA.SHRUGGING or BA.REACHING or BA.SHIMMYING or BA.LASERING;
                case Walk: return L.Action == BA.WALKING || L.Action == BA.ASCENDING;
                case NonWalk: return !(L.Action == BA.WALKING || L.Action == BA.ASCENDING);
            }
            return true;
        }
        for (int i = Lemmings.Count - 1; i >= 0; i--)
        {
            var L = Lemmings[i];
            if (L.Removed || L.Teleporting || L.PortalWarpFrame > 0) continue;
            if (L.CannotReceiveSkills && priority != null) continue;
            if (!InCursor(L)) continue;
            int dx = EffectiveSelectDx;
            if (dx != 0 && dx != L.Dx) continue; // directional select
            if (SelectWalkerOnly && L.Action != BA.WALKING) continue; // the Select Walker hotkey
            if (!L.CannotReceiveSkills) count++;
            int box = 0;
            bool isIn;
            if (SelectWalkerOnly) box = 1;
            else do { isIn = InBox(L, box); box++; } while (!(box > Math.Min(curValue, 4) || isIn));
            if (!MayAssign(newSkill, L)) box = 8;
            if (L.CannotReceiveSkills) box = 9;
            if (box < curValue || (box == curValue && Dist(L) < Dist(priority!))) { priority = L; curValue = box; }
        }
        if (curValue > 6 && action != BA.NONE) priority = null;
        return (priority, count);
    }

    // ---- trigger areas

    public List<int[]> GetGadgetCheckPositions(Lemming L)
    {
        var output = new List<int[]>();
        int cx = L.XOld, cy = L.YOld;
        void Save() => output.Add(new[] { cx, cy });
        void MoveH() { while (cx != L.X) { cx += Math.Sign(L.X - cx); Save(); } }
        void MoveV() { while (cy != L.Y) { cy += Math.Sign(L.Y - cy); Save(); } }
        if (L.X == L.XOld && L.Y == L.YOld) Save();
        else
        {
            if (L.ActionOld == BA.JUMPING)
            {
                foreach (var p in L.JumpPositions) { if (p[0] < 0 || p[1] < 0) break; cx = p[0]; cy = p[1]; Save(); }
            }
        }
        if (L.X == L.XOld && L.Y == L.YOld) { /* saved above */ }
        else if (L.ActionOld == BA.MINING)
        {
            if (L.YOld < L.Y) { cy++; Save(); }
            MoveH(); MoveV();
        }
        else if ((L.Y < L.YOld || L.Action == BA.FALLING) && L.ActionOld != BA.BUILDING) { MoveH(); MoveV(); }
        else { MoveV(); MoveH(); }
        return output;
    }

    public bool CheckTriggerArea(Lemming L, bool isPostTeleportCheck = false)
    {
        int saveX = 0, saveY = 0;
        if (isPostTeleportCheck) { L.XOld = L.X; L.YOld = L.Y; saveX = L.X; saveY = L.Y; }
        var pos = GetGadgetCheckPositions(L);
        int i = -1, needShift = 0;
        bool abort = false;
        do
        {
            i++;
            int px = pos[i][0], py = pos[i][1];
            if (LemNextAction != BA.NONE && px == L.X && py == L.Y
                && (LemNextAction != BA.SPLATTING || !HasTriggerAt(L.X, L.Y, "WATER")))
            {
                Transition(L, LemNextAction);
                if (LemJumpToHoistAdvance) { L.Frame += 2; L.PhysicsFrame += 2; }
                LemNextAction = BA.NONE;
                LemJumpToHoistAdvance = false;
            }
            if (HasTriggerAt(px, py, "PICKUP")) HandlePickup(L, px, py);
            if (HasTriggerAt(px, py, "BUTTON")) HandleButton(L, px, py);
            if (HasTriggerAt(px, py, "FIRE")) abort = HandleFire(L);
            if (!abort && HasTriggerAt(px, py, "WATER")) abort = HandleWaterDrown(L);
            if (!abort && HasTriggerAt(px, py, "TRAP"))
            {
                abort = HandleTrap(L, px, py);
                if (L.Action == BA.FIXING) pos[i][0] = L.X;
            }
            if (!abort && HasTriggerAt(px, py, "PORTAL") && !isPostTeleportCheck) abort = HandlePortal(L, px, py);
            if (!abort && HasTriggerAt(px, py, "TELEPORT") && !isPostTeleportCheck) abort = HandleTeleport(L, px, py);
            if (!abort && HasTriggerAt(px, py, "NEUTRALIZER")) HandleNeutralize(L);
            if (!abort && HasTriggerAt(px, py, "DENEUTRALIZER")) HandleDeneutralize(L);
            if (!abort && HasTriggerAt(px, py, "ADDSKILL")) HandleAddSkill(L, px, py);
            if (!abort && HasTriggerAt(px, py, "REMOVESKILLS"))
            {
                bool wasOnWall = L.Action is BA.CLIMBING or BA.SLIDING or BA.DEHOISTING;
                abort = HandleRemoveSkills(L);
                if (wasOnWall && abort) needShift = -1;
            }
            if (!abort && HasTriggerAt(px, py, "EXIT")) abort = HandleExit(L, px, py);
            if (!abort && HasTriggerAt(px, py, "FLIPPER") && L.Action != BA.BLOCKING
                && !(L.ActionOld == BA.JUMPING || L.Action == BA.JUMPING))
            {
                bool wasOnWall = L.Action is BA.CLIMBING or BA.SLIDING or BA.DEHOISTING;
                abort = HandleFlipper(L, px, py);
                if (wasOnWall && abort) needShift = L.Dx;
            }
            if (!abort && HasTriggerAt(px, py, "ANIM")) HandleAnimation(L, px, py);
            if (abort) { L.X = pos[i][0]; L.Y = pos[i][1]; }
            if (!HasTriggerAt(pos[i][0], pos[i][1], "FLIPPER") && !(L.ActionOld == BA.JUMPING || L.Action == BA.JUMPING)) L.InFlipper = Lem.NO_OBJECT;
            if (!HasTriggerAt(pos[i][0], pos[i][1], "PORTAL")) L.InPortal = Lem.NO_OBJECT;
        } while (!(pos[i][0] == L.X && pos[i][1] == L.Y) && i < pos.Count - 1);
        L.X += L.Dx * needShift;
        if (HasTriggerAt(L.X, L.Y, "WATER")) HandleWaterSwim(L);
        if ((L.Action != BA.MINING || !(L.PhysicsFrame == 1 || L.PhysicsFrame == 2)) && L.Action != BA.JUMPING)
        {
            if (HasTriggerAt(L.X, L.Y, "FORCELEFT", L)) HandleForceField(L, -1);
            else if (HasTriggerAt(L.X, L.Y, "FORCERIGHT", L)) HandleForceField(L, 1);
        }
        if (isPostTeleportCheck) { L.X = saveX; L.Y = saveY; }
        return false; // JS: no return value (undefined); nothing reads it
    }

    public bool HandleTrap(Lemming L, int px, int py)
    {
        int id = FindGadgetId(px, py, "TRAP");
        if (id == Lem.NO_OBJECT) return false;
        var g = Gadgets[id];
        if (L.IsDisarmer && HasPixelAt(px, py) && !(L.Action is BA.DEHOISTING or BA.SLIDING or BA.CLIMBING or BA.HOISTING or BA.SWIMMING or BA.OHNOING or BA.JUMPING))
        {
            L.ActionNew = (L.YOld > L.Y && HasPixelAt(px, py + 1)) ? BA.ASCENDING : BA.WALKING;
            g.Effect = "NONE";
            Transition(L, BA.FIXING);
        }
        else
        {
            g.Triggered = true;
            g.ZombieMode = L.IsZombie;
            g.NeutralMode = L.IsNeutral;
            L.HasBlockerField = false;
            SetBlockerMap();
            RemoveLemming(L, RM.KILL);
            CueSoundEffect(g.Meta.SoundActivate, L);
            DelayEndFrames = Math.Max(DelayEndFrames, g.FrameCount);
            if (g.Effect == "TRAPONCE") g.Effect = "NONE";
        }
        return true;
    }

    public bool HandleAnimation(Lemming L, int px, int py)
    {
        int id = FindGadgetId(px, py, "ANIM");
        if (id == Lem.NO_OBJECT) return false;
        var g = Gadgets[id];
        g.Triggered = true;
        CueSoundEffect(g.Meta.SoundActivate, L);
        if (g.Effect == "ANIMONCE") g.Effect = "NONE";
        return false;
    }

    public bool HandleTeleport(Lemming L, int px, int py)
    {
        if (L.Action == BA.SPLATTING) return false;
        if (L.Action == BA.FALLING && HasPixelAt(px, py) && L.Fallen > Lem.MAX_FALLDISTANCE) return false;
        int id = FindGadgetId(px, py, "TELEPORT");
        if (id == Lem.NO_OBJECT) return false;
        var g = Gadgets[id];
        g.Triggered = true;
        g.ZombieMode = L.IsZombie; g.NeutralMode = L.IsNeutral;
        CueSoundEffect(g.Meta.SoundActivate, L);
        L.Teleporting = true;
        g.TeleLem = L.Index;
        L.HasBlockerField = false;
        L.DehoistPinY = -1;
        SetBlockerMap();
        Gadgets[g.ReceiverId].HoldActive = true;
        return true;
    }

    public bool HandlePortal(Lemming L, int px, int py)
    {
        if (L.Action == BA.SPLATTING) return false;
        if (L.Action == BA.FALLING && HasPixelAt(px, py) && L.Fallen > Lem.MAX_FALLDISTANCE) return false;
        int id = FindGadgetId(px, py, "PORTAL");
        if (id == Lem.NO_OBJECT || id == L.InPortal) return false;
        CueSoundEffect(SFX.PORTAL, L);
        L.PortalWarpFrame = 1;
        L.HasBlockerField = false;
        L.DehoistPinY = -1;
        SetBlockerMap();
        return true;
    }

    public bool CheckLemPortalWarping(Lemming L)
    {
        L.PortalWarpFrame++;
        if (L.PortalWarpFrame == 4)
        {
            int id = FindGadgetId(L.X, L.Y, "PORTAL");
            if (id == Lem.NO_OBJECT) return false;
            var g = Gadgets[id];
            if (g.Effect != "PORTAL" || g.ReceiverId < 0) return false;
            var dest = Gadgets[g.ReceiverId];
            L.X = dest.TriggerRect.X0 + (((dest.TriggerRect.X1 - dest.TriggerRect.X0) + 1) >> 1) - 1;
            L.Y = dest.TriggerRect.Y1 - 1;
            L.InPortal = g.ReceiverId;
            HandlePostTeleport(L);
        }
        else if (L.PortalWarpFrame >= 7) L.PortalWarpFrame = 0;
        return false;
    }

    public bool HandlePickup(Lemming L, int px, int py)
    {
        int id = FindGadgetId(px, py, "PICKUP");
        if (id == Lem.NO_OBJECT) return false;
        if (!L.IsZombie)
        {
            var g = Gadgets[id];
            g.CurrentFrame = g.CurrentFrame & ~1;
            CueSoundEffect(SFX.PICKUP, L);
            // JS: SKILL_TO_ACTION of an unknown name is undefined, a key no count has (-1 here)
            UpdateSkillCount(Lem.SkillToAction.TryGetValue(g.SkillName ?? "", out int action) ? action : -1, g.SkillCount);
        }
        return false;
    }

    public bool HandleButton(Lemming L, int px, int py)
    {
        int id = FindGadgetId(px, py, "BUTTON");
        if (id == Lem.NO_OBJECT) return false;
        if (!L.IsZombie)
        {
            var g = Gadgets[id];
            CueSoundEffect(g.Meta.SoundActivate, L);
            g.Triggered = true;
            ButtonsRemain--;
            if (ButtonsRemain == 0)
            {
                foreach (var e in Gadgets)
                {
                    if (e.Effect != "LOCKEXIT") continue;
                    e.Triggered = true;
                    string? sound = e.Meta.SoundActivate;
                    CueSoundEffect(string.IsNullOrEmpty(sound) ? SFX.EXIT_OPEN : sound, e.X + (e.Width >> 1), e.Y + (e.Height >> 1));
                }
            }
        }
        return false;
    }

    public bool HandleExit(Lemming L, int px, int py)
    {
        if (!L.IsZombie && !(L.Action is BA.FALLING or BA.SPLATTING or BA.JUMPING or BA.REACHING)
            && (HasPixelAt(L.X, L.Y) || !(L.Action == BA.OHNOING || L.Action == BA.STONING)))
        {
            if (IsOutOfTime && UserSetNuking && L.Action == BA.OHNOING) return false;
            int id = FindGadgetId(px, py, "EXIT");
            if (id == Lem.NO_OBJECT) return false;
            var g = Gadgets[id];
            if (g.RemainingLemmings > 0)
            {
                g.RemainingLemmings--;
                if (g.RemainingLemmings == 0) CueSoundEffect(g.Meta.SoundExhaust, g.X, g.Y);
            }
            Transition(L, BA.EXITING);
            CueSoundEffect(SFX.YIPPEE, L);
            return true;
        }
        return false;
    }

    public bool HandleForceField(Lemming L, int direction)
    {
        if (L.Dx == -direction && !(L.Action == BA.DEHOISTING || L.Action == BA.HOISTING))
        {
            TurnAround(L);
            if (L.IsZombie && LastBlockerCheckLem != null && !LastBlockerCheckLem.IsZombie) RemoveLemming(LastBlockerCheckLem, RM.ZOMBIE);
            if (L.Action == BA.MINING)
            {
                if (L.PhysicsFrame == 2) ApplyMinerMask(L, 1, 0, 0);
                else if (L.PhysicsFrame >= 3 && L.PhysicsFrame < 15) ApplyMinerMask(L, 1, -2 * L.Dx, -1);
            }
            else if ((L.Action == BA.BUILDING || L.Action == BA.PLATFORMING) && L.PhysicsFrame >= 9) LayBrick(L);
            else if (L.Action is BA.CLIMBING or BA.SLIDING or BA.DEHOISTING)
            {
                L.X += L.Dx;
                if (!L.IsStartingAction) L.Y++;
                Transition(L, BA.WALKING);
            }
            return true;
        }
        return false;
    }

    public bool HandleFire(Lemming L)
    {
        Transition(L, BA.VAPORIZING);
        CueSoundEffect(SFX.VAPORIZING, L);
        return true;
    }

    public bool HandleFlipper(Lemming L, int px, int py)
    {
        int id = FindGadgetId(px, py, "FLIPPER");
        if (id == Lem.NO_OBJECT) return false;
        var g = Gadgets[id];
        bool result = false;
        if (L.InFlipper != id)
        {
            L.InFlipper = id;
            if ((g.CurrentFrame == 1) != (L.Dx < 0)) result = HandleForceField(L, -L.Dx);
            if (!IsSimulating) g.CurrentFrame = 1 - g.CurrentFrame;
        }
        return result;
    }

    public bool HandleWaterDrown(Lemming L)
    {
        if (L.IsSwimmer) return false;
        if (!(L.Action is BA.SWIMMING or BA.EXPLODING or BA.STONEFINISH or BA.VAPORIZING or BA.EXITING or BA.SPLATTING))
        {
            Transition(L, BA.DROWNING);
            CueSoundEffect(SFX.DROWNING, L);
        }
        return true;
    }

    public bool HandleWaterSwim(Lemming L)
    {
        if (L.IsSwimmer && !(L.Action is BA.SWIMMING or BA.CLIMBING or BA.HOISTING or BA.OHNOING or BA.EXPLODING or BA.STONING
            or BA.STONEFINISH or BA.VAPORIZING or BA.EXITING or BA.SPLATTING))
        {
            Transition(L, BA.SWIMMING);
            CueSoundEffect(SFX.SWIMMING, L);
        }
        return true;
    }

    public bool HandleAddSkill(Lemming L, int px, int py)
    {
        if (L.HasBeenOhnoer) return false;
        int id = FindGadgetId(px, py, "ADDSKILL");
        if (id == Lem.NO_OBJECT) return false;
        string? s = Gadgets[id].SkillName;
        void Give() => CueSoundEffect(SFX.ADD_SKILL, L);
        if (s == "SLIDER" && !L.IsSlider) { L.IsSlider = true; Give(); }
        if (s == "CLIMBER" && !L.IsClimber) { L.IsClimber = true; Give(); }
        if (s == "SWIMMER" && !L.IsSwimmer) { L.IsSwimmer = true; Give(); if (L.Action == BA.DROWNING) Transition(L, BA.SWIMMING); }
        if (s == "FLOATER" && !(L.IsFloater || L.IsGlider)) { L.IsFloater = true; Give(); }
        if (s == "GLIDER" && !(L.IsFloater || L.IsGlider)) { L.IsGlider = true; Give(); }
        if (s == "DISARMER" && !L.IsDisarmer) { L.IsDisarmer = true; Give(); }
        return false;
    }

    public bool HandleRemoveSkills(Lemming L)
    {
        if (!L.HasPermanentSkills) return false;
        CueSoundEffect(SFX.REMOVE_SKILLS, L);
        L.IsClimber = L.IsSlider = L.IsSwimmer = L.IsFloater = L.IsGlider = L.IsDisarmer = false;
        int old = L.Action;
        if (L.Action is BA.CLIMBING or BA.DEHOISTING or BA.SLIDING or BA.FLOATING or BA.GLIDING) Transition(L, BA.FALLING);
        else if (L.Action == BA.SWIMMING) Transition(L, BA.DROWNING);
        if (L.Action == BA.FALLING && old != BA.FALLING) { L.Fallen = -1; L.TrueFallen = -1; }
        return true;
    }

    public bool HandleNeutralize(Lemming L)
    {
        if (!(L.IsNeutral || L.IsZombie)) { CueSoundEffect(SFX.NEUTRALIZE, L); L.IsNeutral = true; }
        return false;
    }

    public bool HandleDeneutralize(Lemming L)
    {
        if (L.IsNeutral && !L.IsZombie) { CueSoundEffect(SFX.DENEUTRALIZE, L); L.IsNeutral = false; }
        return false;
    }

    // ---- masks

    // Draw a mask onto the physics map: where the mask has alpha and the pixel has none of `exclude`, clear terrain.
    public void ApplyMask(Bitmap mask, int sx, int sy, int w, int h, int dx, int dy, int exclude)
    {
        var md = mask.Data;
        for (int y = 0; y < h; y++)
        {
            int py = dy + y;
            if (py < 0 || py >= Height) continue;
            for (int x = 0; x < w; x++)
            {
                int px = dx + x;
                if (px < 0 || px >= Width) continue;
                if (md[((sy + y) * mask.Width + sx + x) * 4 + 3] == 0) continue;
                int i = px + py * Width;
                if ((Physics[i] & exclude) == 0) Physics[i] = (ushort)(Physics[i] & ~PM.TERRAIN);
            }
        }
    }

    public void ApplyStoneLemming(Lemming L)
    {
        int x = L.X;
        if (L.Dx == 1) x++;
        var m = Masks.Stoner;
        var md = m.Data;
        for (int y = 0; y < m.Height; y++) for (int xx = 0; xx < m.Width; xx++)
        {
            int px = x - 8 + xx, py = L.Y - 10 + y;
            if (px < 0 || py < 0 || px >= Width || py >= Height) continue;
            if (md[(y * m.Width + xx) * 4 + 3] == 0) continue;
            int i = px + py * Width;
            if ((Physics[i] & PM.SOLID) == 0)
            {
                Physics[i] = (ushort)(Physics[i] | PM.SOLID);
                if (!IsSimulating)
                {
                    int o = (y * m.Width + xx) * 4;
                    uint c = unchecked((uint)(0xff << 24 | md[o + 2] << 16 | md[o + 1] << 8 | md[o]));
                    Level.SetGroundAt(px, py, c);
                }
            }
        }
    }

    public void ApplyExplosionMask(Lemming L)
    {
        int px = L.X;
        if (L.Dx == 1) px++;
        var m = Masks.Bomber;
        ApplyMask(m, 0, 0, m.Width, m.Height, px - 8, L.Y - 14, PM.STEEL);
        RemoveTerrain(px - 8, L.Y - 14, m.Width, m.Height);
    }

    public void ApplyBashingMask(Lemming L, int maskFrame)
    {
        int excl = L.Dx == 1 ? (PM.STEEL | PM.ONEWAYLEFT | PM.ONEWAYDOWN | PM.ONEWAYUP) : (PM.STEEL | PM.ONEWAYRIGHT | PM.ONEWAYDOWN | PM.ONEWAYUP);
        ApplyMask(Masks.Basher, L.Dx == 1 ? 16 : 0, maskFrame * 10, 16, 10, L.X - 8, L.Y - 10, excl);
        RemoveTerrain(L.X - 8, L.Y - 10, 16, 10);
    }

    public void ApplyFencerMask(Lemming L, int maskFrame)
    {
        int excl = L.Dx == 1 ? (PM.STEEL | PM.ONEWAYLEFT | PM.ONEWAYDOWN) : (PM.STEEL | PM.ONEWAYRIGHT | PM.ONEWAYDOWN);
        ApplyMask(Masks.Fencer, L.Dx == 1 ? 16 : 0, maskFrame * 10, 16, 10, L.X - 8, L.Y - 10, excl);
        RemoveTerrain(L.X - 8, L.Y - 10, 16, 10);
    }

    public void ApplyLaserMask(int px, int py, Lemming L)
    {
        int excl = L.Dx == 1 ? (PM.STEEL | PM.ONEWAYLEFT | PM.ONEWAYDOWN) : (PM.STEEL | PM.ONEWAYRIGHT | PM.ONEWAYDOWN);
        // the 9x9 mask around the hit point, kept to the side the beam came from
        int tx0 = L.Dx == 1 ? L.X : 0, tx1 = L.Dx == 1 ? Width : L.X + 1;
        int x0 = Math.Max(px - 4, tx0), y0 = Math.Max(py - 4, 0), x1 = Math.Min(px + 5, tx1), y1 = Math.Min(py + 5, L.Y);
        if (x1 <= x0 || y1 <= y0) return;
        ApplyMask(Masks.Laser, x0 - (px - 4), y0 - (py - 4), x1 - x0, y1 - y0, x0, y0, excl);
        RemoveTerrain(x0, y0, x1 - x0, y1 - y0);
    }

    public void ApplyMinerMask(Lemming L, int maskFrame, int adjustX, int adjustY)
    {
        int mx = L.X + L.Dx - 8 + adjustX, my = L.Y + maskFrame - 12 + adjustY;
        int excl = L.Dx == 1 ? (PM.STEEL | PM.ONEWAYLEFT | PM.ONEWAYUP) : (PM.STEEL | PM.ONEWAYRIGHT | PM.ONEWAYUP);
        ApplyMask(Masks.Miner, L.Dx == 1 ? 16 : 0, maskFrame * 13, 16, 13, mx, my, excl);
        RemoveTerrain(mx, my, 16, 13);
    }

    // ---- constructive skills

    // JS: brickColors[i] out of range is undefined, which setGroundAt stores as `undefined >>> 0` = 0
    uint BrickColorAt(int i) => i >= 0 && i < BrickColors.Count ? BrickColors[i] : 0u;

    public void LayBrick(Lemming L)
    {
        int brickY = L.Action == BA.BUILDING ? L.Y - 1 : L.Y;
        for (int n = 0; n <= 5; n++) AddConstructivePixel(L.X + n * L.Dx, brickY, BrickColorAt(12 - L.BricksLeft));
    }

    public bool LayStackBrick(Lemming L)
    {
        int brickY = L.Y - 9 + L.BricksLeft;
        if (L.StackLow) brickY++;
        bool result = false;
        for (int n = 1; n <= 3; n++)
        {
            int px = L.X + n * L.Dx;
            if (!HasPixelAt(px, brickY)) { AddConstructivePixel(px, brickY, BrickColorAt(12 - L.BricksLeft)); result = true; }
        }
        return result;
    }

    public bool DigOneRow(int px, int py)
    {
        bool result = false;
        for (int n = -4; n <= 4; n++)
        {
            if (HasPixelAt(px + n, py) && !HasIndestructibleAt(px + n, py, 0, BA.DIGGING))
            {
                RemovePixelAt(px + n, py);
                if (n > -4 && n < 4) result = true;
            }
        }
        RemoveTerrain(px - 4, py, 9, 1);
        return result;
    }

    public bool LemCanPlatform(Lemming L)
    {
        bool r = false;
        for (int n = 0; n <= 5; n++) r = r || !HasPixelAt(L.X + n * L.Dx, L.Y);
        r = r && !HasPixelAt(L.X + L.Dx, L.Y - 1);
        r = r && !HasPixelAt(L.X + 2 * L.Dx, L.Y - 1);
        return r;
    }

    public int FindGroundPixel(int x, int y)
    {
        int r = 0;
        if (HasPixelAt(x, y))
        {
            while (HasPixelAt(x, y + r - 1) && r > -7) r--;
        }
        else
        {
            r++;
            while (!HasPixelAt(x, y + r) && r < 4) r++;
        }
        return r;
    }
}
