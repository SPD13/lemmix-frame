using Godot;
using Lemmix.Render;

namespace Lemmix.App.Board;

// The web version renders with three.js r147's defaults: no colour management, so a texel and a
// vertex colour are multiplied as stored and written out as they are. Godot works in linear light
// and encodes sRGB at the end; to land on the same pixel the product is decoded first
// (ALBEDO = srgb_to_linear(raw)), and the texture is sampled without a source_color hint.
public static class RawColor
{
    const string Common = @"
vec3 srgb_to_lin(vec3 c) { return mix(c / 12.92, pow((c + 0.055) / 1.055, vec3(2.4)), step(vec3(0.04045), c)); }
";

    // MeshBasicMaterial({map, vertexColors, side: DoubleSide}); use_map off: vertexColors alone
    public static readonly Shader Opaque = new()
    {
        Code = @"shader_type spatial;
render_mode unshaded, cull_disabled;
uniform sampler2D map : filter_nearest, repeat_disable;
uniform bool use_map = true;
uniform bool vertex_colors = true;
" + Common + @"
void fragment() {
    vec3 c = use_map ? texture(map, UV).rgb : vec3(1.0);
    if (vertex_colors) c *= COLOR.rgb;
    ALBEDO = srgb_to_lin(c);
}",
    };

    // MeshBasicMaterial({map, transparent: true, depthWrite: false, side: DoubleSide})
    public static readonly Shader Transparent = new()
    {
        Code = @"shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_never, blend_mix;
uniform sampler2D map : filter_nearest, repeat_disable;
uniform float opacity = 1.0;
" + Common + @"
void fragment() {
    vec4 t = texture(map, UV);
    ALBEDO = srgb_to_lin(t.rgb);
    ALPHA = t.a * opacity;
}",
    };

    public static ShaderMaterial Material(Shader shader, Texture2D? map, bool useMap = true, bool vertexColors = true)
    {
        var m = new ShaderMaterial { Shader = shader };
        if (map != null) m.SetShaderParameter("map", map);
        m.SetShaderParameter("use_map", useMap && map != null);
        m.SetShaderParameter("vertex_colors", vertexColors);
        return m;
    }

    // A chunk's buffers as an ArrayMesh: one surface per material group, each with the vertices
    // its indices use (three.js draws a group with its material from the shared buffers).
    public static ArrayMesh? ToMesh(ChunkGeometry g, Material[] materials)
    {
        if (g.VertexCount == 0 || g.Indices.Length == 0) return null;
        var mesh = new ArrayMesh();
        var groups = g.Groups.Length > 0 ? g.Groups : new[] { new GeometryGroup(0, g.Indices.Length, 0) };
        foreach (var grp in groups)
        {
            int count = System.Math.Min(grp.Count, g.Indices.Length - grp.Start);
            if (count <= 0) continue;
            var remap = new System.Collections.Generic.Dictionary<int, int>();
            var idx = new int[count];
            for (int i = 0; i < count; i++)
            {
                int v = g.Indices[grp.Start + i];
                if (!remap.TryGetValue(v, out int n)) { n = remap.Count; remap[v] = n; }
                idx[i] = n;
            }
            int vc = remap.Count;
            var pos = new Vector3[vc];
            var uv = new Vector2[vc];
            var col = g.Colors != null ? new Color[vc] : null;
            foreach (var (v, n) in remap)
            {
                pos[n] = new Vector3(g.Positions[3 * v], g.Positions[3 * v + 1], g.Positions[3 * v + 2]);
                if (g.Uvs.Length >= 2 * (v + 1)) uv[n] = new Vector2(g.Uvs[2 * v], g.Uvs[2 * v + 1]);
                if (col != null) col[n] = new Color(g.Colors![3 * v], g.Colors[3 * v + 1], g.Colors[3 * v + 2]);
            }
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = pos;
            arrays[(int)Mesh.ArrayType.TexUV] = uv;
            if (col != null) arrays[(int)Mesh.ArrayType.Color] = col;
            arrays[(int)Mesh.ArrayType.Index] = idx;
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, materials[System.Math.Min(grp.MaterialIndex, materials.Length - 1)]);
        }
        return mesh;
    }
}
