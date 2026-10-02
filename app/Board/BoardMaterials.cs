using System;
using System.Collections.Generic;
using Godot;
using Lemmix.Render;

namespace Lemmix.App.Board;

// three.js r147's MeshBasicMaterial as the board uses it, in Godot: unshaded, the colour as
// three computes it without colour management (diffuse x texel x vertex colour, raw), decoded
// once at the end (RawColor's srgb_to_lin) so Godot's sRGB output lands on the same byte.
// Render state is compile-time in Godot, so a shader is made per combination (BasicKey) and kept.
//
// three draws opaque things first (renderOrder, then near to far), then transparent ones
// (renderOrder, then far to near). Godot sorts only its transparent pass by render_priority, so
// a material that three draws in the opaque pass with depthTest off (the hover ring) goes into
// Godot's transparent pass at the lowest priority: after every opaque thing, before the rest.
//
// Culling: three's front face is counter-clockwise, Godot's clockwise; both flip it under a
// mirrored transform (the worldGroup's y flip). So three's FrontSide is Godot's cull_front, its
// BackSide Godot's cull_back, DoubleSide cull_disabled.
public enum Side { Front, Back, Double }
public enum Filter { Nearest, Linear, NearestMipmap, LinearMipmap }

// GammaBlend (transparent only): three blends raw, sRGB-encoded values; Godot blends linear ones.
// With it the material reads what is under it (the screen texture: the opaque pass) and does
// three's blend itself, writing the result opaque - exact over opaque things, at the price of a
// screen copy in the frames it is drawn (the clear-physics layer only, which is a mode).
public readonly record struct BasicKey(bool Transparent, bool DepthTest, bool DepthWrite, Side Side, Filter Filter, bool Repeat, bool GammaBlend = false);

public static class BasicShader
{
    const string Srgb = "vec3 srgb_to_lin(vec3 c) { return mix(c / 12.92, pow((c + 0.055) / 1.055, vec3(2.4)), step(vec3(0.04045), c)); }\n"
        + "vec3 lin_to_srgb(vec3 c) { c = max(c, vec3(0.0)); return mix(c * 12.92, 1.055 * pow(c, vec3(1.0 / 2.4)) - 0.055, step(vec3(0.0031308), c)); }\n";
    static readonly Dictionary<BasicKey, Shader> Shaders = new();

    public static Shader Get(BasicKey k)
    {
        if (Shaders.TryGetValue(k, out var s)) return s;
        var modes = new List<string> { "unshaded" };
        modes.Add(k.Side switch { Side.Front => "cull_front", Side.Back => "cull_back", _ => "cull_disabled" });
        if (k.Transparent) modes.Add("blend_mix");
        modes.Add(k.DepthWrite && !k.Transparent ? "depth_draw_opaque" : k.DepthWrite ? "depth_draw_always" : "depth_draw_never");
        if (!k.DepthTest) modes.Add("depth_test_disabled");
        string filter = k.Filter switch
        {
            Filter.Linear => "filter_linear", Filter.NearestMipmap => "filter_nearest_mipmap",
            Filter.LinearMipmap => "filter_linear_mipmap", _ => "filter_nearest",
        };
        string code = "shader_type spatial;\nrender_mode " + string.Join(", ", modes) + ";\n"
            + "uniform sampler2D map : " + filter + ", " + (k.Repeat ? "repeat_enable" : "repeat_disable") + ";\n"
            + @"uniform bool use_map = false;
uniform bool vertex_colors = false;
uniform vec3 color = vec3(1.0);
uniform float opacity = 1.0;
uniform float alpha_test = 0.0;
uniform vec2 uv_repeat = vec2(1.0);
uniform vec2 uv_offset = vec2(0.0);
uniform bool clamp_v = false; // wrapT ClampToEdge on a texture that repeats in s
" + (k.GammaBlend ? "uniform sampler2D screen_tex : hint_screen_texture, filter_nearest;\n" : "") + Srgb + @"
void fragment() {
    vec4 d = vec4(color, opacity);
    if (use_map) {
        vec2 uv = UV * uv_repeat + uv_offset;
        if (clamp_v) { float h = float(textureSize(map, 0).y); uv.y = clamp(uv.y, 0.5 / h, 1.0 - 0.5 / h); }
        d *= texture(map, uv);
    }
    if (vertex_colors) d.rgb *= COLOR.rgb;
    if (alpha_test > 0.0 && d.a < alpha_test) discard;
    ALBEDO = srgb_to_lin(d.rgb);
" + (k.Transparent && k.GammaBlend
            ? "    if (d.a <= 0.0) discard;\n    vec3 under = lin_to_srgb(textureLod(screen_tex, SCREEN_UV, 0.0).rgb);\n    ALBEDO = srgb_to_lin(mix(under, d.rgb, d.a));\n    ALPHA = 1.0;\n"
            : k.Transparent ? "    ALPHA = d.a;\n" : "") + "}\n";
        s = new Shader { Code = code };
        Shaders[k] = s;
        return s;
    }

