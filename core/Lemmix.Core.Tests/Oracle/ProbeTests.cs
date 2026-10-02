using System.Collections.Concurrent;
using System.Text.Json;
using Lemmix.Engine;
using Lemmix.Oracle;

namespace Lemmix.Tests.Oracle;

// oracle/probe.js: which lemming a cursor picks (every skill, direction filter, walkers only)
// and the skill shadows of the picked lemming, every 60 frames of a no-input run.
public class ProbeTests
{
    static readonly int[][] Offsets = { new[] { 0, 0 }, new[] { -6, -5 }, new[] { 3, 2 }, new[] { -9, -9 }, new[] { 5, -12 } };

    static string ProbeFrame(LemGame sim)
    {
        var h = new StateHash();
        var skills = new List<string?> { null };
        skills.AddRange(sim.ActiveSkills);
        var live = sim.Lemmings.Where(L => !L.Removed).Take(6).ToList();
        foreach (var L in live)
            foreach (var o in Offsets)
            {
                int mx = L.X + o[0], my = L.Y + o[1];
                foreach (int dx in new[] { 0, -1, 1 })
                    foreach (bool walkers in new[] { false, true })
                    {
                        sim.SelectDx = dx; sim.SelectWalkerOnly = walkers;
                        foreach (var s in skills)
                        {
                            int action = s != null ? Lem.SkillToAction[s] : BA.NONE;
                            var p = sim.GetPriorityLemming(action, mx, my);
                            h.Word(p.Lemming?.Index ?? -1); h.Word(p.Count);
                        }
                    }
                sim.SelectDx = 0; sim.SelectWalkerOnly = false;
                var pick = sim.GetPriorityLemming(BA.NONE, mx, my);
                if (pick.Lemming == null) continue;
                foreach (string s in sim.ActiveSkills)
                {
                    var r = Shadows.Compute(sim, pick.Lemming, s);
                    foreach (var list in new[] { r.Low, r.High, r.Bricks })
                    {
                        h.Word(list.Count);
                        foreach (var p in list) { h.Word(p[0]); h.Word(p[1]); }
                    }
                }
            }
        return h.Hex();
    }

    [Fact]
    public void PickingAndShadowsMatchTheWebEngine()
    {
        using var doc = OracleData.Load("probe.json");
        Assert.SkipWhen(doc == null || !OracleData.HasAssets, "no oracle output or assets");
        int every = doc!.RootElement.GetProperty("probeEvery").GetInt32(), frames = doc.RootElement.GetProperty("frames").GetInt32();
        var rows = doc.RootElement.GetProperty("levels").EnumerateArray().Where(r => !r.TryGetProperty("error", out _)).Select(r => r.Clone()).ToList();
        var masks = Masks.Load(OracleData.Io);
        var failures = new ConcurrentBag<string>();
        Parallel.ForEach(rows, () => new StyleManager(OracleData.Io), (row, _, styles) =>
        {
            string id = row.GetProperty("id").GetString()!;
            try
            {
                var level = LevelBuilder.Build(LevelBuilder.ParseLevel(OracleData.Io.Text(row.GetProperty("url").GetString()!)!), styles, id);
                var sim = new LemGame(level, masks);
                sim.Start();
                var probes = row.GetProperty("probes").EnumerateArray().Select(p => p.GetString()!).ToList();
                int k = 0;
                for (int f = 1; f <= frames && !sim.GameFinished && !sim.StateIsUnplayable; f++)
                {
                    sim.Update();
                    if (f % every != 0) continue;
                    var parts = probes[k++].Split(':');
                    if (ProbeFrame(sim) != parts[0]) { failures.Add($"{id}: probe at frame {f} differs"); return styles; }
                    if (SimState.HashFrame(sim, true).Hex() != parts[1]) { failures.Add($"{id}: state after the probe at frame {f} differs"); return styles; }
                }
                if (k != probes.Count) failures.Add($"{id}: {k} probes, web {probes.Count}");
            }
            catch (Exception e) { failures.Add($"{id}: {e.GetType().Name} {e.Message} {e.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}"); }
            return styles;
        }, _ => { });
        var list = failures.OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.True(list.Count == 0, $"{list.Count}/{rows.Count} levels differ:\n" + string.Join("\n", list.Take(40)));
    }
}
