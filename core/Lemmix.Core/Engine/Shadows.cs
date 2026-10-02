namespace Lemmix.Engine;

public sealed record ShadowResult(List<int[]> Low, List<int[]> High, List<int[]> Bricks);

// web/lemmix/js/shadows.js - skill shadows (LemRendering.pas DrawShadows): with a skill selected
// and a lemming under the cursor, what the skill would do, by simulating a copy of the lemming
// frame by frame. "low" the paths (a jumper's arc, a glider's flight), "bricks" the terrain a
// builder, platformer or stacker would lay, "high" the terrain a basher, miner, digger, fencer,
// laserer or bomber would take - read off the physics map, which is put back afterwards.
public static class Shadows
{
    // The skills that cast one (CheckForNewShadow's ShadowSkillSet).
    public static readonly HashSet<string> ShadowSkills = new(StringComparer.Ordinal)
    {
        "JUMPER", "SHIMMIER", "PLATFORMER", "BUILDER", "STACKER", "DIGGER",
        "MINER", "BASHER", "FENCER", "BOMBER", "GLIDER", "CLONER", "LASERER",
    };

    const int MaxWorkFrames = 10000; // a tunnel or a hole is simulated to its end, within reason

    sealed class ShadowSet
    {
        readonly LemGame _sim;
        public readonly List<int[]> Low = new(), High = new(), Bricks = new();
        readonly HashSet<long> _seen = new();
        public ShadowSet(LemGame sim) { _sim = sim; }
        void Put(List<int[]> list, int tag, int x, int y)
        {
            if (x < 0 || x >= _sim.Width || y < 0 || y >= _sim.Height) return;
            long key = ((long)tag << 40) | ((long)(uint)x << 20) | (uint)y;
            if (!_seen.Add(key)) return;
            list.Add(new[] { x, y });
        }
        public void LowAt(int x, int y) => Put(Low, 1, x, y);
        public void HighAt(int x, int y) => Put(High, 2, x, y);
        public void BrickAt(int x, int y) => Put(Bricks, 3, x, y);
    }

    // Run `fn` with the physics map and the game's frame scratch put back afterwards; what the map
    // lost and gained meanwhile goes into `output` as the high pixels and the bricks.
    static void WithPhysicsSaved(LemGame sim, ShadowSet output, Action fn)
    {
        var saved = (ushort[])sim.Physics.Clone();
        int nextAction = sim.LemNextAction;
        bool hoist = sim.LemJumpToHoistAdvance, done = sim.DoneAssignmentThisFrame;
        sim.LemNextAction = BA.NONE; sim.LemJumpToHoistAdvance = false;
        try { fn(); }
        finally
        {
            var now = sim.Physics;
            int w = sim.Width;
            for (int i = 0; i < now.Length; i++)
            {
                int was = saved[i] & 1, isNow = now[i] & 1;
                if (was != 0 && isNow == 0) output.HighAt(i % w, i / w);
                else if (was == 0 && isNow != 0) output.BrickAt(i % w, i / w);
            }
            saved.CopyTo(sim.Physics, 0);
            sim.LemNextAction = nextAction; sim.LemJumpToHoistAdvance = hoist; sim.DoneAssignmentThisFrame = done;
        }
    }

    static void DrawJumper(LemGame sim, Lemming L, ShadowSet output)
    {
        int frames = 0;
        output.LowAt(L.X, L.Y - 1);
        int[] follow = { BA.JUMPING, BA.CLIMBING, BA.HOISTING, BA.FALLING, BA.FLOATING, BA.GLIDING, BA.SLIDING };
        while (frames < 2000 && follow.Contains(L.Action))
        {
            frames++;
            var pos = sim.SimulateLem(L, true);
            foreach (var p in pos)
            {
                int x = p[0], y = p[1];
                if (x < 0 || x >= sim.Width || y <= 0) return;
                output.LowAt(x, y - 1); output.HighAt(x, y - 1);
                if (L.X == x && L.Y == y) break;
            }
        }
    }

    static void DrawShimmier(LemGame sim, Lemming L, ShadowSet output)
    {
        int frames = 0;
        List<int[]>? pos = null;
        output.LowAt(L.X, L.Y - 1);
        while (frames < 2000 && (L.Action == BA.REACHING || L.Action == BA.SHIMMYING))
        {
            frames++;
            if (pos != null) foreach (var p in pos) { output.LowAt(p[0], p[1] - 1); if (L.X == p[0] && L.Y == p[1]) break; }
            pos = sim.SimulateLem(L, true);
        }
    }

