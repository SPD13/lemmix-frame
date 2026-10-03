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
    // its indices use (three.js draws a group with its material from the shared buffers), numbered
    // in the order the indices first use them. In two steps: the compaction is plain arrays (any
    // thread: a level's load does it on a worker, BoardData), the mesh is Godot's (main thread).
    public sealed class CompactSurface
    {
        public required Vector3[] Pos;
        public required Vector2[] Uv;
        public Color[]? Col;
        public required int[] Idx;
        public int Material;
    }

    [System.ThreadStatic] static int[]? _remap;
    [System.ThreadStatic] static int[]? _used;

    /** The chunk's surfaces as plain arrays, or null for an empty chunk. Any thread. */
    public static CompactSurface[]? Compact(ChunkGeometry g)
    {
        int total = g.VertexCount;
        if (total == 0 || g.Indices.Length == 0) return null;
        if (_remap == null || _remap.Length < total) { _remap = new int[total]; _used = new int[total]; }
        var remap = _remap; var used = _used!;
        int groupCount = g.Groups.Length > 0 ? g.Groups.Length : 1;
        var surfaces = new System.Collections.Generic.List<CompactSurface>(groupCount);
        for (int gi = 0; gi < groupCount; gi++)
        {
            var grp = g.Groups.Length > 0 ? g.Groups[gi] : new GeometryGroup(0, g.Indices.Length, 0);
            int count = System.Math.Min(grp.Count, g.Indices.Length - grp.Start);
            if (count <= 0) continue;
            System.Array.Fill(remap, -1, 0, total);
            var idx = new int[count];
            int vc = 0;
            for (int i = 0; i < count; i++)
            {
                int v = g.Indices[grp.Start + i];
                int n = remap[v];
                if (n < 0) { n = vc++; remap[v] = n; used[n] = v; }
                idx[i] = n;
            }
            var pos = new Vector3[vc];
            var uv = new Vector2[vc];
            var col = g.Colors != null ? new Color[vc] : null;
            bool hasUv = g.Uvs.Length >= 2 * total;
            for (int n = 0; n < vc; n++)
            {
                int v = used[n];
                pos[n] = new Vector3(g.Positions[3 * v], g.Positions[3 * v + 1], g.Positions[3 * v + 2]);
                if (hasUv || g.Uvs.Length >= 2 * (v + 1)) uv[n] = new Vector2(g.Uvs[2 * v], g.Uvs[2 * v + 1]);
                if (col != null) col[n] = new Color(g.Colors![3 * v], g.Colors[3 * v + 1], g.Colors[3 * v + 2]);
            }
            surfaces.Add(new CompactSurface { Pos = pos, Uv = uv, Col = col, Idx = idx, Material = grp.MaterialIndex });
        }
        return surfaces.Count == 0 ? null : surfaces.ToArray();
    }

    /** The compacted surfaces as an ArrayMesh, each with its material. Main thread. */
    public static ArrayMesh? ToMesh(CompactSurface[]? surfaces, Material[] materials)
    {
        if (surfaces == null) return null;
        var mesh = new ArrayMesh();
        foreach (var sf in surfaces)
        {
            using var arrays = new Godot.Collections.Array(); // let go now, not by a finalizer
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = sf.Pos;
            arrays[(int)Mesh.ArrayType.TexUV] = sf.Uv;
            if (sf.Col != null) arrays[(int)Mesh.ArrayType.Color] = sf.Col;
            arrays[(int)Mesh.ArrayType.Index] = sf.Idx;
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, materials[System.Math.Min(sf.Material, materials.Length - 1)]);
        }
        return mesh;
    }

    public static ArrayMesh? ToMesh(ChunkGeometry g, Material[] materials) => ToMesh(Compact(g), materials);
}
