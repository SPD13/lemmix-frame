using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json;
using Lemmix.Engine;
using Lemmix.Oracle;
using Lemmix.Render;
using Lemmix.Tests.Oracle;

namespace Lemmix.Tests.Render;

// oracle/terrain.js: depth.js, terrain.js and decals.js under node on ~60 levels. Every
// per-pixel map, every chunk's geometry (float32 bytes) for every combination of the effect
// switches, the decal skins and the level texture must hash the same; then a scripted run
// digs and builds while the mesh follows Level.GroundChanged and FlushDirty re-meshes with
// its budget, a rewind goes through LoadExtra + Resync, and every rebuilt chunk must match.
public class TerrainTests
{
    static string ProfileDir => Path.Combine(OracleData.RepoRoot, "web", "3d", "profiles");

    sealed record Combo(string Name, bool Emboss, bool Smooth, bool SmoothTerrain, string ColorBlend)
    {
        public double Softness => ColorBlend switch { "off" => 0, "soft" => 0.5, _ => 1 };
    }

    // ---------------------------------------------------------------- hashing (terrain.js hashGeom and friends)

    static void HashGeom(StateHash h, ChunkGeometry? g)
    {
        if (g == null) { h.Null(); return; }
        h.Word(3); h.Bytes(MemoryMarshal.AsBytes(g.Positions.AsSpan()));
        if (g.Colors == null) h.Null(); else { h.Word(3); h.Bytes(MemoryMarshal.AsBytes(g.Colors.AsSpan())); }
        h.Word(2); h.Bytes(MemoryMarshal.AsBytes(g.Uvs.AsSpan()));
        if (g.Normals == null) h.Null(); else { h.Word(3); h.Bytes(MemoryMarshal.AsBytes(g.Normals.AsSpan())); }
        if (g.Index32) { h.Word(4); h.Bytes(MemoryMarshal.AsBytes(g.Indices.AsSpan())); }
        else
        {
            var u16 = new ushort[g.Indices.Length];
            for (int i = 0; i < u16.Length; i++) u16[i] = (ushort)g.Indices[i];
            h.Word(2); h.Bytes(u16);
        }
        h.Word(g.Groups.Length);
        foreach (var gr in g.Groups) { h.Word(gr.Start); h.Word(gr.Count); h.Word(gr.MaterialIndex); }
    }

    static void HashChunks(StateHash h, TerrainMesh t) { h.Word(t.ChunkMeshes.Length); foreach (var g in t.ChunkMeshes) HashGeom(h, g); }
    static void HashDecalChunks(StateHash h, TerrainMesh t) { h.Word(t.DecalMeshes.Length); foreach (var g in t.DecalMeshes) HashGeom(h, g); }
    static void HashMaps(StateHash h, TerrainMesh t)
    {
        h.Bytes(t.DepthMap);
        h.Bytes(t.Relief);
        if (t.Blend != null) h.Bytes(t.Blend.Slot); else h.Null();
        if (t.Color != null) h.Bytes(t.Color); else h.Null();
    }
    static void HashBlend(StateHash h, BlendMap b)
    {
        h.Bytes(b.Slot);
        h.Word(b.Donors.Count);
        foreach (var p in b.Donors)
        {
            h.Word(p.Length);
            foreach (var d in p) { h.Word(d.Index); h.Word(d.R); h.Word(d.G); h.Word(d.B); }
        }
    }
    static string Hex(Action<StateHash> fn) { var h = new StateHash(); fn(h); return h.Hex(); }

    // ---------------------------------------------------------------- the level and the mesh

    static readonly string[] SkillsInOrder = LevelBuilder.Skills;

    static (Level, LemGame) BuildLevel(StyleManager styles, Masks masks, string id, string url, string[]? simSkills)
    {
        var data = LevelBuilder.ParseLevel(OracleData.Io.Text(url)!);
        if (simSkills != null)
            data.Skills = SkillsInOrder.Where(simSkills.Contains).Select(s => new KeyValuePair<string, int>(s, 30)).ToList();
        var level = LevelBuilder.Build(data, styles, id);
        var game = new LemGame(level, masks);
        game.Start();
        return (level, game);
    }

