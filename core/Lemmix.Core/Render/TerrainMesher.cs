using Lemmix.Engine;
using Lemmix.Util;

namespace Lemmix.Render;

// web/3d/js/terrain.js TerrainMesh - extruded, destructible terrain with depth classes, as plain
// buffers: per 32x32-pixel chunk the greedy-meshed (or per-pixel, for the smoothings and the
// colour blend) front/back faces and step walls, UV-mapped onto one level-sized RGBA texture
// built from level.GroundImage. Level.GroundChanged plays the part of the wrapped
// setGroundAt/clearGroundAt: the depth buffer and texture follow every dig and brick, and dirty
// chunks are re-meshed by FlushDirty with a budget. Geometry is in game pixel space (y down).
//
// Every number is computed in doubles in the JS order and stored as float32 where three.js
// stores a Float32Array (positions, colours, uvs, the donor uv table), so the buffers are
// bitwise the ones the web builds (oracle/terrain.js).
//
// Left out (scene graph only, no geometry): the THREE groups and materials, the 2D view's
// quads (setFlat keeps its state and its re-mesh), setExtrusion (a group transform).

/// <summary>A geometry group: a range of the index buffer drawn with one material (0 textured, 1 vertex colour).</summary>
public readonly record struct GeometryGroup(int Start, int Count, int MaterialIndex);

/// <summary>One chunk's BufferGeometry: attributes as three.js holds them, ready for a Godot ArrayMesh.</summary>
public sealed class ChunkGeometry
{
    public float[] Positions = Array.Empty<float>(); // xyz per vertex
    public float[]? Colors;                           // rgb per vertex (null: no attribute)
    public float[] Uvs = Array.Empty<float>();       // uv per vertex
    public float[]? Normals;                          // never set by the terrain (MeshBasicMaterial)
    public int[] Indices = Array.Empty<int>();
    public bool Index32;                              // three.js stores the index as Uint32 (else Uint16)
    public GeometryGroup[] Groups = Array.Empty<GeometryGroup>();
    public int VertexCount => _released >= 0 ? _released : Positions.Length / 3;

    int _released = -1;
    public bool Released => _released >= 0;

    /// <summary>The host made its own copy (a GPU mesh): the buffers are let go, the vertex count kept.
    /// The mesher never reads a geometry back; a re-mesh makes a new one.</summary>
    public void Release()
    {
        if (_released >= 0) return;
        _released = Positions.Length / 3;
        Positions = Array.Empty<float>(); Uvs = Array.Empty<float>(); Indices = Array.Empty<int>();
        Colors = Colors == null ? null : Array.Empty<float>();
        Normals = Normals == null ? null : Array.Empty<float>();
    }
}

public sealed class TerrainMesh : IDisposable
{
    public const int TERRAIN_CHUNK = 32;
    public const int TERRAIN_DEPTH = 16;
    public const double DECAL_LIFT = 0.1;
    public const double DECAL_FLAT_LIFT = 0.5;

    public const double SHADE_FRONT = 1.0;
    public const double SHADE_BACK = 0.4;
    public const double SHADE_LEFT = 0.6;
    public const double SHADE_RIGHT = 0.66;
    public const double SHADE_TOP = 0.85;
    public const double SHADE_BOTTOM = 0.5;

    public const double TERRAIN_SMOOTH_PULL = 0.35;

    public const int BLEND_BAND_PX = 4;
    public const int BLEND_BANDS_MAX = 4;
    public const int BLEND_RUN = 4;
    public const double WALL_DIFFUSE = 0.75;
    static readonly int[] WALL_DIFFUSE_DX = { -1, 1, 0, 0, -1, 1, -1, 1 };
    static readonly int[] WALL_DIFFUSE_DY = { 0, 0, -1, 1, -1, -1, 1, 1 };
    public const double WALL_DIFFUSE_DEPTH = 4;

    /// <summary>Which colour a band takes, jittered per pixel (blendHash): stateless on purpose.</summary>
    public static uint BlendHash(int x, int y, int face, int band)
    {
        int h = unchecked(x * 0x27d4eb2d) ^ unchecked(y * 0x165667b1) ^
                unchecked((face + 1) * (int)0x9e3779b1) ^ unchecked((band + 1) * (int)0x85ebca6b);
        h ^= (int)((uint)h >> 15); h = unchecked(h * 0x2545f491); h ^= (int)((uint)h >> 13);
        return (uint)h;
    }

    // WALL_ACROSS / WALL_ALONG by face id (2 left, 3 right, 4 top, 5 bottom)
    static int AcrossX(int face) => face == 2 ? -1 : face == 3 ? 1 : 0;
    static int AcrossY(int face) => face == 4 ? -1 : face == 5 ? 1 : 0;
    static int AlongX(int face) => face >= 4 ? 1 : 0;
    static int AlongY(int face) => face <= 3 ? 1 : 0;

    public readonly Level Level;
    public byte[] DepthMap;  // this.depth
    public byte[] Relief;
    public byte[] Depth0, Relief0;
    public readonly int W, H, ChunksX, ChunksY;
    public bool Flat;
    public double FlatZ;
    readonly sbyte[] _mask;
    public bool HasRelief;
    public bool Smooth, SmoothTerrain;

    /// <summary>The level texture (RGBA, W x H): the picture, alpha = the solidity mask.</summary>
    public readonly byte[] TexData;
    /// <summary>texture.needsUpdate: set whenever TexData changed; the renderer clears it after uploading.</summary>
    public bool TextureNeedsUpdate;

    public BlendMap? Blend;
    public ushort[]? BlendSlot0;
    public List<float[]> BlendUv = new();
    public List<(int Index, byte R, byte G, byte B)> BlendPins = new();
    public HashSet<int> BlendPinned = new();

    public byte[]? Color, Color0;
    public double ColorSoftness = 1;

    public readonly ChunkGeometry?[] ChunkMeshes;
    public readonly DirtyChunkSet DirtyChunks;
    public TerrainDecals? Decals;
    public readonly ChunkGeometry?[] DecalMeshes;

    public ushort[]? PhysicsPaint;
    public int PhysicsHighlight;

    /// <summary>A chunk (id = cy * ChunksX + cx) was re-meshed: ChunkMeshes[id] and DecalMeshes[id] are new.</summary>
    public event Action<int>? ChunkRebuilt;

    // The scratch a chunk is meshed with, one per thread: chunks are meshed side by side on the
    // thread pool when many are dirty at once (Rebuild), each from the maps alone, so the
    // geometry is the same whichever thread builds it.
    sealed class MeshScratch
    {
        public readonly TerrainGeometryBuilder B = new();
        public readonly List<Stop> Stops = new();
        public readonly double[] FrSoft = new double[4];
    }
    [ThreadStatic] static MeshScratch? t_scratch;
    static MeshScratch Scratch => t_scratch ??= new MeshScratch();
    static TerrainGeometryBuilder _b => Scratch.B;

    /// <summary>Mesh this many dirty chunks or more on the thread pool (int.MaxValue: never).</summary>
    public static int ParallelMin = 2;

    public TerrainMesh(Level level, byte[] depthMap, byte[]? reliefMap, BlendMap? blendMap, byte[]? colorMap, double? colorSoftness)
    {
        Level = level;
        DepthMap = depthMap;
        Relief = reliefMap ?? new byte[level.Width * level.Height];
        Depth0 = (byte[])DepthMap.Clone();
        Relief0 = (byte[])Relief.Clone();
        W = level.Width;
        H = level.Height;
        ChunksX = (W + TERRAIN_CHUNK - 1) / TERRAIN_CHUNK;
        ChunksY = (H + TERRAIN_CHUNK - 1) / TERRAIN_CHUNK;
        _mask = level.GroundMask.GroundMask;
        HasRelief = Array.Exists(Relief, v => v > 0);

        TexData = new byte[W * H * 4];
        InstallBlend(blendMap); // before the refill: it is what stamps the pins
        RefillTexRect(0, 0, W, H);
        TextureNeedsUpdate = true;

        ColorSoftness = 1;
        InstallColorBlend(colorMap, colorSoftness);

        ChunkMeshes = new ChunkGeometry?[ChunksX * ChunksY];
        DirtyChunks = new DirtyChunkSet(ChunksX * ChunksY);
        DecalMeshes = new ChunkGeometry?[ChunksX * ChunksY];

        RebuildAllChunks();

        HookLevelMutations();
    }

