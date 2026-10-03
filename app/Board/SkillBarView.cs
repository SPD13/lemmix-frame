using System.Collections.Generic;
using Godot;
using Lemmix.Render;

namespace Lemmix.App.Board;

// The skill bar in Godot (web/3d/js/gui.js GuiPanel's scene objects): the data side is
// Lemmix.Render.SkillBar; this node mirrors its objects - the panel plane with the panel's pixels
// as a texture, the socket, the raised copy of the hovered button (the texture cropped), the
// relief meshes (one per button or half, the counters strip, the raised one) and the minimap's
// plane - and turns ray hits into the bar's UV events. Positions and scales are the bar's, in this
// node's space (the web's parent is the camera rig; three's axes are Godot's).
//
// Materials: three.js r147 MeshBasicMaterial without colour management - texel x vertex colour
// multiplied raw - so ALBEDO = srgb_to_linear(raw product). The bar is an overlay: no depth test,
// drawn after the world in the three.js render orders (50 panel .. 54 raised relief), which here
// are render priorities in the transparent pass (alpha 1).
public partial class SkillBarView : Node3D
{
    const string ShaderCode = @"shader_type spatial;
render_mode unshaded, cull_disabled, depth_test_disabled, depth_draw_never, blend_mix, shadows_disabled;
uniform sampler2D map : filter_nearest, repeat_disable;
uniform bool use_map = true;
uniform bool vertex_colors = false;
uniform vec3 color = vec3(1.0);
uniform vec4 crop = vec4(0.0, 0.0, 1.0, 1.0); // u0, v0, du, dv of the texture (top-down)
vec3 srgb_to_lin(vec3 c) { return mix(c / 12.92, pow((c + 0.055) / 1.055, vec3(2.4)), step(vec3(0.04045), c)); }
void fragment() {
    vec3 c = color;
    if (use_map) c *= texture(map, crop.xy + UV * crop.zw).rgb;
    if (vertex_colors) c *= COLOR.rgb;
    ALBEDO = srgb_to_lin(c);
    ALPHA = 1.0;
}";
    static Shader? _shader;
    static Shader BarShader => _shader ??= new Shader { Code = ShaderCode };

    public readonly SkillBar Bar;
    readonly ImageTexture _panelTex;
    ImageTexture? _mapTex;
    Image? _mapImage;                   // the minimap's picture, refilled in place
    Image _panelImage;
    int _textureVersion = -1, _mapVersion = -1;

    readonly MeshInstance3D _panel, _hoverTile, _socket, _hoverRelief, _text;
    MeshInstance3D? _minimap;
    readonly ShaderMaterial _panelMat, _hoverMat, _socketMat, _reliefMat, _hoverReliefMat;
    ShaderMaterial? _mapMat;
    readonly List<MeshInstance3D> _tiles = new();
    readonly Dictionary<ChunkGeometry, ArrayMesh?> _meshes = new(ReferenceEqualityComparer.Instance);

    public SkillBarView(SkillBar bar)
    {
        Bar = bar;
        Name = "SkillBar";
        _panelImage = Image.CreateFromData(bar.CanvasWidth, bar.CanvasHeight, false, Image.Format.Rgba8, bar.Canvas);
        _panelTex = ImageTexture.CreateFromImage(_panelImage);
        _panelMat = Material(_panelTex, false, SkillBar.GUI_ORDER_PANEL);
        _hoverMat = Material(_panelTex, false, SkillBar.GUI_ORDER_HOVER);
        _socketMat = Material(null, false, SkillBar.GUI_ORDER_SOCKET);
        var sc = SkillBar.SOCKET_COLOR;
        _socketMat.SetShaderParameter("color", new Vector3(((sc >> 16) & 255) / 255f, ((sc >> 8) & 255) / 255f, (sc & 255) / 255f));
        _reliefMat = Material(_panelTex, true, SkillBar.GUI_ORDER_RELIEF);
        _hoverReliefMat = Material(_panelTex, true, SkillBar.GUI_ORDER_HOVER_RELIEF);
        _panel = Quad("Panel", _panelMat);
        _socket = Quad("Socket", _socketMat);
        _hoverTile = Quad("HoverTile", _hoverMat);
        _text = new MeshInstance3D { Name = "Text", MaterialOverride = _reliefMat };
        _hoverRelief = new MeshInstance3D { Name = "HoverRelief", MaterialOverride = _hoverReliefMat };
        AddChild(_text);
        AddChild(_hoverRelief);
    }

