using Lemmix.Engine;
using Lemmix.Ui;

namespace Lemmix.Render;

// web/3d/js/gui.js GuiPanel - the skill panel as a plane in the scene: the panel's pixels (the
// GamePanel drawing into a PixelCanvas) as a texture on a plane, the artwork and counts extruded
// into a relief (one mesh per button, two for a split cell) built from layout.reliefMasks, the
// counters strip bevelled, the hovered button raised (a cropped copy of the texture grown and
// moved toward the viewer, a socket behind it, its relief raised with it), the minimap's plane
// over the panel's window (minimap.js layout()), and ray hits (UVs) turned into the panel's mouse
// events - a press on the minimap is the host's (OnMinimapCenter). Also
// buildExtrudedSpriteGeometry (web/3d/js/bridge.js), which the relief is made of.
//
// The scene graph is data here: every three.js object the panel makes is a BarObject (visible,
// position, scale) in the parent's space (the camera rig), its geometry a ChunkGeometry computed
// in doubles in the JS order and stored as float32 as three.js stores it; the host (Godot,
// app/Board/SkillBarView.cs) builds meshes from them. Render orders, materials: see the consts.
// The DOS panel's paths (colour-keyed masks, the flattened dither, the shared border) are kept
// for completeness; a Lemmix panel takes the mask and own-tile paths.
public sealed class BarObject
{
    public bool Visible = true;
    public double Px, Py, Pz;
    public double Sx = 1, Sy = 1, Sz = 1;
    public ChunkGeometry? Geometry;   // the relief meshes' geometry (null: the empty geometry)
    public int RenderOrder;
    public void SetPosition(double x, double y, double z) { Px = x; Py = y; Pz = z; }
    public void SetScale(double x, double y, double z) { Sx = x; Sy = y; Sz = z; }
}

public sealed class SkillBar : IDisposable
{
    // skill-panel button geometry, in panel pixels
    public const int GUI_TILE_W = 16;
    public const int GUI_TILE_TOP = 16;
    public const int GUI_TILE_H = 23;
    public const int GUI_TILE_POP = 5;    // how far a hovered button rises toward the player
    public const double GUI_TILE_GROW = 1.08;
    public const int GUI_ORDER_PANEL = 50, GUI_ORDER_SOCKET = 51, GUI_ORDER_RELIEF = 52, GUI_ORDER_HOVER = 53, GUI_ORDER_HOVER_RELIEF = 54;
    public const int MINIMAP_ORDER = 51;  // minimap.js: over the panel, under the raised buttons
    public const int SOCKET_COLOR = 0x05070c;
    static readonly HashSet<string> GuiBgColors = new(StringComparer.Ordinal) { "240,240,0", "128,128,128" };
    static readonly int[] GuiFlatBg = { 184, 184, 64 };
    const int GUI_ICON_BOTTOM = 39;
    const int GUI_ICON_DEPTH = 1;
    const int GUI_DIGIT_TOP = 17, GUI_DIGIT_BOTTOM = 26;
    const string GUI_DIGIT_COLOR = "255,255,255";
    const int GUI_TEXT_BOTTOM = GUI_TILE_TOP;
    static readonly Dictionary<string, int> GuiTextDepths = new(StringComparer.Ordinal) { ["0,176,0"] = 1, ["240,208,208"] = 2, ["255,255,255"] = 2 };
    const int GUI_TEXT_DEPTH_MAX = 2;
    static readonly string[] GuiDosLabels = { "release rate down", "release rate up", "climber", "floater", "bomber", "blocker", "builder", "basher", "miner", "digger", "pause", "nuke", "speed" };
    static readonly Dictionary<string, (string? Plain, string? Upper, string? Lower)> GuiLemmixLabels = new(StringComparer.Ordinal)
    {
        ["rrminus"] = ("release rate down", null, null), ["rrplus"] = ("release rate up", null, null),
        ["pause"] = ("pause", null, null), ["nuke"] = ("nuke", null, null), ["speed"] = ("fast forward", null, null), ["restart"] = ("restart", null, null),
        ["frameskip"] = (null, "frame back", "frame forward"),
        ["directional"] = (null, "select facing left", "select facing right"),
        ["cpmreplay"] = (null, "clear physics", "load replay"),
    };

    // bridge.js buildExtrudedSpriteGeometry's shades
    const double SPRITE_SHADE_FRONT = 1.0, SPRITE_SHADE_BACK = 0.45, SPRITE_SHADE_LEFT = 0.62, SPRITE_SHADE_RIGHT = 0.66,
        SPRITE_SHADE_TOP = 0.85, SPRITE_SHADE_BOTTOM = 0.5, SPRITE_WALL_UV_INSET = 0.05;

    // the module-level lets gui.js sets from the panel layout
    readonly int _buttons, _lastButton, _digitButtons;
    readonly bool _sharedBorder;
    readonly int _cropH;
    readonly double _cropCy;
    readonly HashSet<int> _split;
    readonly int _halfUpperBottom, _halfLowerTop;