    int ClassAt(int x, int y)
    {
        if (x < 0 || x >= W || y < 0 || y >= H) return DepthClass.EMPTY;
        return DepthMap[x + y * W];
    }

    int ReliefAt(int x, int y)
    {
        if (x < 0 || x >= W || y < 0 || y >= H) return 0;
        return Relief[x + y * W];
    }

    /// <summary>Front face depth of a solid pixel: its class band plus its relief (callers check the class).</summary>
    int FrontAt(int x, int y) => Depth.Front[ClassAt(x, y)] + ReliefAt(x, y);

    // ------------------------------------------------------------------ blend and colour

    void InstallBlend(BlendMap? blendMap)
    {
        bool on = blendMap != null && blendMap.Donors.Count > 0;
        Blend = on ? blendMap : null;
        BlendSlot0 = on ? (ushort[])blendMap!.Slot.Clone() : null;
        BlendUv = new List<float[]>();
        BlendPins = new List<(int, byte, byte, byte)>();
        BlendPinned = new HashSet<int>();
        if (!on) return;
        foreach (var palette in blendMap!.Donors)
        {
            var uv = new float[palette.Length * 2];
            for (int k = 0; k < palette.Length; k++)
            {
                var d = palette[k];
                uv[k * 2] = (float)(((d.Index % W) + 0.5) / W);
                uv[k * 2 + 1] = (float)(((d.Index / W) + 0.5) / H);
                if (!BlendPinned.Add(d.Index)) continue;
                BlendPins.Add((d.Index, (byte)d.R, (byte)d.G, (byte)d.B));
            }
            BlendUv.Add(uv);
        }
    }

    void PaintPins()
    {
        if (Blend == null || PhysicsPaint != null) return;
        foreach (var pin in BlendPins)
        {
            int o = pin.Index * 4;
            TexData[o] = pin.R; TexData[o + 1] = pin.G; TexData[o + 2] = pin.B;
        }
    }

    void InstallColorBlend(byte[]? colorMap, double? softness)
    {
        bool on = colorMap != null && Array.Exists(colorMap, v => v != 0);
        Color = on ? colorMap : null;
        Color0 = on ? (byte[])colorMap!.Clone() : null;
        if (softness != null) ColorSoftness = Math.Max(0, Math.Min(1, softness.Value));
    }

    /// <summary>Swap in new colour-blend flags (the master switch or a tag) and re-mesh.</summary>
    public void SetColorBlend(byte[]? colorMap, double? softness)
    {
        InstallColorBlend(colorMap, softness);
        RebuildAll();
    }

    int ColorAt(int x, int y)
    {
        if (Color == null || x < 0 || x >= W || y < 0 || y >= H) return 0;
        return Color[x + y * W];
    }

    TerrainRgb RgbAt(int x, int y)
    {
        int o = (y * W + x) * 4;
        return new TerrainRgb(TexData[o] / 255.0, TexData[o + 1] / 255.0, TexData[o + 2] / 255.0);
    }

    TerrainRgb WallRgb(int x, int y, int cls)
    {
        var own = RgbAt(x, y);
        if (ColorSoftness < 1) return own;
        double r = own.R, g = own.G, b = own.B;
        int n = 1;
        for (int k = 0; k < 8; k++)
        {
            int dx = WALL_DIFFUSE_DX[k], dy = WALL_DIFFUSE_DY[k];
            if (ClassAt(x + dx, y + dy) != cls) continue;
            var c = RgbAt(x + dx, y + dy);
            r += c.R; g += c.G; b += c.B; n++;
        }
        if (n == 1) return own;
        const double d = WALL_DIFFUSE;
        return new TerrainRgb(own.R + (r / n - own.R) * d, own.G + (g / n - own.G) * d, own.B + (b / n - own.B) * d);
    }

    TerrainRgb CornerColor(int x, int y, int cls, TerrainRgb fallback, bool diffuse)
    {
        double r = 0, g = 0, b = 0;
        int n = 0;
        for (int dy = -1; dy <= 0; dy++)
        {
            for (int dx = -1; dx <= 0; dx++)
            {
                if (ClassAt(x + dx, y + dy) != cls) continue;
                var c = diffuse ? WallRgb(x + dx, y + dy, cls) : RgbAt(x + dx, y + dy);
                r += c.R; g += c.G; b += c.B; n++;
            }
        }
        return n != 0 ? new TerrainRgb(r / n, g / n, b / n) : fallback;
    }

    TerrainRgb EdgeColor(int nx, int ny, int cls, TerrainRgb own)
    {
        if (ClassAt(nx, ny) != cls) return own;
        var c = RgbAt(nx, ny);
        return new TerrainRgb((own.R + c.R) / 2, (own.G + c.G) / 2, (own.B + c.B) / 2);
    }

    struct Stop
    {
        public double T;
        public TerrainRgb[] C;
    }

    static List<Stop> _stops => Scratch.Stops;

    // the corners across a wall: cA, the plateau ends (null), cB
    TerrainRgb[] Across(int nPts, int ax, int ay, int bx, int by, int c, TerrainRgb mid, bool diffuse)
    {
        var arr = new TerrainRgb[nPts];
        for (int i = 0; i < nPts; i++)
            arr[i] = i == 0 ? CornerColor(ax, ay, c, mid, diffuse) : i == nPts - 1 ? CornerColor(bx, by, c, mid, diffuse) : mid;
        return arr;
    }

    /// <summary>The colour stops down a wall standing on corners cA and cB (_wallColors).</summary>
    List<Stop> WallColors(int cAx, int cAy, int cBx, int cBy, int nPts, int cls, TerrainRgb own, int px, int py, int nx, int ny, int faceId, int slot, double span)
    {
        int nCls = ClassAt(nx, ny);
        bool empty = nCls == DepthClass.EMPTY;
        double ramp = ColorSoftness;
        bool diffusing = ramp >= 1;
        var face = Across(nPts, cAx, cAy, cBx, cBy, cls, own, false);
        var diff = Across(nPts, cAx, cAy, cBx, cBy, cls, WallRgb(px, py, cls), true);
        var faceN = empty ? null : Across(nPts, cAx, cAy, cBx, cBy, nCls, RgbAt(nx, ny), false);
        var stops = _stops;
        stops.Clear();
        stops.Add(new Stop { T = 1, C = face });
        int bands = slot != 0 ? BlendBands(span) : 1;
        if (bands >= 2)
        {
            double h = 1.0 / bands;
            int ox = AlongX(faceId), oy = AlongY(faceId);
            stops.Add(new Stop { T = 1 - h + ramp * h / 2, C = diffusing ? diff : face });
            for (int k = 1; k < bands; k++)
            {
                var mine = DonorPick(slot, px, py, faceId, k, bands);
                var c = new TerrainRgb[nPts];
                for (int i = 0; i < nPts; i++)
                {
                    if (i != 0 && i != nPts - 1) { c[i] = mine; continue; }
                    int pxC = i == 0 ? cAx : cBx, pyC = i == 0 ? cAy : cBy;
                    int qx = px + (ox != 0 ? (pxC > px ? 1 : -1) : 0);
                    int qy = py + (oy != 0 ? (pyC > py ? 1 : -1) : 0);
                    if (BlendAt(qx, qy) != slot || !WallExposed(qx, qy, faceId)) { c[i] = mine; continue; }
                    var theirs = DonorPick(slot, qx, qy, faceId, k, bands);
                    c[i] = new TerrainRgb((mine.R + theirs.R) / 2, (mine.G + theirs.G) / 2, (mine.B + theirs.B) / 2);
                }
                stops.Add(new Stop { T = 1 - k * h - ramp * h / 2, C = c });
                stops.Add(new Stop { T = k == bands - 1 && empty ? 0 : 1 - (k + 1) * h + ramp * h / 2, C = c });
            }
        }
        else
        {
            double fade = diffusing && span > 0 ? WALL_DIFFUSE_DEPTH / span : 1;
            double d = Math.Min(faceN != null ? 0.5 : 1, fade);
            stops.Add(new Stop { T = 1 - d, C = diff });
            if (faceN != null) stops.Add(new Stop { T = d, C = Across(nPts, cAx, cAy, cBx, cBy, nCls, WallRgb(nx, ny, nCls), true) });
            else stops.Add(new Stop { T = 0, C = diff });
        }
        if (faceN != null) stops.Add(new Stop { T = 0, C = faceN });
        for (int i = 1; i < stops.Count; i++)
        {
            if (stops[i].T < stops[i - 1].T) continue;
            var a = stops[i - 1].C;
            var b = stops[i].C;
            var m = new TerrainRgb[a.Length];
            for (int k = 0; k < a.Length; k++)
                m[k] = new TerrainRgb((a[k].R + b[k].R) / 2, (a[k].G + b[k].G) / 2, (a[k].B + b[k].B) / 2);
            stops[i - 1] = new Stop { T = stops[i - 1].T, C = m };
            stops.RemoveAt(i--);
        }
        return stops;
    }

