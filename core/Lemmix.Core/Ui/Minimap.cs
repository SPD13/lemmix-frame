using Lemmix.Engine;
using Lemmix.Util;

namespace Lemmix.Ui;

// The minimap's window in panel pixels (GamePanel's Layout.Minimap): level pixels per map pixel,
// and the padding (1 = NeoLemmix's frame room, 0 = none).
public sealed class MinimapSpec
{
    public int X, Y, W, H;
    public int ScaleX, ScaleY;
    public int? Pad;
}

// A rectangle in level pixels (the part of the level the viewer sees).
public readonly record struct LevelRect(double X0, double Y0, double X1, double Y1);

// web/3d/js/minimap.js MiniMap - the minimap in the skill panel's last box: the level shrunk
// down in its own colours, one dot per lemming, and a frame around the part of the level the
// viewer is looking at (GameBaseSkillPanel.pas DrawMinimap, LemRendering.pas RenderMinimap): a
// fixed scale, green dots (red for zombies), a 1-px frame in the panel's brick colour, the map
// padded by a pixel, and a map larger than its window scrolling to keep the frame centred -
// frozen while the player drags on it.
//
// The host hands in the level-space rectangle it worked out from the camera (SetViewRect) and a
// press gets the level point under it (PointToLevel). The window's pixels are View (RGBA, W x H),
// redrawn by Update; Version moves on each redraw (the texture's needsUpdate). The JS keeps two
// canvases (the map and the window); a canvas is RGBA here, and drawImage/fillRect are
// source-over with opaque pixels, which is all the map and the dots use. The mesh placement
// (layout()) belongs to the host.
public sealed class Minimap : IDisposable
{
    static readonly byte[] Frame = { 0xf0, 0xd0, 0xd0 };   // fRectColor, the brick colour
    static readonly byte[] Dot = { 0x00, 0xff, 0x00 };
    static readonly byte[] Zombie = { 0xff, 0x00, 0x00 };

    readonly Game _game;
    readonly Level _level;
    public readonly MinimapSpec Spec;
    readonly int _pad;
    public readonly int MapW, MapH;
    public readonly byte[] MapImage;   // the terrain at map scale (the map canvas), rebuilt when the ground changes
    public readonly byte[] View;       // what the window shows: the map at its offset, the dots, the frame
    public int Version;

    LevelRect? _rect;                  // the last known view, level px
    public int OffX, OffY;             // where the padded map sits in the window
    public bool Frozen;
    public bool TerrainDirty = true;   // the host sets it after a rewind (app.js does)
    string _lastKey = "";
    readonly Action<int, int> _onGround;

    public Minimap(Game game, Level level, MinimapSpec spec)
    {
        _game = game;
        _level = level;
        Spec = spec;
        _pad = spec.Pad ?? 1;
        MapW = (int)Math.Ceiling(level.Width / (double)spec.ScaleX);
        MapH = (int)Math.Ceiling(level.Height / (double)spec.ScaleY);
        MapImage = new byte[MapW * MapH * 4];
        View = new byte[spec.W * spec.H * 4];
        // every dig, bash, brick and crater passes through SetGroundAt / ClearGroundAt
        _onGround = (_, _) => TerrainDirty = true;
        level.GroundChanged += _onGround;
    }

    // The map pixels: a block with any solid pixel takes that pixel's colour.
    void BuildMap()
    {
        var level = _level;
        int W = level.Width, H = level.Height;
        var img = level.GroundImage;
        var m = level.GroundMask.GroundMask;
        var output = MapImage;
        Array.Clear(output);
        for (int my = 0; my < MapH; my++)
        {
            int y0 = my * Spec.ScaleY, y1 = Math.Min(H, y0 + Spec.ScaleY);
            for (int mx = 0; mx < MapW; mx++)
            {
                int x0 = mx * Spec.ScaleX, x1 = Math.Min(W, x0 + Spec.ScaleX);
                int found = -1;
                for (int y = y0; y < y1 && found < 0; y++)
                {
                    int i = y * W + x0;
                    for (int x = x0; x < x1; x++, i++)
                        if (m[i] != 0) { found = i; break; }
                }
                if (found < 0) continue;
                int o = (my * MapW + mx) * 4, p = found * 4;
                output[o] = img[p]; output[o + 1] = img[p + 1]; output[o + 2] = img[p + 2]; output[o + 3] = 255;
            }
        }
    }

    public void SetViewRect(LevelRect? rect) { if (rect != null) _rect = rect; }

    public void SetFreeze(bool on) { Frozen = on; }

    public readonly record struct FrameRect(int L, int T, int R, int B);

    // The view frame in padded-map pixels: one pixel outside the visible map pixels.
    public FrameRect? ComputeFrame()
    {
        if (_rect is not LevelRect r) return null;
        int sx = Spec.ScaleX, sy = Spec.ScaleY, pad = _pad;
        int l = JsMath.Floor(r.X0 / sx) + pad - 1, t = JsMath.Floor(r.Y0 / sy) + pad - 1;
        int rr = JsMath.ToInt32(Math.Ceiling(r.X1 / sx)) + pad, b = JsMath.ToInt32(Math.Ceiling(r.Y1 / sy)) + pad;
        int maxX = MapW + 2 * pad - 1, maxY = MapH + 2 * pad - 1;
        l = Math.Max(0, Math.Min(maxX, l)); rr = Math.Max(l, Math.Min(maxX, rr));
        t = Math.Max(0, Math.Min(maxY, t)); b = Math.Max(t, Math.Min(maxY, b));
        return new FrameRect(l, t, rr, b);
    }

