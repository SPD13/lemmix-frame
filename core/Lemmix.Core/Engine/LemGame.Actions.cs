namespace Lemmix.Engine;

// web/lemmix/js/lemgame.js lines 1346-2086: the per-lemming step (handleLemming,
// checkLevelBoundaries) and every action handler, handleWalking through handleExploding,
// in the JS file's order.
public sealed partial class LemGame
{
    // ---- the per-lemming step

    public bool HandleLemming(Lemming L)
    {
        L.XOld = L.X; L.YOld = L.Y; L.DxOld = L.Dx; L.ActionOld = L.Action;
        LemNextAction = BA.NONE;
        LemJumpToHoistAdvance = false;
        L.Frame++;
        L.PhysicsFrame++;
        if (L.PhysicsFrame > L.MaxPhysicsFrame)
        {
            L.PhysicsFrame = 0;
            if (L.Action == BA.FLOATING || L.Action == BA.GLIDING) L.PhysicsFrame = 9;
            switch (L.Action)
            {
                case BA.DROWNING: case BA.HOISTING: case BA.SPLATTING: case BA.EXITING: case BA.VAPORIZING:
                case BA.SHRUGGING: case BA.OHNOING: case BA.EXPLODING: case BA.STONING: case BA.REACHING:
                case BA.DEHOISTING:
                    L.EndOfAnimation = true;
                    break;
            }
        }
        bool result;
        switch (L.Action)
        {
            case BA.WALKING: case BA.TOWALKING: result = HandleWalking(L); break;
            case BA.ASCENDING: result = HandleAscending(L); break;
            case BA.DIGGING: result = HandleDigging(L); break;
            case BA.CLIMBING: result = HandleClimbing(L); break;
            case BA.DROWNING: result = HandleDrowning(L); break;
            case BA.HOISTING: result = HandleHoisting(L); break;
            case BA.BUILDING: result = HandleBuilding(L); break;
            case BA.BASHING: result = HandleBashing(L); break;
            case BA.MINING: result = HandleMining(L); break;
            case BA.FALLING: result = HandleFalling(L); break;
            case BA.FLOATING: result = HandleFloating(L); break;
            case BA.SPLATTING: result = HandleSplatting(L); break;
            case BA.EXITING: result = HandleExiting(L); break;
            case BA.VAPORIZING: result = HandleVaporizing(L); break;
            case BA.BLOCKING: result = HandleBlocking(L); break;
            case BA.SHRUGGING: result = HandleShrugging(L); break;
            case BA.OHNOING: case BA.STONING: result = HandleOhNoing(L); break;
            case BA.EXPLODING: case BA.STONEFINISH: result = HandleExploding(L); break;
            case BA.PLATFORMING: result = HandlePlatforming(L); break;
            case BA.STACKING: result = HandleStacking(L); break;
            case BA.SWIMMING: result = HandleSwimming(L); break;
            case BA.GLIDING: result = HandleGliding(L); break;
            case BA.FIXING: result = HandleDisarming(L); break;
            case BA.FENCING: result = HandleFencing(L); break;
            case BA.REACHING: result = HandleReaching(L); break;
            case BA.SHIMMYING: result = HandleShimmying(L); break;
            case BA.JUMPING: result = HandleJumping(L); break;
            case BA.DEHOISTING: result = HandleDehoisting(L); break;
            case BA.SLIDING: result = HandleSliding(L); break;
            case BA.LASERING: result = HandleLasering(L); break;
            default: Transition(L, BA.WALKING); result = true; break;
        }
        if (L.IsZombie && !IsSimulating) SetZombieField(L);
        return result;
    }

    public bool CheckLevelBoundaries(Lemming L)
    {
        bool result = true;
        if (L.Y <= 0 || L.Y > Lem.LEMMING_MAX_Y + Height) { RemoveLemming(L, RM.NEUTRAL); result = false; }
        if (L.X < 0 || L.X >= Width) { RemoveLemming(L, RM.NEUTRAL); result = false; }
        return result;
    }

    public bool HandleWalking(Lemming L)
    {
        bool adjusted = L.WalkerPositionAdjusted;
        L.WalkerPositionAdjusted = false;
        L.X += L.Dx;
        int dy = FindGroundPixel(L.X, L.Y);
        if (dy > 0 && L.IsSlider && LemCanDehoist(L, true))
        {
            L.X -= L.Dx;
            Transition(L, BA.DEHOISTING, true);
            return true;
        }
        if (dy < -6)
        {
            if (L.IsClimber) Transition(L, BA.CLIMBING);
            else { TurnAround(L); if (!adjusted) L.X += L.Dx; }
        }
        else if (dy < -2)
        {
            Transition(L, BA.ASCENDING);
            L.Y -= 2;
        }
        else if (dy < 1) L.Y += dy;
        dy = FindGroundPixel(L.X, L.Y);
        if (dy > 3) { L.Y += 4; Transition(L, BA.FALLING); }
        else if (dy > 0) L.Y += dy;
        return true;
    }