    double WallShade(int x, int y, int cls, double fallback)
    {
        int front = Depth.Front[cls];
        int Solid(int px, int py)
        {
            int c = ClassAt(px, py);
            return c != DepthClass.EMPTY && Depth.Front[c] >= front ? 1 : 0;
        }
        int tl = Solid(x - 1, y - 1), tr = Solid(x, y - 1);
        int bl = Solid(x - 1, y), br = Solid(x, y);
        int nx = (tl + bl) - (tr + br);
        int ny = (tl + tr) - (bl + br);
        int ax = Math.Abs(nx), ay = Math.Abs(ny);
        if (ax == 0 && ay == 0) return fallback;
        return (ax * (nx < 0 ? SHADE_LEFT : SHADE_RIGHT) + ay * (ny < 0 ? SHADE_TOP : SHADE_BOTTOM)) / (ax + ay);
    }

    int BlendAt(int x, int y)
    {
        if (Blend == null || x < 0 || x >= W || y < 0 || y >= H) return 0;
        return Blend.Slot[x + y * W];
    }

    static int BlendBands(double span) => (int)Math.Max(1, Math.Min(BLEND_BANDS_MAX, JsMath.Round(span / BLEND_BAND_PX)));

    TerrainUv4 BlendUvFor(int slot, int qx, int qy, int face, int band, int bands)
    {
        var uv = BlendUv[slot - 1];
        int i = DonorIndex(slot, qx, qy, face, band, bands);
        double u = uv[i * 2], v = uv[i * 2 + 1];
        return new TerrainUv4(u, v, u, v, u, v, u, v);
    }

    int DonorIndex(int slot, int qx, int qy, int face, int band, int bands)
    {
        int n = Blend!.Donors[slot - 1].Length;
        double t = bands > 1 ? (double)band / (bands - 1) : 0;
        double i = JsMath.Round(t * (n - 1)) + (BlendHash(qx, qy, face, band) % 3) - 1;
        return (int)Math.Max(0, Math.Min(n - 1, i));
    }

    TerrainRgb DonorPick(int slot, int qx, int qy, int face, int band, int bands)
    {
        var d = Blend!.Donors[slot - 1][DonorIndex(slot, qx, qy, face, band, bands)];
        return RgbAt(d.Index % W, d.Index / W);
    }

    bool WallExposed(int x, int y, int faceId)
    {
        int c = ClassAt(x, y);
        if (c == DepthClass.EMPTY) return false;
        int nc = ClassAt(x + AcrossX(faceId), y + AcrossY(faceId));
        return nc == DepthClass.EMPTY || (nc != c && Depth.Front[nc] < Depth.Front[c]);
    }

    interface IBandEmitter { void Emit(TerrainGeometryBuilder b, double loA, double hiA, double hiB, double loB, in TerrainUv4 uv); }

    /// <summary>A wall, as one quad or as a stack of colour bands down its depth (_wallBands).</summary>
    void WallBands<TE>(double baseZ, double zA, double zB, in TerrainUv4 uv0, int slot, int face, int qx, int qy, ref TE emit) where TE : struct, IBandEmitter
    {
        int bands = slot != 0 ? BlendBands(Math.Max(zA, zB) - baseZ) : 1;
        if (bands < 2) { emit.Emit(_b, baseZ, zA, zB, baseZ, uv0); return; }
        for (int k = 0; k < bands; k++)
        {
            double hi = 1 - (double)k / bands, lo = 1 - (double)(k + 1) / bands;
            var uv = k == 0 ? uv0 : BlendUvFor(slot, qx, qy, face, k, bands);
            emit.Emit(_b, baseZ + (zA - baseZ) * lo, baseZ + (zA - baseZ) * hi,
                baseZ + (zB - baseZ) * hi, baseZ + (zB - baseZ) * lo, uv);
        }
    }

    int KeyAt(int x, int y)
    {
        int c = ClassAt(x, y);
        if (c == DepthClass.EMPTY) return 0;
        return c * (Depth.RELIEF_TOP + 1) + ReliefAt(x, y);
    }

    // ------------------------------------------------------------------ decals

    /// <summary>The decals painted on the face: a second skin per covered pixel (setDecals).</summary>
    public void SetDecals(TerrainDecals? decals)
    {
        Decals = decals;
        if (Flat)
        {
            for (int i = 0; i < DecalMeshes.Length; i++) DecalMeshes[i] = null;
        }
        else RebuildAll();
    }

    ChunkGeometry? BuildDecalChunk(int cx, int cy)
    {
        if (Decals == null) return null;
        var coverage = Decals.Coverage;
        int x0 = cx * TERRAIN_CHUNK, y0 = cy * TERRAIN_CHUNK;
        int cw = Math.Min(TERRAIN_CHUNK, W - x0), ch = Math.Min(TERRAIN_CHUNK, H - y0);
        bool slope = Smooth && HasRelief;
        var b = _b;
        b.Reset();
        for (int ly = 0; ly < ch; ly++)
        {
            for (int lx = 0; lx < cw; lx++)
            {
                int px = x0 + lx, py = y0 + ly;
                if (coverage[px + py * W] == 0) continue;
                int c = ClassAt(px, py);
                if (c == DepthClass.EMPTY) continue;
                int front = FrontAt(px, py);
                double z00 = (slope ? CornerZ(px, py, c, front) : front) + DECAL_LIFT;
                double z10 = (slope ? CornerZ(px + 1, py, c, front) : front) + DECAL_LIFT;
                double z11 = (slope ? CornerZ(px + 1, py + 1, c, front) : front) + DECAL_LIFT;
                double z01 = (slope ? CornerZ(px, py + 1, c, front) : front) + DECAL_LIFT;
                var p00 = CornerPos(px, py); var p10 = CornerPos(px + 1, py);
                var p11 = CornerPos(px + 1, py + 1); var p01 = CornerPos(px, py + 1);
                int bse = b.VertexCount;
                b.Pos(p00.X, p00.Y, z00); b.Pos(p10.X, p10.Y, z10); b.Pos(p11.X, p11.Y, z11); b.Pos(p01.X, p01.Y, z01);
                double u0 = (double)px / W, u1 = (double)(px + 1) / W, v0 = (double)py / H, v1 = (double)(py + 1) / H;
                b.Uv(u0, v0); b.Uv(u1, v0); b.Uv(u1, v1); b.Uv(u0, v1);
                b.Quad(b.Indices, bse);
            }
        }
        if (b.Indices.Count == 0) return null;
        return b.ToGeometry(withColors: false, groups: false);
    }

