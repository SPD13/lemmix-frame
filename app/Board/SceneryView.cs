using System;
using System.Collections.Generic;
using Godot;
using Lemmix.Io;
using Lemmix.Render;
using Bitmap = Lemmix.Engine.Bitmap;

namespace Lemmix.App.Board;

// A gallery's scenery (core Render/Scenery.cs, made by tools/scenery-gen), drawn in the room's
// frame in place of envgen's rings: a ground disc to the horizon, one open drum per strip round
// the player's place, and a sky sphere. One unshaded shader paints all of it: the sky by
// elevation, the strips and the ground graded and hazed toward the sky's colour in the direction
// they are seen (so the far strips melt into the horizon), by distance and nearer the ground.
// Opaque with an alpha test, so nothing is sorted. Native only: the web page has no scenery.
public sealed partial class SceneryView : Node3D
{
    // What a gallery's scenery is once read (on the room's worker): the manifest and its pictures;
    // the textures are made on the main thread the first time it is shown and kept with the
    // gallery, the pictures let go then.
    public sealed class Data
    {
        public required SceneryManifest Manifest;
        public readonly Dictionary<string, Bitmap> Pictures = new(StringComparer.Ordinal);
        public readonly Dictionary<string, ImageTexture> Textures = new(StringComparer.Ordinal);
        public int Horizon => Hex(Manifest.Sky.Horizon);
    }

    public static Data? Load(IFileSource io, string? style)
    {
        var m = SceneryManifest.Load(io, style);
        if (m == null) return null;
        var d = new Data { Manifest = m };
        string dir = SceneryManifest.DirFor(style!);
        foreach (var l in m.Layers)
        {
            var b = io.Image(dir + l.File);
            if (b == null) { GD.PushWarning("[scenery] missing " + dir + l.File); return null; }
            d.Pictures[l.File] = b;
        }
        if (m.Ground != null && io.Image(dir + m.Ground.File) is { } g) d.Pictures[m.Ground.File] = g;
        return d;
    }

    static int Hex(string s) => int.Parse(s.TrimStart('#'), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);

    public const string ShaderCode = @"shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_opaque, fog_disabled;
uniform int kind = 0;                 // 0 a strip, 1 the ground, 2 the sky
uniform sampler2D map : filter_nearest_mipmap, repeat_enable;
uniform sampler2D ground_map : filter_linear_mipmap_anisotropic, repeat_enable; // no mip seams at a grazing angle
uniform float grade = 1.0;
uniform float desat = 0.0;
uniform float fade_top = 0.0;          // a strip's top this fraction of it melting into the sky
uniform float tile_px = 1024.0;       // the ground's tile, in room units
uniform vec3 eye;                     // the eye in the room's frame
uniform float px_per_m = 400.0;       // room units a metre, times the rings' scale
uniform float floor_y = 0.0;
uniform vec3 zenith; uniform vec3 high; uniform vec3 horizon; uniform vec3 below;
uniform bool dither = true;
uniform float fog_m = 32.0; uniform float fog_max = 0.94;
uniform float mist = 0.5; uniform float mist_m = 2.5;
varying vec3 lpos;

vec3 srgb_to_lin(vec3 c) { return mix(c / 12.92, pow((c + 0.055) / 1.055, vec3(2.4)), step(vec3(0.04045), c)); }

// SceneryLook.Sky
vec3 sky_at(float e) {
    if (e < 0.0) return mix(horizon, below, smoothstep(0.0, 0.3, -e));
    return e < 0.28 ? mix(horizon, high, smoothstep(0.0, 0.28, e)) : mix(high, zenith, smoothstep(0.28, 1.0, e));
}

// SceneryLook.Haze
float haze(float dm, float hm) {
    float k = min(fog_max, 1.0 - exp(-dm / fog_m));
    float mh = mist_m * (0.4 + dm / 30.0);
    float m = mist * (1.0 - smoothstep(0.0, mh, hm)) * smoothstep(3.0, 30.0, dm);
    return clamp(k + (1.0 - k) * m, 0.0, 1.0);
}

float bayer4(vec2 p) {
    float m[16] = float[16](0.0, 8.0, 2.0, 10.0, 12.0, 4.0, 14.0, 6.0, 3.0, 11.0, 1.0, 9.0, 15.0, 7.0, 13.0, 5.0);
    int i = int(mod(p.x, 4.0)) + 4 * int(mod(p.y, 4.0));
    return m[i] / 16.0;
}

void vertex() { lpos = VERTEX; }

void fragment() {
    vec3 d = lpos - eye;
    float len = max(length(d), 1e-3);
    float e = d.y / len;
    vec3 sky = sky_at(e);
    vec3 col;
    if (kind == 2) {
        col = sky;
    } else {
        vec4 t = kind == 1 ? texture(ground_map, lpos.xz / tile_px) : texture(map, UV);
        if (t.a < 0.5) discard;
        float l = dot(t.rgb, vec3(0.299, 0.587, 0.114));
        vec3 g = mix(vec3(l), t.rgb, 1.0 - desat) * grade;
        float f = haze(length(d.xz) / px_per_m, (lpos.y - floor_y) / px_per_m);
        if (fade_top > 0.0) f = max(f, 1.0 - smoothstep(0.0, fade_top, UV.y));
        col = mix(g, sky, f);
    }
    // the haze and the sky are long dark gradients: the 3D buffer (10-bit linear on Mobile) has
    // few steps there and draws them as rings; an ordered dither of about one step breaks them
    vec3 lin = srgb_to_lin(col);
    if (dither) lin = max(lin + (bayer4(FRAGCOORD.xy) - 0.47) * (1.5 / 1023.0), vec3(0.0));
    ALBEDO = lin;
}
";