    public bool HandleSwimming(Lemming L)
    {
        L.Fallen = 0;
        L.X += L.Dx;
        int LemDive()
        {
            int r = 1;
            while (HasPixelAt(L.X, L.Y + r) && r <= 4)
            {
                r++; L.Fallen++;
                if (HasTriggerAt(L.X, L.Y + r, "WATER")) L.Fallen = 0;
                if (L.Y + r >= Height) break;
            }
            return r > 4 ? 0 : r;
        }
        if (HasTriggerAt(L.X, L.Y, "WATER") || HasPixelAt(L.X, L.Y))
        {
            int dy = FindGroundPixel(L.X, L.Y);
            if (dy >= -1 && HasTriggerAt(L.X, L.Y - 1, "WATER") && !HasPixelAt(L.X, L.Y - 1)) L.Y--;
            else if (dy < -6)
            {
                int dive = LemDive();
                if (dive > 0)
                {
                    L.Y += dive;
                    if (!HasTriggerAt(L.X, L.Y, "WATER")) Transition(L, BA.WALKING);
                }
                else if (L.IsClimber && !HasTriggerAt(L.X, L.Y - 1, "WATER")) Transition(L, BA.CLIMBING);
                else { TurnAround(L); L.X += L.Dx; }
            }
            else if (dy <= -3) { Transition(L, BA.ASCENDING); L.Y -= 2; }
            else if (dy <= -1 || (dy == 0 && !HasTriggerAt(L.X, L.Y, "WATER"))) { Transition(L, BA.WALKING); L.Y += dy; }
        }
        else
        {
            int dy = FindGroundPixel(L.X, L.Y);
            if (dy > 1) { L.Y++; Transition(L, BA.FALLING); }
            else { L.Y += dy; Transition(L, BA.WALKING); }
        }
        return true;
    }

    public bool HandleAscending(Lemming L)
    {
        int dy = 0;
        while (dy < 2 && L.Ascended < 5 && HasPixelAt(L.X, L.Y - 1)) { dy++; L.Y--; L.Ascended++; }
        if (dy < 2 && !HasPixelAt(L.X, L.Y - 1)) LemNextAction = BA.WALKING;
        else if ((L.Ascended == 4 && HasPixelAt(L.X, L.Y - 1) && HasPixelAt(L.X, L.Y - 2)) || (L.Ascended >= 5 && HasPixelAt(L.X, L.Y - 1)))
        {
            L.X -= L.Dx;
            while (HasPixelAt(L.X, L.Y) && L.Ascended > 0) { L.Y++; L.Ascended--; }
            Transition(L, BA.FALLING, true);
        }
        return true;
    }

    public bool HandleDigging(Lemming L)
    {
        if (L.IsStartingAction)
        {
            L.IsStartingAction = false;
            DigOneRow(L.X, L.Y - 1);
            L.PhysicsFrame--;
        }
        if (L.PhysicsFrame == 0 || L.PhysicsFrame == 8)
        {
            L.Y++;
            bool cont = DigOneRow(L.X, L.Y - 1);
            if (HasIndestructibleAt(L.X, L.Y, L.Dx, BA.DIGGING))
            {
                if (HasSteelAt(L.X, L.Y)) CueSoundEffect(SFX.HITS_STEEL, L);
                Transition(L, BA.WALKING);
            }
            else if (!cont) Transition(L, BA.FALLING);
        }
        return true;
    }

    public bool HandleClimbing(Lemming L)
    {
        if (L.PhysicsFrame <= 3)
        {
            bool clip = HasPixelAt(L.X - L.Dx, L.Y - 6 - L.PhysicsFrame)
                || (HasPixelAt(L.X - L.Dx, L.Y - 5 - L.PhysicsFrame) && !L.IsStartingAction);
            if (L.PhysicsFrame == 0) clip = clip && HasPixelAt(L.X - L.Dx, L.Y - 7);
            if (clip)
            {
                if (!L.IsStartingAction) L.Y = L.Y - L.PhysicsFrame + 3;
                if (L.IsSlider) { L.Y--; Transition(L, BA.SLIDING); }
                else { L.X -= L.Dx; Transition(L, BA.FALLING, true); L.Fallen++; }
            }
            else if (!HasPixelAt(L.X, L.Y - 7 - L.PhysicsFrame))
            {
                if (!(L.IsStartingAction && L.PhysicsFrame == 1)) { L.Y = L.Y - L.PhysicsFrame + 2; L.IsStartingAction = false; }
                Transition(L, BA.HOISTING);
            }
        }
        else
        {
            L.Y--;
            L.IsStartingAction = false;
            bool clip = HasPixelAt(L.X - L.Dx, L.Y - 7);
            if (L.PhysicsFrame == 7) clip = clip && HasPixelAt(L.X, L.Y - 7);
            if (clip)
            {
                L.Y++;
                if (L.IsSlider) Transition(L, BA.SLIDING);
                else { L.X -= L.Dx; Transition(L, BA.FALLING, true); }
            }
        }
        return true;
    }

    public bool HandleDrowning(Lemming L) { if (L.EndOfAnimation) RemoveLemming(L, RM.KILL); return false; }

    public bool HandleDisarming(Lemming L)
    {
        L.DisarmingFrames--;
        if (L.DisarmingFrames <= 0)
        {
            Transition(L, L.ActionNew != BA.NONE ? L.ActionNew : BA.WALKING);
            L.ActionNew = BA.NONE;
        }
        else if (L.PhysicsFrame % 8 == 0) CueSoundEffect(SFX.FIXING, L);
        return false;
    }

    public bool HandleHoisting(Lemming L)
    {
        if (L.EndOfAnimation) Transition(L, BA.WALKING);
        else if (L.PhysicsFrame == 1 && L.IsStartingAction) L.Y -= 1;
        else if (L.PhysicsFrame <= 4) L.Y -= 2;
        return true;
    }