    // ------------------------------------------------------------------ switches

    /// <summary>Swap in a new relief map (the 3D-terrain toggle) and re-mesh.</summary>
    public void SetRelief(byte[]? reliefMap)
    {
        Relief = reliefMap ?? new byte[W * H];
        Relief0 = (byte[])Relief.Clone();
        HasRelief = Array.Exists(Relief, v => v > 0);
        RebuildAll();
    }

    /// <summary>Swap in a new blend map (a surface-blend tag changed) and re-mesh.</summary>
    public void SetBlend(BlendMap? blendMap)
    {
        InstallBlend(blendMap);
        PaintPins();
        TextureNeedsUpdate = true;
        RebuildAll();
    }

    /// <summary>Slope between neighbouring heights instead of stepping (toggle).</summary>
    public void SetSmooth(bool smooth)
    {
        Smooth = smooth;
        RebuildAll();
    }

    /// <summary>Round the outline's corners off across the board (toggle).</summary>
    public void SetSmoothTerrain(bool on)
    {
        if (SmoothTerrain == on) return;
        SmoothTerrain = on;
        RebuildAll();
    }

    /// <summary>Where a corner of the pixel grid actually sits (_cornerPos).</summary>
    TerrainP2 CornerPos(int x, int y)
    {
        if (!SmoothTerrain) return new TerrainP2(x, y);
        bool a = ClassAt(x - 1, y - 1) != DepthClass.EMPTY, b = ClassAt(x, y - 1) != DepthClass.EMPTY;
        bool c = ClassAt(x - 1, y) != DepthClass.EMPTY, d = ClassAt(x, y) != DepthClass.EMPTY;
        int n = (a ? 1 : 0) + (b ? 1 : 0) + (c ? 1 : 0) + (d ? 1 : 0);
        if (n != 1 && n != 3) return new TerrainP2(x, y);
        bool o0 = n == 1 ? a : !a, o1 = n == 1 ? b : !b, o2 = n == 1 ? c : !c;
        int dx = (o0 || o2) ? -1 : 1;
        int dy = (o0 || o1) ? -1 : 1;
        return new TerrainP2(x + dx * TERRAIN_SMOOTH_PULL, y + dy * TERRAIN_SMOOTH_PULL);
    }

    void RebuildAll()
    {
        if (Flat) return;
        RebuildAllChunks();
    }

    void RebuildAllChunks()
    {
        _ids.Clear();
        for (int id = 0; id < ChunksX * ChunksY; id++) _ids.Add(id);
        Rebuild(_ids);
    }

    /// <summary>The 2D view's state (setFlat): the chunks wait while it is up and are re-meshed when it comes down.</summary>
    public void SetFlat(bool on, double? z = null)
    {
        if (z != null) FlatZ = z.Value;
        if (Flat == on) return;
        Flat = on;
        if (!on)
        {
            DirtyChunks.Clear();
            RebuildAll();
        }
    }

    double CornerZ(int x, int y, int cls, double fallback)
    {
        int sum = 0, n = 0;
        if (ClassAt(x - 1, y - 1) == cls) { sum += FrontAt(x - 1, y - 1); n++; }
        if (ClassAt(x, y - 1) == cls) { sum += FrontAt(x, y - 1); n++; }
        if (ClassAt(x - 1, y) == cls) { sum += FrontAt(x - 1, y); n++; }
        if (ClassAt(x, y) == cls) { sum += FrontAt(x, y); n++; }
        return n != 0 ? (double)sum / n : fallback;
    }

    void RefillTexRect(int x0, int y0, int w, int h)
    {
        if (x0 == 0 && y0 == 0 && w == W && h == H && PhysicsPaint == null) RefillPicture();
        else
            for (int y = y0; y < y0 + h; y++)
                for (int x = x0; x < x0 + w; x++) PaintPixel(x, y);
        PaintPins();
    }

    // every pixel's PaintPixel without the clear-physics paint: the picture's colours, the mask's
    // alpha - as one copy and one pass over the alpha bytes
    void RefillPicture()
    {
        int n = W * H;
        var src = Level.GroundImage;
        Buffer.BlockCopy(src, 0, TexData, 0, n * 4);
        var tex = TexData.AsSpan(0, n * 4);
        var mask = _mask.AsSpan(0, n);
        for (int j = 0; j < n; j++) tex[j * 4 + 3] = mask[j] != 0 ? (byte)255 : (byte)0;
    }

    /// <summary>One texture pixel: the picture, or the clear-physics paint (_paintPixel).</summary>
    void PaintPixel(int x, int y)
    {
        int i = (y * W + x) * 4, j = y * W + x;
        byte solid = _mask[j] != 0 ? (byte)255 : (byte)0;
        if (PhysicsPaint != null)
        {
            int bits = PhysicsPaint[j];
            int c = (bits & 2) != 0 ? 0x60 : 0xB0;
            int b = c;
            if ((bits & 2) == 0 && (bits & PhysicsHighlight) != 0) { c = 0x60; b = 0xB0; }
            int shade = ((x & 1) != (y & 1)) ? 0x20 : 0;
            TexData[i] = (byte)(c - shade); TexData[i + 1] = (byte)(c - shade); TexData[i + 2] = (byte)(b - shade);
        }
        else
        {
            var src = Level.GroundImage;
            TexData[i] = src[i]; TexData[i + 1] = src[i + 1]; TexData[i + 2] = src[i + 2];
        }
        TexData[i + 3] = solid;
    }

    /// <summary>Paint the terrain from this physics map with these one-way bits lit, or from its picture again with null.</summary>
    public void SetPhysicsPaint(ushort[]? physics, int highlight = 0)
    {
        highlight = physics != null ? highlight : 0;
        if (ReferenceEquals(PhysicsPaint, physics) && PhysicsHighlight == highlight) return;
        PhysicsPaint = physics;
        PhysicsHighlight = highlight;
        RefillTexRect(0, 0, W, H);
        TextureNeedsUpdate = true;
        if (Color != null) RebuildAll();
    }

    // ------------------------------------------------------------------ mutations

    void HookLevelMutations() => Level.GroundChanged += OnGroundChanged;

    // the wrapped setGroundAt (builder bricks are terrain) / clearGroundAt: which one it was is
    // what the mask now says
    void OnGroundChanged(int x, int y) =>
        ApplyMutation(x, y, _mask[x + y * W] != 0 ? DepthClass.TERRAIN : DepthClass.EMPTY);

    void ApplyMutation(int x, int y, byte depthClass)
    {
        if (x < 0 || x >= W || y < 0 || y >= H) return;
        int i = x + y * W;
        DepthMap[i] = depthClass;
        Relief[i] = 0;
        if (Blend != null) Blend.Slot[i] = 0;
        if (Color != null) Color[i] = 0;
        PaintPixel(x, y);
        if (BlendPinned.Contains(i)) PaintPins();
        TextureNeedsUpdate = true;
        MarkDirty(x, y);
    }

    void MarkDirty(int x, int y)
    {
        int cx = x / TERRAIN_CHUNK, cy = y / TERRAIN_CHUNK;
        var dirty = DirtyChunks;
        dirty.Add(cy * ChunksX + cx);
        int lx = x % TERRAIN_CHUNK, ly = y % TERRAIN_CHUNK;
        if (lx == 0 && cx > 0) dirty.Add(cy * ChunksX + cx - 1);
        if (lx == TERRAIN_CHUNK - 1 && cx < ChunksX - 1) dirty.Add(cy * ChunksX + cx + 1);
        if (ly == 0 && cy > 0) dirty.Add((cy - 1) * ChunksX + cx);
        if (ly == TERRAIN_CHUNK - 1 && cy < ChunksY - 1) dirty.Add((cy + 1) * ChunksX + cx);
    }

