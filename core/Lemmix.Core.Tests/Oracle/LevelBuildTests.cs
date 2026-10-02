using Lemmix.Engine;
using Lemmix.Oracle;

namespace Lemmix.Tests.Oracle;

// Every level built by the port must give what the web engine builds (oracle/out/build.json):
// the terrain byte for byte (physics map, picture, ground mask), and the derived numbers.
public class LevelBuildTests
{
    // oracle/build.js summary(), field for field
    public static string Summary(Level level)
    {
        var h = new StateHash();
        foreach (int v in new[] { level.Width, level.Height, level.ReleaseCount, level.NeedCount, level.ZombieCount, level.NeutralCount, level.SpawnInterval })
            h.Word(v);
        h.Bool(level.SpawnLocked);
        foreach (int v in new[] { level.TimeLimitSeconds, level.StartX, level.StartY, level.ScreenPositionX }) h.Word(v);
        h.Word(level.SpawnOrder.Count); foreach (int i in level.SpawnOrder) h.Word(i);
        h.Word(level.Skills.Count); foreach (var s in level.Skills) { h.Str(s.Name); h.Word(s.Count); }
        h.Word(level.Gadgets.Count);
        foreach (var g in level.Gadgets)
        {
            h.Str(g.Effect); h.Str(g.EffectBase); h.Word(g.X); h.Word(g.Y); h.Word(g.Width); h.Word(g.Height);
            var r = g.TriggerRect; h.Word(r.X0); h.Word(r.Y0); h.Word(r.X1); h.Word(r.Y1);
            h.Word(g.ReceiverId); h.Word(g.PairingId); h.Bool(g.FlipLemming); h.Word(g.RemainingLemmings);
            h.Word(g.Skill); h.Word(g.SkillCount); h.Word(g.AngleSegment);
            h.Word(g.Animations.Count); foreach (var a in g.Animations) { h.Word(a.Frame); h.Str(a.State); h.Bool(a.Visible); }
        }
        h.Word(level.Background.Color);
        h.Int(level.Background.Image?.Width);
        return h.Hex();
    }

    [Fact]
    public void EveryLevelBuildsTheSameAsTheWebEngine()
    {
        using var doc = OracleData.Load("build.json");
        Assert.SkipWhen(doc == null || !OracleData.HasAssets, "no oracle output or assets");
        var terrain = new List<string>();
        var summary = new List<string>();
        int n = 0;
        foreach (var row in doc!.RootElement.GetProperty("levels").EnumerateArray())
        {
            if (row.TryGetProperty("error", out _)) continue;
            string id = row.GetProperty("id").GetString()!;
            n++;
            try
            {
                var level = OracleData.BuildLevel(id, row.GetProperty("url").GetString()!);
                if (StateHash.TerrainHash(level) != row.GetProperty("terrain").GetString()) terrain.Add(id);
                if (Summary(level) != row.GetProperty("summary").GetString()) summary.Add(id);
            }
            catch (Exception e) { terrain.Add($"{id}: {e.GetType().Name} {e.Message}"); }
        }
        Assert.True(terrain.Count == 0 && summary.Count == 0,
            $"{n} levels; terrain differs in {terrain.Count}, summary in {summary.Count}:\n" +
            string.Join("\n", terrain.Take(15).Select(x => "terrain " + x).Concat(summary.Take(15).Select(x => "summary " + x))));
    }
}
