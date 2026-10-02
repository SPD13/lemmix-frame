using System;
using System.Collections.Generic;
using Godot;
using Lemmix.Engine;
using Lemmix.Render;

namespace Lemmix.App.Board;

// app.js makeClearPhysicsOverlay: clear physics mode's own layer, the level's size, over the
// terrain - NeoLemmix's trigger areas (a pink checker, darker over terrain and darker still over
// steel, darker where two overlap), a blocker's two fields, the gold-and-orange mark at each
// hatch's spawn point. Repainted every tick the mode is on, hidden otherwise. Transparent at 0.75,
// no depth test, renderOrder -5: first among the transparent things.
public sealed partial class ClearPhysicsOverlay : MeshInstance3D
{
    public static readonly HashSet<string> CPM_NO_TRIGGER = new(StringComparer.Ordinal)
        { "NONE", "WINDOW", "BACKGROUND", "PAINT", "BLOCKER", "ONEWAYLEFT", "ONEWAYRIGHT", "ONEWAYDOWN", "ONEWAYUP" };
    public const int PM_ONEWAYFLAGS = 0x78;

    readonly Level _level;
    public readonly byte[] Data;
    readonly ImageTexture _tex;
    readonly Image _image;
    readonly int _w, _h;

    public ClearPhysicsOverlay(Level level)
    {
        Name = "cpm-overlay";
        _level = level;
        _w = level.Width; _h = level.Height;
        Data = new byte[_w * _h * 4];
        _image = Image.CreateFromData(_w, _h, false, Image.Format.Rgba8, Data);
        _tex = ImageTexture.CreateFromImage(_image);
        Mesh = BoardMaterials.ToMesh(ReplayMarkers.PlaneGeometry(1, 1));
        MaterialOverride = BasicShader.Material(new BasicKey(true, false, false, Side.Double, Filter.Nearest, false, GammaBlend: true), 0xffffff, 0.75, _tex, renderOrder: -5);
        Transform = BoardMaterials.Place(_w / 2.0, _h / 2.0, BoardZ.OBJECT_DECAL_Z + 0.5, scaleX: _w, scaleY: _h);
        Visible = false;
        CastShadow = ShadowCastingSetting.Off;
    }

    // cpmHighlightBits: the one-way wall under the pointer, as its direction bits
    public static int HighlightBits(Level level, (int X, int Y)? cursor)
    {
        if (cursor is not { } c) return 0;
        if (c.X < 0 || c.Y < 0 || c.X >= level.Width || c.Y >= level.Height) return 0;
        return level.Physics[c.X + c.Y * level.Width] & PM_ONEWAYFLAGS;
    }

    void Put(int x, int y, byte r, byte g, byte b)
    {
        if (x < 0 || y < 0 || x >= _w || y >= _h) return;
        int i = (y * _w + x) * 4;
        Data[i] = r; Data[i + 1] = g; Data[i + 2] = b; Data[i + 3] = 255;
    }

    void TriggerRect(int x0, int y0, int x1, int y1)
    {
        var phys = _level.Physics;
        for (int y = Math.Max(0, y0); y < Math.Min(_h, y1); y++)
            for (int x = Math.Max(0, x0); x < Math.Min(_w, x1); x++)
            {
                int i = (y * _w + x) * 4, bits = phys[y * _w + x];
                bool present = Data[i + 3] != 0;
                int c = (bits & 1) == 0 ? 0xFF : (bits & 2) != 0 ? 0x60 : 0xA0;
                if (((x - y) & 1) != 0) c -= 0x20;
                if (present) c -= 0x30;
                Data[i] = (byte)c; Data[i + 1] = 0; Data[i + 2] = (byte)c; Data[i + 3] = 255;
            }
    }