    /// <summary>
    /// The level's arrays changed under the mesh without the mutation hook (a saved state was put
    /// back, Game.OnRestore): the depth buffer is put back in step, the texture refilled and the
    /// chunks whose pixels changed re-meshed (resync).
    /// </summary>
    public void Resync()
    {
        MarkResync();
        FlushDirty(int.MaxValue);
    }

    /// <summary>Resync's first half: the maps and the texture put back in step and the changed chunks
    /// marked dirty; FlushDirty(int.MaxValue) re-meshes them (a host may do that a frame later).</summary>
    public void MarkResync()
    {
        var mask = _mask;
        var depth = DepthMap; var depth0 = Depth0; var relief = Relief; var relief0 = Relief0;
        var tex = TexData;
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                int i = y * W + x;
                bool solid = mask[i] != 0;
                bool changed = (tex[i * 4 + 3] != 0) != solid;
                // the common pixel: in step on every count
                if (!changed && solid == (depth[i] != DepthClass.EMPTY)) continue;
                if (solid && depth[i] == DepthClass.EMPTY)
                {
                    depth[i] = depth0[i] != DepthClass.EMPTY ? depth0[i] : DepthClass.TERRAIN;
                    relief[i] = relief0[i];
                    if (Blend != null) Blend.Slot[i] = BlendSlot0![i];
                    if (Color != null) Color[i] = Color0![i];
                    changed = true;
                }
                else if (!solid && depth[i] != DepthClass.EMPTY)
                {
                    depth[i] = DepthClass.EMPTY;
                    relief[i] = 0;
                    if (Blend != null) Blend.Slot[i] = 0;
                    if (Color != null) Color[i] = 0;
                    changed = true;
                }
                if (!changed) continue;
                MarkDirty(x, y);
            }
        }
        RefillTexRect(0, 0, W, H);
        TextureNeedsUpdate = true;
    }

    /// <summary>What app.js keeps per saved state (game.states.onSave): the depth and relief maps.</summary>
    public void SaveExtra(SavedState s)
    {
        // a recycled state's own arrays filled in again (SaveStates' spares)
        s.Extra["depth"] = LemGame.CopyInto(DepthMap, s.Extra.TryGetValue("depth", out var d) ? d as byte[] : null);
        s.Extra["relief"] = LemGame.CopyInto(Relief, s.Extra.TryGetValue("relief", out var r) ? r as byte[] : null);
    }

    /// <summary>And takes back (game.states.onLoad), before Resync.</summary>
    public void LoadExtra(SavedState s)
    {
        if (s.Extra.TryGetValue("depth", out var d) && d is byte[] depth) depth.CopyTo(DepthMap, 0);
        if (s.Extra.TryGetValue("relief", out var r) && r is byte[] relief) relief.CopyTo(Relief, 0);
    }

    /// <summary>Re-mesh dirty chunks, at most `budget` per call (nuke-proofing).</summary>
    public void FlushDirty(int budget = 24)
    {
        if (Flat) return;
        _ids.Clear();
        while (_ids.Count < budget && DirtyChunks.TryTakeFirst(out int id)) _ids.Add(id);
        Rebuild(_ids);
    }

    readonly List<int> _ids = new();
    ChunkGeometry?[] _builtChunks = Array.Empty<ChunkGeometry?>(), _builtDecals = Array.Empty<ChunkGeometry?>();

    // these chunks re-meshed, in this order: built (side by side when there are many), then put
    // in and announced one after the other, as one at a time would
    void Rebuild(List<int> ids)
    {
        int n = ids.Count;
        if (n < ParallelMin)
        {
            foreach (int id in ids) RebuildChunk(id % ChunksX, id / ChunksX);
            return;
        }
        if (_builtChunks.Length < n) { _builtChunks = new ChunkGeometry?[n]; _builtDecals = new ChunkGeometry?[n]; }
        var chunks = _builtChunks; var decals = _builtDecals;
        System.Threading.Tasks.Parallel.For(0, n, k =>
        {
            int id = ids[k], cx = id % ChunksX, cy = id / ChunksX;
            decals[k] = BuildDecalChunk(cx, cy);
            chunks[k] = BuildChunkGeometry(cx, cy);
        });
        for (int k = 0; k < n; k++)
        {
            int id = ids[k];
            DecalMeshes[id] = decals[k];
            ChunkMeshes[id] = chunks[k];
            chunks[k] = decals[k] = null;
            ChunkRebuilt?.Invoke(id);
        }
    }

    void RebuildChunk(int cx, int cy)
    {
        int id = cy * ChunksX + cx;
        ChunkMeshes[id] = null;
        DecalMeshes[id] = BuildDecalChunk(cx, cy);
        ChunkMeshes[id] = BuildChunkGeometry(cx, cy);
        ChunkRebuilt?.Invoke(id);
    }

    // ------------------------------------------------------------------ the smooth path

    static readonly double[] FrTwo = { 0, 1 };
    static double[] _frSoft => Scratch.FrSoft;

    // the windings the walls use: side keeps (aLo, aHi, bHi, bLo), cap is (bLo, aLo, aHi, bHi)
    static void Place<T>(bool cap, ref T a, ref T b, ref T c, ref T d)
    {
        if (!cap) return;
        var t = d; d = c; c = b; b = a; a = t;
    }

    struct SmoothWallEmitter : IBandEmitter
    {
        public TerrainP2 PA, PB;
        public bool Cap;
        public double Shade;
        public void Emit(TerrainGeometryBuilder b, double loA, double hiA, double hiB, double loB, in TerrainUv4 uv)
        {
            var q0 = new TerrainP3(PA.X, PA.Y, loA); var q1 = new TerrainP3(PA.X, PA.Y, hiA);
            var q2 = new TerrainP3(PB.X, PB.Y, hiB); var q3 = new TerrainP3(PB.X, PB.Y, loB);
            Place(Cap, ref q0, ref q1, ref q2, ref q3);
            b.PushQuad(q0, q1, q2, q3, uv, Shade);
        }
    }

    void SmoothWall(int px, int py, int c, TerrainRgb own, bool blendCol, int slot, double ramp, in TerrainUv4 wallUv,
        int nx, int ny, double baseZ, double zA, double zB, int faceId, double shade, TerrainP2 pA, TerrainP2 pB,
        int cAx, int cAy, int cBx, int cBy, bool cap)
    {
        if (!blendCol)
        {
            var em = new SmoothWallEmitter { PA = pA, PB = pB, Cap = cap, Shade = shade };
            WallBands(baseZ, zA, zB, wallUv, slot, faceId, px, py, ref em);
            return;
        }
        var b = _b;
        double[] fr;
        if (ramp < 1)
        {
            fr = _frSoft;
            fr[0] = 0; fr[1] = ramp / 2; fr[2] = 1 - ramp / 2; fr[3] = 1;
        }
        else fr = FrTwo;
        var stops = WallColors(cAx, cAy, cBx, cBy, fr.Length, c, own, px, py, nx, ny, faceId, slot, (zA + zB) / 2 - baseZ);
        double sA = WallShade(cAx, cAy, c, shade);
        double sB = WallShade(cBx, cBy, c, shade);
        for (int i = 0; i + 1 < fr.Length; i++)
        {
            double fA = fr[i], fB = fr[i + 1];
            double qAx = pA.X + (pB.X - pA.X) * fA, qAy = pA.Y + (pB.Y - pA.Y) * fA;
            double qBx = pA.X + (pB.X - pA.X) * fB, qBy = pA.Y + (pB.Y - pA.Y) * fB;
            double tA = zA + (zB - zA) * fA, tB = zA + (zB - zA) * fB;
            double shA = sA + (sB - sA) * fA, shB = sA + (sB - sA) * fB;
            double s0 = shA, s1 = shA, s2 = shB, s3 = shB;
            Place(cap, ref s0, ref s1, ref s2, ref s3);
            for (int k = 0; k + 1 < stops.Count; k++)
            {
                var hi = stops[k];
                var lo = stops[k + 1];
                var q0 = new TerrainP3(qAx, qAy, baseZ + (tA - baseZ) * lo.T);
                var q1 = new TerrainP3(qAx, qAy, baseZ + (tA - baseZ) * hi.T);
                var q2 = new TerrainP3(qBx, qBy, baseZ + (tB - baseZ) * hi.T);
                var q3 = new TerrainP3(qBx, qBy, baseZ + (tB - baseZ) * lo.T);
                Place(cap, ref q0, ref q1, ref q2, ref q3);
                var c0 = lo.C[i]; var c1 = hi.C[i]; var c2 = hi.C[i + 1]; var c3 = lo.C[i + 1];
                Place(cap, ref c0, ref c1, ref c2, ref c3);
                b.PushColorQuad(q0, q1, q2, q3, c0, c1, c2, c3, s0, s1, s2, s3);
            }
        }
    }

    /// <summary>Wall base for the smooth path: where this pixel overhangs empty space or a lower class (NaN: none).</summary>
    double WallBase(int c, int nx, int ny)
    {
        int nc = ClassAt(nx, ny);
        if (nc == DepthClass.EMPTY) return Depth.Back[c];
        if (nc != c && Depth.Front[nc] < Depth.Front[c]) return Depth.Front[nc];
        return double.NaN;
    }

    ChunkGeometry? BuildChunkGeometrySmooth(int cx, int cy)
    {
        int x0 = cx * TERRAIN_CHUNK, y0 = cy * TERRAIN_CHUNK;
        int cw = Math.Min(TERRAIN_CHUNK, W - x0), ch = Math.Min(TERRAIN_CHUNK, H - y0);
        bool slope = Smooth && HasRelief;
        double ramp = ColorSoftness;
        var b = _b;
        b.Reset();
        Span<double> U = stackalloc double[4];
        U[0] = 0; U[1] = ramp / 2; U[2] = 1 - ramp / 2; U[3] = 1;
        Span<TerrainP3> grid = stackalloc TerrainP3[16];

        for (int ly = 0; ly < ch; ly++)
        {
            for (int lx = 0; lx < cw; lx++)
            {
                int px = x0 + lx, py = y0 + ly;
                int c = ClassAt(px, py);
                if (c == DepthClass.EMPTY) continue;
                int bandBack = Depth.Back[c];
                double frontShade = Depth.FrontShade[c];
                int front = FrontAt(px, py);
                double z00 = slope ? CornerZ(px, py, c, front) : front;
                double z10 = slope ? CornerZ(px + 1, py, c, front) : front;
                double z11 = slope ? CornerZ(px + 1, py + 1, c, front) : front;
                double z01 = slope ? CornerZ(px, py + 1, c, front) : front;
                var p00 = CornerPos(px, py); var p10 = CornerPos(px + 1, py);
                var p11 = CornerPos(px + 1, py + 1); var p01 = CornerPos(px, py + 1);

                double u0 = (double)px / W, u1 = (double)(px + 1) / W;
                double v0 = (double)py / H, v1 = (double)(py + 1) / H;
                var uv = new TerrainUv4(u0, v0, u1, v0, u1, v1, u0, v1);
                bool blendCol = ColorAt(px, py) != 0;
                TerrainRgb own = default, cc0 = default, cc1 = default, cc2 = default, cc3 = default;
                if (blendCol)
                {
                    own = RgbAt(px, py);
                    cc0 = CornerColor(px, py, c, own, false); cc1 = CornerColor(px + 1, py, c, own, false);
                    cc2 = CornerColor(px + 1, py + 1, c, own, false); cc3 = CornerColor(px, py + 1, c, own, false);
                }
                var f0 = new TerrainP3(p00.X, p00.Y, z00); var f1 = new TerrainP3(p10.X, p10.Y, z10);
                var f2 = new TerrainP3(p11.X, p11.Y, z11); var f3 = new TerrainP3(p01.X, p01.Y, z01);
                double faceShade = SHADE_FRONT * frontShade;
                if (blendCol && ramp < 1)
                {
                    // the plateau: a 3x3 grid of quads, the middle one the pixel's own colour
                    var em0 = EdgeColor(px, py - 1, c, own); var em1 = EdgeColor(px + 1, py, c, own);
                    var em2 = EdgeColor(px, py + 1, c, own); var em3 = EdgeColor(px - 1, py, c, own);
                    for (int j = 0; j < 4; j++)
                        for (int i = 0; i < 4; i++)
                            grid[j * 4 + i] = Lerp3(Lerp3(f0, f1, U[i]), Lerp3(f3, f2, U[i]), U[j]);
                    TerrainRgb Col(int i, int j)
                    {
                        int iu = i == 0 ? 0 : i == 3 ? 2 : 1, jv = j == 0 ? 0 : j == 3 ? 2 : 1;
                        if (iu == 1 && jv == 1) return own;
                        if (iu == 1) return jv == 0 ? em0 : em2;
                        if (jv == 1) return iu == 0 ? em3 : em1;
                        return jv == 0 ? (iu == 0 ? cc0 : cc1) : (iu == 0 ? cc3 : cc2);
                    }
                    for (int j = 0; j < 3; j++)
                        for (int i = 0; i < 3; i++)
                            b.PushColorQuad(grid[j * 4 + i], grid[j * 4 + i + 1], grid[(j + 1) * 4 + i + 1], grid[(j + 1) * 4 + i],
                                Col(i, j), Col(i + 1, j), Col(i + 1, j + 1), Col(i, j + 1),
                                faceShade, faceShade, faceShade, faceShade);
                }
                else if (blendCol) b.PushColorQuad(f0, f1, f2, f3, cc0, cc1, cc2, cc3, faceShade, faceShade, faceShade, faceShade);
                else b.PushQuad(f0, f1, f2, f3, uv, faceShade);
                var k0 = new TerrainP3(p00.X, p00.Y, bandBack); var k1 = new TerrainP3(p10.X, p10.Y, bandBack);
                var k2 = new TerrainP3(p11.X, p11.Y, bandBack); var k3 = new TerrainP3(p01.X, p01.Y, bandBack);
                if (blendCol) b.PushColorQuad(k0, k1, k2, k3, cc0, cc1, cc2, cc3, SHADE_BACK, SHADE_BACK, SHADE_BACK, SHADE_BACK);
                else b.PushQuad(k0, k1, k2, k3, uv, SHADE_BACK);

                double uMid = (px + 0.5) / W, vMid = (py + 0.5) / H;
                var wallUv = new TerrainUv4(uMid, vMid, uMid, vMid, uMid, vMid, uMid, vMid);
                int slot = BlendAt(px, py);

                double bse = WallBase(c, px - 1, py);
                if (!double.IsNaN(bse))
                    SmoothWall(px, py, c, own, blendCol, slot, ramp, wallUv, px - 1, py, bse, z00, z01, 2, SHADE_LEFT,
                        p00, p01, px, py, px, py + 1, false);
                bse = WallBase(c, px + 1, py);
                if (!double.IsNaN(bse))
                    SmoothWall(px, py, c, own, blendCol, slot, ramp, wallUv, px + 1, py, bse, z10, z11, 3, SHADE_RIGHT,
                        p10, p11, px + 1, py, px + 1, py + 1, false);
                bse = WallBase(c, px, py - 1);
                if (!double.IsNaN(bse))
                    SmoothWall(px, py, c, own, blendCol, slot, ramp, wallUv, px, py - 1, bse, z10, z00, 4, SHADE_TOP,
                        p10, p00, px + 1, py, px, py, true);
                bse = WallBase(c, px, py + 1);
                if (!double.IsNaN(bse))
                    SmoothWall(px, py, c, own, blendCol, slot, ramp, wallUv, px, py + 1, bse, z11, z01, 5, SHADE_BOTTOM,
                        p11, p01, px + 1, py + 1, px, py + 1, true);
            }
        }

        if (b.Indices.Count == 0 && b.ColorIndices.Count == 0) return null;
        return b.ToGeometry(withColors: true, groups: b.ColorIndices.Count > 0);
    }

    static TerrainP3 Lerp3(in TerrainP3 a, in TerrainP3 b, double t) =>
        new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t);

    bool ChunkHasColor(int x0, int y0, int cw, int ch)
    {
        if (Color == null) return false;
        for (int y = y0; y < y0 + ch; y++)
        {
            int row = y * W;
            for (int x = x0; x < x0 + cw; x++) if (Color[row + x] != 0) return true;
        }
        return false;
    }

    /// <summary>The geometry of one chunk (null when it has nothing solid).</summary>
    public ChunkGeometry? BuildChunkGeometry(int cx, int cy)
    {
        int x0 = cx * TERRAIN_CHUNK, y0 = cy * TERRAIN_CHUNK;
        int cw = Math.Min(TERRAIN_CHUNK, W - x0), ch = Math.Min(TERRAIN_CHUNK, H - y0);
        if (SmoothTerrain || (Smooth && HasRelief) || ChunkHasColor(x0, y0, cw, ch))
            return BuildChunkGeometrySmooth(cx, cy);
        return BuildChunkGeometryStepped(cx, cy);
    }

    // ------------------------------------------------------------------ the stepped path

    /// <summary>Wall span between a pixel and its neighbour (_wallSpanAt): false when none.</summary>
    bool WallSpanAt(int x, int y, int nx, int ny, out int s0, out int s1)
    {
        s0 = s1 = 0;
        int c = ClassAt(x, y);
        if (c == DepthClass.EMPTY) return false;
        int front = FrontAt(x, y);
        if (ClassAt(nx, ny) == DepthClass.EMPTY) { s0 = Depth.Back[c]; s1 = front; return true; }
        int nFront = FrontAt(nx, ny);
        if (nFront < front) { s0 = nFront; s1 = front; return true; }
        return false;
    }

    struct VerticalWallEmitter : IBandEmitter
    {
        public int Wx, Ya, Yb;
        public double Shade;
        public void Emit(TerrainGeometryBuilder b, double loA, double hiA, double hiB, double loB, in TerrainUv4 uv) =>
            b.PushQuad(new TerrainP3(Wx, Ya, loA), new TerrainP3(Wx, Ya, hiA), new TerrainP3(Wx, Yb, hiB), new TerrainP3(Wx, Yb, loB), uv, Shade);
    }

    struct HorizontalWallEmitter : IBandEmitter
    {
        public int Xa, Xb, Wy;
        public double Shade;
        public void Emit(TerrainGeometryBuilder b, double loA, double hiA, double hiB, double loB, in TerrainUv4 uv) =>
            b.PushQuad(new TerrainP3(Xa, Wy, loA), new TerrainP3(Xb, Wy, loB), new TerrainP3(Xb, Wy, hiB), new TerrainP3(Xa, Wy, hiA), uv, Shade);
    }

    ChunkGeometry? BuildChunkGeometryStepped(int cx, int cy)
    {
        int x0 = cx * TERRAIN_CHUNK, y0 = cy * TERRAIN_CHUNK;
        int cw = Math.Min(TERRAIN_CHUNK, W - x0), ch = Math.Min(TERRAIN_CHUNK, H - y0);
        var b = _b;
        b.Reset();

        // --- front + back faces from greedy same-class, same-height rectangles
        Span<byte> visited = stackalloc byte[TERRAIN_CHUNK * TERRAIN_CHUNK];
        visited.Clear();
        for (int ly = 0; ly < ch; ly++)
        {
            for (int lx = 0; lx < cw; lx++)
            {
                if (visited[ly * cw + lx] != 0) continue;
                int c = ClassAt(x0 + lx, y0 + ly);
                if (c == DepthClass.EMPTY) continue;
                int key = KeyAt(x0 + lx, y0 + ly);
                int rw = 1;
                while (lx + rw < cw && visited[ly * cw + lx + rw] == 0 && KeyAt(x0 + lx + rw, y0 + ly) == key) rw++;
                int rh = 1;
                while (ly + rh < ch)
                {
                    bool stop = false;
                    for (int i = 0; i < rw; i++)
                    {
                        if (visited[(ly + rh) * cw + lx + i] != 0 || KeyAt(x0 + lx + i, y0 + ly + rh) != key) { stop = true; break; }
                    }
                    if (stop) break;
                    rh++;
                }
                for (int yy = 0; yy < rh; yy++)
                    for (int xx = 0; xx < rw; xx++) visited[(ly + yy) * cw + lx + xx] = 1;

                int bandBack = Depth.Back[c];
                int front = FrontAt(x0 + lx, y0 + ly);
                int px = x0 + lx, py = y0 + ly;
                double u0 = (double)px / W, u1 = (double)(px + rw) / W;
                double v0 = (double)py / H, v1 = (double)(py + rh) / H;
                var uv = new TerrainUv4(u0, v0, u1, v0, u1, v1, u0, v1);
                b.PushQuad(new TerrainP3(px, py, front), new TerrainP3(px + rw, py, front),
                    new TerrainP3(px + rw, py + rh, front), new TerrainP3(px, py + rh, front), uv, SHADE_FRONT * Depth.FrontShade[c]);
                b.PushQuad(new TerrainP3(px, py, bandBack), new TerrainP3(px + rw, py, bandBack),
                    new TerrainP3(px + rw, py + rh, bandBack), new TerrainP3(px, py + rh, bandBack), uv, SHADE_BACK);
            }
        }

        // --- walls where a pixel borders empty space or a lower class
        for (int lx = 0; lx < cw; lx++)
        {
            int px = x0 + lx;
            for (int dir = -1; dir <= 1; dir += 2)
            {
                int ly = 0;
                while (ly < ch)
                {
                    int c = ClassAt(px, y0 + ly);
                    if (!WallSpanAt(px, y0 + ly, px + dir, y0 + ly, out int s0, out int s1)) { ly++; continue; }
                    int slot = BlendAt(px, y0 + ly);
                    int run = 1;
                    while (ly + run < ch)
                    {
                        int y = y0 + ly + run;
                        if (slot != 0 && y % BLEND_RUN == 0) break;
                        int c2 = ClassAt(px, y);
                        bool has2 = WallSpanAt(px, y, px + dir, y, out int t0, out int t1);
                        if (c2 != c || !has2 || t0 != s0 || t1 != s1) break;
                        if (BlendAt(px, y) != slot) break;
                        run++;
                    }
                    int wx = dir == -1 ? px : px + 1;
                    double u = (px + 0.5) / W, va = (double)(y0 + ly) / H, vb = (double)(y0 + ly + run) / H;
                    var uv0 = new TerrainUv4(u, va, u, va, u, vb, u, vb);
                    int ya = y0 + ly, yb = y0 + ly + run;
                    var em = new VerticalWallEmitter { Wx = wx, Ya = ya, Yb = yb, Shade = dir == -1 ? SHADE_LEFT : SHADE_RIGHT };
                    WallBands(s0, s1, s1, uv0, slot, dir == -1 ? 2 : 3, px, ya / BLEND_RUN, ref em);
                    ly += run;
                }
            }
        }
        for (int ly = 0; ly < ch; ly++)
        {
            int py = y0 + ly;
            for (int dir = -1; dir <= 1; dir += 2)
            {
                int lx = 0;
                while (lx < cw)
                {
                    int c = ClassAt(x0 + lx, py);
                    if (!WallSpanAt(x0 + lx, py, x0 + lx, py + dir, out int s0, out int s1)) { lx++; continue; }
                    int slot = BlendAt(x0 + lx, py);
                    int run = 1;
                    while (lx + run < cw)
                    {
                        int x = x0 + lx + run;
                        if (slot != 0 && x % BLEND_RUN == 0) break;
                        int c2 = ClassAt(x, py);
                        bool has2 = WallSpanAt(x, py, x, py + dir, out int t0, out int t1);
                        if (c2 != c || !has2 || t0 != s0 || t1 != s1) break;
                        if (BlendAt(x, py) != slot) break;
                        run++;
                    }
                    int wy = dir == -1 ? py : py + 1;
                    double v = (py + 0.5) / H, ua = (double)(x0 + lx) / W, ub = (double)(x0 + lx + run) / W;
                    var uv0 = new TerrainUv4(ua, v, ub, v, ub, v, ua, v);
                    int xa = x0 + lx, xb = x0 + lx + run;
                    var em = new HorizontalWallEmitter { Xa = xa, Xb = xb, Wy = wy, Shade = dir == -1 ? SHADE_TOP : SHADE_BOTTOM };
                    WallBands(s0, s1, s1, uv0, slot, dir == -1 ? 4 : 5, xa / BLEND_RUN, py, ref em);
                    lx += run;
                }
            }
        }

        if (b.Indices.Count == 0) return null;
        return b.ToGeometry(withColors: true, groups: false);
    }

    public void Dispose() => Level.GroundChanged -= OnGroundChanged;
}

