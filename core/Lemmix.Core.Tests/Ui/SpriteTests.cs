using System.Collections.Concurrent;
using System.Text.Json;
using Lemmix.Engine;
using Lemmix.Oracle;
using Lemmix.Tests.Oracle;

namespace Lemmix.Tests.Ui;

// oracle/sprites.js: every sprite set's frames for every action, direction, frame index and
// variant; and the pickup pictures generatePickupIcons paints on a sample of levels.
public class SpriteTests
{
    const int Actions = 35;

    static IEnumerable<int> FrameIndices(SpriteAnim? anim)
    {
        if (anim == null) { yield return 0; yield break; }
        int n = anim.FrameCount;
        for (int i = 0; i < n; i++) yield return i;
        yield return n; yield return n + 1; yield return 2 * n + 3; yield return -1; yield return -n - 2;
    }

    [Fact]
    public void SpriteFramesMatchTheWebEngine()
    {
        using var doc = OracleData.Load("sprites.json");
        Assert.SkipWhen(doc == null || !OracleData.HasAssets, "no oracle output or assets");
        var variants = doc!.RootElement.GetProperty("variants").EnumerateArray().Select(v => v.GetString()!).ToList();
        var failures = new ConcurrentBag<string>();
        var sets = doc.RootElement.GetProperty("sets").EnumerateArray().Select(s => s.Clone()).ToList();
        Parallel.ForEach(sets, row =>
        {
            string name = row.GetProperty("name").GetString()!;
            var sprites = new SpriteSet(OracleData.Io).Load(name);
            var rh = new StateHash();
            foreach (string kind in new[] { "athlete", "zombie", "neutral", "selected" })
            {
                if (!sprites.Recolor.TryGetValue(kind, out var pairs)) { rh.Null(); continue; }
                rh.Word(pairs.Count); foreach (var (a, b) in pairs) { rh.Word(a); rh.Word(b); }
            }
            if (rh.Hex() != row.GetProperty("recolor").GetString()) failures.Add($"{name}: recolour pairs differ");
            var anims = row.GetProperty("anims");
            if (anims.EnumerateObject().Count() != sprites.Anims.Count) failures.Add($"{name}: {sprites.Anims.Count} animations, web {anims.EnumerateObject().Count()}");
            foreach (var p in anims.EnumerateObject())
            {
                var want = p.Value.EnumerateArray().Select(x => x.GetInt32()).ToArray();
                if (!sprites.Anims.TryGetValue(p.Name, out var a)) { failures.Add($"{name}/{p.Name}: missing"); continue; }
                var got = new[] { a.FrameCount, a.FrameDiff, a.Width, a.Height, a.Right.FootX, a.Right.FootY, a.Left.FootX, a.Left.FootY };
                if (!got.SequenceEqual(want)) failures.Add($"{name}/{p.Name}: [{string.Join(",", got)}] web [{string.Join(",", want)}]");
            }
            var frames = row.GetProperty("frames");
            int bad = 0;
            for (int action = 0; action < Actions; action++)
            {
                var anim = sprites.AnimationFor(action);
                foreach (int dx in new[] { 1, -1 })
                    foreach (string v in variants)
                    {
                        var h = new StateHash();
                        foreach (int f in FrameIndices(anim)) UiHash.Frame(h, sprites.Frame(action, dx, f, v));
                        string key = action + "/" + (dx > 0 ? "r" : "l") + "/" + v;
                        if (h.Hex() != frames.GetProperty(key).GetString() && bad++ < 5) failures.Add($"{name}: {key} differs");
                    }
            }
        });
        var list = failures.OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.True(list.Count == 0, $"{list.Count} differences:\n" + string.Join("\n", list.Take(40)));
    }

    [Fact]
    public void PickupIconsMatchTheWebEngine()
    {
        using var doc = OracleData.Load("sprites.json");
        Assert.SkipWhen(doc == null || !OracleData.HasAssets, "no oracle output or assets");
        Masks.Load(OracleData.Io);
        var rows = doc!.RootElement.GetProperty("pickups").EnumerateArray().Where(r => !r.TryGetProperty("error", out _)).Select(r => r.Clone()).ToList();
        Assert.True(rows.Count > 0);
        var spriteSets = new ConcurrentDictionary<string, SpriteSet>();
        var failures = new ConcurrentBag<string>();
        Parallel.ForEach(rows, () => new StyleManager(OracleData.Io), (row, _, styles) =>
        {
            string id = row.GetProperty("id").GetString()!;
            try
            {
                var level = LevelBuilder.Build(LevelBuilder.ParseLevel(OracleData.Io.Text(row.GetProperty("url").GetString()!)!), styles, id);
                string setName = level.Theme.Lemmings is { Length: > 0 } s ? s : "default";
                var sprites = spriteSets.GetOrAdd(setName, n => new SpriteSet(OracleData.Io).Load(n));
                SpriteSet.GeneratePickupIcons(level, sprites, level.Theme);
                var h = new StateHash();
                int pickups = 0;
                var seen = new HashSet<MetaAnimation>(ReferenceEqualityComparer.Instance);
                foreach (var g in level.Gadgets)
                {
                    if (g.EffectBase != "PICKUP") continue;
                    pickups++;
                    var primary = g.Meta.Base.Primary!;
                    if (seen.Add(primary))
                    {
                        h.Word(primary.Frames.Count); h.Word(primary.Width); h.Word(primary.Height);
                        foreach (var f in primary.Frames) UiHash.Bitmap(h, f);
                    }
                    var pa = g.Animations.FirstOrDefault(a => a.Primary);
                    int saved = pa?.Frame ?? 0;
                    UiHash.Frame(h, g.Render());
                    if (pa != null) { pa.Frame = saved - 1; UiHash.Frame(h, g.Render()); pa.Frame = saved; }
                }
                if (pickups != row.GetProperty("pickups").GetInt32()) failures.Add($"{id}: {pickups} pickups, web {row.GetProperty("pickups").GetInt32()}");
                else if (h.Hex() != row.GetProperty("hash").GetString()) failures.Add($"{id}: pickup pictures differ");
            }
            catch (Exception e) { failures.Add($"{id}: {e.GetType().Name} {e.Message} {e.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}"); }
            return styles;
        }, _ => { });
        var list = failures.OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.True(list.Count == 0, $"{list.Count}/{rows.Count} levels differ:\n" + string.Join("\n", list.Take(40)));
    }
}
