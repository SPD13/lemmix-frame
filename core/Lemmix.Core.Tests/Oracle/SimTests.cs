using System.Collections.Concurrent;
using System.Text.Json;
using Lemmix.Engine;
using Lemmix.Oracle;

namespace Lemmix.Tests.Oracle;

// The sim oracle (oracle/sim.js): the port must reproduce the web engine's state every frame.
// Block hashes cover 100 frames each; a mismatch names the level and the first differing block,
// and `dotnet run --project core/Lemmix.Core.Tests -- … ` / oracle/sim.js trace narrow it to a frame.
public class SimTests
{
    static Masks? _masks;
    static Masks MasksOnce() => _masks ??= Masks.Load(OracleData.Io);

    static List<string> Compare(string file, bool replays)
    {
        using var doc = OracleData.Load(file);
        Assert.SkipWhen(doc == null || !OracleData.HasAssets, "no oracle output or assets");
        var root = doc!.RootElement;
        int maxFrames = root.GetProperty("maxFrames").GetInt32();
        var rows = root.GetProperty("levels").EnumerateArray().Where(r => !r.TryGetProperty("error", out _)).Select(r => r.Clone()).ToList();
        var masks = MasksOnce();
        var failures = new ConcurrentBag<string>();
        // levels are independent: a style manager per worker (it caches, it is not thread safe)
        Parallel.ForEach(Partitioner.Create(0, rows.Count, Math.Max(1, rows.Count / (Environment.ProcessorCount * 4))),
            () => new StyleManager(OracleData.Io),
            (range, _, styles) =>
            {
                for (int i = range.Item1; i < range.Item2; i++) Check(rows[i], styles, masks, maxFrames, replays, failures);
                return styles;
            }, _ => { });
        return failures.OrderBy(x => x, StringComparer.Ordinal).ToList();
    }

    static void Check(JsonElement row, StyleManager styles, Masks masks, int maxFrames, bool replays, ConcurrentBag<string> failures)
    {
        string id = row.GetProperty("id").GetString()!;
        try
        {
            var data = LevelBuilder.ParseLevel(OracleData.Io.Text(row.GetProperty("url").GetString()!)!);
            var level = LevelBuilder.Build(data, styles, id);
            var game = new LemGame(level, masks);
            ParsedReplay? replay = null;
            if (replays) replay = Replay.Parse(File.ReadAllText(Path.Combine(OracleData.RepoRoot, "web", row.GetProperty("nxrp").GetString()!)));
            var r = SimState.Run(game, level, replay, maxFrames, replays);
            if (r.BuildTerrain != row.GetProperty("buildTerrain").GetString()) { failures.Add($"{id}: built terrain differs"); return; }
            if (r.Build != row.GetProperty("build").GetString()) { failures.Add($"{id}: frame 0 (after start) differs"); return; }
            var blocks = row.GetProperty("blocks").EnumerateArray().Select(b => b.GetString()!).ToList();
            for (int b = 0; b < Math.Min(blocks.Count, r.Blocks.Count); b++)
                if (blocks[b] != r.Blocks[b]) { failures.Add($"{id}: first difference in frames {b * SimState.Block + 1}..{(b + 1) * SimState.Block}"); return; }
            if (r.Frames != row.GetProperty("frames").GetInt32()) failures.Add($"{id}: ended at frame {r.Frames}, web {row.GetProperty("frames").GetInt32()}");
        }
        catch (Exception e) { failures.Add($"{id}: {e.GetType().Name} {e.Message} {e.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}"); }
    }

    [Fact]
    public void NoInputRunsMatchTheWebEngine()
    {
        var failures = Compare(Environment.GetEnvironmentVariable("SIM_NOINPUT") ?? "sim/noinput.json", replays: false);
        Assert.True(failures.Count == 0, $"{failures.Count} levels differ:\n" + string.Join("\n", failures.Take(40)));
    }

    [Fact]
    public void SolutionsReplayLikeTheWebEngine()
    {
        var failures = Compare("sim/solutions.json", replays: true);
        Assert.True(failures.Count == 0, $"{failures.Count} solutions differ:\n" + string.Join("\n", failures.Take(40)));
    }

