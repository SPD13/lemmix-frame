using Lemmix.Oracle;

namespace Lemmix.Engine;

// TLemming, with LemX/LemY/... shortened to X/Y/... (lemgame.js Lemming). The fields are in
// the JS constructor's order, which is the order the oracle hashes them in (HashInto).
public sealed class Lemming
{
    public int Index;          // LemIndex
    public string Identifier = "";
    public int X, Y, Dx = 1;
    public int Ascended;
    public int Fallen, TrueFallen;
    public int ExplosionTimer;
    public int DisarmingFrames;
    public int Frame, MaxFrame = -1, FrameDiff;
    public int PhysicsFrame, MaxPhysicsFrame;
    public int ParticleTimer = -1;
    public int BricksLeft;
    public int Action = BA.NONE;
    public bool Removed;
    public bool Teleporting;
    public bool EndOfAnimation;
    public bool IsPhysicsSimulation;
    public bool IsSlider, IsClimber, IsSwimmer, IsFloater, IsGlider, IsDisarmer;
    public bool IsZombie, IsNeutral;
    public bool HasBeenOhnoer;
    public bool PlacedBrick;
    public int InFlipper = -1;
    public bool HasBlockerField;
    public bool IsStartingAction;
    public bool Exploded;
    public bool TimerToStone;
    public bool HideCountdown;
    public bool StackLow;
    public int JumpProgress;
    public int DehoistPinY = -1;
    public bool LaserHit; public int LaserRemainTime;
    public bool ConstructivePositionFreeze;
    public bool WalkerPositionAdjusted;
    public bool InitialFall;
    public int XOld, YOld, DxOld = 1;
    public int ActionOld = BA.NONE;
    public int ActionNew = BA.NONE;
    public int PortalWarpFrame, InPortal = -1;
    public List<int[]> JumpPositions = new();
    public int QueueAction = BA.NONE, QueueFrame;
    // set by HandleLasering outside the JS constructor (so last in the oracle's field order)
    public int[]? LaserHitPoint;

    public Lemming(int index) { Index = index; }

    // assign(s): every field but the index and the queue; the jump path shared by reference.
    public void Assign(Lemming s)
    {
        int index = Index, queueAction = QueueAction, queueFrame = QueueFrame;
        CopyFields(s, this);
        Index = index; QueueAction = queueAction; QueueFrame = queueFrame;
    }

    // A copy for a saved state: every field, the jump path by value.
    public Lemming Clone()
    {
        var c = new Lemming(Index);
        CopyFields(this, c);
        c.JumpPositions = JumpPositions.Select(p => (int[])p.Clone()).ToList();
        return c;
    }

    static void CopyFields(Lemming s, Lemming d)
    {
        d.Index = s.Index; d.Identifier = s.Identifier; d.X = s.X; d.Y = s.Y; d.Dx = s.Dx; d.Ascended = s.Ascended;
        d.Fallen = s.Fallen; d.TrueFallen = s.TrueFallen; d.ExplosionTimer = s.ExplosionTimer; d.DisarmingFrames = s.DisarmingFrames;
        d.Frame = s.Frame; d.MaxFrame = s.MaxFrame; d.FrameDiff = s.FrameDiff; d.PhysicsFrame = s.PhysicsFrame;
        d.MaxPhysicsFrame = s.MaxPhysicsFrame; d.ParticleTimer = s.ParticleTimer; d.BricksLeft = s.BricksLeft; d.Action = s.Action;
        d.Removed = s.Removed; d.Teleporting = s.Teleporting; d.EndOfAnimation = s.EndOfAnimation; d.IsPhysicsSimulation = s.IsPhysicsSimulation;
        d.IsSlider = s.IsSlider; d.IsClimber = s.IsClimber; d.IsSwimmer = s.IsSwimmer; d.IsFloater = s.IsFloater;
        d.IsGlider = s.IsGlider; d.IsDisarmer = s.IsDisarmer; d.IsZombie = s.IsZombie; d.IsNeutral = s.IsNeutral;
        d.HasBeenOhnoer = s.HasBeenOhnoer; d.PlacedBrick = s.PlacedBrick; d.InFlipper = s.InFlipper; d.HasBlockerField = s.HasBlockerField;
        d.IsStartingAction = s.IsStartingAction; d.Exploded = s.Exploded; d.TimerToStone = s.TimerToStone; d.HideCountdown = s.HideCountdown;
        d.StackLow = s.StackLow; d.JumpProgress = s.JumpProgress; d.DehoistPinY = s.DehoistPinY; d.LaserHit = s.LaserHit;
        d.LaserRemainTime = s.LaserRemainTime; d.ConstructivePositionFreeze = s.ConstructivePositionFreeze;
        d.WalkerPositionAdjusted = s.WalkerPositionAdjusted; d.InitialFall = s.InitialFall; d.XOld = s.XOld; d.YOld = s.YOld;
        d.DxOld = s.DxOld; d.ActionOld = s.ActionOld; d.ActionNew = s.ActionNew; d.PortalWarpFrame = s.PortalWarpFrame;
        d.InPortal = s.InPortal; d.JumpPositions = s.JumpPositions; d.QueueAction = s.QueueAction; d.QueueFrame = s.QueueFrame;
        // only when set: assign() copies the keys the source has, so an absent one leaves the target's
        if (s.LaserHitPoint != null) d.LaserHitPoint = s.LaserHitPoint; // shared, as the JS copies the array reference
    }