    static ShaderMaterial Material(Texture2D? map, bool vertexColors, int order)
    {
        var m = new ShaderMaterial { Shader = BarShader, RenderPriority = order - 50 };
        if (map != null) m.SetShaderParameter("map", map);
        m.SetShaderParameter("use_map", map != null);
        m.SetShaderParameter("vertex_colors", vertexColors);
        return m;
    }

    MeshInstance3D Quad(string name, Material mat)
    {
        var q = new MeshInstance3D { Name = name, Mesh = new QuadMesh { Size = Vector2.One }, MaterialOverride = mat, Visible = false };
        AddChild(q);
        return q;
    }

    static void Apply(Node3D node, BarObject? o)
    {
        if (o == null) { node.Visible = false; return; }
        node.Visible = o.Visible;
        node.Position = new Vector3((float)o.Px, (float)o.Py, (float)o.Pz);
        node.Scale = new Vector3((float)o.Sx, (float)o.Sy, (float)o.Sz);
    }

    // the counters' relief changes with the text: only the current one is kept
    ChunkGeometry? _textGeometry;
    ArrayMesh? _textMesh;
    ArrayMesh? TextMeshOf(ChunkGeometry? g)
    {
        if (ReferenceEquals(g, _textGeometry)) return _textMesh;
        _textGeometry = g;
        _textMesh?.Dispose(); // the managed handle on the old one (the node holds its own until it gets the new)
        _textMesh = g == null ? null : ToMesh(g);
        return _textMesh;
    }

    ArrayMesh? MeshOf(ChunkGeometry? g)
    {
        if (g == null) return null;
        if (_meshes.TryGetValue(g, out var m)) return m;
        m = ToMesh(g);
        if (_meshes.Count > 512) _meshes.Clear(); // the digits rebuild their tiles: forget the old ones
        _meshes[g] = m;
        return m;
    }