    static Shader? _shader;
    static Shader TheShader => _shader ??= new Shader { Code = ShaderCode };

    sealed class Part
    {
        public required MeshInstance3D Mesh;
        public required ShaderMaterial Material;
        public SceneryLayer? Layer;    // a strip
        public int Kind;
    }

    readonly List<Part> _parts = new();
    Data? _data;
    double _scale = 1, _pxPerM = EnvironmentLayout.PxPerMetre, _yFloor;
    (double X, double Z) _center;

    public Data? Shown => _data;
    public double Scale => _scale;

    public SceneryView() { Name = "scenery"; }

    // put up a gallery's scenery (main thread): the textures made once per gallery
    public void Show(Data d)
    {
        if (_data == d) return;
        Clear();
        _data = d;
        var m = d.Manifest;
        foreach (var (file, bmp) in d.Pictures)
        {
            if (d.Textures.ContainsKey(file)) continue;
            d.Textures[file] = BoardMaterials.TextureOf(bmp.Data, bmp.Width, bmp.Height, flipY: false, mipmaps: true);
        }
        d.Pictures.Clear();
        Add(2, null, null);
        if (m.Ground != null && d.Textures.TryGetValue(m.Ground.File, out var gt)) Add(1, null, gt);
        foreach (var l in m.Layers)
            if (d.Textures.TryGetValue(l.File, out var t)) Add(0, l, t);
        Layout();
    }

    void Add(int kind, SceneryLayer? layer, Texture2D? tex)
    {
        var m = _data!.Manifest;
        var mat = new ShaderMaterial { Shader = TheShader };
        mat.SetShaderParameter("kind", kind);
        if (tex != null) mat.SetShaderParameter(kind == 1 ? "ground_map" : "map", tex);
        mat.SetShaderParameter("fade_top", (float)(layer?.FadeTop ?? 0));
        double grade = layer?.Grade ?? m.Ground?.Grade ?? 1, desat = layer?.Desat ?? m.Ground?.Desat ?? 0;
        mat.SetShaderParameter("grade", (float)(kind == 2 ? 1 : grade));
        mat.SetShaderParameter("desat", (float)(kind == 2 ? 0 : desat));
        Vector3 C(string hex) { var (r, g, b) = SceneryLook.Hex(hex); return new Vector3((float)r, (float)g, (float)b); }
        mat.SetShaderParameter("zenith", C(m.Sky.Zenith));
        mat.SetShaderParameter("high", C(m.Sky.High));
        mat.SetShaderParameter("horizon", C(m.Sky.Horizon));
        mat.SetShaderParameter("below", C(m.Sky.Below));
        mat.SetShaderParameter("dither", m.Sky.Dither);
        mat.SetShaderParameter("fog_m", (float)m.Fog.DistanceM);
        mat.SetShaderParameter("fog_max", (float)m.Fog.Max);
        mat.SetShaderParameter("mist", (float)m.Fog.Mist);
        mat.SetShaderParameter("mist_m", (float)m.Fog.MistM);
        var mesh = new MeshInstance3D
        {
            Name = kind == 2 ? "sky" : kind == 1 ? "ground" : "strip-" + layer!.Name, MaterialOverride = mat,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, ExtraCullMargin = 1e6f,
        };
        AddChild(mesh);
        _parts.Add(new Part { Mesh = mesh, Material = mat, Layer = layer, Kind = kind });
    }

