using Godot;
using Lemmix.Render;

namespace Lemmix.App.Board;

// web/3d/js/terrain.js, the scene half: the chunks TerrainMesh builds, each an ArrayMesh, under a
// node in game pixel space (y down, as the web's group before its flip); the level texture (an
// RGBA DataTexture with nearest filtering) re-uploaded when the mesher marks it; the decal skins
// over them with their own texture. The mesher re-meshes dirty chunks (FlushDirty) and says which
// (ChunkRebuilt); this node swaps those meshes.
public partial class TerrainView : Node3D
{
    readonly TerrainMesh _tm;
    readonly ImageTexture _texture;
    ImageTexture? _decalTexture;
    readonly Material[] _materials;
    Material? _decalMaterial;
    readonly MeshInstance3D?[] _chunks;
    readonly MeshInstance3D?[] _decals;

    public TerrainView(TerrainMesh tm)
    {
        _tm = tm;
        _texture = ImageTexture.CreateFromImage(Image.CreateFromData(tm.W, tm.H, false, Image.Format.Rgba8, tm.TexData));
        _materials = new Material[]
        {
            RawColor.Material(RawColor.Opaque, _texture),                    // map + vertexColors
            RawColor.Material(RawColor.Opaque, null, useMap: false),         // colour blend: vertexColors alone
        };
        _chunks = new MeshInstance3D?[tm.ChunkMeshes.Length];
        _decals = new MeshInstance3D?[tm.DecalMeshes.Length];
        tm.ChunkRebuilt += id => _dirty.Add(id);
        for (int i = 0; i < _chunks.Length; i++) _dirty.Add(i);
    }

    readonly System.Collections.Generic.HashSet<int> _dirty = new();

    // Per frame (the web's per-tick flush is the caller's: TerrainMesh.FlushDirty)
    public void Sync()
    {
        var upload = Perf.Time(Perf.S.TexUpload);
        if (_tm.TextureNeedsUpdate)
        {
            _texture.Update(Image.CreateFromData(_tm.W, _tm.H, false, Image.Format.Rgba8, _tm.TexData));
            _tm.TextureNeedsUpdate = false;
        }
        var decals = _tm.Decals;
        if (decals != null)
        {
            if (_decalTexture == null)
            {
                _decalTexture = ImageTexture.CreateFromImage(Image.CreateFromData(decals.W, decals.H, false, Image.Format.Rgba8, decals.Data));
                _decalMaterial = RawColor.Material(RawColor.Transparent, _decalTexture);
            }
            else if (decals.TextureNeedsUpdate)
                _decalTexture.Update(Image.CreateFromData(decals.W, decals.H, false, Image.Format.Rgba8, decals.Data));
            decals.TextureNeedsUpdate = false;
        }
        upload.Dispose();
        if (_dirty.Count == 0) return;
        using var _ = Perf.Time(Perf.S.MeshSwap);
        foreach (int id in _dirty)
        {
            Swap(_chunks, id, _tm.ChunkMeshes[id], _materials);
            if (_decalMaterial != null) Swap(_decals, id, _tm.DecalMeshes[id], new[] { _decalMaterial });
        }
        _dirty.Clear();
    }

    void Swap(MeshInstance3D?[] slots, int id, ChunkGeometry? g, Material[] materials)
    {
        var mesh = g == null ? null : RawColor.ToMesh(g, materials);
        if (mesh == null) { slots[id]?.QueueFree(); slots[id] = null; return; }
        if (slots[id] == null) { slots[id] = new MeshInstance3D(); AddChild(slots[id]); }
        slots[id]!.Mesh = mesh;
    }
}