    public bool HandlePlatforming(Lemming L)
    {
        bool Check(int x, int y) => HasPixelAt(x, y - 1) || HasPixelAt(x, y - 2);
        if (L.PhysicsFrame == 9) { L.PlacedBrick = LemCanPlatform(L); LayBrick(L); }
        else if (L.PhysicsFrame == 10 && L.BricksLeft <= 3) CueSoundEffect(SFX.BUILDER_WARNING, L);
        else if (L.PhysicsFrame == 15)
        {
            if (!L.PlacedBrick) Transition(L, BA.WALKING, true);
            else if (Check(L.X + 2 * L.Dx, L.Y)) { L.X += L.Dx; Transition(L, BA.WALKING, true); }
            else if (!L.ConstructivePositionFreeze) L.X += L.Dx;
        }
        else if (L.PhysicsFrame == 0)
        {
            if (Check(L.X + 2 * L.Dx, L.Y) && L.BricksLeft > 1) { L.X += L.Dx; Transition(L, BA.WALKING, true); }
            else if (Check(L.X + 3 * L.Dx, L.Y) && L.BricksLeft > 1) { L.X += 2 * L.Dx; Transition(L, BA.WALKING, true); }
            else
            {
                if (!L.ConstructivePositionFreeze) L.X += 2 * L.Dx;
                L.BricksLeft--;
                if (L.BricksLeft == 0)
                {
                    if (HasPixelAt(L.X, L.Y - 1)) L.X -= L.Dx;
                    Transition(L, BA.SHRUGGING);
                }
            }
        }
        if (L.PhysicsFrame == 0) L.ConstructivePositionFreeze = false;
        return true;
    }

    public bool HandleBuilding(Lemming L)
    {
        if (L.PhysicsFrame == 9) LayBrick(L);
        else if (L.PhysicsFrame == 10 && L.BricksLeft <= 3) CueSoundEffect(SFX.BUILDER_WARNING, L);
        else if (L.PhysicsFrame == 0)
        {
            L.BricksLeft--;
            if (HasPixelAt(L.X + L.Dx, L.Y - 2)) Transition(L, BA.WALKING, true);
            else if (HasPixelAt(L.X + L.Dx, L.Y - 3) || HasPixelAt(L.X + 2 * L.Dx, L.Y - 2)
                || (HasPixelAt(L.X + 2 * L.Dx, L.Y - 10) && L.BricksLeft > 0))
            {
                L.Y--; L.X += L.Dx; Transition(L, BA.WALKING, true);
            }
            else
            {
                if (!L.ConstructivePositionFreeze) { L.Y--; L.X += 2 * L.Dx; }
                if (HasPixelAt(L.X, L.Y - 2) || HasPixelAt(L.X, L.Y - 3) || HasPixelAt(L.X + L.Dx, L.Y - 3)
                    || (HasPixelAt(L.X + L.Dx, L.Y - 9) && L.BricksLeft > 0)) Transition(L, BA.WALKING, true);
                else if (L.BricksLeft == 0) Transition(L, BA.SHRUGGING);
            }
        }
        if (L.PhysicsFrame == 0) L.ConstructivePositionFreeze = false;
        return true;
    }

    public bool HandleStacking(Lemming L)
    {
        bool MayPlaceNext()
        {
            int by = L.Y - 9 + L.BricksLeft;
            if (L.StackLow) by++;
            return !(HasPixelAt(L.X + L.Dx, by) && HasPixelAt(L.X + 2 * L.Dx, by) && HasPixelAt(L.X + 3 * L.Dx, by));
        }
        if (L.PhysicsFrame == 7) L.PlacedBrick = LayStackBrick(L);
        else if (L.PhysicsFrame == 0)
        {
            L.BricksLeft--;
            if (L.BricksLeft < 3) CueSoundEffect(SFX.BUILDER_WARNING, L);
            if (!L.PlacedBrick)
            {
                if (L.BricksLeft < 7 || !MayPlaceNext()) Transition(L, BA.WALKING, true);
            }
            else if (L.BricksLeft == 0) Transition(L, BA.SHRUGGING);
        }
        return true;
    }

    // shared by handleBashing and handleFencing (two identical closures in the JS)
    bool StepUpCheck(int x, int y, int d, int step)
    {
        bool H(int px, int py) => HasPixelAt(px, py);
        if (step == -1)
        {
            if (!H(x + d, y + step - 1) && H(x + d, y + step) && H(x + 2 * d, y + step) && H(x + 2 * d, y + step - 1) && H(x + 2 * d, y + step - 2)) return false;
            if (!H(x + d, y + step - 2) && H(x + d, y + step) && H(x + d, y + step - 1) && H(x + 2 * d, y + step - 1) && H(x + 2 * d, y + step - 2)) return false;
            if (H(x + d, y + step - 2) && H(x + d, y + step - 1) && H(x + d, y + step)) return false;
        }
        else if (step == -2)
        {
            if (!H(x + d, y + step) && H(x + d, y + step + 1) && H(x + 2 * d, y + step + 1) && H(x + 2 * d, y + step) && H(x + 2 * d, y + step - 1)) return false;
            if (!H(x + d, y + step - 1) && H(x + d, y + step) && H(x + 2 * d, y + step) && H(x + 2 * d, y + step - 1)) return false;
            if (H(x + d, y + step - 1) && H(x + d, y + step)) return false;
        }
        return true;
    }

