namespace Lemmix.Engine;

// web/lemmix/js/rewind.js - saved states and skipping through time, as NeoLemmix does them
// (TLemmingGameSavedStateList, GameWindow GotoSaveState): a state at frame 0 and every 170
// frames, thinned to one per ten seconds over the last minute, one per half minute over the
// last three and one per minute beyond. Going to a frame loads the nearest state before it and
// simulates forward silently until it is reached.
public sealed class SaveStates
{
    public const int SaveEvery = 170;                 // frames: every 10 seconds
    const int Minute = 17 * 60, HalfMinute = 17 * 30, TenSeconds = 17 * 10;

    public List<SavedState> States = new();
    public Action<SavedState>? OnSave;   // the renderer adds what it keeps per state
    public Action<SavedState>? OnLoad;   // and takes it back

    public static int FrameOf(SavedState s) => s.Scalars.CurrentIteration;
    public int Count => States.Count;

    // Keep this frame.
    public SavedState Add(LemGame sim)
    {
        var s = sim.SaveState();
        OnSave?.Invoke(s);
        States.Add(s);
        return s;
    }

    // Put the game back at this state.
    public void Load(LemGame sim, SavedState s)
    {
        sim.LoadState(s);
        OnLoad?.Invoke(s);
    }

    // TidyList: what is worth keeping, seen from the current frame.
    public void Tidy(int current)
    {
        States = States.Where(s =>
        {
            int f = FrameOf(s);
            if (f == 0) return true;
            if (f % Minute == 0) return true;
            if (f % HalfMinute == 0 && current - f <= Minute * 3) return true;
            if (f % TenSeconds == 0 && current - f <= Minute) return true;
            return false;
        }).ToList();
    }

    // FindNearestState: the latest state strictly before the frame, or null.
    public SavedState? NearestBefore(int target)
    {
        SavedState? best = null;
        foreach (var s in States)
        {
            int f = FrameOf(s);
            if (f < target && (best == null || f > FrameOf(best))) best = s;
        }
        return best;
    }

    // The state at frame 0, if any.
    public SavedState? First() => States.FirstOrDefault(s => FrameOf(s) == 0);

    // ClearAfterIteration: states past the frame are stale once the past is replayed.
    public void ClearAfter(int frame) => States = States.Where(s => FrameOf(s) <= frame).ToList();
}

public static class Rewind
{
    // GotoSaveState: the game at `target`. Returns how many frames were simulated.
    public static int GotoFrame(LemGame sim, SaveStates states, int target)
    {
        target = Math.Max(0, target);
        var from = target > 0 ? states.NearestBefore(target) : states.First();
        if (from == null) throw new InvalidOperationException("rewind: no saved state before frame " + target);
        states.Load(sim, from);
        states.ClearAfter(sim.CurrentIteration);
        int n = 0;
        while (sim.CurrentIteration < target && !sim.GameFinished && !sim.StateIsUnplayable)
        {
            sim.Update();
            n++;
            if (sim.CurrentIteration % SaveStates.SaveEvery == 0)
            {
                states.Add(sim);
                states.Tidy(sim.CurrentIteration);
            }
        }
        return n;
    }

    // Forward at hyperspeed until `stop(sim)` says so (checked after each frame), the game ends,
    // or `maxFrames` have gone by (fHyperSpeedStopCondition). Returns the frames simulated.
    public static int RunUntil(LemGame sim, SaveStates states, Func<LemGame, bool> stop, int maxFrames)
    {
        int n = 0;
        while (n < maxFrames && !sim.GameFinished && !sim.StateIsUnplayable)
        {
            sim.Update();
            n++;
            if (sim.CurrentIteration % SaveStates.SaveEvery == 0)
            {
                states.Add(sim);
                states.Tidy(sim.CurrentIteration);
            }
            if (stop(sim)) break;
        }
        return n;
    }
}
