using Lemmix.Engine;

namespace Lemmix.Oracle;

// oracle/lib/state.js hashFrame: the canonical per-frame state of a LemGame, in the same order.
public static class SimState
{
    public static StateHash HashFrame(LemGame g, bool withTerrain)
    {
        var h = new StateHash();
        // SAVED_SCALARS, in order
        h.Word(g.CurrentIteration); h.Word(g.ClockFrame); h.Word(g.TimePlay); h.Word(g.LemmingsToRelease);
        h.Word(g.LemmingsCloned); h.Word(g.LemmingsOut); h.Word(g.LemmingsIn); h.Word(g.LemmingsRemoved);
        h.Word(g.SpawnedDead); h.Word(g.DelayEndFrames); h.Word(g.ParticleFinishTimer); h.Word(g.NextLemmingCountdown);
        h.Bool(g.HatchesOpened); h.Word(g.ButtonsRemain); h.Word(g.CurrSpawnInterval); h.Bool(g.UserSetNuking);
        h.Bool(g.ExploderAssignInProgress); h.Word(g.IndexLemmingToBeNuked); h.Bool(g.GameFinished);
        h.Word(g.LemNextAction); h.Bool(g.LemJumpToHoistAdvance);
        foreach (var counts in new[] { g.CurrSkillCount, g.UsedSkillCount })
        {
            var keys = counts.Keys.OrderBy(k => k).ToList();
            h.Word(keys.Count);
            foreach (int k in keys) { h.Word(k); h.Word(counts[k]); }
        }
        var tal = g.TalismansAchieved.Select(t => t.ToString(System.Globalization.CultureInfo.InvariantCulture)).OrderBy(t => t, StringComparer.Ordinal).ToList();
        h.Word(tal.Count); foreach (string t in tal) h.Str(t);
        h.Word(g.Lemmings.Count);
        foreach (var L in g.Lemmings) L.HashInto(h);
        h.Word(g.Gadgets.Count);
        foreach (var gd in g.Gadgets)
        {
            h.Word(gd.RemainingLemmings); h.Bool(gd.HoldActive); h.Bool(gd.Triggered); h.Bool(gd.SecondariesTreatAsBusy);
            h.Word(gd.TeleLem); h.Bool(gd.ZombieMode); h.Bool(gd.NeutralMode); h.Word(gd.X); h.Word(gd.Y); h.Str(gd.Effect);
            h.Word(gd.Animations.Count);
            foreach (var a in gd.Animations) { h.Word(a.Frame); h.Str(a.State); h.Bool(a.Visible); }
        }
        h.Word(g.Sounds.Count);
        foreach (var s in g.Sounds) { h.Str(s.Name); h.Int(s.X); h.Int(s.Y); }
        h.Word(g.Recorded.Count);
        if (withTerrain) h.Terrain(g.Level);
        return h;
    }

    public const int Block = 100, TerrainEvery = 17;

    public sealed record RunRecord(string Build, string BuildTerrain, int Frames, List<string> Blocks,
        bool Finished, bool Unplayable, int Out, int Saved, int Removed, List<string>? FrameHashes);

    // oracle/sim.js run(), step for step.
    public static RunRecord Run(LemGame game, Level level, ParsedReplay? replay, int maxFrames, bool nukeWhenOutOfTime, bool keepFrames = false)
    {
        string builtTerrain = StateHash.TerrainHash(level);
        game.Start();
        if (replay != null) game.LoadReplay(replay);
        var build = HashFrame(game, true);
        var blocks = new List<string>();
        var frames = keepFrames ? new List<string>() : null;
        var block = new StateHash();
        int f = 0;
        while (f < maxFrames && !game.GameFinished && !game.StateIsUnplayable)
        {
            if (nukeWhenOutOfTime && game.IsOutOfTime && !game.UserSetNuking) game.Nuke();
            game.Update();
            f++;
            var h = HashFrame(game, f % TerrainEvery == 0);
            frames?.Add(h.Hex());
            block.Word(h.H1); block.Word(h.H2);
            if (f % Block == 0) { blocks.Add(block.Hex()); block = new StateHash(); }
        }
        var last = HashFrame(game, true);
        block.Word(last.H1); block.Word(last.H2);
        blocks.Add(block.Hex());
        return new RunRecord(build.Hex(), builtTerrain, f, blocks, game.GameFinished, game.StateIsUnplayable,
            game.LemmingsOut, game.LemmingsIn, game.LemmingsRemoved, frames);
    }
}