    public bool HandleBashing(Lemming L)
    {
        bool Indestructible(int x, int y, int d) => HasIndestructibleAt(x, y - 3, d, BA.BASHING) || HasIndestructibleAt(x, y - 4, d, BA.BASHING) || HasIndestructibleAt(x, y - 5, d, BA.BASHING);
        void Turn(bool steelSound)
        {
            L.X -= L.Dx;
            Transition(L, BA.WALKING, true);
            if (steelSound) CueSoundEffect(SFX.HITS_STEEL, L);
        }
        bool H(int x, int y) => HasPixelAt(x, y);
        bool DoTurnAtSteel()
        {
            var copy = new Lemming(L.Index);
            copy.Assign(L);
            copy.IsPhysicsSimulation = true;
            var saved = (ushort[])Physics.Clone();
            bool result = false;
            copy.PhysicsFrame = 10;
            for (int i = 0; i <= 10; i++)
            {
                if (copy.PhysicsFrame == 0 || copy.PhysicsFrame == 16)
                {
                    SimulationDepth++;
                    for (int f = 0; f < 4; f++) ApplyBashingMask(copy, f);
                    SimulationDepth--;
                    copy.PhysicsFrame = 10;
                }
                SimulateLem(copy, false);
                if (copy.Dx == -L.Dx && copy.Action != BA.DEHOISTING) { result = true; break; }
                else if (copy.Removed || copy.Action != BA.BASHING) break;
            }
            saved.CopyTo(Physics, 0);
            return result;
        }

        if (L.PhysicsFrame >= 2 && L.PhysicsFrame <= 5) ApplyBashingMask(L, L.PhysicsFrame - 2);
        if (L.PhysicsFrame == 5)
        {
            bool cont = false;
            for (int n = 1; n <= 14; n++)
            {
                if (H(L.X + n * L.Dx, L.Y - 6) && !HasIndestructibleAt(L.X + n * L.Dx, L.Y - 6, L.Dx, BA.BASHING)) cont = true;
                if (H(L.X + n * L.Dx, L.Y - 5) && !HasIndestructibleAt(L.X + n * L.Dx, L.Y - 5, L.Dx, BA.BASHING)) cont = true;
            }
            if (!cont && !L.IsPhysicsSimulation) cont = DoTurnAtSteel();
            if (!cont) Transition(L, H(L.X, L.Y) ? BA.WALKING : BA.FALLING);
        }
        if (L.PhysicsFrame >= 11 && L.PhysicsFrame <= 15)
        {
            L.X += L.Dx;
            int dy = FindGroundPixel(L.X, L.Y);
            if (dy > 0 && L.IsSlider && LemCanDehoist(L, true)) { L.X -= L.Dx; Transition(L, BA.DEHOISTING, true); }
            else if (dy == 4) { L.Y += dy; Transition(L, BA.FALLING); }
            else if (dy == 3) { L.Y += dy; Transition(L, BA.WALKING); }
            else if (dy >= 0 && dy <= 2)
            {
                if (Indestructible(L.X, L.Y + dy, L.Dx)) Turn(HasSteelAt(L.X, L.Y + dy - 4));
                else L.Y += dy;
            }
            else if (dy == -1 || dy == -2)
            {
                if (Indestructible(L.X, L.Y + dy, L.Dx)) Turn(HasSteelAt(L.X, L.Y + dy - 4));
                else if (!StepUpCheck(L.X, L.Y, L.Dx, dy))
                {
                    if (Indestructible(L.X + L.Dx, L.Y + 2, L.Dx)) Turn(HasSteelAt(L.X + L.Dx, L.Y + dy) || HasSteelAt(L.X + L.Dx, L.Y + dy + 1));
                    else L.X -= L.Dx;
                }
                else L.Y += dy;
            }
            else if (dy < -2)
            {
                if (Indestructible(L.X, L.Y, L.Dx)) Turn(HasSteelAt(L.X, L.Y - 3) || HasSteelAt(L.X, L.Y - 4) || HasSteelAt(L.X, L.Y - 5));
                else L.X -= L.Dx;
            }
        }
        return true;
    }

    public bool HandleFencing(Lemming L)
    {
        bool H(int x, int y) => HasPixelAt(x, y);
        bool Indestructible(int x, int y, int d) => HasIndestructibleAt(x, y - 3, d, BA.FENCING);
        bool needUndoMoveUp = false;
        void Turn(bool steelSound)
        {
            L.X -= L.Dx;
            if (needUndoMoveUp) L.Y++;
            Transition(L, BA.WALKING, true);
            if (steelSound) CueSoundEffect(SFX.HITS_STEEL, L);
        }
        (bool steelContinue, bool moveUpContinue) ContinueTests()
        {
            var copy = new Lemming(L.Index);
            copy.Assign(L);
            copy.IsPhysicsSimulation = true;
            var saved = (ushort[])Physics.Clone();
            bool steelContinue = false, moveUpContinue = false;
            copy.PhysicsFrame = 10;
            for (int i = 0; i <= 10; i++)
            {
                if (copy.PhysicsFrame == 0)
                {
                    SimulationDepth++;
                    for (int f = 0; f < 4; f++) ApplyFencerMask(copy, f);
                    SimulationDepth--;
                    copy.PhysicsFrame = 10;
                }
                SimulateLem(copy, false);
                if (copy.Y < L.Y) moveUpContinue = true;
                if (copy.Dx == -L.Dx && copy.Action != BA.DEHOISTING) { steelContinue = true; break; }
                else if (copy.Removed || copy.Action != BA.FENCING) break;
            }
            saved.CopyTo(Physics, 0);
            return (steelContinue, moveUpContinue);
        }

        if (L.PhysicsFrame >= 2 && L.PhysicsFrame <= 5) ApplyFencerMask(L, L.PhysicsFrame - 2);
        if (L.PhysicsFrame == 15) L.IsStartingAction = false;
        if (L.PhysicsFrame == 5)
        {
            bool cont = false;
            for (int n = 1; n <= 14; n++)
            {
                if (H(L.X + n * L.Dx, L.Y - 6) && !HasIndestructibleAt(L.X + n * L.Dx, L.Y - 6, L.Dx, BA.FENCING)) cont = true;
                if (H(L.X + n * L.Dx, L.Y - 5) && !HasIndestructibleAt(L.X + n * L.Dx, L.Y - 5, L.Dx, BA.FENCING)) cont = true;
            }
            if (!L.IsPhysicsSimulation && !(cont && L.IsStartingAction))
            {
                var t = ContinueTests();
                if (cont && !L.IsStartingAction) cont = t.moveUpContinue;
                if (!cont) cont = t.steelContinue;
            }
            if (!cont) Transition(L, H(L.X, L.Y) ? BA.WALKING : BA.FALLING);
        }
        if (L.PhysicsFrame >= 11 && L.PhysicsFrame <= 14)
        {
            L.X += L.Dx;
            int dy = FindGroundPixel(L.X, L.Y);
            if (dy == -1 && (L.PhysicsFrame == 11 || L.PhysicsFrame == 13)) { L.Y -= 1; dy = 0; needUndoMoveUp = true; }
            if (dy > 0 && L.IsSlider && LemCanDehoist(L, true)) { L.X -= L.Dx; Transition(L, BA.DEHOISTING, true); }
            else if (dy == 4) { L.Y += dy; Transition(L, BA.FALLING); }
            else if (dy > 0) { L.Y += dy; Transition(L, BA.WALKING); }
            else if (dy == 0) { if (Indestructible(L.X, L.Y, L.Dx)) Turn(HasSteelAt(L.X, L.Y - 4)); }
            else if (dy == -1 || dy == -2)
            {
                if (Indestructible(L.X, L.Y + dy, L.Dx)) Turn(HasSteelAt(L.X, L.Y + dy - 4));
                else if (!StepUpCheck(L.X, L.Y, L.Dx, dy))
                {
                    if (Indestructible(L.X + L.Dx, L.Y + 2, L.Dx)) Turn(HasSteelAt(L.X + L.Dx, L.Y + dy) || HasSteelAt(L.X + L.Dx, L.Y + dy + 1));
                    else { L.X -= L.Dx; if (needUndoMoveUp) L.Y++; }
                }
                else L.Y += dy;
            }
            else if (dy < -2)
            {
                if (Indestructible(L.X, L.Y, L.Dx)) Turn(HasSteelAt(L.X, L.Y - 3) || HasSteelAt(L.X, L.Y - 4) || HasSteelAt(L.X, L.Y - 5));
                else L.X -= L.Dx;
            }
        }
        return true;
    }

