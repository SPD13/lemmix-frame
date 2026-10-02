using System.Collections.Concurrent;
using System.Text.Json;
using Lemmix.Engine;
using Lemmix.Oracle;
using Lemmix.Render;
using Lemmix.Tests.Oracle;

namespace Lemmix.Tests.Render;

// oracle/portals.js: the openings (smoothing off and on, with what they carve), the pools and
// the wave stacks of the levels bridge.js samples, identical to web/3d/js/portals.js.
public class PortalsTests
{
    static byte[] DepthMapOf(Level level)
    {
        var mask = level.GroundMask.GroundMask;
        var d = new byte[level.Width * level.Height];
        for (int i = 0; i < d.Length; i++) d[i] = mask[i] != 0 ? Portals.DEPTH_TERRAIN : Portals.DEPTH_EMPTY;
        return d;
    }

    static string PortalsHash(Level level, PortalObjectData data, bool smooth)
    {
        var depthMap = DepthMapOf(level);
        var portals = Portals.BuildPortals(level, data, null, depthMap, Portals.OBJECT_Z, smooth);
        var h = new StateHash();
        h.Word(portals.Count);
        foreach (var p in portals)
        {
            h.Word(p.Index); h.Word(p.ObjectId); h.Str(p.Shape);
            RenderHash.Geometry(h, p.Geometry);
            RenderHash.F64(h, p.OriginX); RenderHash.F64(h, p.OriginY); RenderHash.F64(h, p.SfxX); RenderHash.F64(h, p.SfxY);
            h.Word(p.Carved);
            RenderHash.Frame(h, p.ClosedFrame);
            if (p.Rebuild != null) { RenderHash.F64(h, p.Rebuild.Depth); RenderHash.Bytes(h, p.Rebuild.Opening); }
            else h.Null();
            if (p.Hatch == null) { h.Null(); continue; }
            var k = p.Hatch;
            foreach (double v in new[] { k.LeftX, k.RightX, k.Y, k.HalfWidth, k.Depth }) RenderHash.F64(h, v);
            h.Word(k.DoorRows.Count);
            foreach (var r in k.DoorRows) { h.Word(r.Y); h.Word(r.Min); h.Word(r.Max); }
            h.Word(p.Openness!.Length);
            foreach (double v in p.Openness) RenderHash.F64(h, v);
            foreach (int sign in new[] { 1, -1 }) RenderHash.Geometry(h, Portals.BuildFlapGeometry(p.ClosedFrame, k.DoorRows, k.HalfWidth, k.Depth, sign));
        }
        h.Bytes(depthMap);
        return h.Hex();
    }

    static string WaterHash(Level level, PortalObjectData data)
    {
        var h = new StateHash();
        var pools = Portals.WaterObjectsFrom(level, data, null);
        h.Word(pools.Count);
        foreach (var p in pools)
        {
            h.Word(p.Index); h.Word(p.Y0); h.Word(p.Colour); RenderHash.F64(h, p.Z0); RenderHash.F64(h, p.Z1);
            h.Word(p.Runs.Count);
            foreach (var r in p.Runs) { h.Word(r.X0); h.Word(r.X1); h.Word(r.Floor); }
            RenderHash.Geometry(h, Portals.BuildPoolGeometry(p.Runs, p.Y0, p.Z0, p.Z1));
        }
        return h.Hex();
    }

    static string StacksHash(Level level, PortalObjectData data)
    {
        var cache = new SpriteGeometryCache();
        var h = new StateHash();
        var stacks = Portals.StackedObjectsFrom(level, data, null);
        h.Word(stacks.Count);
        Frame? Cut(WaveStack stack, Frame? frame) => frame == null ? null
            : SpriteBuild.ClipFrameToBounds(frame, stack.MapObject.X, stack.MapObject.Y, stack.FlipY, level.Width, level.Height);
        foreach (var stack in stacks)
        {
            h.Word(stack.Index); h.Bool(stack.FlipY); h.Bytes(stack.Phases);
            var obj = stack.MapObject; var g = obj.Gadget;
            var first = Cut(stack, obj.Frames[0]);
            RenderHash.Frame(h, first);
            if (first != null)
                foreach (bool smooth in new[] { false, true })
                    RenderHash.Geometry(h, (smooth ? cache.ForFrameBlended(null, first, null) : cache.ForFrame(first)).Geometry);
            int saved = g.CurrentFrame, count = g.FrameCount != 0 ? g.FrameCount : 1;
            for (int t = 0; t < 3; t++)
            {
                g.CurrentFrame = (saved + t) % count;
                var shown = new List<Frame?>();
                for (int k = 0; k < stack.Phases.Length; k++) shown.Add(Cut(stack, Portals.FrameAtPhase(obj, t, stack.Phases[k])));
                for (int k = 0; k < shown.Count; k++)
                {
                    var frame = shown[k];
                    RenderHash.Frame(h, frame);
                    if (frame == null) continue;
                    foreach (bool smooth in new[] { false, true })
                    {
                        var entry = smooth ? cache.ForFrameBlended(k > 0 ? shown[k - 1] : null, frame, k + 1 < shown.Count ? shown[k + 1] : null) : cache.ForFrame(frame);
                        RenderHash.Geometry(h, entry.Geometry);
                        RenderHash.F64(h, (double)(obj.X + frame.OffsetX));
                        RenderHash.F64(h, (double)(obj.Y + frame.OffsetY + (stack.FlipY ? entry.H : 0)));
                        RenderHash.F64(h, Portals.WAVE_FRONT_Z - (k + 1) * SpriteBuild.SPRITE_DEPTH);
                    }
                }
            }
            g.CurrentFrame = saved;
        }
        return h.Hex();
    }

    [Fact]
    public void OpeningsPoolsAndStacksMatchTheWebLayer()
    {
        using var doc = OracleData.Load("portals.json");
        Assert.SkipWhen(doc == null || !OracleData.HasAssets, "no oracle output or assets");
        Assert.Equal(Portals.OBJECT_Z, doc!.RootElement.GetProperty("objectZ").GetDouble());
        Assert.Equal(Portals.WAVE_FRONT_Z, doc.RootElement.GetProperty("waveFrontZ").GetDouble());
        var rows = doc.RootElement.GetProperty("levels").EnumerateArray().Where(r => !r.TryGetProperty("error", out _)).Select(r => r.Clone()).ToList();
        var failures = new ConcurrentBag<string>();
        Parallel.ForEach(rows, () => new StyleManager(OracleData.Io), (row, _, styles) =>
        {
            string id = row.GetProperty("id").GetString()!;
            try
            {
                var level = RenderHash.Build(row, styles);
                var data = PortalObjectData.For(level);
                if (PortalsHash(level, data, false) != row.GetProperty("portals").GetString()) failures.Add(id + ": portals");
                if (PortalsHash(level, data, true) != row.GetProperty("portalsSmooth").GetString()) failures.Add(id + ": portals (smooth)");
                if (WaterHash(level, data) != row.GetProperty("water").GetString()) failures.Add(id + ": water");
                if (StacksHash(level, data) != row.GetProperty("stacks").GetString()) failures.Add(id + ": stacks");
            }
            catch (Exception e) { failures.Add($"{id}: {e}"); }
            return styles;
        }, _ => { });
        Assert.True(failures.IsEmpty, $"{rows.Count} levels; {failures.Count} differ:\n" + string.Join("\n", failures.Take(25)));
    }
}