    public bool HasPermanentSkills => IsSlider || IsClimber || IsSwimmer || IsFloater || IsGlider || IsDisarmer;
    public bool CannotReceiveSkills => IsZombie || IsNeutral || HasBeenOhnoer;

    // what the 3D layer and the DOS-style display read
    public int Id => Index;
    public bool LookRight => Dx > 0;
    public string ActionName => Lem.ActionNames[Action];

    // oracle/lib/state.js: the fields in schema order (oracle/out/sim/*.json "schema.lemming")
    public void HashInto(StateHash h)
    {
        h.Word(Index); h.Str(Identifier); h.Word(X); h.Word(Y); h.Word(Dx); h.Word(Ascended);
        h.Word(Fallen); h.Word(TrueFallen); h.Word(ExplosionTimer); h.Word(DisarmingFrames);
        h.Word(Frame); h.Word(MaxFrame); h.Word(FrameDiff); h.Word(PhysicsFrame); h.Word(MaxPhysicsFrame);
        h.Word(ParticleTimer); h.Word(BricksLeft); h.Word(Action);
        h.Bool(Removed); h.Bool(Teleporting); h.Bool(EndOfAnimation); h.Bool(IsPhysicsSimulation);
        h.Bool(IsSlider); h.Bool(IsClimber); h.Bool(IsSwimmer); h.Bool(IsFloater); h.Bool(IsGlider); h.Bool(IsDisarmer);
        h.Bool(IsZombie); h.Bool(IsNeutral); h.Bool(HasBeenOhnoer); h.Bool(PlacedBrick);
        h.Word(InFlipper); h.Bool(HasBlockerField); h.Bool(IsStartingAction); h.Bool(Exploded);
        h.Bool(TimerToStone); h.Bool(HideCountdown); h.Bool(StackLow); h.Word(JumpProgress); h.Word(DehoistPinY);
        h.Bool(LaserHit); h.Word(LaserRemainTime); h.Bool(ConstructivePositionFreeze); h.Bool(WalkerPositionAdjusted);
        h.Bool(InitialFall); h.Word(XOld); h.Word(YOld); h.Word(DxOld); h.Word(ActionOld); h.Word(ActionNew);
        h.Word(PortalWarpFrame); h.Word(InPortal);
        h.Word(JumpPositions.Count);
        foreach (var p in JumpPositions) { h.Word(p.Length); foreach (int v in p) h.Word(v); }
        h.Word(QueueAction); h.Word(QueueFrame);
        // LEMMING_EXTRA
        if (LaserHitPoint == null) h.Null(); else { h.Word(LaserHitPoint.Length); foreach (int v in LaserHitPoint) h.Word(v); }
    }

    public static readonly string[] SchemaOrder =
    {
        "index", "identifier", "x", "y", "dx", "ascended", "fallen", "trueFallen", "explosionTimer", "disarmingFrames", "frame",
        "maxFrame", "frameDiff", "physicsFrame", "maxPhysicsFrame", "particleTimer", "bricksLeft", "action", "removed",
        "teleporting", "endOfAnimation", "isPhysicsSimulation", "isSlider", "isClimber", "isSwimmer", "isFloater", "isGlider",
        "isDisarmer", "isZombie", "isNeutral", "hasBeenOhnoer", "placedBrick", "inFlipper", "hasBlockerField", "isStartingAction",
        "exploded", "timerToStone", "hideCountdown", "stackLow", "jumpProgress", "dehoistPinY", "laserHit", "laserRemainTime",
        "constructivePositionFreeze", "walkerPositionAdjusted", "initialFall", "xOld", "yOld", "dxOld", "actionOld", "actionNew",
        "portalWarpFrame", "inPortal", "jumpPositions", "queueAction", "queueFrame",
    };
}
