using System.Collections.Concurrent;
using System.Text.Json;
using Lemmix.Engine;
using Lemmix.Oracle;
using Lemmix.Render;
using Lemmix.Tests.Oracle;

namespace Lemmix.Tests.Render;

// oracle/bridge.js: the sprite geometry (web/3d/js/bridge.js) of every gadget frame of the
// sampled levels, of the default lemming sprites and of the gfx/mask pictures, plus each
// object's capture and placement and a particle sync - identical buffers, float32 for float32.
public class BridgeTests
{
    static readonly double[] Softness = { 0.5, 1 };

    static void Entry(StateHash h, SpriteEntry e)
    {
        h.Word(e.W); h.Word(e.H);
        RenderHash.Geometry(h, e.Geometry);
        var t = e.Material.Map;
        h.Word(t.Width); h.Word(t.Height); h.Bytes(t.Rgba);
    }

    // oracle/bridge.js frameHashes
    public static string FrameHashes(SpriteGeometryCache cache, IReadOnlyList<Frame> frames, int k)
    {
        var f = frames[k];
        Frame? At(int i) => i >= 0 && i < frames.Count ? frames[i] : null;
        var a = new StateHash();
        Entry(a, cache.ForFrame(f));
        a.Bytes(cache.FlatMaterialFor(f).Map.Rgba);
        var b = new StateHash();
        foreach (double s in Softness)
        {
            cache.SetColorBlend(s);
            var t = cache.BlendedMaterialFor(f).Map;
            b.Word(t.Width); b.Word(t.Height); b.Bytes(t.Rgba);
        }
        cache.SetColorBlend(0);
        var c = new StateHash();
        var parts = SpriteBuild.SpriteBodyParts(f.Mask, f.Width, f.Height);
        if (parts == null) c.Null();
        else
        {
            c.Bytes(parts.Body); c.Word(parts.Loose != null ? 1 : 0); if (parts.Loose != null) c.Bytes(parts.Loose);
            c.Word(parts.Parts); c.Word(parts.LooseCount);
        }
        foreach (var (p, n) in new[] { (At(k - 1), At(k + 1)), (null, null), (At(k + 1), (Frame?)null) })
            RenderHash.Geometry(c, cache.ForFrameBlended(p, f, n).Geometry);
        return a.Hex() + b.Hex() + c.Hex();
    }

    static string CaptureHash(Level level, SpriteGeometryCache cache)
    {
        var cap = new SpriteCapture();
        cap.SetBounds(level.Width, level.Height);
        cap.Begin();
        foreach (var obj in level.Objects)
        {
            var g = obj.Gadget;
            if (g.EffectBase == "NONE" && g.Effect == "NONE" && g.Animations.Count == 0) continue;
            cap.DrawFrameFlags(g.Render(), obj);
        }
        var h = new StateHash();
        h.Word(cap.Items.Count);
        double ZFor(int layer) => layer < -1 ? -1.4 : layer < 0 ? 5.9 : layer > 0 ? 16.25 : 6.2;
        var placed = BillboardPool.Place(cap.Items, cache, ZFor, false, false);
        for (int i = 0; i < cap.Items.Count; i++)
        {
            var item = cap.Items[i];
            h.Bool(item.Off); h.Word(item.Layer); h.Bool(item.OneWay); h.Bool(item.FlipY);
            RenderHash.Frame(h, item.Frame);
            if (item.Off) continue;
            var p = placed[i];
            RenderHash.F64(h, p.X); RenderHash.F64(h, p.Y); RenderHash.F64(h, p.Z);
        }
        return h.Hex();
    }