    public bool HandleMining(Lemming L)
    {
        void MinerTurn(int x, int y)
        {
            if (HasSteelAt(x, y)) CueSoundEffect(SFX.HITS_STEEL, L);
            if (HasPixelAt(L.X, L.Y - 1)) L.Y--;
            Transition(L, BA.WALKING, true);
        }
        if (L.PhysicsFrame == 1 || L.PhysicsFrame == 2) ApplyMinerMask(L, L.PhysicsFrame - 1, 0, 0);
        else if (L.PhysicsFrame == 3 || L.PhysicsFrame == 15)
        {
            if (L.IsSlider && LemCanDehoist(L, false)) { Transition(L, BA.DEHOISTING, true); return true; }
            L.X += 2 * L.Dx;
            L.Y++;
            if (L.IsSlider && LemCanDehoist(L, true)) { L.X -= L.Dx; Transition(L, BA.DEHOISTING, true); return true; }
            bool Ind(int x, int y) => HasIndestructibleAt(x, y, L.Dx, BA.MINING);
            if (Ind(L.X - L.Dx, L.Y - 1) && Ind(L.X, L.Y - 1)) { L.X -= 2 * L.Dx; MinerTurn(L.X + 2 * L.Dx, L.Y - 1); }
            else if (L.PhysicsFrame == 3 && Ind(L.X - L.Dx, L.Y - 2)) { L.X -= 2 * L.Dx; MinerTurn(L.X + L.Dx, L.Y - 2); }
            else if (!HasPixelAt(L.X - L.Dx, L.Y - 1) && !HasPixelAt(L.X - L.Dx, L.Y) && !HasPixelAt(L.X - L.Dx, L.Y + 1))
            {
                L.X -= L.Dx; L.Y++; Transition(L, BA.FALLING); L.Fallen++;
            }
            else if (Ind(L.X, L.Y - 2)) { L.X -= L.Dx; MinerTurn(L.X + L.Dx, L.Y - 2); }
            else if (!HasPixelAt(L.X, L.Y)) { L.Y++; Transition(L, BA.FALLING); }
            else if (Ind(L.X + L.Dx, L.Y - 2)) MinerTurn(L.X + L.Dx, L.Y - 2);
            else if (Ind(L.X, L.Y)) MinerTurn(L.X, L.Y);
        }
        return true;
    }

    public bool LemCanDehoist(Lemming L, bool alreadyMovedX)
    {
        int curX = L.X, nextX = L.X;
        if (alreadyMovedX) curX -= L.Dx; else nextX += L.Dx;
        if (nextX < 0 || nextX >= Width) return false;
        if (!HasPixelAt(curX, L.Y) || HasPixelAt(nextX, L.Y)) return false;
        for (int n = 1; n <= 3; n++)
        {
            if (HasPixelAt(nextX, L.Y + n)) return false;
            if (!HasPixelAt(curX, L.Y + n)) break;
        }
        return true;
    }

    public bool LemSliderTerrainChecks(Lemming L, int maxYCheckOffset = 7)
    {
        bool Has(int x, int y)
        {
            bool r = HasPixelAt(x, y);
            if (!r && x == L.X && y == L.DehoistPinY && y >= 0) r = HasPixelAt(x, y + 1);
            return r;
        }
        if (Has(L.X, L.Y) && !Has(L.X, L.Y - 1)) { Transition(L, BA.WALKING); return false; }
        if (!Has(L.X, L.Y - Math.Min(maxYCheckOffset, 7))) { Transition(L, BA.FALLING); return false; }
        if (Has(L.X, L.Y))
        {
            if (HasTriggerAt(L.X - L.Dx, L.Y, "WATER", L))
            {
                L.X -= L.Dx;
                if (L.IsSwimmer) { Transition(L, BA.SWIMMING, true); CueSoundEffect(SFX.SWIMMING, L); }
                else { Transition(L, BA.DROWNING, true); CueSoundEffect(SFX.DROWNING, L); }
                return false;
            }
            if (Has(L.X - L.Dx, L.Y)) { L.X -= L.Dx; Transition(L, BA.WALKING, true); return false; }
        }
        return true;
    }