/// <summary>
/// The dirty chunk ids in a JS Set's order: insertion order, an id already present keeps its
/// place, and FlushDirty takes them from the front.
/// </summary>
public sealed class DirtyChunkSet
{
    readonly bool[] _member;
    readonly List<int> _queue = new();
    int _head;
    public int Count { get; private set; }

    public DirtyChunkSet(int size) { _member = new bool[size]; }

    public void Add(int id)
    {
        if (_member[id]) return;
        _member[id] = true;
        _queue.Add(id);
        Count++;
    }

    public bool Contains(int id) => _member[id];

    public bool TryTakeFirst(out int id)
    {
        if (_head >= _queue.Count) { id = -1; return false; }
        id = _queue[_head++];
        _member[id] = false;
        Count--;
        if (_head == _queue.Count) { _queue.Clear(); _head = 0; }
        return true;
    }

    public void Clear()
    {
        for (int i = _head; i < _queue.Count; i++) _member[_queue[i]] = false;
        _queue.Clear();
        _head = 0;
        Count = 0;
    }

    /// <summary>The ids in order, for inspection.</summary>
    public IEnumerable<int> Ids() { for (int i = _head; i < _queue.Count; i++) yield return _queue[i]; }
}

public readonly record struct TerrainRgb(double R, double G, double B);
public readonly record struct TerrainP2(double X, double Y);
public readonly record struct TerrainP3(double X, double Y, double Z);
public readonly record struct TerrainUv4(double U0, double V0, double U1, double V1, double U2, double V2, double U3, double V3);