    public void Clear()
    {
        foreach (var p in _parts) { RemoveChild(p.Mesh); p.Mesh.QueueFree(); }
        _parts.Clear();
        _data = null;
    }

    // where the room is: its centre, its floor, and how far its first ring reaches (metres) - the
    // strips are pushed out together when that ring is wider than the scenery's nearest
    public void Place((double X, double Z) center, double yFloor, double pxPerMetre, double firstRingM)
    {
        _center = center; _yFloor = yFloor; _pxPerM = pxPerMetre;
        _scale = _data == null ? 1 : SceneryLayout.Scale(_data.Manifest, firstRingM);
        Layout();
    }

    void Layout()
    {
        if (_data == null) return;
        var m = _data.Manifest;
        double P = _pxPerM, k = _scale;
        double eyeY = _yFloor + m.EyeM * P;
        // heights scale round the eye with the radii: every strip keeps the angles it was drawn for
        double Y(double metres) => eyeY + (metres - m.EyeM) * P * k;
        foreach (var p in _parts)
        {
            GeometryBuffers g;
            if (p.Kind == 2)
            {
                g = EnvironmentLayout.SphereGeometry(m.Sky.RadiusM * P * k, 48, 24);
                g.Translate(_center.X, eyeY, _center.Z);
            }
            else if (p.Kind == 1)
            {
                g = EnvironmentLayout.RingGeometry(_center, 0, m.Ground!.RadiusM * P * k, _yFloor, 24, null);
                p.Material.SetShaderParameter("tile_px", (float)(m.Ground.TileM * P));
            }
            else g = Drum(_center, p.Layer!.RadiusM * P * k, Y(p.Layer.BottomM), Y(p.Layer.BottomM + p.Layer.HeightM));
            p.Mesh.Mesh = BoardMaterials.ToMesh(g);
            p.Material.SetShaderParameter("px_per_m", (float)(P * k));
            p.Material.SetShaderParameter("floor_y", (float)_yFloor);
        }
        SetEye(new Vector3((float)_center.X, (float)eyeY, (float)_center.Z));
    }

    // per frame: the eye in the room's frame (the haze and the sky are seen from it)
    public void SetEye(Vector3 eye)
    {
        foreach (var p in _parts) p.Material.SetShaderParameter("eye", eye);
    }

    // an open drum round c from y0 up to y1, seen from inside: u = 0.5 straight ahead (-z, where
    // the board is) and growing to the right, v = 0 at the top (the strip's row 0)
    public static GeometryBuffers Drum((double X, double Z) c, double r, double y0, double y1)
    {
        int segs = EnvironmentLayout.ENV_SEGMENTS * 2;
        var pos = new float[(segs + 1) * 2 * 3];
        var uv = new float[(segs + 1) * 2 * 2];
        var idx = new List<int>();
        for (int j = 0; j <= 1; j++)
            for (int i = 0; i <= segs; i++)
            {
                double u = (double)i / segs, a = (u - 0.5) * Math.PI * 2;
                int v = j * (segs + 1) + i;
                pos[v * 3] = (float)(c.X + r * Math.Sin(a));
                pos[v * 3 + 1] = (float)(j == 0 ? y1 : y0);
                pos[v * 3 + 2] = (float)(c.Z - r * Math.Cos(a));
                uv[v * 2] = (float)u;
                uv[v * 2 + 1] = j;
            }
        for (int i = 0; i < segs; i++)
        {
            int a = i, b = i + segs + 1;
            idx.Add(a); idx.Add(b); idx.Add(a + 1);
            idx.Add(a + 1); idx.Add(b); idx.Add(b + 1);
        }
        return new GeometryBuffers { Position = pos, Uv = uv, Index = idx.ToArray() };
    }
}