    public void UpdateFor(Game game)
    {
        bool on = game.ClearPhysics;
        Visible = on;
        if (!on) return;
        Array.Clear(Data);
        foreach (var g in _level.Gadgets)
        {
            if (g.OffMap || CPM_NO_TRIGGER.Contains(g.Effect)) continue;
            if ((g.EffectBase == "TELEPORT" || g.EffectBase == "RECEIVER") && g.PairingId < 0) continue;
            var t = g.TriggerRect;
            TriggerRect(t.X0, t.Y0, t.X1, t.Y1);
        }
        foreach (var L in game.Sim.Lemmings)
        {
            if (L.Removed || L.Action != BA.BLOCKING) continue;
            int left = L.X - 6;
            if (L.Dx == 1) left++;
            int top = L.Y - 6;
            TriggerRect(left, top, left + 4, top + 11);
            TriggerRect(left + 8, top, left + 12, top + 11);
        }
        foreach (var g in _level.Gadgets)
        {
            if (g.OffMap || g.EffectBase != "WINDOW") continue;
            int x = g.TriggerRect.X0, y = g.TriggerRect.Y0;
            Put(x, y, 0xFF, 0xD7, 0x00);
            Put(x - 1, y, 0xFF, 0x45, 0x00); Put(x + 1, y, 0xFF, 0x45, 0x00);
            Put(x, y - 1, 0xFF, 0x45, 0x00); Put(x, y + 1, 0xFF, 0x45, 0x00);
        }
        _image.SetData(_w, _h, false, Image.Format.Rgba8, Data);
        _tex.Update(_image);
    }
}

// app.js makeShadowOverlay: NeoLemmix's skill shadows as translucent volumes - the terrain a
// tunnel or crater would take cut through the whole slab (dark), the bricks a builder would lay
// as slab-deep blocks (light), a path as a thin ribbon at the lemmings' depth - each the greedy
// relief the sprites use, drawn over everything without a depth test (renderOrder -4). Rebuilt
// when the lemming, the skill or the frame changes; NeoLemmix's masking kept.
public sealed partial class ShadowOverlay : Node3D
{
    sealed class Part { public required MeshInstance3D Mesh; public double Depth, Z; }
    readonly Level _level;
    readonly Part _cuts, _bricks, _paths;
    object? _lastKey;
    public int Rebuilds { get; private set; }

    public ShadowOverlay(Level level)
    {
        Name = "skill-shadows";
        _level = level;
        Part Make(string name, int color, double opacity, double depth, double z)
        {
            var m = new MeshInstance3D
            {
                Name = name, Visible = false, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                MaterialOverride = BasicShader.Material(new BasicKey(true, false, false, Side.Double, Filter.Nearest, false), color, opacity, vertexColors: true, renderOrder: -4),
            };
            AddChild(m);
            return new Part { Mesh = m, Depth = depth, Z = z };
        }
        _cuts = Make("cuts", 0x585858, 0.62, TerrainMesh.TERRAIN_DEPTH, 0);
        _bricks = Make("bricks", 0xd8d8d8, 0.55, TerrainMesh.TERRAIN_DEPTH, 0);
        _paths = Make("paths", 0xe8e8e8, 0.85, SpriteBuild.SPRITE_DEPTH, BoardZ.LEMMING_Z);
    }

    public MeshInstance3D Cuts => _cuts.Mesh;
    public MeshInstance3D Bricks => _bricks.Mesh;
    public MeshInstance3D Paths => _paths.Mesh;

    bool Keep(int x, int y, bool overTerrain)
    {
        int bits = _level.Physics[y * _level.Width + x];
        bool solid = (bits & 1) != 0;
        return overTerrain ? (solid && (bits & 2) == 0) : !solid;
    }

    void Rebuild(Part p, List<int[]> pixels, bool overTerrain)
    {
        int x0 = int.MaxValue, y0 = int.MaxValue, x1 = int.MinValue, y1 = int.MinValue, n = 0;
        foreach (var px in pixels)
        {
            if (!Keep(px[0], px[1], overTerrain)) continue;
            n++;
            x0 = Math.Min(x0, px[0]); y0 = Math.Min(y0, px[1]); x1 = Math.Max(x1, px[0]); y1 = Math.Max(y1, px[1]);
        }
        if (n == 0) { p.Mesh.Mesh = null; p.Mesh.Visible = false; return; }
        int bw = x1 - x0 + 1, bh = y1 - y0 + 1;
        var grid = new byte[bw * bh];
        foreach (var px in pixels) if (Keep(px[0], px[1], overTerrain)) grid[(px[1] - y0) * bw + (px[0] - x0)] = 1;
        var g = SpriteBuild.BuildExtrudedSpriteGeometry((x, y) => grid[y * bw + x] != 0, bw, bh, p.Depth);
        p.Mesh.Mesh = g == null ? null : BoardMaterials.ToMesh(g);
        p.Mesh.Transform = BoardMaterials.Place(x0, y0, p.Z);
        p.Mesh.Visible = true;
    }