    public bool HandleDehoisting(Lemming L)
    {
        if (L.EndOfAnimation)
        {
            if ((L.X <= 0 && L.Dx == -1) || (L.X >= Width - 1 && L.Dx == 1)) RemoveLemming(L, RM.NEUTRAL);
            else if (HasPixelAt(L.X, L.Y - 7)) Transition(L, BA.SLIDING);
            else Transition(L, BA.FALLING);
        }
        else if (L.PhysicsFrame >= 2)
        {
            for (int n = 0; n <= 1; n++)
            {
                L.Y++;
                if (!LemSliderTerrainChecks(L, L.PhysicsFrame * 2 - 3 + n)) return L.Action != BA.DROWNING;
            }
        }
        return true;
    }

    public bool HandleSliding(Lemming L)
    {
        if ((L.X <= 0 && L.Dx == -1) || (L.X >= Width - 1 && L.Dx == 1)) RemoveLemming(L, RM.NEUTRAL);
        for (int n = 0; n <= 1; n++)
        {
            L.Y++;
            if (!LemSliderTerrainChecks(L)) return L.Action != BA.DROWNING;
        }
        return true;
    }

    static readonly int[] ReachingMovement = { 0, 3, 2, 2, 1, 1, 1, 0 };

    public bool HandleReaching(Lemming L)
    {
        var movement = ReachingMovement;
        bool H(int x, int y) => HasPixelAt(x, y);
        int empty;
        if (H(L.X, L.Y - 10)) empty = 0; else if (H(L.X, L.Y - 11)) empty = 1; else if (H(L.X, L.Y - 12)) empty = 2; else if (H(L.X, L.Y - 13)) empty = 3; else empty = 4;
        if (H(L.X, L.Y - 5) || H(L.X, L.Y - 6) || H(L.X, L.Y - 7) || H(L.X, L.Y - 8)) Transition(L, BA.FALLING);
        else if (L.PhysicsFrame == 1 && H(L.X, L.Y - 9)) Transition(L, BA.FALLING);
        else if (empty <= movement[L.PhysicsFrame]) { L.Y -= empty + 1; Transition(L, BA.SHIMMYING); }
        else { L.Y -= movement[L.PhysicsFrame]; if (L.PhysicsFrame == 7) Transition(L, BA.FALLING); }
        return true;
    }

    public bool HandleShimmying(Lemming L)
    {
        bool H(int x, int y) => HasPixelAt(x, y);
        if (L.PhysicsFrame % 2 != 0) return true;
        for (int i = 0; i <= 2; i++)
        {
            if (H(L.X + L.Dx, L.Y - i) && !H(L.X + L.Dx, L.Y - i - 1)) { L.X += L.Dx; L.Y -= i; Transition(L, BA.WALKING); return true; }
        }
        for (int i = 3; i <= 5; i++)
        {
            if (H(L.X + L.Dx, L.Y - i) && !H(L.X + L.Dx, L.Y - i - 1))
            {
                L.X += L.Dx; L.Y -= i - 4; L.IsStartingAction = false;
                Transition(L, BA.HOISTING); L.Frame += 2; L.PhysicsFrame += 2;
                return true;
            }
        }
        for (int i = 6; i <= 7; i++)
        {
            if (H(L.X + L.Dx, L.Y - i))
            {
                if (L.IsSlider) { L.X += L.Dx; Transition(L, BA.SLIDING); } else Transition(L, BA.FALLING);
                return true;
            }
        }
        if (!(H(L.X + L.Dx, L.Y - 9) || H(L.X + L.Dx, L.Y - 10))) { Transition(L, BA.FALLING); return true; }
        if (H(L.X + L.Dx, L.Y - 8) && !H(L.X + L.Dx, L.Y - 9)) { Transition(L, BA.FALLING); return true; }
        L.X += L.Dx;
        if (H(L.X, L.Y - 8))
        {
            L.Y += 1;
            if (H(L.X, L.Y)) { Transition(L, BA.WALKING); return true; }
        }
        if (!H(L.X, L.Y - 9)) L.Y -= 1;
        if (H(L.X, L.Y - 5)) { L.Y -= 5; Transition(L, BA.WALKING); return true; }
        if (L.Y >= Height + 8) { RemoveLemming(L, RM.NEUTRAL); }
        return true;
    }