    [Fact]
    public void GadgetFramesBuildTheSameGeometry()
    {
        using var doc = OracleData.Load("bridge.json");
        Assert.SkipWhen(doc == null || !OracleData.HasAssets, "no oracle output or assets");
        var rows = doc!.RootElement.GetProperty("levels").EnumerateArray().Where(r => !r.TryGetProperty("error", out _)).Select(r => r.Clone()).ToList();
        _ = RenderHash.Masks;
        var failures = new ConcurrentBag<string>();
        int objects = 0;
        Parallel.ForEach(rows, () => new StyleManager(OracleData.Io), (row, _, styles) =>
        {
            string id = row.GetProperty("id").GetString()!;
            try
            {
                var level = RenderHash.Build(row, styles);
                var cache = new SpriteGeometryCache();
                var want = row.GetProperty("objects").EnumerateArray().Select(e => e.GetString()!).ToList();
                if (want.Count != level.Objects.Count) { failures.Add($"{id}: {level.Objects.Count} objects, oracle {want.Count}"); return styles; }
                for (int i = 0; i < want.Count; i++)
                {
                    var frames = level.Objects[i].Frames;
                    string got = string.Join(",", Enumerable.Range(0, frames.Count).Select(k => FrameHashes(cache, frames, k)));
                    Interlocked.Increment(ref objects);
                    if (got != want[i])
                    {
                        var g = got.Split(','); var w = want[i].Split(',');
                        int k = Enumerable.Range(0, Math.Min(g.Length, w.Length)).FirstOrDefault(j => g[j] != w[j], -1);
                        string part = k < 0 ? "frame count" : new[] { "relief/texture", "blend", "parts/slices" }[Enumerable.Range(0, 3).First(p => g[k].Substring(p * 16, 16) != w[k].Substring(p * 16, 16))];
                        failures.Add($"{id}: object {i} ({level.Objects[i].Gadget.Meta.Gs}:{level.Objects[i].Gadget.Meta.Piece}) frame {k}: {part}");
                    }
                }
                if (CaptureHash(level, cache) != row.GetProperty("capture").GetString()) failures.Add($"{id}: capture");
            }
            catch (Exception e) { failures.Add($"{id}: {e}"); }
            return styles;
        }, _ => { });
        Assert.True(failures.IsEmpty, $"{rows.Count} levels, {objects} objects; {failures.Count} differ:\n" + string.Join("\n", failures.Take(20)));
    }

    [Fact]
    public void LemmingFramesAndMasksBuildTheSameGeometry()
    {
        using var doc = OracleData.Load("bridge.json");
        Assert.SkipWhen(doc == null || !OracleData.HasAssets, "no oracle output or assets");
        var root = doc!.RootElement;
        var failures = new List<string>();
        var cache = new SpriteGeometryCache();
        foreach (var anim in root.GetProperty("lemmings").EnumerateArray())
        {
            string name = anim.GetProperty("name").GetString()!;
            int count = anim.GetProperty("frameCount").GetInt32();
            var image = OracleData.Io.Image("neolemmix/styles/default/lemmings/" + name + ".png")!;
            int w = image.Width >> 1, h = image.Height / count;
            var frames = new List<Frame>();
            foreach (int x0 in new[] { w, 0 })
                for (int i = 0; i < count; i++) frames.Add(Frame.FromBitmap(image.Crop(x0, i * h, w, h), 0, 0));
            var want = anim.GetProperty("hashes").EnumerateArray().Select(e => e.GetString()!).ToList();
            for (int k = 0; k < frames.Count; k++)
                if (FrameHashes(cache, frames, k) != want[k]) { failures.Add($"lemming {name} frame {k}"); break; }
        }
        foreach (var m in root.GetProperty("masks").EnumerateArray())
        {
            string file = m.GetProperty("file").GetString()!;
            var bmp = OracleData.Io.Image("neolemmix/gfx/mask/" + file)!;
            var bits = new sbyte[bmp.Width * bmp.Height];
            for (int i = 0; i < bits.Length; i++) bits[i] = (sbyte)(bmp.Data[i * 4 + 3] != 0 ? 1 : 0);
            var mask = new SpriteMask(bmp.Width, bmp.Height, bits);
            var h = new StateHash();
            Entry(h, cache.ForMask(mask));
            var cut = SpriteBuild.ClipFrameToBounds(mask, -(mask.Width >> 2), -(mask.Height >> 2), false, mask.Width, mask.Height);
            if (cut == null) h.Null();
            else { h.Word(cut.Width); h.Word(cut.Height); h.Word(cut.OffsetX); h.Word(cut.OffsetY); h.Bytes(cut.Bits); Entry(h, cache.ForMask(cut)); }
            if (h.Hex() != m.GetProperty("hash").GetString()) failures.Add("mask " + file);
        }
        // ParticleCloud.sync over the oracle's generated particles
        var flat = new List<double>();
        uint s = 12345;
        uint Next() { s = unchecked(s * 1103515245u + 12345u); return s; }
        for (int i = 0; i < 500; i++)
        {
            double a = Next() % 2000 - 300.0, b = Next() % 900 - 100.0, r = Next() & 255, g = Next() & 255, bl = Next() & 255;
            flat.Add(a); flat.Add(b); flat.Add(r); flat.Add(g); flat.Add(bl);
        }
        var (pos, col) = SpriteBuild.ParticleBuffers(flat, 8.0);
        var ph = new StateHash(); RenderHash.Floats(ph, pos); RenderHash.Floats(ph, col);
        if (ph.Hex() != root.GetProperty("particles").GetString()) failures.Add("particles");
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }
}