/// <summary>The growing buffers of one geometry (the JS arrays pushed to), reused from chunk to chunk.</summary>
public sealed class TerrainGeometryBuilder
{
    public readonly TerrainFloatList Positions = new(), Colors = new(), Uvs = new();
    public readonly TerrainIntList Indices = new(), ColorIndices = new();

    public int VertexCount => Positions.Count / 3;

    public void Reset()
    {
        Positions.Clear(); Colors.Clear(); Uvs.Clear(); Indices.Clear(); ColorIndices.Clear();
    }

    public void Pos(double x, double y, double z) { Positions.Add((float)x); Positions.Add((float)y); Positions.Add((float)z); }
    public void Uv(double u, double v) { Uvs.Add((float)u); Uvs.Add((float)v); }
    public void Col(double r, double g, double b) { Colors.Add((float)r); Colors.Add((float)g); Colors.Add((float)b); }

    public void Quad(TerrainIntList into, int bse)
    {
        into.Add(bse); into.Add(bse + 1); into.Add(bse + 2); into.Add(bse); into.Add(bse + 2); into.Add(bse + 3);
    }

    /// <summary>pushQuad: a textured quad shaded by one factor.</summary>
    public void PushQuad(in TerrainP3 p0, in TerrainP3 p1, in TerrainP3 p2, in TerrainP3 p3, in TerrainUv4 uv, double shade)
    {
        int bse = VertexCount;
        Pos(p0.X, p0.Y, p0.Z); Col(shade, shade, shade); Uv(uv.U0, uv.V0);
        Pos(p1.X, p1.Y, p1.Z); Col(shade, shade, shade); Uv(uv.U1, uv.V1);
        Pos(p2.X, p2.Y, p2.Z); Col(shade, shade, shade); Uv(uv.U2, uv.V2);
        Pos(p3.X, p3.Y, p3.Z); Col(shade, shade, shade); Uv(uv.U3, uv.V3);
        Quad(Indices, bse);
    }