    static (TerrainMesh, TerrainDecals?) MakeTerrain(Level level, GroundData gd, DepthProfile profile, Combo combo)
    {
        var depthMap = Depth.BuildDepthMap(level, gd, profile);
        var pieceMap = Depth.BuildPieceMap(level, gd);
        var reliefMap = Depth.BuildReliefMap(level, pieceMap, profile, combo.Emboss, gd);
        var blendMap = Depth.BuildBlendMap(level, pieceMap, profile, gd);
        var colorMap = Depth.BuildColorBlendMap(level, pieceMap, profile, combo.ColorBlend != "off", gd);
        var terrain = new TerrainMesh(level, depthMap, reliefMap, blendMap, colorMap, combo.Softness);
        if (combo.Smooth) terrain.SetSmooth(true);
        if (combo.SmoothTerrain) terrain.SetSmoothTerrain(true);
        var decals = TerrainDecals.ForLevel(level, level.Physics);
        if (decals != null) terrain.SetDecals(decals);
        return (terrain, decals);
    }

    // ---------------------------------------------------------------- the oracle file

    sealed class Oracle
    {
        public required JsonElement Root;
        public required List<Combo> Combos;
        public required List<JsonElement> Levels;
        public int Get(string k) => Root.GetProperty(k).GetInt32();
    }

    static Oracle? _oracle;
    static readonly object OracleLock = new();
    static Oracle? LoadOracle()
    {
        lock (OracleLock)
        {
            if (_oracle != null) return _oracle;
            var doc = OracleData.Load("terrain.json");
            if (doc == null) return null;
            var root = doc.RootElement.Clone();
            var combos = root.GetProperty("combos").EnumerateArray().Select(c => new Combo(c.GetProperty("name").GetString()!,
                c.GetProperty("emboss").GetBoolean(), c.GetProperty("smooth").GetBoolean(), c.GetProperty("smoothTerrain").GetBoolean(),
                c.GetProperty("colorBlend").GetString()!)).ToList();
            var levels = root.GetProperty("levels").EnumerateArray().Where(r => !r.TryGetProperty("error", out _)).ToList();
            return _oracle = new Oracle { Root = root, Combos = combos, Levels = levels };
        }
    }

    static void ForEachLevel(Oracle o, Func<JsonElement, StyleManager, Masks, IEnumerable<string>> check, string what)
    {
        var masks = Masks.Load(OracleData.Io);
        var failures = new ConcurrentBag<string>();
        Parallel.ForEach(o.Levels, () => new StyleManager(OracleData.Io), (row, _, styles) =>
        {
            string id = row.GetProperty("id").GetString()!;
            try { foreach (var f in check(row, styles, masks)) failures.Add(id + ": " + f); }
            catch (Exception e) { failures.Add($"{id}: {e.GetType().Name} {e.Message} {e.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}"); }
            return styles;
        }, _ => { });
        var list = failures.OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.True(list.Count == 0, $"{what}: {list.Count} differences over {o.Levels.Count} levels:\n" + string.Join("\n", list.Take(60)));
    }

    // ---------------------------------------------------------------- static: maps and every combination

    [Fact]
    public void MapsAndChunkGeometryMatchTheWeb()
    {
        var o = LoadOracle();
        Assert.SkipWhen(o == null || !OracleData.HasAssets, "no oracle output or assets");
        string paintCombo = o!.Root.GetProperty("paintCombo").GetString()!;
        string? dump = Environment.GetEnvironmentVariable("TERRAIN_DUMP"); // "<level id>|<combo>": per-chunk hashes to stdout
        ForEachLevel(o, (row, styles, masks) => StaticLevel(o, row, styles, masks, paintCombo, dump), "static");
    }

