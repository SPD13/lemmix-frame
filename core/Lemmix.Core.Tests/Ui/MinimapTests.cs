using System.Collections.Concurrent;
using Lemmix.Engine;
using Lemmix.Oracle;
using Lemmix.Tests.Oracle;
using Lemmix.Ui;

namespace Lemmix.Tests.Ui;

// oracle/minimap.js: two minimaps over one game through a fixed script of view rectangles, a
// frozen offset, the game running with skills given, and a rewind.
public class MinimapTests
{
    static readonly string[] DigSkills = { "DIGGER", "MINER", "BASHER", "BUILDER", "BOMBER", "PLATFORMER", "STACKER", "FENCER" };

    static string ViewHash(Minimap m)
    {
        var h = new StateHash();
        h.Bytes(m.View);
        h.Word(m.OffX); h.Word(m.OffY);
        if (m.ComputeFrame() is Minimap.FrameRect f) { h.Word(f.L); h.Word(f.T); h.Word(f.R); h.Word(f.B); } else h.Null();
        var s = m.Spec;
        foreach (var (px, py) in new[] { (s.X + 0.0, s.Y + 0.0), (s.X + 50.5, s.Y + 17.0), (s.X + 103.0, s.Y + 33.0), (s.X - 10.0, s.Y + 5.0), (s.X + 104.0, s.Y + 34.0) })
        {
            h.Bool(m.Contains(px, py));
            var p = m.PointToLevel(px, py);
            h.Word((int)Lemmix.Util.JsMath.Round(p.X * 16)); h.Word((int)Lemmix.Util.JsMath.Round(p.Y * 16));
        }
        return h.Hex();
    }

    static List<(string, string)> Script(Game game, List<Minimap> maps)
    {
        var steps = new List<(string, string)>();
        double W = game.Level.Width, H = game.Level.Height;
        void Rec(string label) { foreach (var m in maps) m.Update(); steps.Add((label, string.Join(":", maps.Select(ViewHash)))); }
        void View(double[]? r) { foreach (var m in maps) m.SetViewRect(r == null ? null : new LevelRect(r[0], r[1], r[2], r[3])); }
        Rec("no view");
        var rects = new[]
        {
            new[] { 0, 0, W, H }, new[] { 0, 0, 160.0, 100 }, new[] { W / 2 - 80, H / 2 - 50, W / 2 + 80, H / 2 + 50 }, new[] { W - 100, H - 60, W + 40, H + 20 },
            new[] { -30.5, -10.25, 120.75, 90.5 }, new[] { 33.7, 12.2, 33.9, 12.4 }, new[] { W * 0.75, 0, W * 0.75 + 320, 160 },
        };
        for (int i = 0; i < rects.Length; i++) { View(rects[i]); Rec("rect " + i); }
        View(null); Rec("rect null");
        foreach (var m in maps) m.SetFreeze(true);
        View(new[] { 0, 0, 160.0, 100 }); Rec("frozen");
        foreach (var m in maps) m.SetFreeze(false);
        Rec("unfrozen");
        game.Start();
        var timer = game.GameTimer;
        for (int f = 1; f <= 150; f++)
        {
            timer.Tick();
            if (f == 40 || f == 80)
            {
                var names = game.Sim.ActiveSkills;
                int i = names.FindIndex(n => DigSkills.Contains(n) && game.Sim.SkillCountOf(n) > 0);
                if (i < 0) i = 0;
                game.Skills.SetSelectedSkill(i);
                var live = game.Sim.Lemmings.Where(L => !L.Removed).ToList();
                var L = live.Count == 0 ? null : live[f == 40 ? 0 : live.Count - 1];
                if (L != null) game.QueueCommand(new CommandLemmingsAction(L.Id));
            }
            if (f % 25 == 0)
            {
                var L = game.Sim.Lemmings.FirstOrDefault(x => !x.Removed);
                if (L != null) View(new double[] { L.X - 80, L.Y - 50, L.X + 80, L.Y + 50 });
                Rec("frame " + f);
            }
        }
        game.BackFrames(60);
        foreach (var m in maps) m.TerrainDirty = true; // app.js refreshAfterRestore
        Rec("back 60");
        for (int i = 0; i < 30; i++) timer.Tick();
        Rec("30 more");
        return steps;
    }

    [Fact]
    public void MinimapMatchesTheWebPage()
    {
        using var doc = OracleData.Load("minimap.json");
        Assert.SkipWhen(doc == null || !OracleData.HasAssets, "no oracle output or assets");
        var specs = doc!.RootElement.GetProperty("specs").EnumerateArray().Select(s => new MinimapSpec
        {
            X = s.GetProperty("x").GetInt32(), Y = s.GetProperty("y").GetInt32(), W = s.GetProperty("w").GetInt32(), H = s.GetProperty("h").GetInt32(),
            ScaleX = s.GetProperty("scaleX").GetInt32(), ScaleY = s.GetProperty("scaleY").GetInt32(), Pad = s.GetProperty("pad").GetInt32(),
        }).ToList();
        var rows = doc.RootElement.GetProperty("levels").EnumerateArray().Where(r => !r.TryGetProperty("error", out _)).Select(r => r.Clone()).ToList();
        var factory = new UiGameFactory();
        var failures = new ConcurrentBag<string>();
        Parallel.ForEach(rows, () => new StyleManager(OracleData.Io), (row, _, styles) =>
        {
            string id = row.GetProperty("id").GetString()!;
            try
            {
                var (game, _) = factory.Create(styles, id, row.GetProperty("url").GetString()!);
                var maps = specs.Select(s => new Minimap(game, game.Level, s)).ToList();
                var got = Script(game, maps);
                foreach (var m in maps) m.Dispose();
                var want = row.GetProperty("steps").EnumerateArray().Select(s => (s[0].GetString()!, s[1].GetString()!)).ToList();
                for (int i = 0; i < Math.Min(got.Count, want.Count); i++)
                    if (got[i] != want[i]) { failures.Add($"{id}: step {i} '{want[i].Item1}' differs (got '{got[i].Item1}')"); return styles; }
                if (got.Count != want.Count) failures.Add($"{id}: {got.Count} steps, web {want.Count}");
            }
            catch (Exception e) { failures.Add($"{id}: {e.GetType().Name} {e.Message} {e.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}"); }
            return styles;
        }, _ => { });
        var list = failures.OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.True(list.Count == 0, $"{list.Count}/{rows.Count} levels differ:\n" + string.Join("\n", list.Take(40)));
    }
}
