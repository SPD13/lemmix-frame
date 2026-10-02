using System.Runtime.InteropServices;
using Lemmix.Engine;
using Lemmix.Util;

namespace Lemmix.Render;

// web/3d/js/decals.js TerrainDecals - the objects drawn on the terrain's face rather than standing
// in front of it (ONLY_ON_TERRAIN gadgets cut to the solid pixels, one-way arrows cut to
// PM_ONEWAY), painted into one level-sized RGBA texture every tick; the terrain gives the pixels
// they can reach a skin a hair in front of its face (TerrainMesh.SetDecals).

/// <summary>One captured draw of the decal layer (a bridge.js SpriteCapture item of layer 1).</summary>
public struct DecalItem
{
    public Frame? Frame;
    public int X, Y;
    public bool FlipY, OneWay, Off;
}

public sealed class TerrainDecals
{
    /// <summary>NeoLemmix's PM_ONEWAY: a one-way piece under one arrow.</summary>
    public const int DECAL_PM_ONEWAY = 0x0004;

    public readonly int W, H;
    readonly sbyte[] _ground;
    public readonly ushort[]? Physics;
    /// <summary>One byte per level pixel, non-zero where some decal object can draw.</summary>
    public readonly byte[] Coverage;
    /// <summary>The decal texture (RGBA, W x H).</summary>
    public readonly byte[] Data;
    /// <summary>texture.needsUpdate.</summary>
    public bool TextureNeedsUpdate;
    readonly List<(int X0, int Y0, int X1, int Y1)> _painted = new();

    public TerrainDecals(Level level, byte[] coverage, ushort[]? physics)
    {
        W = level.Width;
        H = level.Height;
        _ground = level.GroundMask.GroundMask;
        Physics = physics;
        Coverage = coverage;
        Data = new byte[W * H * 4];
        TextureNeedsUpdate = true;
    }

    /// <summary>The decals of a level, or null when it has none (TerrainDecals.forLevel).</summary>
    public static TerrainDecals? ForLevel(Level level, ushort[]? physics)
    {
        int w = level.Width, h = level.Height;
        var coverage = new byte[w * h];
        bool any = false;
        foreach (var obj in level.Objects)
        {
            if (!obj.Decal) continue; // drawProperties.onlyOverwrite
            foreach (var frame in obj.Frames)
            {
                if (frame == null) continue;
                int x0 = Math.Max(0, obj.X + frame.OffsetX);
                int y0 = Math.Max(0, obj.Y + frame.OffsetY);
                int x1 = Math.Min(w, obj.X + frame.OffsetX + frame.Width);
                int y1 = Math.Min(h, obj.Y + frame.OffsetY + frame.Height);
                for (int y = y0; y < y1; y++)
                    for (int x = x0; x < x1; x++) { coverage[x + y * w] = 1; any = true; }
            }
        }
        return any ? new TerrainDecals(level, coverage, physics) : null;
    }

    /// <summary>
    /// The decal draws of a tick: what lemmix/js/game.js ObjectManager.render draws, as
    /// SpriteCapture.drawFrameFlags files it, kept when it lands on layer 1 (the decal layer).
    /// </summary>
    public static List<DecalItem> ItemsFor(Level level, List<DecalItem>? into = null)
    {
        var items = into ?? new List<DecalItem>();
        items.Clear();
        foreach (var obj in level.Objects)
        {
            var g = obj.Gadget;
            if (g.EffectBase == "NONE" && g.Effect == "NONE" && g.Animations.Count == 0) continue;
            int layer = obj.Behind ? -2 : obj.Decal ? 1 : obj.Low ? -1 : 0;
            if (layer != 1) continue;
            items.Add(new DecalItem { Frame = g.Render(), X = obj.X, Y = obj.Y, FlipY = false, OneWay = obj.OneWay });
        }
        return items;
    }

    bool Admits(int i, bool oneWay)
    {
        if (oneWay) return Physics != null ? (Physics[i] & DECAL_PM_ONEWAY) != 0 : _ground[i] != 0;
        return _ground[i] != 0;
    }

    /// <summary>Repaint from this tick's captured draws; `oneWayOnly`: clear physics mode.</summary>
    public void Paint(IReadOnlyList<DecalItem> items, bool oneWayOnly)
    {
        int w = W, h = H;
        var data = Data;
        foreach (var r in _painted)
            for (int y = r.Y0; y < r.Y1; y++) Array.Clear(data, (y * w + r.X0) * 4, (r.X1 - r.X0) * 4);
        _painted.Clear();
        for (int n = 0; n < items.Count; n++)
        {
            var item = items[n];
            if (item.Frame == null || item.Off) continue;
            if (oneWayOnly && !item.OneWay) continue;
            var frame = item.Frame;
            int fw = frame.Width, fh = frame.Height;
            var src = MemoryMarshal.AsBytes(frame.Data.AsSpan());
            var mask = frame.Mask;
            int ox = item.X + frame.OffsetX, oy = item.Y + frame.OffsetY;
            int x0 = Math.Max(0, ox), y0 = Math.Max(0, oy);
            int x1 = Math.Min(w, ox + fw), y1 = Math.Min(h, oy + fh);
            if (x1 <= x0 || y1 <= y0) continue;
            for (int y = y0; y < y1; y++)
            {
                int sy = item.FlipY ? fh - 1 - (y - oy) : y - oy;
                for (int x = x0; x < x1; x++)
                {
                    int si = sy * fw + (x - ox), s = si * 4;
                    if (mask[si] == 0) continue;
                    int a = src[s + 3];
                    if (a == 0) a = 255; // the mask says drawn
                    int i = x + y * w;
                    if (!Admits(i, item.OneWay)) continue;
                    int d = i * 4;
                    if (a == 255 || data[d + 3] == 0)
                    {
                        data[d] = src[s]; data[d + 1] = src[s + 1]; data[d + 2] = src[s + 2]; data[d + 3] = (byte)a;
                    }
                    else
                    {
                        // "over": this pixel on what an earlier decal left here
                        double sa = a / 255.0, da = data[d + 3] / 255.0, oa = sa + da * (1 - sa);
                        for (int k = 0; k < 3; k++)
                            data[d + k] = (byte)JsMath.Round((src[s + k] * sa + data[d + k] * da * (1 - sa)) / oa);
                        data[d + 3] = (byte)JsMath.Round(oa * 255);
                    }
                }
            }
            _painted.Add((x0, y0, x1, y1));
        }
        TextureNeedsUpdate = true;
    }
}