    public readonly Game Game;
    public readonly PixelCanvas Display;
    public readonly GamePanel Panel;
    readonly Func<double> _now;
    public bool Dirty = true;
    public string? HoverHalf;
    public int? HoverIndex;
    public double HoverSince;
    public readonly MinimapSpec? MinimapSpec;
    public Minimap? Minimap;
    public BarObject? MinimapPlane;
    public bool MinimapDrag;
    public Action<double, double>? OnMinimapCenter;   // (level x, y) => the host moves the view
    LevelRect? _viewRect;

    // the canvas the texture is made from (an HTML canvas starts 300x150, transparent)
    public int CanvasWidth = 300, CanvasHeight = 150;
    public byte[] Canvas = new byte[300 * 150 * 4];
    bool _hasCtx;
    public int TextureVersion;           // texture.needsUpdate (the hover copy and the relief share the canvas)

    public BarObject? Mesh, HoverTile, Socket, HoverRelief, TextMesh;
    public double HoverRepeatX, HoverRepeatY, HoverOffsetX, HoverOffsetY; // the hover texture's crop
    public List<BarObject>? TileReliefs;
    public List<(int Index, string? Half)>? ReliefParts;
    public double ReliefDepth = 1;
    public bool ReliefOn = true;
    public bool FlatSkills;
    readonly Dictionary<string, ChunkGeometry?> _tileGeoms = new(StringComparer.Ordinal);
    byte[]? _iconMask, _digitMask, _textMask;
    int? _digitSum, _textSum;
    (double Width, double Y, double Z)? _placement;
    bool _placed;

    // GuiPanel's constructor: the game's panel drawn into a PixelCanvas of ours (game.setGuiDisplay).
    public SkillBar(Game game, PanelAssets? assets, SpriteSet? sprites, Func<double>? now = null)
    {
        Game = game;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        _now = now ?? (() => clock.Elapsed.TotalMilliseconds);
        Display = new PixelCanvas(() => Dirty = true);
        Panel = GamePanel.SetGuiDisplay(game, Display, assets, sprites, _now);
        var layout = Panel.Layout;
        _buttons = layout.Buttons;
        _lastButton = _buttons - 1;
        _digitButtons = layout.DigitButtons;
        _sharedBorder = !(layout.SharedBorder == false);
        _cropH = _sharedBorder ? GUI_TILE_H + 1 : GUI_TILE_H;
        _cropCy = GUI_TILE_TOP + _cropH / 2.0;
        _split = new HashSet<int>(layout.SplitCells);
        _halfUpperBottom = layout.HalfUpperBottom;
        _halfLowerTop = layout.HalfLowerTop;
        MinimapSpec = layout.Minimap;
    }

    int CropW(int index) => !_sharedBorder ? GUI_TILE_W : index >= _lastButton ? GUI_TILE_W - 1 : GUI_TILE_W + 1;
    double GrowFor(int index) => _sharedBorder && index >= _lastButton ? 1 : GUI_TILE_GROW;
    int IconTop(int index) => index < _digitButtons ? 26 : 17;

    // A Lemmix panel paints its tiles itself, and flattens them itself.
    static bool OwnsBackground() => true;

    // The buttons' background as one colour, or the original texture.
    public void SetFlatSkills(bool on)
    {
        if (FlatSkills == on) return;
        FlatSkills = on;
        if (OwnsBackground()) Panel.SetFlatBackground(on);
        else Dirty = true;
    }

    static string Key(byte[] d, int i) => d[i] + "," + d[i + 1] + "," + d[i + 2];