    // update(sim, lem, skill, key): null lemming or skill hides them; an unchanged key keeps them
    public void UpdateFor(LemGame sim, Lemming? lem, string? skill, object? key)
    {
        if (lem == null || skill == null) { _cuts.Mesh.Visible = _bricks.Mesh.Visible = _paths.Mesh.Visible = false; _lastKey = null; return; }
        if (Equals(key, _lastKey)) return;
        _lastKey = key;
        Rebuilds++;
        var shadow = Shadows.Compute(sim, lem, skill);
        Rebuild(_cuts, shadow.High, true);
        Rebuild(_bricks, shadow.Bricks, false);
        Rebuild(_paths, shadow.Low, false);
    }
}

// web/3d/js/replay-markers.js, the scene half: the record's markers (ReplayMarkers.Set) as planes
// in the worldGroup - a ring at the spot (renderOrder 20), the outlined picture beside it (21),
// a hatch's release rate under its icon and the countdown under the picture (22), all
// transparent with no depth test, each plane flipped back upright. The countdown and the release
// rate are text: drawn with a Label3D in the bundled Noto Sans Mono Bold over the label's dark box
// (the browser's monospace differs).
public sealed partial class MarkersView : Node3D
{
    public const int LabelW = 48, LabelH = 20;
    readonly ReplayMarkers.Set _set;
    readonly Game _game;
    readonly ArrayMesh _ring;
    readonly Dictionary<string, ImageTexture> _icons = new(StringComparer.Ordinal);
    readonly Dictionary<(double, double), ArrayMesh> _planes = new();
    sealed class Nodes
    {
        public required ShaderMaterial Ring;
        public ShaderMaterial? Icon, TextBox;
        public Label3D? TextLabel;
        public required Node3D Countdown;
        public required ShaderMaterial CountdownBox;
        public required Label3D CountdownText;
        public int Seconds = -1;
    }
    readonly List<Nodes> _nodes = new();
    int _builtFor = -1;
    static FontFile? _font;
    public bool Hidden;

    public ReplayMarkers.Set Set => _set;
    public int MarkerCount => _nodes.Count;

    public MarkersView(Level level, Game game, double z)
    {
        Name = "replay-markers";
        _game = game;
        // the panel supplies the pictures (game.gui, whichever panel is the game's when a marker is made)
        _set = new ReplayMarkers.Set(level, z,
            name => (game.Gui as Lemmix.Ui.GamePanel)?.SkillIcon(name),
            name => (game.Gui as Lemmix.Ui.GamePanel)?.Assets?[name]);
        _ring = BoardMaterials.ToMesh(_set.RingGeometry)!;
        Visible = false;
    }

    static Font Font()
    {
        if (_font != null) return _font;
        _font = GD.Load<FontFile>("res://Ui/Fonts/NotoSansMono-Bold.ttf");
        return _font;
    }

    static BasicKey Key(bool linear) => new(true, false, false, Side.Double, linear ? Filter.Linear : Filter.Nearest, false);

    ArrayMesh Plane(double w, double h)
    {
        if (!_planes.TryGetValue((w, h), out var m)) { m = BoardMaterials.ToMesh(ReplayMarkers.PlaneGeometry(w, h))!; _planes[(w, h)] = m; }
        return m;
    }