    public bool HandleJumping(Lemming L)
    {
        bool H(int x, int y) => HasPixelAt(x, y);
        void TriggerChecks()
        {
            if (!HasTriggerAt(L.X, L.Y, "FLIPPER")) L.InFlipper = Lem.NO_OBJECT;
            else if (HandleFlipper(L, L.X, L.Y)) return;
            if (HasTriggerAt(L.X, L.Y, "ZOMBIE", L) && !L.IsZombie) RemoveLemming(L, RM.ZOMBIE);
            if (HasTriggerAt(L.X, L.Y, "FORCELEFT", L)) HandleForceField(L, -1);
            else if (HasTriggerAt(L.X, L.Y, "FORCERIGHT", L)) HandleForceField(L, 1);
        }
        bool MakeJumpMovement()
        {
            int patternIndex;
            int p = L.JumpProgress;
            if (p <= 1) patternIndex = 0; else if (p <= 3) patternIndex = 1; else if (p <= 8) patternIndex = p - 2;
            else if (p <= 10) patternIndex = 7; else if (p <= 12) patternIndex = 8; else return false;
            var pattern = Lem.JumpPatterns[patternIndex];
            // a new list, never the old one changed in place: saved states share it (lemgame.js:1889)
            L.JumpPositions = new List<int[]>();
            for (int i = 0; i < 6; i++) L.JumpPositions.Add(new[] { -1, -1 });
            bool firstStep = L.JumpProgress == 0;
            for (int i = 0; i < 6; i++)
            {
                L.JumpPositions[i] = new[] { L.X, L.Y };
                if (pattern[i][0] == 0 && pattern[i][1] == 0) break;
                if (pattern[i][0] != 0)
                {
                    int checkX = L.X + L.Dx;
                    if (H(checkX, L.Y))
                    {
                        for (int n = 1; n <= 8; n++)
                        {
                            if (!H(checkX, L.Y - n))
                            {
                                if (n <= 2) { L.X = checkX; L.Y = L.Y - n + 1; LemNextAction = BA.WALKING; }
                                else if (n <= 5) { L.X = checkX; L.Y = L.Y - n + 5; LemNextAction = BA.HOISTING; LemJumpToHoistAdvance = true; }
                                else { L.X = checkX; L.Y = L.Y - n + 8; LemNextAction = BA.HOISTING; }
                                return false;
                            }
                            if ((n == 5 && !L.IsClimber) || n == 7)
                            {
                                if (L.IsClimber) { L.X = checkX; LemNextAction = BA.CLIMBING; }
                                else if (L.IsSlider) { L.X += L.Dx; LemNextAction = BA.SLIDING; }
                                else { L.Dx = -L.Dx; LemNextAction = BA.FALLING; }
                                return false;
                            }
                        }
                    }
                }
                if (pattern[i][1] < 0)
                {
                    for (int n = 1; n <= 9; n++)
                    {
                        if (n == 1 && firstStep) continue;
                        if (H(L.X, L.Y - n)) { LemNextAction = BA.FALLING; return false; }
                    }
                }
                L.X += pattern[i][0] * L.Dx;
                L.Y += pattern[i][1];
                TriggerChecks();
                if (firstStep) firstStep = false;
                else if (H(L.X, L.Y)) { LemNextAction = BA.WALKING; return false; }
            }
            return true;
        }
        if (MakeJumpMovement())
        {
            L.JumpProgress++;
            if (L.JumpProgress >= 8 && L.IsGlider) LemNextAction = BA.GLIDING;
            else if (L.JumpProgress == 13) LemNextAction = BA.WALKING;
        }
        return true;
    }

    static readonly int[][] LaserOffsets =
    {
        new[] { 1, -1 }, new[] { 0, -1 }, new[] { 1, 0 }, new[] { -1, -1 }, new[] { -1, -2 }, new[] { 0, -2 },
        new[] { 1, -2 }, new[] { 2, -1 }, new[] { 2, 0 }, new[] { 2, 2 }, new[] { 1, 1 },
    };

    public bool HandleLasering(Lemming L)
    {
        if (!HasPixelAt(L.X, L.Y)) { Transition(L, BA.FALLING); return true; }
        // kind: the JS strings "none", "out", "indestructible", "solid"
        const int KNone = 0, KOut = 1, KIndestructible = 2, KSolid = 3;
        int tx = L.X + L.Dx * 2, ty = L.Y - 5;
        bool hit = false, useful = false;
        for (int i = 0; i < 112; i++)
        {
            int kind = KNone;
            if (tx < -4 || ty < -4 || tx >= Width + 4) kind = KOut;
            else
            {
                foreach (var o in LaserOffsets)
                {
                    int cx = tx + o[0] * L.Dx, cy = ty + o[1];
                    if (!HasPixelAt(cx, cy)) continue;
                    if (HasIndestructibleAt(cx, cy, L.Dx, BA.LASERING) && kind != KSolid) kind = KIndestructible;
                    else kind = KSolid;
                }
            }
            if (kind == KNone) { tx += L.Dx; ty--; continue; }
            if (kind == KSolid) { hit = true; useful = true; }
            else if (kind == KIndestructible) hit = true;
            break;
        }
        L.LaserHitPoint = new[] { tx, ty };
        if (hit) { L.LaserHit = true; ApplyLaserMask(tx, ty, L); } else L.LaserHit = false;
        if (useful) L.LaserRemainTime = 10;
        else { L.LaserRemainTime--; if (L.LaserRemainTime <= 0) Transition(L, BA.WALKING); }
        return true;
    }

    public bool HandleFalling(Lemming L)
    {
        int curr = 0, maxFall = 3;
        if (HasTriggerAt(L.X, L.Y, "UPDRAFT")) maxFall = 2;
        bool IsFatal() => !(L.IsFloater || L.IsGlider) && !HasTriggerAt(L.X, L.Y, "NOSPLAT")
            && (L.Fallen > Lem.MAX_FALLDISTANCE || HasTriggerAt(L.X, L.Y, "SPLAT"));
        bool FloaterOrGlider()
        {
            if (L.IsFloater && L.TrueFallen > 16 && curr == 0) { Transition(L, BA.FLOATING); return true; }
            if (L.IsGlider && (L.TrueFallen > 8 || (L.InitialFall && L.TrueFallen > 6))) { Transition(L, BA.GLIDING); return true; }
            return false;
        }
        if (FloaterOrGlider()) return true;
        while (curr < maxFall && !HasPixelAt(L.X, L.Y))
        {
            if (curr > 0 && FloaterOrGlider()) return true;
            L.Y++; curr++; L.Fallen++; L.TrueFallen++;
            if (HasTriggerAt(L.X, L.Y, "UPDRAFT")) L.Fallen = 0;
        }
        if (L.Fallen > Lem.MAX_FALLDISTANCE) L.Fallen = Lem.MAX_FALLDISTANCE + 1;
        if (L.TrueFallen > Lem.MAX_FALLDISTANCE) L.TrueFallen = Lem.MAX_FALLDISTANCE + 1;
        if (curr < maxFall) LemNextAction = IsFatal() ? BA.SPLATTING : BA.WALKING;
        return true;
    }