    // The DOS dither repainted as one colour (a panel that does not paint its own tiles).
    void FlattenBackground()
    {
        int W = CanvasWidth, H = CanvasHeight;
        int w = Math.Min(_buttons * GUI_TILE_W, W), h = H - GUI_TILE_TOP;
        if (w <= 0 || h <= 0) return;
        for (int y = GUI_TILE_TOP; y < H; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * W + x) * 4;
                if (GuiBgColors.Contains(Key(Canvas, i))) { Canvas[i] = (byte)GuiFlatBg[0]; Canvas[i + 1] = (byte)GuiFlatBg[1]; Canvas[i + 2] = (byte)GuiFlatBg[2]; }
            }
    }

    ReliefMasks? PanelMasks()
    {
        var layout = Panel.Layout;
        return layout.ReliefFromMasks ? layout.ReliefMasks : null;
    }

    // 1 where a pixel belongs to a tile's picture, 0 elsewhere.
    byte[] BuildIconMask()
    {
        var masks = PanelMasks();
        if (masks != null) return masks.Art;
        int W = CanvasWidth, H = CanvasHeight;
        var data = Display.Data!;
        var mask = new byte[W * H];
        for (int x = 0; x < _buttons * GUI_TILE_W && x < W; x++)
        {
            int col = x % GUI_TILE_W;
            if (col == 0 || col == GUI_TILE_W - 1) continue;
            int top = IconTop(x / GUI_TILE_W);
            for (int y = top; y < GUI_ICON_BOTTOM; y++)
                if (!GuiBgColors.Contains(Key(data, (y * W + x) * 4))) mask[y * W + x] = 1;
        }
        return mask;
    }

    // Build the button relief once the panel canvas holds real pixels.
    void BuildRelief()
    {
        _iconMask = BuildIconMask();
        _digitMask = BuildDigitMask();
        _digitSum = DigitChecksum();
        TileReliefs = new List<BarObject>();
        ReliefParts = new List<(int, string?)>();
        for (int i = 0; i < _buttons; i++)
            foreach (string? half in _split.Contains(i) ? new[] { "upper", "lower" } : new string?[] { null })
            {
                TileReliefs.Add(new BarObject { Geometry = TileGeometry(i, half), RenderOrder = GUI_ORDER_RELIEF });
                ReliefParts.Add((i, half));
            }
        HoverRelief = new BarObject { Visible = false, RenderOrder = GUI_ORDER_HOVER_RELIEF };
        Socket = new BarObject { Visible = false, RenderOrder = GUI_ORDER_SOCKET };
        TextMesh = new BarObject { RenderOrder = GUI_ORDER_RELIEF };
        RefreshText();
        ApplyReliefVisibility();
        LayoutRelief();
    }

    // White digit pixels of the counts.
    byte[] BuildDigitMask()
    {
        var masks = PanelMasks();
        if (masks != null) return masks.Digits;
        int W = CanvasWidth, H = CanvasHeight;
        var mask = new byte[W * H];
        for (int x = 0; x < _digitButtons * GUI_TILE_W && x < W; x++)
        {
            int col = x % GUI_TILE_W;
            if (col == 0 || col == GUI_TILE_W - 1) continue;
            for (int y = GUI_DIGIT_TOP; y < GUI_DIGIT_BOTTOM; y++)
                if (Key(Canvas, (y * W + x) * 4) == GUI_DIGIT_COLOR) mask[y * W + x] = 1;
        }
        return mask;
    }

    // Cheap fingerprint of the digit strip.
    int DigitChecksum()
    {
        int w = Math.Min(_digitButtons * GUI_TILE_W, CanvasWidth);
        int h = 0;
        for (int y = GUI_DIGIT_TOP; y < GUI_DIGIT_BOTTOM; y++)
            for (int x = 0; x < w; x++) h = unchecked(h * 31 + Canvas[(y * CanvasWidth + x) * 4]);
        return h;
    }

    void RefreshDigits()
    {
        int sum = DigitChecksum();
        if (sum == _digitSum) return;
        _digitSum = sum;
        _digitMask = BuildDigitMask();
        _tileGeoms.Clear();
        for (int i = 0; i < TileReliefs!.Count; i++)
        {
            var part = ReliefParts![i];
            TileReliefs[i].Geometry = TileGeometry(part.Index, part.Half);
        }
        if (HoverIndex is int hi) HoverRelief!.Geometry = TileGeometry(hi, HoverHalf);
    }

    // How far each pixel of the counters text stands off the panel.
    byte[] BuildTextMask()
    {
        int W = CanvasWidth;
        var height = new byte[W * CanvasHeight];
        for (int y = 0; y < GUI_TEXT_BOTTOM; y++)
            for (int x = 0; x < W; x++)
                height[y * W + x] = (byte)(GuiTextDepths.TryGetValue(Key(Canvas, (y * W + x) * 4), out int d) ? d : 0);
        return height;
    }

    int TextChecksum()
    {
        int h = 0;
        for (int i = 0; i < CanvasWidth * GUI_TEXT_BOTTOM * 4; i += 4) h = unchecked(h * 31 + Canvas[i]);
        return h;
    }

    // Smoothed relief for the text: a centre vertex at each pixel's height fanned out to corners
    // at the mean of the four heights meeting there.
    ChunkGeometry? BuildTextGeometry()
    {
        int W = CanvasWidth, H = CanvasHeight;
        var height = _textMask!;
        int At(int x, int y) => x >= 0 && x < W && y >= 0 && y < H ? height[y * W + x] : 0;
        double CornerZ(int x, int y) => (At(x - 1, y - 1) + At(x, y - 1) + At(x - 1, y) + At(x, y)) / 4.0;
        var positions = new List<double>(); var colors = new List<double>(); var uvs = new List<double>(); var indices = new List<int>();
        void Push(double px, double py, double pz, double u, double v)
        {
            positions.Add(px); positions.Add(py); positions.Add(pz);
            double shade = 0.72 + 0.28 * (pz / GUI_TEXT_DEPTH_MAX);
            colors.Add(shade); colors.Add(shade); colors.Add(shade);
            uvs.Add(u); uvs.Add(v);
        }
        for (int y = 0; y < GUI_TEXT_BOTTOM; y++)
            for (int x = 0; x < W; x++)
            {
                int h = height[y * W + x];
                if (h == 0) continue;
                int b = positions.Count / 3;
                Push(x + 0.5, y + 0.5, h, (x + 0.5) / W, (y + 0.5) / H);
                Push(x, y, CornerZ(x, y), x / (double)W, y / (double)H);
                Push(x + 1, y, CornerZ(x + 1, y), (x + 1) / (double)W, y / (double)H);
                Push(x + 1, y + 1, CornerZ(x + 1, y + 1), (x + 1) / (double)W, (y + 1) / (double)H);
                Push(x, y + 1, CornerZ(x, y + 1), x / (double)W, (y + 1) / (double)H);
                indices.AddRange(new[] { b, b + 1, b + 2, b, b + 2, b + 3, b, b + 3, b + 4, b, b + 4, b + 1 });
            }
        if (indices.Count == 0) return null;
        return MakeGeometry(positions, colors, uvs, indices);
    }

    // three.js: Float32BufferAttribute from the numbers, setIndex(array) Uint32 when any index >= 65535
    static ChunkGeometry MakeGeometry(List<double> positions, List<double> colors, List<double> uvs, List<int> indices)
    {
        static float[] F(List<double> l) { var a = new float[l.Count]; for (int i = 0; i < a.Length; i++) a[i] = (float)l[i]; return a; }
        bool big = false;
        for (int i = indices.Count - 1; i >= 0; --i) if (indices[i] >= 65535) { big = true; break; }
        return new ChunkGeometry { Positions = F(positions), Colors = F(colors), Uvs = F(uvs), Indices = indices.ToArray(), Index32 = big };
    }

    void RefreshText()
    {
        int sum = TextChecksum();
        if (sum == _textSum) return;
        _textSum = sum;
        _textMask = BuildTextMask();
        var geom = BuildTextGeometry();
        TextMesh!.Geometry = geom;
        TextMesh.Visible = ReliefOn && geom != null;
    }

    public readonly record struct VCropRect(int Y0, int H, double Cy);

    // The rows a raised copy of a button takes: its cell, or one of a split cell's halves.
    VCropRect VCrop(string? half)
    {
        if (half == "upper") { int y0 = GUI_TILE_TOP, y1 = _halfLowerTop; return new VCropRect(y0, y1 - y0, (y0 + y1) / 2.0); }
        if (half == "lower") { int y0 = _halfLowerTop, y1 = GUI_TILE_TOP + _cropH; return new VCropRect(y0, y1 - y0, (y0 + y1) / 2.0); }
        return new VCropRect(GUI_TILE_TOP, _cropH, _cropCy);
    }

    public readonly record struct TileRectangle(int X0, int W, double Cx, int Y0, int H, double Cy);

    // What a raised copy shows: on a panel of own tiles, the box of the cell's lit pixels.
    public TileRectangle TileRect(int index, string? half)
    {
        var crop = Crop(index);
        var v = VCrop(half);
        var rect = new TileRectangle(crop.X0, crop.W, crop.Cx, v.Y0, v.H, v.Cy);
        if (_sharedBorder || !_hasCtx) return rect;
        int x0 = crop.W, x1 = -1, y0 = v.H, y1 = -1;
        for (int y = 0; y < v.H; y++)
            for (int x = 0; x < crop.W; x++)
            {
                int cx = crop.X0 + x, cy = v.Y0 + y;
                // getImageData outside the canvas reads transparent black
                int sum = cx >= 0 && cy >= 0 && cx < CanvasWidth && cy < CanvasHeight
                    ? Canvas[(cy * CanvasWidth + cx) * 4] + Canvas[(cy * CanvasWidth + cx) * 4 + 1] + Canvas[(cy * CanvasWidth + cx) * 4 + 2] : 0;
                if (sum <= 40) continue;
                if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; if (y > y1) y1 = y;
            }
        if (x1 < 0) return rect;
        return new TileRectangle(crop.X0 + x0, x1 - x0 + 1, crop.X0 + (x0 + x1 + 1) / 2.0, v.Y0 + y0, y1 - y0 + 1, v.Y0 + (y0 + y1 + 1) / 2.0);
    }

    // Which half of a split cell a panel row falls in; null on an ordinary cell.
    string? HalfAt(int index, double py) => !_split.Contains(index) ? null : py >= _halfLowerTop ? "lower" : "upper";

    // Per-button relief geometry (cached), for the raised hovered button.
    ChunkGeometry? TileGeometry(int index, string? half)
    {
        string key = index + "/" + (half ?? "");
        if (_tileGeoms.TryGetValue(key, out var cached)) return cached;
        int W = CanvasWidth, H = CanvasHeight;
        int x0 = index * GUI_TILE_W, x1 = x0 + GUI_TILE_W;
        var v = VCrop(half);
        var icon = _iconMask!; var digits = _digitMask;
        var geom = BuildExtrudedSpriteGeometry((x, y) => x >= x0 && x < x1 && y >= v.Y0 && y < v.Y0 + v.H &&
            (icon[y * W + x] != 0 || (digits != null && digits[y * W + x] != 0)), W, H, GUI_ICON_DEPTH);
        _tileGeoms[key] = geom;
        return geom;
    }

    void SetPartVisible(int? index, string? half, bool visible)
    {
        if (TileReliefs == null || index == null) return;
        for (int i = 0; i < ReliefParts!.Count; i++)
            if (ReliefParts[i].Index == index && ReliefParts[i].Half == half) TileReliefs[i].Visible = visible;
    }

    void LayoutRelief()
    {
        if (TileReliefs == null || Mesh == null) return;
        double sx = Mesh.Sx / CanvasWidth, sy = Mesh.Sy / CanvasHeight;
        var meshes = TextMesh != null ? TileReliefs.Append(TextMesh) : TileReliefs;
        foreach (var m in meshes)
        {
            m.SetScale(sx, -sy, sx * ReliefDepth);
            m.SetPosition(Mesh.Px - Mesh.Sx / 2, Mesh.Py + Mesh.Sy / 2, Mesh.Pz + 0.2 * sx);
        }
        if (HoverIndex is int hi)
        {
            LayoutSocket(hi, HoverHalf);
            LayoutHoverRelief(hi, HoverHalf);
        }
    }

    // The panel cell of the selected skill on the DOS panel (where the selection frame sits on shared lines).
    int SelectedIndex()
    {
        if (!_sharedBorder) return -1;
        int skill = Game.Skills.GetSelectedSkill();
        return skill > 0 ? skill + 1 : -1;
    }

    public readonly record struct CropRect(int X0, int W, double Cx);

    CropRect Crop(int index)
    {
        int x0 = index * GUI_TILE_W, x1 = x0 + GUI_TILE_W;
        if (_sharedBorder)
        {
            int sel = SelectedIndex();
            if (index >= _lastButton) x1 -= 1;
            else if (sel == index) x1 += 1;
            if (sel == index - 1) x0 += 1;
        }
        return new CropRect(x0, x1 - x0, (x0 + x1) / 2.0);
    }

    void LayoutSocket(int index, string? half)
    {
        if (Socket == null || Mesh == null) return;
        double cw = CanvasWidth, ch = CanvasHeight, pw = Mesh.Sx, ph = Mesh.Sy;
        var r = TileRect(index, half);
        Socket.SetScale((r.W / cw) * pw, (r.H / ch) * ph, 1);
        Socket.SetPosition(Mesh.Px + (r.Cx / cw - 0.5) * pw, Mesh.Py + (0.5 - r.Cy / ch) * ph, Mesh.Pz + 0.1 * Unit);
    }

    // Same transform as the relief, grown about the tile centre and raised with the tile.
    void LayoutHoverRelief(int index, string? half)
    {
        if (HoverRelief == null || Mesh == null) return;
        double pw = Mesh.Sx, ph = Mesh.Sy;
        double sx = pw / CanvasWidth, sy = ph / CanvasHeight;
        double g = GrowFor(index);
        var r = TileRect(index, half);
        double cx = r.Cx, cy = r.Cy;
        HoverRelief.SetScale(sx * g, -sy * g, sx * g * ReliefDepth);
        HoverRelief.SetPosition(Mesh.Px - pw / 2 + cx * sx * (1 - g), Mesh.Py + ph / 2 + cy * sy * (g - 1), Mesh.Pz + (GUI_TILE_POP + 0.2) * sx);
    }

    // Create the plane once the panel's buffer exists.
    bool EnsureMesh()
    {
        if (Mesh != null) return true;
        if (Display.Data == null) return false;
        CanvasWidth = Display.Width;
        CanvasHeight = Display.Height;
        Canvas = new byte[CanvasWidth * CanvasHeight * 4];
        _hasCtx = true;
        Mesh = new BarObject { RenderOrder = GUI_ORDER_PANEL };
        HoverRepeatX = CropW(0) / (double)CanvasWidth; HoverRepeatY = _cropH / (double)CanvasHeight;
        HoverOffsetX = 0; HoverOffsetY = 0;
        HoverTile = new BarObject { Visible = false, RenderOrder = GUI_ORDER_HOVER };
        if (MinimapSpec != null)
        {
            Minimap = new Minimap(Game, Game.Level, MinimapSpec);
            MinimapPlane = new BarObject { RenderOrder = MINIMAP_ORDER };
            if (_viewRect != null) Minimap.SetViewRect(_viewRect);
        }
        return true;
    }

    // Panel-space UV -> button index, or null outside the row.
    int? ButtonIndexAt(double uvX, double uvY)
    {
        if (CanvasWidth == 0) return null;
        double px = uvX * CanvasWidth, py = (1 - uvY) * CanvasHeight;
        if (py <= GUI_TILE_TOP) return null; // the counters strip, not a button
        int index = (int)Math.Truncate(px / GUI_TILE_W);
        return index >= 0 && index <= _lastButton ? index : null;
    }

    // Raise the button under the pointer (a panel ray hit's UV, or null).
    public void SetHover((double X, double Y)? uv)
    {
        if (!EnsureMesh()) return;
        int? index = uv is { } u ? ButtonIndexAt(u.X, u.Y) : null;
        string? half = index is int ix ? HalfAt(ix, (1 - uv!.Value.Y) * CanvasHeight) : null;
        if (index == HoverIndex && half == HoverHalf) return;
        HoverSince = _now(); // a new button: its label waits again
        SetPartVisible(HoverIndex, HoverHalf, ReliefOn);
        HoverIndex = index;
        HoverHalf = half;
        HoverTile!.Visible = index != null;
        if (Socket != null) Socket.Visible = index != null;
        if (index is not int i)
        {
            if (HoverRelief != null) HoverRelief.Visible = false;
            return;
        }
        SetPartVisible(i, half, false);
        if (HoverRelief != null)
        {
            var geom = TileGeometry(i, half);
            HoverRelief.Visible = ReliefOn && geom != null;
            if (geom != null) HoverRelief.Geometry = geom;
        }
        LayoutHoverTile(i, half);
        LayoutSocket(i, half);
        LayoutHoverRelief(i, half);
    }

    // What the button at `index` (on a split cell, `half`) is called, or null.
    public string? ButtonLabel(int? index, string? half)
    {
        if (index is not int i) return null;
        var cells = Panel.Layout.Cells;
        string? what = i >= 0 && i < cells.Count ? cells[i] : null;
        if (what == null) return null;
        if (what.StartsWith("skill:", StringComparison.Ordinal)) return what[6..].ToLowerInvariant();
        if (!GuiLemmixLabels.TryGetValue(what, out var label)) return null;
        if (label.Plain == null) return half == "upper" ? label.Upper : half == "lower" ? label.Lower : null;
        return label.Plain;
    }

    // The label a pointer resting on a button earns, and when it arrived there.
    public (string Text, double Since)? HoverTip()
    {
        if (HoverIndex == null || HoverTile == null || !HoverTile.Visible) return null;
        string? text = ButtonLabel(HoverIndex, HoverHalf);
        if (string.IsNullOrEmpty(text)) return null;
        return (text, HoverSince);
    }

    void LayoutHoverTile(int index, string? half)
    {
        double cw = CanvasWidth, ch = CanvasHeight, pw = Mesh!.Sx, phh = Mesh.Sy;
        var r = TileRect(index, half);
        HoverRepeatX = r.W / cw; HoverRepeatY = r.H / ch;
        HoverOffsetX = r.X0 / cw; HoverOffsetY = 1 - (r.Y0 + r.H) / ch;
        double grow = GrowFor(index);
        HoverTile!.SetScale((r.W / cw) * pw * grow, (r.H / ch) * phh * grow, 1);
        HoverTile.SetPosition(Mesh.Px + (r.Cx / cw - 0.5) * pw, Mesh.Py + (0.5 - r.Cy / ch) * phh, Mesh.Pz + GUI_TILE_POP * Unit);
    }

    // How far the extruded artwork stands off the panel, as a multiple of one canvas pixel.
    public void SetReliefDepth(double mult)
    {
        if (ReliefDepth == mult) return;
        ReliefDepth = mult;
        LayoutRelief();
    }

    // Whether the artwork and counters are extruded at all.
    public void SetRelief(bool on)
    {
        if (ReliefOn == on) return;
        ReliefOn = on;
        ApplyReliefVisibility();
    }

    void ApplyReliefVisibility()
    {
        if (TileReliefs == null) return;
        bool on = ReliefOn;
        for (int i = 0; i < TileReliefs.Count; i++)
        {
            var p = ReliefParts![i];
            TileReliefs[i].Visible = on && !(p.Index == HoverIndex && p.Half == HoverHalf);
        }
        if (HoverRelief != null)
            HoverRelief.Visible = on && HoverIndex is int hi && TileGeometry(hi, HoverHalf) != null;
        if (TextMesh != null) TextMesh.Visible = on && TextMesh.Geometry != null;
    }

    // The panel in its parent's space: `width` wide, centred on x = 0, at (y, z).
    public void Place(double width, double y, double z)
    {
        if (_placed && _placement is { } p && p.Width == width && p.Y == y && p.Z == z) return;
        _placement = (width, y, z);
        _placed = false;
        ApplyPlacement();
    }

    void ApplyPlacement()
    {
        if (_placed || _placement == null || !EnsureMesh()) return;
        var (width, y, z) = _placement.Value;
        double height = width * CanvasHeight / CanvasWidth;
        Mesh!.SetScale(width, height, 1);
        Mesh.SetPosition(0, y, z);
        _placed = true;
        if (HoverIndex is int hi)
        {
            LayoutHoverTile(hi, HoverHalf);
            LayoutSocket(hi, HoverHalf);
            LayoutHoverRelief(hi, HoverHalf);
        }
        LayoutRelief();
        if (Minimap != null) LayoutMinimap();
    }

    // minimap.js layout(): over the panel's window, in the panel mesh's space.
    void LayoutMinimap()
    {
        var m = Mesh;
        if (m == null || MinimapPlane == null) return;
        double cw = CanvasWidth, ch = CanvasHeight, pw = m.Sx, ph = m.Sy;
        var s = MinimapSpec!;
        MinimapPlane.SetScale((s.W / cw) * pw, (s.H / ch) * ph, 1);
        MinimapPlane.SetPosition(m.Px + ((s.X + s.W / 2.0) / cw - 0.5) * pw, m.Py + (0.5 - (s.Y + s.H / 2.0) / ch) * ph, m.Pz + 0.1 * (pw / cw));
    }

    // The part of the level in view, in level px, from the host each frame.
    public void SetViewRect(LevelRect? rect)
    {
        if (rect != null) _viewRect = rect;
        Minimap?.SetViewRect(rect);
    }

    // How far a raised button's bottom edge sits below the panel's centre, as a fraction of its height.
    public double RaisedTileBottomOffset()
    {
        double ch = CanvasHeight != 0 ? CanvasHeight : 40;
        double centre = _cropCy / ch;
        double bottom = (GUI_TILE_TOP + _cropH) / ch;
        return (bottom - centre) * GUI_TILE_GROW + (centre - 0.5);
    }

    // Parent-space units per panel pixel.
    double Unit => Mesh != null ? Mesh.Sx / CanvasWidth : 1;

    // Once per frame: placement, the minimap, the held frame skip, and the texture when the panel redrew.
    public void Update()
    {
        if (!EnsureMesh()) return;
        ApplyPlacement();
        Minimap?.Update();
        Panel.Poll(_now());
        if (!Dirty) return;
        Dirty = false;
        Buffer.BlockCopy(Display.Data!, 0, Canvas, 0, Math.Min(Canvas.Length, Display.Data!.Length));
        if (FlatSkills && !OwnsBackground()) FlattenBackground();
        TextureVersion++;
        if (TileReliefs == null)
        {
            var layout = Panel.Layout;
            if (!(layout.ReliefFromMasks && layout.ReliefMasks == null)) BuildRelief();
        }
        else
        {
            RefreshDigits();
            RefreshText();
        }
    }

    // A ray hit's UV as panel pixels.
    (int X, int Y) UvToPixels(double uvX, double uvY) =>
        ((int)Math.Floor(uvX * CanvasWidth), (int)Math.Floor((1 - uvY) * CanvasHeight));

    // Does this UV land on the minimap's window?
    public bool IsMinimap((double X, double Y)? uv)
    {
        if (uv == null || Minimap == null) return false;
        var p = UvToPixels(uv.Value.X, uv.Value.Y);
        return Minimap.Contains(p.X, p.Y);
    }

    void CenterFromMinimap((int X, int Y) p)
    {
        if (OnMinimapCenter != null) { var pt = Minimap!.PointToLevel(p.X, p.Y); OnMinimapCenter(pt.X, pt.Y); }
    }

    void EndMinimapDrag()
    {
        MinimapDrag = false;
        Minimap?.SetFreeze(false);
    }

    // A press on the map is the host's; anywhere else it is the panel's (button 0 left, 1 middle, 2 right).
    public void OnMouseDown((double X, double Y) uv, int button)
    {
        var p = UvToPixels(uv.X, uv.Y);
        if (Minimap != null && Minimap.Contains(p.X, p.Y))
        {
            MinimapDrag = true;
            Minimap.SetFreeze(true);
            CenterFromMinimap(p);
            return;
        }
        Display.OnMouseDown.Trigger(new PanelPointer(p.X, p.Y, button));
    }

    public void OnMouseMove((double X, double Y)? uv)
    {
        if (!MinimapDrag) return;
        (int X, int Y)? p = uv is { } u ? UvToPixels(u.X, u.Y) : null;
        if (p is { } q && Minimap!.Contains(q.X, q.Y)) CenterFromMinimap(q);
        else EndMinimapDrag();
    }

    public void OnMouseUp((double X, double Y)? uv)
    {
        if (MinimapDrag) { EndMinimapDrag(); return; }
        if (uv is { } u) { var p = UvToPixels(u.X, u.Y); Display.OnMouseUp.Trigger(new PanelPointer(p.X, p.Y)); }
    }

    public void OnDoubleClick((double X, double Y) uv)
    {
        var p = UvToPixels(uv.X, uv.Y);
        if (Minimap != null && Minimap.Contains(p.X, p.Y)) return;
        Display.OnDoubleClick.Trigger(new PanelPointer(p.X, p.Y));
    }

    public void Dispose()
    {
        Minimap?.Dispose();
        Minimap = null;
    }

    // ---- bridge.js buildExtrudedSpriteGeometry

    sealed class Quads
    {
        public readonly List<(double[] P, double[] Uv, double Shade)> Items = new();
        public void Add(double[] p, double[] uv, double shade) => Items.Add((p, uv, shade));
    }

    // Extrude a mask into a relief: greedy front/back rectangles plus edge walls (runs merged per
    // direction), in sprite pixel space (origin top-left, y down, z toward the viewer), emitted
    // back -> walls -> fronts so the painter's order is right without depth testing.
    public static ChunkGeometry? BuildExtrudedSpriteGeometry(Func<int, int, bool> isSolidRaw, int w, int h, double depth)
    {
        bool IsSolid(int x, int y) => x >= 0 && x < w && y >= 0 && y < h && isSolidRaw(x, y);
        var backQuads = new Quads(); var wallQuads = new Quads(); var frontQuads = new Quads();
        var visited = new byte[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                if (visited[y * w + x] != 0 || !IsSolid(x, y)) continue;
                int rw = 1;
                while (x + rw < w && visited[y * w + x + rw] == 0 && IsSolid(x + rw, y)) rw++;
                int rh = 1;
                while (y + rh < h)
                {
                    bool ok = true;
                    for (int i = 0; i < rw; i++)
                        if (visited[(y + rh) * w + x + i] != 0 || !IsSolid(x + i, y + rh)) { ok = false; break; }
                    if (!ok) break;
                    rh++;
                }
                for (int yy = 0; yy < rh; yy++)
                    for (int xx = 0; xx < rw; xx++) visited[(y + yy) * w + x + xx] = 1;
                double u0 = x / (double)w, u1 = (x + rw) / (double)w, v0 = y / (double)h, v1 = (y + rh) / (double)h;
                frontQuads.Add(new double[] { x, y, depth, x + rw, y, depth, x + rw, y + rh, depth, x, y + rh, depth },
                    new[] { u0, v0, u1, v0, u1, v1, u0, v1 }, SPRITE_SHADE_FRONT);
                backQuads.Add(new double[] { x, y, 0, x + rw, y, 0, x + rw, y + rh, 0, x, y + rh, 0 },
                    new[] { u0, v0, u1, v0, u1, v1, u0, v1 }, SPRITE_SHADE_BACK);
            }
        const double IN = SPRITE_WALL_UV_INSET;
        for (int x = 0; x < w; x++)
            foreach (int dir in new[] { -1, 1 })
            {
                int y = 0;
                while (y < h)
                {
                    if (!IsSolid(x, y) || IsSolid(x + dir, y)) { y++; continue; }
                    int run = 1;
                    while (y + run < h && IsSolid(x, y + run) && !IsSolid(x + dir, y + run)) run++;
                    int wx = dir == -1 ? x : x + 1;
                    double u = (x + (dir == -1 ? IN : 1 - IN)) / w;
                    double va = (y + IN) / h, vb = (y + run - IN) / h;
                    wallQuads.Add(new double[] { wx, y, 0, wx, y, depth, wx, y + run, depth, wx, y + run, 0 },
                        new[] { u, va, u, va, u, vb, u, vb }, dir == -1 ? SPRITE_SHADE_LEFT : SPRITE_SHADE_RIGHT);
                    y += run;
                }
            }
        for (int y = 0; y < h; y++)
            foreach (int dir in new[] { -1, 1 })
            {
                int x = 0;
                while (x < w)
                {
                    if (!IsSolid(x, y) || IsSolid(x, y + dir)) { x++; continue; }
                    int run = 1;
                    while (x + run < w && IsSolid(x + run, y) && !IsSolid(x + run, y + dir)) run++;
                    int wy = dir == -1 ? y : y + 1;
                    double v = (y + (dir == -1 ? IN : 1 - IN)) / h;
                    double ua = (x + IN) / w, ub = (x + run - IN) / w;
                    wallQuads.Add(new double[] { x, wy, 0, x + run, wy, 0, x + run, wy, depth, x, wy, depth },
                        new[] { ua, v, ub, v, ub, v, ua, v }, dir == -1 ? SPRITE_SHADE_TOP : SPRITE_SHADE_BOTTOM);
                    x += run;
                }
            }
        var positions = new List<double>(); var colors = new List<double>(); var uvs = new List<double>(); var indices = new List<int>();
        foreach (var q in backQuads.Items.Concat(wallQuads.Items).Concat(frontQuads.Items))
        {
            int b = positions.Count / 3;
            for (int i = 0; i < 4; i++)
            {
                positions.Add(q.P[i * 3]); positions.Add(q.P[i * 3 + 1]); positions.Add(q.P[i * 3 + 2]);
                colors.Add(q.Shade); colors.Add(q.Shade); colors.Add(q.Shade);
                uvs.Add(q.Uv[i * 2]); uvs.Add(q.Uv[i * 2 + 1]);
            }
            indices.AddRange(new[] { b, b + 1, b + 2, b, b + 2, b + 3 });
        }
        if (indices.Count == 0) return null;
        return MakeGeometry(positions, colors, uvs, indices);
    }
}