    MeshInstance3D AddPlane(ReplayMarkers.Plane p, ShaderMaterial mat)
    {
        var mi = new MeshInstance3D { Mesh = Plane(p.W, p.H), MaterialOverride = mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        mi.Transform = BoardMaterials.Place(p.X, p.Y, p.Z, -1); // upright in a y-down group
        AddChild(mi);
        return mi;
    }

    // a labelCanvas: the 48 x 20 box, rgba(10, 10, 14, 0.8), with the text in #ffd866 bold 15px,
    // on a 12 x 5 plane: the box as a plane, the text as a Label3D a canvas pixel down
    (ShaderMaterial Box, Label3D Text) AddLabel(ReplayMarkers.Plane p, int priority, Node3D parent)
    {
        var box = BasicShader.Material(Key(true), 0x0a0a0e, 0.8, renderOrder: priority);
        var mi = new MeshInstance3D { Mesh = Plane(p.W, p.H), MaterialOverride = box, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        parent.AddChild(mi);
        double px = p.W / LabelW; // world units per canvas pixel
        var text = new Label3D
        {
            Font = Font(), FontSize = 15, PixelSize = (float)px, Modulate = new Color(1, 0xd8 / 255f, 0x66 / 255f),
            NoDepthTest = true, RenderPriority = Math.Min(127, priority + 1), OutlineSize = 0, Shaded = false, DoubleSided = true,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Position = new Vector3(0, (float)(-1 * px), 0.01f), TextureFilter = BaseMaterial3D.TextureFilterEnum.Linear,
        };
        parent.AddChild(text);
        return (box, text);
    }

    ImageTexture IconTexture(ReplayMarkers.Icon icon)
    {
        if (_icons.TryGetValue(icon.Key, out var t)) return t;
        t = BoardMaterials.TextureOf(icon.Picture.Data, icon.Picture.Width, icon.Picture.Height, flipY: true); // a CanvasTexture: flipY
        _icons[icon.Key] = t;
        return t;
    }

    void Clear()
    {
        foreach (var c in GetChildren()) { RemoveChild(c); c.QueueFree(); }
        _nodes.Clear();
    }

    void Build()
    {
        Clear();
        foreach (var m in _set.Markers)
        {
            var ring = BasicShader.Material(Key(false), ReplayMarkers.MARKER_COLOR, 1, renderOrder: 20);
            var ri = new MeshInstance3D { Mesh = _ring, MaterialOverride = ring, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            ri.Transform = BoardMaterials.Place(m.Ring.X, m.Ring.Y, m.Ring.Z);
            AddChild(ri);
            ShaderMaterial? icon = null, textBox = null;
            Label3D? textLabel = null;
            if (m.Icon != null && m.IconPlane != null)
            {
                icon = BasicShader.Material(Key(false), 0xffffff, 1, IconTexture(m.Icon), renderOrder: 21);
                AddPlane(m.IconPlane, icon);
                if (m.TextLabel != null && m.TextPlane != null)
                {
                    var holder = new Node3D { Transform = BoardMaterials.Place(m.TextPlane.X, m.TextPlane.Y, m.TextPlane.Z, -1) };
                    AddChild(holder);
                    (textBox, textLabel) = AddLabel(m.TextPlane, 22, holder);
                    textLabel.Text = m.TextLabel.Text;
                }
            }
            var cd = new Node3D { Transform = BoardMaterials.Place(m.Countdown.X, m.Countdown.Y, m.Countdown.Z, -1), Visible = false };
            AddChild(cd);
            var (cdBox, cdText) = AddLabel(m.Countdown, 22, cd);
            _nodes.Add(new Nodes { Ring = ring, Icon = icon, TextBox = textBox, TextLabel = textLabel, Countdown = cd, CountdownBox = cdBox, CountdownText = cdText });
        }
        _builtFor = _set.BuiltFor;
    }

    // update(now): build, clear or restyle the markers to the game's state
    public void UpdateFor(double now)
    {
        var sim = _game.Sim;
        _set.Hidden = Hidden;
        _set.Update(_game.ReplayEngaged, sim.Recorded, sim.RecordVersion, sim.CurrentIteration, now);
        if (!_set.Engaged)
        {
            if (_nodes.Count > 0) Clear();
            _builtFor = -1;
            Visible = false;
            return;
        }
        if (_builtFor != _set.BuiltFor || _nodes.Count != _set.Markers.Count) Build();
        Visible = _set.Visible;
        for (int i = 0; i < _nodes.Count; i++)
        {
            var m = _set.Markers[i];
            var n = _nodes[i];
            n.Ring.SetShaderParameter("opacity", (float)m.Opacities[0]);
            int k = 1;
            if (n.Icon != null) n.Icon.SetShaderParameter("opacity", (float)m.Opacities[k++]);
            if (n.TextBox != null)
            {
                float o = (float)m.Opacities[k++];
                n.TextBox.SetShaderParameter("opacity", 0.8f * o);
                n.TextLabel!.Modulate = n.TextLabel.Modulate with { A = o };
            }
            n.Countdown.Visible = m.LabelVisible;
            if (!m.LabelVisible) continue;
            if (m.LabelSeconds != n.Seconds)
            {
                n.Seconds = m.LabelSeconds;
                n.CountdownText.Text = ReplayMarkers.Set.CountdownLabel(m.LabelSeconds).Text;
            }
            n.CountdownBox.SetShaderParameter("opacity", 0.8f * (float)m.LabelOpacity);
            n.CountdownText.Modulate = n.CountdownText.Modulate with { A = (float)m.LabelOpacity };
        }
    }
}