    // NeoLemmix's DrawMinimap offsets: a small map is centred, a large one scrolls to keep the
    // frame in the middle and stops at its edges.
    void UpdateOffset(FrameRect? frame)
    {
        if (Frozen) return;
        int fullW = MapW + 2 * _pad, fullH = MapH + 2 * _pad;
        int w = Spec.W, h = Spec.H;
        if (fullW <= w) OffX = JsMath.Floor((w - fullW) / 2.0);
        else if (frame is FrameRect f)
        {
            int fw = f.R - f.L + 1;
            OffX = (int)JsMath.Round(-f.L + (w - fw) / 2.0);
            OffX = Math.Min(Math.Max(OffX, w - fullW), 0);
        }
        if (fullH <= h) OffY = JsMath.Floor((h - fullH) / 2.0);
        else if (frame is FrameRect g)
        {
            int fh = g.B - g.T + 1;
            OffY = (int)JsMath.Round(-g.T + (h - fh) / 2.0);
            OffY = Math.Min(Math.Max(OffY, h - fullH), 0);
        }
    }

    static string S(int n) => n.ToString(System.Globalization.CultureInfo.InvariantCulture);

    // Repaint when something moved: the sim, the ground, the camera, the map. True when it did.
    public bool Update()
    {
        int tick = _game.GameTimer.GetGameTicks();
        var frame = ComputeFrame();
        UpdateOffset(frame);
        string key = S(tick) + "|" + (frame is FrameRect fr ? S(fr.L) + "," + S(fr.T) + "," + S(fr.R) + "," + S(fr.B) : "-") +
            "|" + S(OffX) + "," + S(OffY) + "|" + (TerrainDirty ? "t" : "");
        if (key == _lastKey) return false;
        _lastKey = key;
        if (TerrainDirty) { BuildMap(); TerrainDirty = false; }
        int pad = _pad, ox = OffX + pad, oy = OffY + pad;
        FillRect(0, 0, Spec.W, Spec.H, 0, 0, 0);
        DrawMap(ox, oy);
        int sx = Spec.ScaleX, sy = Spec.ScaleY;
        foreach (var L in _game.LemmingManager.Lemmings)
        {
            if (L.Removed) continue;
            var c = L.IsZombie ? Zombie : Dot;
            FillRect(JsMath.Floor(L.X / (double)sx) + ox, JsMath.Floor(L.Y / (double)sy) + oy, 1, 1, c[0], c[1], c[2]);
        }
        if (frame is FrameRect f)
        {
            int l = f.L + OffX, t = f.T + OffY;
            int w = f.R - f.L + 1, h = f.B - f.T + 1;
            FillRect(l, t, w, 1, Frame[0], Frame[1], Frame[2]);
            FillRect(l, t + h - 1, w, 1, Frame[0], Frame[1], Frame[2]);
            FillRect(l, t, 1, h, Frame[0], Frame[1], Frame[2]);
            FillRect(l + w - 1, t, 1, h, Frame[0], Frame[1], Frame[2]);
        }
        Version++;
        return true;
    }

    // ctx.fillRect with an opaque colour, clipped to the window
    void FillRect(int x, int y, int w, int h, byte r, byte g, byte b)
    {
        int x0 = Math.Max(0, x), y0 = Math.Max(0, y), x1 = Math.Min(Spec.W, x + w), y1 = Math.Min(Spec.H, y + h);
        for (int yy = y0; yy < y1; yy++)
            for (int xx = x0; xx < x1; xx++)
            {
                int p = (yy * Spec.W + xx) * 4;
                View[p] = r; View[p + 1] = g; View[p + 2] = b; View[p + 3] = 255;
            }
    }

    // ctx.drawImage(map, ox, oy): the map's opaque pixels over the window (the transparent ones leave it)
    void DrawMap(int ox, int oy)
    {
        for (int y = 0; y < MapH; y++)
        {
            int ty = oy + y;
            if (ty < 0 || ty >= Spec.H) continue;
            for (int x = 0; x < MapW; x++)
            {
                int tx = ox + x;
                if (tx < 0 || tx >= Spec.W) continue;
                int s = (y * MapW + x) * 4;
                if (MapImage[s + 3] == 0) continue;
                int d = (ty * Spec.W + tx) * 4;
                View[d] = MapImage[s]; View[d + 1] = MapImage[s + 1]; View[d + 2] = MapImage[s + 2]; View[d + 3] = 255;
            }
        }
    }

    // Is this panel pixel inside the window?
    public bool Contains(double px, double py) => px >= Spec.X && px < Spec.X + Spec.W && py >= Spec.Y && py < Spec.Y + Spec.H;

    // The level point a panel pixel in the window stands for (the middle of that map pixel).
    public (double X, double Y) PointToLevel(double px, double py)
    {
        double mx = px - Spec.X - OffX - _pad, my = py - Spec.Y - OffY - _pad;
        return (Math.Max(0, Math.Min(_level.Width, (mx + 0.5) * Spec.ScaleX)),
            Math.Max(0, Math.Min(_level.Height, (my + 0.5) * Spec.ScaleY)));
    }

    public void Dispose() { _level.GroundChanged -= _onGround; }
}