    // a MeshBasicMaterial: colour 0xRRGGBB, opacity, map, vertex colours, alpha test; render state in the key
    public static ShaderMaterial Material(BasicKey key, int color = 0xffffff, double opacity = 1, Texture2D? map = null,
        bool vertexColors = false, double alphaTest = 0, int renderOrder = 0)
    {
        var m = new ShaderMaterial { Shader = Get(key) };
        SetColor(m, color);
        m.SetShaderParameter("opacity", (float)opacity);
        m.SetShaderParameter("use_map", map != null);
        if (map != null) m.SetShaderParameter("map", map);
        m.SetShaderParameter("vertex_colors", vertexColors);
        m.SetShaderParameter("alpha_test", (float)alphaTest);
        m.RenderPriority = Priority(renderOrder, key);
        return m;
    }

    public static void SetColor(ShaderMaterial m, int hex) =>
        m.SetShaderParameter("color", new Vector3(((hex >> 16) & 255) / 255f, ((hex >> 8) & 255) / 255f, (hex & 255) / 255f));

    public static void SetMap(ShaderMaterial m, Texture2D? map)
    {
        m.SetShaderParameter("use_map", map != null);
        m.SetShaderParameter("map", map!);
    }

    // three's renderOrder as Godot's render_priority (transparent pass only); an opaque-pass
    // material without a depth test (the ring) is made transparent at OpaquePassLast instead
    public const int OpaquePassLast = -128;
    public static int Priority(int renderOrder, BasicKey key) => Math.Clamp(renderOrder, -128, 127);

    public static Color Rgb(int hex) => new(((hex >> 16) & 255) / 255f, ((hex >> 8) & 255) / 255f, (hex & 255) / 255f);
}

// Godot resources for the core's described materials and buffers, made once and kept per object:
// a SpriteMaterial (bridge.js SpriteGeometryCache's MeshBasicMaterial: map, vertex colours, an
// alpha test, DoubleSide, opaque) becomes a ShaderMaterial, its SpriteTexture an ImageTexture,
// a GeometryBuffers an ArrayMesh. The flat silhouettes' one colour (SetFlatColor) is followed by
// SyncTints, which only touches the materials whose colour moved.
public sealed class BoardMaterials
{
    static readonly BasicKey SpriteNearest = new(false, true, true, Side.Double, Filter.Nearest, false);
    static readonly BasicKey SpriteLinear = new(false, true, true, Side.Double, Filter.Linear, false);

    readonly Dictionary<SpriteMaterial, (ShaderMaterial M, int Color)> _materials = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<SpriteTexture, ImageTexture> _textures = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<GeometryBuffers, ArrayMesh> _meshes = new(ReferenceEqualityComparer.Instance);
    readonly List<SpriteMaterial> _tinted = new();

    public int MaterialCount => _materials.Count;
    public int MeshCount => _meshes.Count;

