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
    readonly Image _image;              // the level texture's picture, refilled in place for an update
    ImageTexture? _decalTexture;
    Image? _decalImage;
    Material[]? _decalMaterials;
    readonly Material[] _materials;
    Material? _decalMaterial;
    readonly MeshInstance3D?[] _chunks;
    readonly MeshInstance3D?[] _decals;

    public TerrainView(TerrainMesh tm)
    {
        _tm = tm;
        _image = Image.CreateFromData(tm.W, tm.H, false, Image.Format.Rgba8, tm.TexData);
        _texture = ImageTexture.CreateFromImage(_image);
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

    /** Held: the view keeps what it shows (a jump's refresh is spread over frames, GameSession). */
    public bool Held;

    /** The chunks' buffers let go once they are meshes (the heavy look's are hundreds of MB). */
    public bool ReleaseConverted;

    // Per frame (the web's per-tick flush is the caller's: TerrainMesh.FlushDirty)
    public void Sync()
    {
        if (Held) return;
        var upload = Perf.Time(Perf.S.TexUpload);
        if (_tm.TextureNeedsUpdate)
        {
            _image.SetData(_tm.W, _tm.H, false, Image.Format.Rgba8, _tm.TexData);
            _texture.Update(_image);
            _tm.TextureNeedsUpdate = false;
        }
        var decals = _tm.Decals;
        if (decals != null)
        {
            if (_decalTexture == null)
            {
                _decalImage = Image.CreateFromData(decals.W, decals.H, false, Image.Format.Rgba8, decals.Data);
                _decalTexture = ImageTexture.CreateFromImage(_decalImage);
                _decalMaterial = RawColor.Material(RawColor.Transparent, _decalTexture);
                _decalMaterials = new[] { _decalMaterial };
            }
            else if (decals.TextureNeedsUpdate)
            {
                _decalImage!.SetData(decals.W, decals.H, false, Image.Format.Rgba8, decals.Data);
                _decalTexture.Update(_decalImage);
            }
            decals.TextureNeedsUpdate = false;
        }
        upload.Dispose();
        if (_dirty.Count == 0) return;
        using var _ = Perf.Time(Perf.S.MeshSwap);
        foreach (int id in _dirty)
        {
            Swap(_chunks, id, _tm.ChunkMeshes[id], _materials);
            if (_decalMaterials != null) Swap(_decals, id, _tm.DecalMeshes[id], _decalMaterials);
        }
        _dirty.Clear();
    }

    void Swap(MeshInstance3D?[] slots, int id, ChunkGeometry? g, Material[] materials)
    {
        var mesh = g == null ? null : RawColor.ToMesh(g, materials);
        if (ReleaseConverted) g?.Release();
        if (mesh == null) { slots[id]?.QueueFree(); slots[id] = null; return; }
        if (slots[id] == null) { slots[id] = new MeshInstance3D(); AddChild(slots[id]); }
        slots[id]!.Mesh = mesh;
    }
}
