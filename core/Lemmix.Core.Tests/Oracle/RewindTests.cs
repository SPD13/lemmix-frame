using System.Collections.Concurrent;
using Lemmix.Engine;
using Lemmix.Oracle;

namespace Lemmix.Tests.Oracle;

// Rewind self-check: a frame reached by going back (load the nearest saved state, simulate
// silently forward) must be the frame the game had when it first played it. The replay's length
// is left out: going back keeps the replay, and the Game's time-up nuke may have been recorded.
public class RewindTests
{
    [Fact]
    public void GoingBackGivesTheFrameAsFirstPlayed()
    {
        using var doc = OracleData.Load("sim/noinput.json");
        Assert.SkipWhen(doc == null || !OracleData.HasAssets, "no oracle output or assets");
        var rows = doc!.RootElement.GetProperty("levels").EnumerateArray().Where((r, i) => i % 13 == 0 && !r.TryGetProperty("error", out _)).Select(r => r.Clone()).ToList();
        var masks = Masks.Load(OracleData.Io);
        var failures = new ConcurrentBag<string>();
        Parallel.ForEach(rows, () => new StyleManager(OracleData.Io), (row, _, styles) =>
        {
            string id = row.GetProperty("id").GetString()!;
            var level = LevelBuilder.Build(LevelBuilder.ParseLevel(OracleData.Io.Text(row.GetProperty("url").GetString()!)!), styles, id);
            var game = new Game(level, masks);
            int[] targets = { 1199, 850, 510, 340, 171, 170, 169, 17, 1, 0 };
            var forward = new Dictionary<int, string> { [0] = SimState.HashFrame(game.Sim, true, false).Hex() };
            game.Start();
            while (game.Sim.CurrentIteration < 1200 && !game.Sim.GameFinished && !game.Sim.StateIsUnplayable)
            {
                game.GameTimer.Tick();
                if (targets.Contains(game.Sim.CurrentIteration)) forward[game.Sim.CurrentIteration] = SimState.HashFrame(game.Sim, true, false).Hex();
            }
            int end = game.Sim.CurrentIteration;
            foreach (int t in targets)
            {
                if (t >= end || !forward.ContainsKey(t)) continue;
                game.GotoFrame(t, true);
                string got = SimState.HashFrame(game.Sim, true, false).Hex();
                if (game.Sim.CurrentIteration != t || got != forward[t]) { failures.Add($"{id}: back to frame {t} differs"); break; }
            }
            return styles;
        }, _ => { });
        var list = failures.OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.True(list.Count == 0, $"{list.Count}/{rows.Count} levels differ:\n" + string.Join("\n", list.Take(40)));
    }
}