    /// <summary>pushColorQuad: a quad whose colour is its four corners', shaded per corner.</summary>
    public void PushColorQuad(in TerrainP3 p0, in TerrainP3 p1, in TerrainP3 p2, in TerrainP3 p3, in TerrainRgb c0, in TerrainRgb c1, in TerrainRgb c2, in TerrainRgb c3,
        double s0, double s1, double s2, double s3)
    {
        int bse = VertexCount;
        Pos(p0.X, p0.Y, p0.Z); Col(c0.R * s0, c0.G * s0, c0.B * s0); Uv(0, 0);
        Pos(p1.X, p1.Y, p1.Z); Col(c1.R * s1, c1.G * s1, c1.B * s1); Uv(0, 0);
        Pos(p2.X, p2.Y, p2.Z); Col(c2.R * s2, c2.G * s2, c2.B * s2); Uv(0, 0);
        Pos(p3.X, p3.Y, p3.Z); Col(c3.R * s3, c3.G * s3, c3.B * s3); Uv(0, 0);
        Quad(ColorIndices, bse);
    }

    /// <summary>The BufferGeometry: setIndex(indices.concat(colorIndices)), the groups when there are colour quads.</summary>
    public ChunkGeometry ToGeometry(bool withColors, bool groups)
    {
        var g = new ChunkGeometry
        {
            Positions = Positions.ToArray(),
            Colors = withColors ? Colors.ToArray() : null,
            Uvs = Uvs.ToArray(),
        };
        int n = Indices.Count, m = ColorIndices.Count;
        var idx = new int[n + m];
        Indices.CopyTo(idx, 0);
        ColorIndices.CopyTo(idx, n);
        g.Indices = idx;
        bool big = false;
        for (int i = idx.Length - 1; i >= 0; --i) if (idx[i] >= 65535) { big = true; break; } // arrayNeedsUint32
        g.Index32 = big;
        g.Groups = groups ? new[] { new GeometryGroup(0, n, 0), new GeometryGroup(n, m, 1) } : Array.Empty<GeometryGroup>();
        return g;
    }
}

public sealed class TerrainFloatList
{
    float[] _a = new float[1024];
    public int Count;
    public void Clear() => Count = 0;
    public void Add(float v)
    {
        if (Count == _a.Length) Array.Resize(ref _a, _a.Length * 2);
        _a[Count++] = v;
    }
    public float[] ToArray() => _a.AsSpan(0, Count).ToArray();
}

public sealed class TerrainIntList
{
    int[] _a = new int[1024];
    public int Count;
    public void Clear() => Count = 0;
    public void Add(int v)
    {
        if (Count == _a.Length) Array.Resize(ref _a, _a.Length * 2);
        _a[Count++] = v;
    }
    public void CopyTo(int[] dst, int at) => _a.AsSpan(0, Count).CopyTo(dst.AsSpan(at));
}