    static IEnumerable<string> StaticLevel(Oracle o, JsonElement row, StyleManager styles, Masks masks, string paintCombo, string? dump)
    {
        var fails = new List<string>();
        string id = row.GetProperty("id").GetString()!;
        var (level, _) = BuildLevel(styles, masks, id, row.GetProperty("url").GetString()!, null);
        var gd = GroundData.FromLevel(level);
        var files = DepthProfile.FilesForGroundData(gd);
        var wantFiles = row.GetProperty("profiles").EnumerateArray().Select(e => e.GetString()!).ToList();
        if (!files.SequenceEqual(wantFiles)) fails.Add($"profile files [{string.Join(",", files)}], web [{string.Join(",", wantFiles)}]");
        var profile = DepthProfile.Load(ProfileDir, gd);

        var maps = row.GetProperty("maps");
        var pieceMap = Depth.BuildPieceMap(level, gd);
        void Check(string name, string got, string want) { if (got != want) fails.Add($"{name} {got}, web {want}"); }
        Check("depth", Hex(h => h.Bytes(Depth.BuildDepthMap(level, gd, profile))), maps.GetProperty("depth").GetString()!);
        Check("piece", Hex(h => h.Bytes(pieceMap)), maps.GetProperty("piece").GetString()!);
        for (int k = 0; k < 2; k++)
            Check("relief" + k, Hex(h => h.Bytes(Depth.BuildReliefMap(level, pieceMap, profile, k == 1, gd))), maps.GetProperty("relief")[k].GetString()!);
        var blend = Depth.BuildBlendMap(level, pieceMap, profile, gd);
        Check("blend", Hex(h => HashBlend(h, blend)), maps.GetProperty("blend").GetString()!);
        for (int k = 0; k < 2; k++)
            Check("color" + k, Hex(h => h.Bytes(Depth.BuildColorBlendMap(level, pieceMap, profile, k == 1, gd))), maps.GetProperty("color")[k].GetString()!);

        var combos = row.GetProperty("combos");
        foreach (var combo in o.Combos)
        {
            var want = combos.GetProperty(combo.Name);
            var (terrain, _) = MakeTerrain(level, gd, profile, combo);
            try
            {
                int verts = terrain.ChunkMeshes.Sum(g => g?.VertexCount ?? 0);
                string geo = Hex(h => HashChunks(h, terrain)), dec = Hex(h => HashDecalChunks(h, terrain)), tex = Hex(h => h.Bytes(terrain.TexData));
                if (geo != want[0].GetString() || verts != want[3].GetInt32()) fails.Add($"{combo.Name} chunks {geo} ({verts} verts), web {want[0].GetString()} ({want[3].GetInt32()})");
                if (dec != want[1].GetString()) fails.Add($"{combo.Name} decal skins differ");
                if (tex != want[2].GetString()) fails.Add($"{combo.Name} texture differs");
                if (dump == id + "|" + combo.Name)
                    for (int i = 0; i < terrain.ChunkMeshes.Length; i++)
                        Console.WriteLine($"{i} {Hex(h => HashGeom(h, terrain.ChunkMeshes[i]))} {terrain.ChunkMeshes[i]?.VertexCount ?? 0}");
                if (combo.Name == paintCombo)
                {
                    terrain.SetPhysicsPaint(level.Physics, PM.ONEWAYLEFT);
                    string on = Hex(h => { h.Bytes(terrain.TexData); HashChunks(h, terrain); });
                    terrain.SetPhysicsPaint(null);
                    string off = Hex(h => { h.Bytes(terrain.TexData); HashChunks(h, terrain); });
                    var paint = row.GetProperty("paint");
                    if (on != paint[0].GetString()) fails.Add("clear-physics paint differs");
                    if (off != paint[1].GetString()) fails.Add("paint back to the picture differs");
                }
            }
            finally { terrain.Dispose(); }
        }
        return fails;
    }

    // ---------------------------------------------------------------- the scripted run

    [Fact]
    public void DigsBuildsAndRewindsRemeshTheSame()
    {
        var o = LoadOracle();
        Assert.SkipWhen(o == null || !OracleData.HasAssets, "no oracle output or assets");
        ForEachLevel(o!, (row, styles, masks) => SimLevel(o!, row, styles, masks), "sim");
    }