    public ShaderMaterial For(SpriteMaterial sm)
    {
        if (_materials.TryGetValue(sm, out var e)) return e.M;
        var m = BasicShader.Material(sm.Map.Linear ? SpriteLinear : SpriteNearest, sm.Color, 1, Texture(sm.Map),
            sm.VertexColors, sm.AlphaTest);
        _materials[sm] = (m, sm.Color);
        if (!sm.VertexColors) _tinted.Add(sm); // the flat silhouettes (the only ones with a colour)
        return m;
    }

    // setFlatColor reached the described materials; follow it on ours
    public void SyncTints()
    {
        foreach (var sm in _tinted)
        {
            var e = _materials[sm];
            if (e.Color == sm.Color) continue;
            BasicShader.SetColor(e.M, sm.Color);
            _materials[sm] = (e.M, sm.Color);
        }
    }

    public ImageTexture Texture(SpriteTexture t)
    {
        if (_textures.TryGetValue(t, out var tex)) return tex;
        tex = ImageTexture.CreateFromImage(Image.CreateFromData(t.Width, t.Height, false, Image.Format.Rgba8, t.Rgba));
        _textures[t] = tex;
        return tex;
    }

    public ArrayMesh? Mesh(GeometryBuffers? g)
    {
        if (g == null) return null;
        if (_meshes.TryGetValue(g, out var m)) return m;
        m = ToMesh(g);
        if (m != null) _meshes[g] = m;
        return m;
    }

    // A BufferGeometry as an ArrayMesh, one surface (the geometries here have no groups)
    public static ArrayMesh? ToMesh(GeometryBuffers g)
    {
        int vc = g.Position.Length / 3;
        if (vc == 0 || g.Index.Length == 0) return null;
        var pos = new Vector3[vc];
        for (int i = 0; i < vc; i++) pos[i] = new Vector3(g.Position[3 * i], g.Position[3 * i + 1], g.Position[3 * i + 2]);
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Godot.Mesh.ArrayType.Max);
        arrays[(int)Godot.Mesh.ArrayType.Vertex] = pos;
        if (g.Uv != null && g.Uv.Length >= 2 * vc)
        {
            var uv = new Vector2[vc];
            for (int i = 0; i < vc; i++) uv[i] = new Vector2(g.Uv[2 * i], g.Uv[2 * i + 1]);
            arrays[(int)Godot.Mesh.ArrayType.TexUV] = uv;
        }
        if (g.Color != null && g.Color.Length >= 3 * vc)
        {
            var col = new Color[vc];
            for (int i = 0; i < vc; i++) col[i] = new Color(g.Color[3 * i], g.Color[3 * i + 1], g.Color[3 * i + 2]);
            arrays[(int)Godot.Mesh.ArrayType.Color] = col;
        }
        arrays[(int)Godot.Mesh.ArrayType.Index] = g.Index;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Godot.Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    // An RGBA picture as a texture; `flipY` as three's texture.flipY (row 0 at v = 1)
    public static ImageTexture TextureOf(byte[] rgba, int w, int h, bool flipY = false, bool mipmaps = false)
    {
        var img = Image.CreateFromData(w, h, false, Image.Format.Rgba8, rgba);
        if (flipY) img.FlipY();
        if (mipmaps) img.GenerateMipmaps();
        return ImageTexture.CreateFromImage(img);
    }

    // matrixWorld: the node's transform through its parents (in the tree or not yet)
    public static Transform3D WorldOf(Node3D n)
    {
        var t = n.Transform;
        for (var p = n.GetParent(); p != null; p = p.GetParent()) if (p is Node3D q) t = q.Transform * t;
        return t;
    }

    // A node's transform from three's position / rotation.z / scale (no other rotation here)
    public static Transform3D Place(double x, double y, double z, double scaleY = 1, double rotZ = 0, double scaleX = 1, double scaleZ = 1)
    {
        var basis = rotZ == 0 ? Basis.Identity : new Basis(Vector3.Back, (float)rotZ);
        basis = basis * Basis.FromScale(new Vector3((float)scaleX, (float)scaleY, (float)scaleZ));
        return new Transform3D(basis, new Vector3((float)x, (float)y, (float)z));
    }
}