    // oracle/fuzz.js: a script of player calls by frame, on a fresh game, hashed as sim.js does;
    // the replay the game recorded must serialize to the same .nxrp text.
    [Fact]
    public void FuzzedInputMatchesTheWebEngine()
    {
        using var doc = OracleData.Load(Environment.GetEnvironmentVariable("SIM_FUZZ") ?? "sim/fuzz.json");
        Assert.SkipWhen(doc == null || !OracleData.HasAssets, "no oracle output or assets");
        var root = doc!.RootElement;
        int maxFrames = root.GetProperty("maxFrames").GetInt32();
        var rows = root.GetProperty("levels").EnumerateArray().Where(r => !r.TryGetProperty("error", out _)).Select(r => r.Clone()).ToList();
        var masks = MasksOnce();
        var failures = new ConcurrentBag<string>();
        Parallel.ForEach(Partitioner.Create(0, rows.Count, Math.Max(1, rows.Count / (Environment.ProcessorCount * 4))),
            () => new StyleManager(OracleData.Io),
            (range, _, styles) =>
            {
                for (int i = range.Item1; i < range.Item2; i++) CheckFuzz(rows[i], styles, masks, maxFrames, failures);
                return styles;
            }, _ => { });
        var list = failures.OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.True(list.Count == 0, $"{list.Count}/{rows.Count} fuzz runs differ:\n" + string.Join("\n", list.Take(40)));
    }

    static void CheckFuzz(JsonElement row, StyleManager styles, Masks masks, int maxFrames, ConcurrentBag<string> failures)
    {
        string id = row.GetProperty("id").GetString()! + "#" + row.GetProperty("run").GetInt32();
        try
        {
            var data = LevelBuilder.ParseLevel(OracleData.Io.Text(row.GetProperty("url").GetString()!)!);
            var level = LevelBuilder.Build(data, styles, row.GetProperty("id").GetString()!);
            var game = new LemGame(level, masks);
            var script = row.GetProperty("script").EnumerateArray().ToList();
            game.Start();
            if (SimState.HashFrame(game, true).Hex() != row.GetProperty("build").GetString()) { failures.Add($"{id}: frame 0 differs"); return; }
            var blocks = row.GetProperty("blocks").EnumerateArray().Select(b => b.GetString()!).ToList();
            var block = new StateHash();
            int f = 0, k = 0, b = 0;
            while (f < maxFrames && !game.GameFinished && !game.StateIsUnplayable)
            {
                while (k < script.Count && script[k].GetProperty("f").GetInt32() == game.CurrentIteration)
                {
                    var s = script[k++];
                    switch (s.GetProperty("op").GetString())
                    {
                        case "assign": game.AssignSkillTo(game.Lemmings[s.GetProperty("lem").GetInt32()], s.GetProperty("skill").GetString()); break;
                        case "si": game.AdjustSpawnInterval(s.GetProperty("si").GetInt32()); break;
                        case "nuke": game.Nuke(); break;
                    }
                }
                game.Update();
                f++;
                var h = SimState.HashFrame(game, f % SimState.TerrainEvery == 0);
                block.Word(h.H1); block.Word(h.H2);
                if (f % SimState.Block == 0)
                {
                    if (b >= blocks.Count || block.Hex() != blocks[b]) { failures.Add($"{id}: first difference in frames {b * SimState.Block + 1}..{(b + 1) * SimState.Block}"); return; }
                    b++; block = new StateHash();
                }
            }
            var last = SimState.HashFrame(game, true);
            block.Word(last.H1); block.Word(last.H2);
            if (b >= blocks.Count || block.Hex() != blocks[b]) { failures.Add($"{id}: last block (frames {b * SimState.Block + 1}..{f}) differs"); return; }
            if (f != row.GetProperty("frames").GetInt32()) { failures.Add($"{id}: ended at frame {f}, web {row.GetProperty("frames").GetInt32()}"); return; }
            if (Replay.Serialize(game) != row.GetProperty("nxrp").GetString()) failures.Add($"{id}: .nxrp text differs");
        }
        catch (Exception e) { failures.Add($"{id}: {e.GetType().Name} {e.Message} {e.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}"); }
    }
}