    // A relief geometry as an ArrayMesh: positions in panel pixels (y down, flipped by the
    // object's scale), UVs top-down as Godot reads an image, the shade as the vertex colour.
    static ArrayMesh? ToMesh(ChunkGeometry g)
    {
        if (g.VertexCount == 0 || g.Indices.Length == 0) return null;
        int n = g.VertexCount;
        var pos = new Vector3[n]; var uv = new Vector2[n]; var col = new Color[n];
        for (int v = 0; v < n; v++)
        {
            pos[v] = new Vector3(g.Positions[3 * v], g.Positions[3 * v + 1], g.Positions[3 * v + 2]);
            uv[v] = new Vector2(g.Uvs[2 * v], g.Uvs[2 * v + 1]);
            col[v] = g.Colors != null ? new Color(g.Colors[3 * v], g.Colors[3 * v + 1], g.Colors[3 * v + 2]) : Colors.White;
        }
        using var arrays = new Godot.Collections.Array(); // let go now, not by a finalizer
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = pos;
        arrays[(int)Mesh.ArrayType.TexUV] = uv;
        arrays[(int)Mesh.ArrayType.Color] = col;
        arrays[(int)Mesh.ArrayType.Index] = g.Indices;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    // Once per frame, after Bar.Update(): the texture when the panel redrew, every object's state.
    public void Sync()
    {
        var bar = Bar;
        if (bar.TextureVersion != _textureVersion)
        {
            _textureVersion = bar.TextureVersion;
            if (_panelImage.GetWidth() != bar.CanvasWidth || _panelImage.GetHeight() != bar.CanvasHeight)
            {
                _panelImage = Image.CreateFromData(bar.CanvasWidth, bar.CanvasHeight, false, Image.Format.Rgba8, bar.Canvas);
                _panelTex.SetImage(_panelImage);
            }
            else
            {
                _panelImage.SetData(bar.CanvasWidth, bar.CanvasHeight, false, Image.Format.Rgba8, bar.Canvas);
                _panelTex.Update(_panelImage);
            }
        }
        Apply(_panel, bar.Mesh);
        Apply(_socket, bar.Socket);
        Apply(_hoverTile, bar.HoverTile);
        // three's repeat/offset are y-up on a flipped texture: top-down, the crop starts at 1 - offset - repeat
        _hoverMat.SetShaderParameter("crop", new Vector4((float)bar.HoverOffsetX, (float)(1 - bar.HoverOffsetY - bar.HoverRepeatY), (float)bar.HoverRepeatX, (float)bar.HoverRepeatY));
        if (bar.TileReliefs != null)
        {
            while (_tiles.Count < bar.TileReliefs.Count)
            {
                var t = new MeshInstance3D { Name = "Relief" + _tiles.Count, MaterialOverride = _reliefMat };
                AddChild(t);
                _tiles.Add(t);
            }
            for (int i = 0; i < bar.TileReliefs.Count; i++)
            {
                var o = bar.TileReliefs[i];
                Apply(_tiles[i], o);
                _tiles[i].Mesh = MeshOf(o.Geometry);
            }
        }
        Apply(_text, bar.TextMesh);
        _text.Mesh = TextMeshOf(bar.TextMesh?.Geometry);
        Apply(_hoverRelief, bar.HoverRelief);
        _hoverRelief.Mesh = MeshOf(bar.HoverRelief?.Geometry);
        if (bar.Minimap != null && bar.MinimapPlane != null)
        {
            var mm = bar.Minimap;
            if (_minimap == null)
            {
                var img = Image.CreateFromData(mm.Spec.W, mm.Spec.H, false, Image.Format.Rgba8, mm.View);
                _mapTex = ImageTexture.CreateFromImage(img);
                _mapMat = Material(_mapTex, false, SkillBar.MINIMAP_ORDER);
                _minimap = Quad("Minimap", _mapMat);
            }
            if (mm.Version != _mapVersion)
            {
                _mapVersion = mm.Version;
                if (_mapImage == null) _mapImage = Image.CreateFromData(mm.Spec.W, mm.Spec.H, false, Image.Format.Rgba8, mm.View);
                else _mapImage.SetData(mm.Spec.W, mm.Spec.H, false, Image.Format.Rgba8, mm.View);
                _mapTex!.Update(_mapImage);
            }
            Apply(_minimap, bar.MinimapPlane);
        }
    }

    // Where a ray (global space) meets the panel plane, as the plane's UV (three's: x right, y up), or null.
    public Vector2? HitUv(Vector3 origin, Vector3 dir)
    {
        var m = Bar.Mesh;
        if (m == null || !_panel.IsInsideTree()) return null;
        var inv = _panel.GlobalTransform.AffineInverse();
        var o = inv * origin;
        var d = inv.Basis * dir;
        if (Mathf.IsZeroApprox(d.Z)) return null;
        float t = -o.Z / d.Z;
        if (t < 0) return null;
        var p = o + d * t;
        if (p.X < -0.5f || p.X > 0.5f || p.Y < -0.5f || p.Y > 0.5f) return null;
        return new Vector2(p.X + 0.5f, p.Y + 0.5f);
    }

    static (double, double)? Uv(Vector2? uv) => uv is { } u ? (u.X, u.Y) : null;

    // The pointer over the bar (or off it: null): the hovered button rises, a minimap drag follows.
    public void PointerMove(Vector2? uv)
    {
        Bar.SetHover(Uv(uv));
        Bar.OnMouseMove(Uv(uv));
    }

    // A press: 0 left (trigger), 1 middle, 2 right.
    public void PointerDown(Vector2 uv, int button) => Bar.OnMouseDown((uv.X, uv.Y), button);
    public void PointerUp(Vector2? uv) => Bar.OnMouseUp(Uv(uv));
    public void DoubleClick(Vector2 uv) => Bar.OnDoubleClick((uv.X, uv.Y));
}