    static void DrawGlider(LemGame sim, Lemming L, ShadowSet output)
    {
        int frames = 0;
        List<int[]>? pos = null;
        output.LowAt(L.X, L.Y - 1);
        while (frames < 2000 && (L.Action == BA.GLIDING || (frames < 15 && (L.Action == BA.FALLING || L.Action == BA.JUMPING))))
        {
            frames++;
            if (pos != null) foreach (var p in pos) { output.LowAt(p[0], p[1] - 1); if (L.X == p[0] && L.Y == p[1]) break; }
            pos = sim.SimulateLem(L, true);
        }
    }

    // The skill's work simulated to its end: what the map lost and gained is the shadow.
    static void Work(LemGame sim, Lemming L, int action)
    {
        int frames = 0;
        while (L.Action == action && frames < MaxWorkFrames)
        {
            sim.SimulateLem(L, true);
            frames++;
        }
    }

    // The laserer: until the beam stops hitting, or hits the same spot twice.
    static void DrawLaserer(LemGame sim, Lemming L)
    {
        int[]? last = null;
        while (L.Action == BA.LASERING)
        {
            sim.SimulateLem(L, true);
            if (!L.LaserHit || L.LaserHitPoint == null || (last != null && last[0] == L.LaserHitPoint[0] && last[1] == L.LaserHitPoint[1])) break;
            last = (int[])L.LaserHitPoint.Clone();
        }
    }

    // The bomber: the crater its mask leaves, applied in simulation.
    static void DrawExploder(LemGame sim, Lemming L)
    {
        sim.SimulationDepth++;
        try { sim.ApplyExplosionMask(L); } finally { sim.SimulationDepth--; }
    }

    // DrawShadows: the pixels the skill would touch, for this lemming as it stands now.
    static void Draw(LemGame sim, Lemming L, string skill, ShadowSet output)
    {
        var copy = L.Clone();
        switch (skill)
        {
            case "JUMPER": sim.SimulateTransitionLem(copy, BA.JUMPING); DrawJumper(sim, copy, output); break;
            case "SHIMMIER":
                sim.SimulateTransitionLem(copy, (copy.Action == BA.CLIMBING || copy.Action == BA.JUMPING) ? BA.SHIMMYING : BA.REACHING);
                DrawShimmier(sim, copy, output); break;
            case "BUILDER": sim.SimulateTransitionLem(copy, BA.BUILDING); Work(sim, copy, BA.BUILDING); break;
            case "PLATFORMER": sim.SimulateTransitionLem(copy, BA.PLATFORMING); Work(sim, copy, BA.PLATFORMING); break;
            case "STACKER": sim.SimulateTransitionLem(copy, BA.STACKING); Work(sim, copy, BA.STACKING); break;
            case "DIGGER": sim.SimulateTransitionLem(copy, BA.DIGGING); Work(sim, copy, BA.DIGGING); break;
            case "MINER": sim.SimulateTransitionLem(copy, BA.MINING); Work(sim, copy, BA.MINING); break;
            case "BASHER": sim.SimulateTransitionLem(copy, BA.BASHING); Work(sim, copy, BA.BASHING); break;
            case "FENCER": sim.SimulateTransitionLem(copy, BA.FENCING); Work(sim, copy, BA.FENCING); break;
            case "BOMBER": DrawExploder(sim, copy); break;
            case "GLIDER": copy.IsGlider = true; DrawGlider(sim, copy, output); break;
            case "LASERER": sim.SimulateTransitionLem(copy, BA.LASERING); DrawLaserer(sim, copy); break;
            case "CLONER":
            {
                // the clone goes the other way, doing what this one does
                copy.Dx = -copy.Dx;
                if (Lem.ActionToSkill.TryGetValue(copy.Action, out var doing) && ShadowSkills.Contains(doing) && doing != "CLONER")
                    Draw(sim, copy, doing, output);
                break;
            }
        }
    }

    public static ShadowResult Compute(LemGame sim, Lemming? L, string? skill)
    {
        if (L == null || L.Removed || skill == null || !ShadowSkills.Contains(skill)) return new ShadowResult(new(), new(), new());
        var output = new ShadowSet(sim);
        WithPhysicsSaved(sim, output, () => Draw(sim, L, skill, output));
        return new ShadowResult(output.Low, output.High, output.Bricks);
    }
}
