using System;
using System.Collections.Generic;
using Godot;
using Lemmix.Render;

namespace Lemmix.App.Board;

// web/3d/js/bridge.js BillboardPool, the scene half: one MeshInstance3D per captured draw,
// reused tick to tick, wearing the cached relief and material of its frame
// (SpriteGeometryCache), at the place core's BillboardPool.Place computes (inlined here, the same
// arithmetic, so a tick allocates nothing once the frames are cached). With `interpolate` the
// positions slide between the last two ticks by the draw's key (ApplyInterpolation).
public sealed partial class SpritePool : Node3D
{
    readonly SpriteGeometryCache _cache;
    readonly BoardMaterials _materials;
    readonly bool _blend;
    readonly List<MeshInstance3D> _pool = new();
    readonly List<Slot> _slots = new();
    // what each node was last given: a call into the engine only when it changes (most sprites keep
    // their mesh and material from tick to tick, a still one its place)
    readonly List<Shown> _shown = new();
    struct Shown { public bool Visible; public Mesh? Mesh; public Material? Material; public Transform3D Transform; public bool Placed; }
    Dictionary<string, (double X, double Y)> _prev = new(StringComparer.Ordinal);
    Dictionary<string, (double X, double Y)> _next = new(StringComparer.Ordinal);
    public int ActiveCount { get; private set; }

    struct Slot { public bool Interp; public double Px, Py, Cx, Cy, Z, ScaleY; }

    public SpritePool(SpriteGeometryCache cache, BoardMaterials materials, bool blend, string name)
    {
        _cache = cache; _materials = materials; _blend = blend; Name = name;
    }

    public IReadOnlyList<MeshInstance3D> Meshes => _pool;
    // lemmingPool.prevPositions.clear() (refreshAfterRestore)
    public void ClearPrevPositions() => _prev.Clear();

    MeshInstance3D Acquire(int i)
    {
        while (_pool.Count <= i)
        {
            var m = new MeshInstance3D { CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            _pool.Add(m);
            _slots.Add(default);
            _shown.Add(new Shown { Visible = true });
            AddChild(m);
        }
        var mesh = _pool[i];
        SetVisible(i, true);
        return mesh;
    }

    void SetVisible(int i, bool v)
    {
        var s = _shown[i];
        if (s.Visible == v) return;
        _pool[i].Visible = v;
        s.Visible = v;
        _shown[i] = s;
    }

    void Show(int i, Mesh? mesh, Material? material, in Transform3D t)
    {
        var s = _shown[i];
        var node = _pool[i];
        if (!ReferenceEquals(s.Mesh, mesh)) { node.Mesh = mesh; s.Mesh = mesh; }
        if (!ReferenceEquals(s.Material, material)) { node.MaterialOverride = material; s.Material = material; }
        if (!s.Placed || s.Transform != t) { node.Transform = t; s.Transform = t; s.Placed = true; }
        _shown[i] = s;
    }

    void Place(int i, in Transform3D t)
    {
        var s = _shown[i];
        if (s.Placed && s.Transform == t) return;
        _pool[i].Transform = t;
        s.Transform = t; s.Placed = true;
        _shown[i] = s;
    }

    // sync(items, zFor, interpolate, flat)
    public void Sync(List<CapturedDraw> items, Func<int, double> zFor, bool interpolate, bool flat)
    {
        if (interpolate) _next.Clear();
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item.Off)
            {
                Acquire(i);
                SetVisible(i, false);
                _slots[i] = default;
                continue;
            }
            var entry = item.Frame != null ? _cache.ForFrame(item.Frame) : _cache.ForMask(item.Mask!);
            int offX = item.Frame != null ? item.Frame.OffsetX : item.Mask!.OffsetX;
            int offY = item.Frame != null ? item.Frame.OffsetY : item.Mask!.OffsetY;
            Acquire(i);
            var sm = flat && item.Frame != null ? _cache.FlatMaterialFor(item.Frame)
                : (_blend && item.Frame != null) ? _cache.BlendedMaterialFor(item.Frame)
                : entry.Material;
            double bx = item.X + offX;
            double by = item.Y + offY + (item.FlipY ? entry.H : 0);
            double z = zFor(item.Layer) + i * 0.02;
            double sy = item.FlipY ? -1 : 1;
            Show(i, _materials.Mesh(entry.Geometry), _materials.For(sm), BoardMaterials.Place(bx, by, z, sy));
            var slot = new Slot { Z = z, ScaleY = sy, Cx = bx, Cy = by, Px = bx, Py = by };
            if (interpolate && item.Key != null)
            {
                if (_prev.TryGetValue(item.Key, out var p)) { slot.Px = p.X; slot.Py = p.Y; }
                slot.Interp = true;
                _next[item.Key] = (bx, by);
            }
            _slots[i] = slot;
        }
        for (int i = items.Count; i < _pool.Count; i++)
        {
            SetVisible(i, false);
            _slots[i] = default;
        }
        ActiveCount = items.Count;
        if (interpolate) (_prev, _next) = (_next, _prev);
    }

    // applyInterpolation(alpha): x and y between the last two ticks
    public void ApplyInterpolation(double alpha)
    {
        for (int i = 0; i < ActiveCount; i++)
        {
            var s = _slots[i];
            if (!s.Interp) continue;
            Place(i, BoardMaterials.Place(BillboardPool.Lerp(s.Px, s.Cx, alpha), BillboardPool.Lerp(s.Py, s.Cy, alpha), s.Z, s.ScaleY));
        }
    }

    // the interpolated position of slot i now (tests)
    public (double X, double Y) SlotPosition(int i) { var t = _pool[i].Transform.Origin; return (t.X, t.Y); }
}