    static IEnumerable<string> SimLevel(Oracle o, JsonElement row, StyleManager styles, Masks masks)
    {
        var fails = new List<string>();
        string id = row.GetProperty("id").GetString()!, url = row.GetProperty("url").GetString()!;
        var simSkills = o.Root.GetProperty("simSkills").EnumerateArray().Select(e => e.GetString()!).ToArray();
        int simFrames = o.Get("simFrames"), saveAt = o.Get("saveAt"), afterFrames = o.Get("afterFrames"), blockN = o.Get("block"),
            decalEvery = o.Get("decalEvery"), texEvery = o.Get("texEvery");
        var sim = row.GetProperty("sim");
        var script = sim.GetProperty("script").EnumerateArray()
            .Select(s => (Phase: s[0].GetInt32(), Frame: s[1].GetInt32(), Lem: s[2].GetInt32(), Skill: s[3].GetString()!)).ToList();
        foreach (var run in sim.GetProperty("runs").EnumerateObject())
        {
            var combo = o.Combos.First(c => c.Name == run.Name);
            var want = run.Value;
            var (level, game) = BuildLevel(styles, masks, id, url, simSkills);
            var gd = GroundData.FromLevel(level);
            var profile = DepthProfile.Load(ProfileDir, gd);
            var (terrain, decals) = MakeTerrain(level, gd, profile, combo);
            try
            {
                var rebuilt = new List<int>();
                terrain.ChunkRebuilt += rebuilt.Add;
                var items = new List<DecalItem>();
                var blocks = new List<string>();
                var block = new StateHash();
                int tick = 0;
                game.AdjustSpawnInterval(4);
                void Step(int phase)
                {
                    foreach (var s in script)
                        if (s.Phase == phase && s.Frame == game.CurrentIteration)
                            if (!game.AssignSkillTo(game.Lemmings[s.Lem], s.Skill)) throw new InvalidOperationException($"assignment refused at {s.Frame}");
                    game.Update();
                    tick++;
                    if (decals != null) decals.Paint(TerrainDecals.ItemsFor(level, items), false);
                    rebuilt.Clear();
                    terrain.FlushDirty();
                    block.Word(game.CurrentIteration);
                    block.Word(rebuilt.Count);
                    foreach (int cid in rebuilt) { block.Word(cid); HashGeom(block, terrain.ChunkMeshes[cid]); HashGeom(block, terrain.DecalMeshes[cid]); }
                    block.Word(terrain.DirtyChunks.Count);
                    if (decals != null && tick % decalEvery == 0) block.Bytes(decals.Data);
                    if (tick % texEvery == 0) { block.Bytes(terrain.TexData); HashMaps(block, terrain); }
                    if (tick % blockN == 0) { blocks.Add(block.Hex()); block = new StateHash(); }
                }
                SavedState? saved = null;
                for (int f = 0; f < simFrames && !game.GameFinished && !game.StateIsUnplayable; f++)
                {
                    if (game.CurrentIteration == saveAt) { saved = game.SaveState(); terrain.SaveExtra(saved); }
                    Step(0);
                }
                int ticksBefore = tick; // the oracle's `frames`: the ticks before the rewind
                string? rewind = null;
                if (saved != null)
                {
                    game.LoadState(saved);
                    terrain.LoadExtra(saved);
                    rebuilt.Clear();
                    terrain.Resync();
                    rewind = Hex(h =>
                    {
                        h.Word(rebuilt.Count); foreach (int cid in rebuilt) h.Word(cid);
                        HashChunks(h, terrain); HashDecalChunks(h, terrain); h.Bytes(terrain.TexData); HashMaps(h, terrain);
                    });
                    for (int f = 0; f < afterFrames && !game.GameFinished && !game.StateIsUnplayable; f++) Step(1);
                }
                blocks.Add(block.Hex());
                string end = Hex(h => { HashChunks(h, terrain); HashDecalChunks(h, terrain); h.Bytes(terrain.TexData); HashMaps(h, terrain); });

                if (ticksBefore != want.GetProperty("frames").GetInt32()) fails.Add($"{run.Name}: {ticksBefore} ticks, web {want.GetProperty("frames").GetInt32()}");
                var wantBlocks = want.GetProperty("blocks").EnumerateArray().Select(e => e.GetString()!).ToList();
                int first = Enumerable.Range(0, Math.Min(blocks.Count, wantBlocks.Count)).FirstOrDefault(i => blocks[i] != wantBlocks[i], -1);
                if (first >= 0 || blocks.Count != wantBlocks.Count) fails.Add($"{run.Name}: block {first} differs ({blocks.Count} blocks, web {wantBlocks.Count})");
                string? wantRewind = want.GetProperty("rewind").ValueKind == JsonValueKind.Null ? null : want.GetProperty("rewind").GetString();
                if (rewind != wantRewind) fails.Add($"{run.Name}: rewind {rewind}, web {wantRewind}");
                if (end != want.GetProperty("end").GetString()) fails.Add($"{run.Name}: end state differs");
            }
            finally { terrain.Dispose(); }
        }
        return fails;
    }
}