    static readonly int[] FloatingTable = { 3, 3, 3, 3, -1, 0, 1, 1, 2, 2, 2, 2, 2, 2, 2, 2, 2 };

    // physicsFrame is 1..16 here (handleLemming wraps it to 9), so the table lookups stay in range
    public bool HandleFloating(Lemming L)
    {
        int maxFall = FloatingTable[L.PhysicsFrame - 1];
        if (HasTriggerAt(L.X, L.Y, "UPDRAFT")) maxFall--;
        int ground = Math.Max(FindGroundPixel(L.X, L.Y), 0);
        if (maxFall > ground) { L.Y += ground; LemNextAction = BA.WALKING; }
        else L.Y += maxFall;
        return true;
    }

    static readonly int[] GlidingTable = { 3, 3, 3, 3, -1, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1 };

    public bool HandleGliding(Lemming L)
    {
        bool H(int x, int y) => HasPixelAt(x, y);
        bool DoTurnAround(bool moveForwardFirst)
        {
            int cx = L.X;
            if (moveForwardFirst) cx += L.Dx;
            int dy = 0;
            do
            {
                if (H(cx, L.Y + dy) && H(cx - L.Dx, L.Y + dy)) return true;
                dy++;
            } while (!(dy > 3 || !H(cx, L.Y + dy)));
            return dy > 3;
        }
        bool HeadCheck(int x, int y) => !(H(x - 1, y - 12) && H(x, y - 12) && H(x + 1, y - 12));
        void CheckOnePixelShaft()
        {
            bool HasConsecutive()
            {
                string type = L.Dx > 0 ? "FORCELEFT" : "FORCERIGHT";
                for (int i = 1; i <= 3; i++) if (!(H(L.X + L.Dx, L.Y + i) || HasTriggerAt(L.X + L.Dx, L.Y + i, type))) return false;
                return true;
            }
            int yDir = HasTriggerAt(L.X, L.Y, "UPDRAFT") ? -1 : 1;
            if ((FindGroundPixel(L.X + L.Dx, L.Y) < -4 && DoTurnAround(true)) || HasConsecutive())
            {
                if (H(L.X, L.Y) && yDir == 1) LemNextAction = BA.WALKING;
                else if (H(L.X, L.Y - 2) && yDir == -1) { /* nothing */ }
                else L.Y += yDir;
            }
        }
        int maxFall = GlidingTable[L.PhysicsFrame - 1];
        if (HasTriggerAt(L.X, L.Y, "UPDRAFT"))
        {
            maxFall--;
            if (L.PhysicsFrame >= 9 && L.PhysicsFrame % 2 == 1 && !H(L.X + L.Dx, L.Y + maxFall - 1) && HeadCheck(L.X, L.Y - 1)) maxFall--;
        }
        L.X += L.Dx;
        if (maxFall < 0) L.Y += maxFall;
        int groundDist = FindGroundPixel(L.X, L.Y);
        if (groundDist < -4)
        {
            if (DoTurnAround(false)) { L.X -= L.Dx; TurnAround(L); CheckOnePixelShaft(); }
            else { int dy = 0; do { dy++; } while (H(L.X, L.Y + dy)); L.Y += dy; }
        }
        else if (groundDist < 0) { L.Y += groundDist; LemNextAction = BA.WALKING; }
        else if (maxFall > 0)
        {
            if (maxFall > groundDist) { L.Y += groundDist; LemNextAction = BA.WALKING; }
            else L.Y += maxFall;
        }
        else if (HasTriggerAt(L.X, L.Y, "UPDRAFT"))
        {
            int dy = -1;
            while (!HeadCheck(L.X, L.Y) && dy < 2)
            {
                L.Y++; dy++;
                if (H(L.X, L.Y)) { LemNextAction = BA.WALKING; dy = 4; }
            }
        }
        return true;
    }

    public bool HandleSplatting(Lemming L) { if (L.EndOfAnimation) RemoveLemming(L, RM.KILL); return false; }

    public bool HandleExiting(Lemming L)
    {
        if (IsOutOfTime)
        {
            L.Frame--; L.PhysicsFrame--;
            if (UserSetNuking && L.ExplosionTimer <= 0 && IndexLemmingToBeNuked > L.Index) Transition(L, BA.OHNOING);
        }
        else if (L.EndOfAnimation) RemoveLemming(L, RM.SAVE);
        return false;
    }

    public bool HandleVaporizing(Lemming L) { if (L.EndOfAnimation) RemoveLemming(L, RM.KILL); return false; }
    public bool HandleBlocking(Lemming L) { if (!HasPixelAt(L.X, L.Y)) Transition(L, BA.FALLING); return true; }
    public bool HandleShrugging(Lemming L) { if (L.EndOfAnimation) Transition(L, BA.WALKING); return true; }

    public bool HandleOhNoing(Lemming L)
    {
        if (L.EndOfAnimation)
        {
            Transition(L, L.Action == BA.OHNOING ? BA.EXPLODING : BA.STONEFINISH);
            L.HasBlockerField = false;
            SetBlockerMap();
            return false;
        }
        if (!HasPixelAt(L.X, L.Y))
        {
            L.HasBlockerField = false;
            SetBlockerMap();
            L.Y += Math.Min(FindGroundPixel(L.X, L.Y), HasTriggerAt(L.X, L.Y, "UPDRAFT") ? 2 : 3);
        }
        return true;
    }

    public bool HandleExploding(Lemming L)
    {
        if (L.Action == BA.EXPLODING) ApplyExplosionMask(L);
        else ApplyStoneLemming(L);
        RemoveLemming(L, RM.KILL);
        L.Exploded = true;
        L.ParticleTimer = Lem.PARTICLE_FRAMECOUNT;
        ParticleFinishTimer = Lem.PARTICLE_FRAMECOUNT;
        return false;
    }
}