// web/3d/js/bridge.js ParticleCloud: the captured setPixel calls as a point cloud at `z`, every
// point a level pixel's colour; a point is PARTICLE_SIZE level pixels across at any distance
// (three's sizeAttenuation: size x (viewport height / 2) / depth), times the board's world scale.
public sealed partial class ParticleCloud : MeshInstance3D
{
    readonly double _z;
    readonly ImmediateMesh _mesh = new();
    readonly ShaderMaterial _material;
    public int Count { get; private set; }

    const string Code = @"shader_type spatial;
render_mode unshaded, cull_disabled;
uniform float size = 2.2;
vec3 srgb_to_lin(vec3 c) { return mix(c / 12.92, pow((c + 0.055) / 1.055, vec3(2.4)), step(vec3(0.04045), c)); }
void vertex() {
    vec4 mv = MODELVIEW_MATRIX * vec4(VERTEX, 1.0);
    POINT_SIZE = size * (VIEWPORT_SIZE.y * 0.5) / max(-mv.z, 1e-4);
}
void fragment() { ALBEDO = srgb_to_lin(COLOR.rgb); }";

    public ParticleCloud(double z)
    {
        _z = z;
        Name = "particles";
        Mesh = _mesh;
        _material = new ShaderMaterial { Shader = new Shader { Code = Code } };
        _material.SetShaderParameter("size", (float)SpriteBuild.PARTICLE_SIZE);
        MaterialOverride = _material;
        CastShadow = ShadowCastingSetting.Off;
        // frustumCulled = false
        ExtraCullMargin = 16384;
    }

    // sync(flat): x, y, r, g, b per particle
    public void Sync(List<double> flat)
    {
        _mesh.ClearSurfaces();
        Count = flat.Count / 5;
        if (Count == 0) return;
        _mesh.SurfaceBegin(Godot.Mesh.PrimitiveType.Points);
        for (int i = 0; i < Count; i++)
        {
            _mesh.SurfaceSetColor(new Color((float)(flat[i * 5 + 2] / 255), (float)(flat[i * 5 + 3] / 255), (float)(flat[i * 5 + 4] / 255)));
            _mesh.SurfaceAddVertex(new Vector3((float)flat[i * 5], (float)flat[i * 5 + 1], (float)_z));
        }
        _mesh.SurfaceEnd();
    }

    // updateScale: three's size is in the camera's units scaled by the projection; keep a level
    // pixel's worth whatever the diorama's scale (the world scale of the cloud)
    public void UpdateScale()
    {
        if (!IsInsideTree()) return;
        double s = Math.Abs(GlobalTransform.Basis.X.Length());
        _material.SetShaderParameter("size", (float)SpriteBuild.ParticleSize(null, s));
    }
}
