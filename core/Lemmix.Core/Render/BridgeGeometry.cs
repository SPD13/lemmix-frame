using System.Runtime.CompilerServices;
using Lemmix.Engine;
using Lemmix.Util;

namespace Lemmix.Render;

// web/3d/js/bridge.js, its data side: the voxel relief every sprite frame is drawn as
// (buildExtrudedSpriteGeometry), the rounded and blended slices of the surfaces drawn as a
// stack (spriteBodyParts, buildBlendedSpriteGeometry), the colour blend baked into a texture
// (buildBlendedFrameRgba), the cache that builds them once per frame (SpriteGeometryCache), the
// cut of a sprite to the level (clipFrameToBounds), the recorded draws (SpriteCapture) and where
// BillboardPool / ParticleCloud put them. The buffers are what three.js is handed: Float32
// attributes (computed in doubles, in the JS order, rounded to float once at the end) and an
// index three.js stores as Uint16 or Uint32 (Index32). Materials are described, not made.

// A BufferGeometry's data: position (xyz), color (rgb, null when the geometry has none), uv,
// and the index. `new THREE.Float32BufferAttribute(array)` rounds each double to float32, which
// (float)double does the same way.
public sealed class GeometryBuffers
{
    public required float[] Position;
    public float[]? Color;
    public float[]? Uv;
    public required int[] Index;

    // BufferGeometry.setIndex(array): a Uint32 index when any entry is >= 65535 (three.js arrayNeedsUint32)
    public bool Index32
    {
        get
        {
            for (int i = Index.Length - 1; i >= 0; --i) if (Index[i] >= 65535) return true;
            return false;
        }
    }

    // The index as three.js stores it, little-endian bytes of Uint16 or Uint32 elements.
    public byte[] IndexBytes()
    {
        bool wide = Index32;
        var b = new byte[Index.Length * (wide ? 4 : 2)];
        for (int i = 0; i < Index.Length; i++)
        {
            uint v = unchecked((uint)Index[i]);
            if (wide) { b[i * 4] = (byte)v; b[i * 4 + 1] = (byte)(v >> 8); b[i * 4 + 2] = (byte)(v >> 16); b[i * 4 + 3] = (byte)(v >> 24); }
            else { ushort s = unchecked((ushort)v); b[i * 2] = (byte)s; b[i * 2 + 1] = (byte)(s >> 8); }
        }
        return b;
    }

    public static float[] ToFloat(List<double> list)
    {
        var a = new float[list.Count];
        for (int i = 0; i < a.Length; i++) a[i] = (float)list[i];
        return a;
    }

    // BufferGeometry.translate: each position through Vector3.applyMatrix4 of a translation
    // (x * 1 + y * 0 + z * 0 + t, times w = 1), read from and written back to float32.
    public void Translate(double tx, double ty, double tz)
    {
        for (int i = 0; i < Position.Length; i += 3)
        {
            double x = Position[i], y = Position[i + 1], z = Position[i + 2];
            Position[i] = (float)((1 * x + 0 * y + 0 * z + tx) * 1.0);
            Position[i + 1] = (float)((0 * x + 1 * y + 0 * z + ty) * 1.0);
            Position[i + 2] = (float)((0 * x + 0 * y + 1 * z + tz) * 1.0);
        }
    }
}

// The quads a sprite geometry collects, per face group, before they are emitted in painter's
// order (back -> walls -> fronts).
sealed class QuadList
{
    public readonly List<double> P = new(), Uv = new(), Shade = new();
    public int Count => Shade.Count;

    public void Add(double x0, double y0, double z0, double x1, double y1, double z1,
        double x2, double y2, double z2, double x3, double y3, double z3,
        double u0, double v0, double u1, double v1, double u2, double v2, double u3, double v3, double shade)
    {
        P.Add(x0); P.Add(y0); P.Add(z0); P.Add(x1); P.Add(y1); P.Add(z1);
        P.Add(x2); P.Add(y2); P.Add(z2); P.Add(x3); P.Add(y3); P.Add(z3);
        Uv.Add(u0); Uv.Add(v0); Uv.Add(u1); Uv.Add(v1); Uv.Add(u2); Uv.Add(v2); Uv.Add(u3); Uv.Add(v3);
        Shade.Add(shade);
    }

    // backQuads.concat(wallQuads, frontQuads) -> positions / colors / uvs / indices; null when empty
    public static GeometryBuffers? Emit(params QuadList[] lists)
    {
        int n = 0;
        foreach (var l in lists) n += l.Count;
        if (n == 0) return null;
        var pos = new float[n * 12];
        var col = new float[n * 12];
        var uv = new float[n * 8];
        var idx = new int[n * 6];
        int q = 0;
        foreach (var l in lists)
        {
            for (int k = 0; k < l.Count; k++, q++)
            {
                for (int i = 0; i < 12; i++) pos[q * 12 + i] = (float)l.P[k * 12 + i];
                float s = (float)l.Shade[k];
                for (int i = 0; i < 12; i++) col[q * 12 + i] = s;
                for (int i = 0; i < 8; i++) uv[q * 8 + i] = (float)l.Uv[k * 8 + i];
                int b = q * 4;
                idx[q * 6] = b; idx[q * 6 + 1] = b + 1; idx[q * 6 + 2] = b + 2;
                idx[q * 6 + 3] = b; idx[q * 6 + 4] = b + 2; idx[q * 6 + 5] = b + 3;
            }
        }
        return new GeometryBuffers { Position = pos, Color = col, Uv = uv, Index = idx };
    }
}

// A 1-bit stencil drawn as solid white (a Lemmings.Mask, or the stand-in clipFrameToBounds
// makes of one).
public sealed class SpriteMask
{
    public SpriteMask(int width, int height, sbyte[] bits, int offsetX = 0, int offsetY = 0)
    {
        Width = width; Height = height; Bits = bits; OffsetX = offsetX; OffsetY = offsetY;
    }
    public int Width { get; }
    public int Height { get; }
    public int OffsetX { get; }
    public int OffsetY { get; }
    public sbyte[] Bits { get; }
}

// A texture as three.js is handed it (DataTexture): RGBA bytes, its size, how it is filtered.
public sealed record SpriteTexture(byte[] Rgba, int Width, int Height, bool Linear);

// A MeshBasicMaterial as bridge.js makes it: its texture, vertex colours or not, its alpha test,
// and the tint (the flat silhouettes' one colour; white otherwise).
public sealed class SpriteMaterial
{
    public required SpriteTexture Map;
    public bool VertexColors;
    public double AlphaTest = 0.5;
    public int Color = 0xffffff;
}

// What the cache hands out for a frame: its material, its geometry (null: the empty geometry
// of a frame with nothing opaque) and its size.
public sealed class SpriteEntry
{
    public required SpriteMaterial Material;
    public GeometryBuffers? Geometry;
    public int W, H;
}

// spriteBodyParts' result: the largest 8-connected run of pixels, the rest, how many parts.
public sealed class SpriteBodyParts
{
    public required byte[] Body;
    public byte[]? Loose;
    public int Parts, LooseCount;
}

public static class SpriteBuild
{
    public const int SPRITE_DEPTH = 2;
    public const double SPRITE_SHADE_FRONT = 1.0;
    public const double SPRITE_SHADE_BACK = 0.45;
    public const double SPRITE_SHADE_LEFT = 0.62;
    public const double SPRITE_SHADE_RIGHT = 0.66;
    public const double SPRITE_SHADE_TOP = 0.85;
    public const double SPRITE_SHADE_BOTTOM = 0.5;
    public const double SPRITE_SMOOTH_PULL = 0.35;
    public const double SPRITE_SMOOTH_BEVEL = 0.4;
    public const double SPRITE_WALL_UV_INSET = 0.05;
    public const double SPRITE_BLEND_PINCH = 0.15;
    public const double PARTICLE_SIZE = 2.2;
    public const int FRAME_BLEND_SCALE = 4;

    // buildExtrudedSpriteGeometry: greedy front/back rectangles plus edge walls (runs merged per
    // direction), in sprite pixel space (origin top-left, y down, z toward the viewer).
    public static GeometryBuffers? BuildExtrudedSpriteGeometry(Func<int, int, bool> isSolidRaw, int w, int h, double depth)
    {
        bool IsSolid(int x, int y) => x >= 0 && x < w && y >= 0 && y < h && isSolidRaw(x, y);
        var backQuads = new QuadList();
        var wallQuads = new QuadList();
        var frontQuads = new QuadList();

        var visited = new byte[w * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                if (visited[y * w + x] != 0 || !IsSolid(x, y)) continue;
                int rw = 1;
                while (x + rw < w && visited[y * w + x + rw] == 0 && IsSolid(x + rw, y)) rw++;
                int rh = 1;
                while (y + rh < h)
                {
                    bool stop = false;
                    for (int i = 0; i < rw; i++)
                        if (visited[(y + rh) * w + x + i] != 0 || !IsSolid(x + i, y + rh)) { stop = true; break; }
                    if (stop) break;
                    rh++;
                }
                for (int yy = 0; yy < rh; yy++)
                    for (int xx = 0; xx < rw; xx++) visited[(y + yy) * w + x + xx] = 1;

                double u0 = (double)x / w, u1 = (double)(x + rw) / w;
                double v0 = (double)y / h, v1 = (double)(y + rh) / h;
                frontQuads.Add(x, y, depth, x + rw, y, depth, x + rw, y + rh, depth, x, y + rh, depth,
                    u0, v0, u1, v0, u1, v1, u0, v1, SPRITE_SHADE_FRONT);
                backQuads.Add(x, y, 0, x + rw, y, 0, x + rw, y + rh, 0, x, y + rh, 0,
                    u0, v0, u1, v0, u1, v1, u0, v1, SPRITE_SHADE_BACK);
            }
        }

        const double IN = SPRITE_WALL_UV_INSET;
        for (int x = 0; x < w; x++)
        {
            foreach (int dir in new[] { -1, 1 })
            {
                int y = 0;
                while (y < h)
                {
                    if (!IsSolid(x, y) || IsSolid(x + dir, y)) { y++; continue; }
                    int run = 1;
                    while (y + run < h && IsSolid(x, y + run) && !IsSolid(x + dir, y + run)) run++;
                    int wx = dir == -1 ? x : x + 1;
                    double u = (x + (dir == -1 ? IN : 1 - IN)) / w;
                    double va = (y + IN) / h, vb = (y + run - IN) / h;
                    wallQuads.Add(wx, y, 0, wx, y, depth, wx, y + run, depth, wx, y + run, 0,
                        u, va, u, va, u, vb, u, vb, dir == -1 ? SPRITE_SHADE_LEFT : SPRITE_SHADE_RIGHT);
                    y += run;
                }
            }
        }
        for (int y = 0; y < h; y++)
        {
            foreach (int dir in new[] { -1, 1 })
            {
                int x = 0;
                while (x < w)
                {
                    if (!IsSolid(x, y) || IsSolid(x, y + dir)) { x++; continue; }
                    int run = 1;
                    while (x + run < w && IsSolid(x + run, y) && !IsSolid(x + run, y + dir)) run++;
                    int wy = dir == -1 ? y : y + 1;
                    double v = (y + (dir == -1 ? IN : 1 - IN)) / h;
                    double ua = (x + IN) / w, ub = (x + run - IN) / w;
                    wallQuads.Add(x, wy, 0, x + run, wy, 0, x + run, wy, depth, x, wy, depth,
                        ua, v, ub, v, ub, v, ua, v, dir == -1 ? SPRITE_SHADE_TOP : SPRITE_SHADE_BOTTOM);
                    x += run;
                }
            }
        }
        return QuadList.Emit(backQuads, wallQuads, frontQuads);
    }

    // spriteBodyParts: the largest run of touching pixels (corners count) is the body, the rest loose.
    public static SpriteBodyParts? SpriteBodyParts(sbyte[] mask, int w, int h)
    {
        var label = new int[w * h];
        Array.Fill(label, -1);
        var sizes = new List<int>();
        var stack = new Stack<int>();
        for (int seed = 0; seed < w * h; seed++)
        {
            if (mask[seed] == 0 || label[seed] >= 0) continue;
            int id = sizes.Count;
            int n = 0;
            label[seed] = id;
            stack.Push(seed);
            while (stack.Count > 0)
            {
                int i = stack.Pop();
                n++;
                int x = i % w, y = i / w;
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        int nx = x + dx, ny = y + dy;
                        if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                        int j = ny * w + nx;
                        if (mask[j] != 0 && label[j] < 0) { label[j] = id; stack.Push(j); }
                    }
                }
            }
            sizes.Add(n);
        }
        if (sizes.Count == 0) return null;
        int big = 0;
        for (int i = 1; i < sizes.Count; i++) if (sizes[i] > sizes[big]) big = i;
        var body = new byte[w * h];
        var loose = new byte[w * h];
        int looseCount = 0;
        for (int i = 0; i < w * h; i++)
        {
            if (label[i] < 0) continue;
            if (label[i] == big) body[i] = 1;
            else { loose[i] = 1; looseCount++; }
        }
        return new SpriteBodyParts { Body = body, Loose = looseCount != 0 ? loose : null, Parts = sizes.Count, LooseCount = looseCount };
    }

    sealed class Corner { public double X, Y, Front, Back; }

    // buildBlendedSpriteGeometry: one slice of a surface, rounded off in all three directions
    // and blended into the slices either side of it (`prevAt` / `nextAt`, null at the ends).
    public static GeometryBuffers? BuildBlendedSpriteGeometry(byte[] body, byte[]? loose,
        Func<int, int, bool>? prevAt, Func<int, int, bool>? nextAt, int w, int h, double depth)
    {
        double half = depth / 2;
        bool At(byte[] m, int x, int y) => x >= 0 && x < w && y >= 0 && y < h && m[y * w + x] != 0;
        var backQuads = new QuadList();
        var wallQuads = new QuadList();
        var frontQuads = new QuadList();

        Func<int, int, double> RimOf(byte[] mask) => (x, y) =>
            (!At(mask, x - 1, y) || !At(mask, x + 1, y) || !At(mask, x, y - 1) || !At(mask, x, y + 1)) ? 1 - SPRITE_SMOOTH_BEVEL : 1;

        void Emit(byte[] mask, Func<int, int, double> frontFactor, Func<int, int, double> backFactor)
        {
            bool IsSolid(int x, int y) => At(mask, x, y);
            var corners = new Corner?[(w + 1) * (h + 1)];
            Corner CornerAt(int x, int y)
            {
                int slot = y * (w + 1) + x;
                var c = corners[slot];
                if (c != null) return c;
                bool a = IsSolid(x - 1, y - 1), b = IsSolid(x, y - 1);
                bool cc = IsSolid(x - 1, y), d = IsSolid(x, y);
                int n = (a ? 1 : 0) + (b ? 1 : 0) + (cc ? 1 : 0) + (d ? 1 : 0);
                int dx = 0, dy = 0;
                if (n == 1 || n == 3)
                {
                    bool o0, o1, o2;
                    if (n == 1) { o0 = a; o1 = b; o2 = cc; } else { o0 = !a; o1 = !b; o2 = !cc; }
                    dx = (o0 || o2) ? -1 : 1;
                    dy = (o0 || o1) ? -1 : 1;
                }
                double fs = 0, bs = 0; int k = 0;
                void Add(int px, int py)
                {
                    if (!IsSolid(px, py)) return;
                    fs += frontFactor(px, py); bs += backFactor(px, py); k++;
                }
                Add(x - 1, y - 1); Add(x, y - 1); Add(x - 1, y); Add(x, y);
                double f = k != 0 ? fs / k : 1, bk = k != 0 ? bs / k : 1;
                c = new Corner
                {
                    X = x + dx * SPRITE_SMOOTH_PULL, Y = y + dy * SPRITE_SMOOTH_PULL,
                    Front = half + half * f, Back = half - half * bk,
                };
                corners[slot] = c;
                return c;
            }

            const double IN = SPRITE_WALL_UV_INSET;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    if (!IsSolid(x, y)) continue;
                    Corner c00 = CornerAt(x, y), c10 = CornerAt(x + 1, y);
                    Corner c11 = CornerAt(x + 1, y + 1), c01 = CornerAt(x, y + 1);
                    double u0 = (double)x / w, u1 = (double)(x + 1) / w, v0 = (double)y / h, v1 = (double)(y + 1) / h;
                    frontQuads.Add(c00.X, c00.Y, c00.Front, c10.X, c10.Y, c10.Front, c11.X, c11.Y, c11.Front, c01.X, c01.Y, c01.Front,
                        u0, v0, u1, v0, u1, v1, u0, v1, SPRITE_SHADE_FRONT);
                    backQuads.Add(c00.X, c00.Y, c00.Back, c10.X, c10.Y, c10.Back, c11.X, c11.Y, c11.Back, c01.X, c01.Y, c01.Back,
                        u0, v0, u1, v0, u1, v1, u0, v1, SPRITE_SHADE_BACK);

                    double Ux(bool right) => (x + (right ? 1 - IN : IN)) / w;
                    double Vy(bool down) => (y + (down ? 1 - IN : IN)) / h;
                    void Wall(Corner a, Corner b, double shade, bool ra, bool da, bool rb, bool db)
                    {
                        double ua = Ux(ra), va = Vy(da), ub = Ux(rb), vb = Vy(db);
                        wallQuads.Add(a.X, a.Y, a.Back, a.X, a.Y, a.Front, b.X, b.Y, b.Front, b.X, b.Y, b.Back,
                            ua, va, ua, va, ub, vb, ub, vb, shade);
                    }
                    if (!IsSolid(x - 1, y)) Wall(c00, c01, SPRITE_SHADE_LEFT, false, false, false, true);
                    if (!IsSolid(x + 1, y)) Wall(c11, c10, SPRITE_SHADE_RIGHT, true, true, true, false);
                    if (!IsSolid(x, y - 1)) Wall(c10, c00, SPRITE_SHADE_TOP, true, false, false, false);
                    if (!IsSolid(x, y + 1)) Wall(c01, c11, SPRITE_SHADE_BOTTOM, false, true, true, true);
                }
            }
        }

        var bodyRim = RimOf(body);
        Emit(body,
            prevAt != null ? (x, y) => prevAt(x, y) ? 1 : SPRITE_BLEND_PINCH : bodyRim,
            nextAt != null ? (x, y) => nextAt(x, y) ? 1 : SPRITE_BLEND_PINCH : bodyRim);
        if (loose != null)
        {
            var looseRim = RimOf(loose);
            Emit(loose, looseRim, looseRim);
        }
        return QuadList.Emit(backQuads, wallQuads, frontQuads);
    }

    // A store into a Uint8Array: ToUint8 (truncate, wrap modulo 256).
    static byte U8(double v) => (byte)(JsMath.ToInt32(v) & 255);

    // buildBlendedFrameRgba: each pixel FRAME_BLEND_SCALE texels across, every sub-texel the
    // colour the terrain's grid would put there; transparent texels filled with the mean of the
    // opaque pixels beside them, alpha not blended.
    // a colour as three doubles (the JS arrays), by value
    readonly record struct Rgb3(double R, double G, double B)
    {
        public static Rgb3 Lerp(in Rgb3 a, in Rgb3 b, double t) =>
            new(a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t);
    }

    public static byte[] BuildBlendedFrameRgba(byte[] rgba, int w, int h, double softness)
    {
        const int S = FRAME_BLEND_SCALE;
        bool Opaque(int x, int y) => x >= 0 && x < w && y >= 0 && y < h && rgba[(y * w + x) * 4 + 3] != 0;
        // the mean of whichever of these pixels are opaque (`pts` as x, y pairs), false for none
        bool MeanOf(ReadOnlySpan<int> pts, out Rgb3 mean)
        {
            double r = 0, g = 0, b = 0; int n = 0;
            for (int k = 0; k < pts.Length; k += 2)
            {
                int px = pts[k], py = pts[k + 1];
                if (!Opaque(px, py)) continue;
                int o = (py * w + px) * 4;
                r += rgba[o]; g += rgba[o + 1]; b += rgba[o + 2]; n++;
            }
            mean = n != 0 ? new Rgb3(r / n, g / n, b / n) : default;
            return n != 0;
        }
        Rgb3 CornerMean(int x, int y, in Rgb3 own) =>
            MeanOf(stackalloc int[] { x - 1, y - 1, x, y - 1, x - 1, y, x, y }, out var m) ? m : own;
        Rgb3 EdgeMean(int x, int y, int nx, int ny, in Rgb3 own) =>
            MeanOf(stackalloc int[] { x, y, nx, ny }, out var m) ? m : own;
        Rgb3 AroundMean(int x, int y) =>
            MeanOf(stackalloc int[] { x - 1, y, x + 1, y, x, y - 1, x, y + 1, x - 1, y - 1, x + 1, y - 1, x - 1, y + 1, x + 1, y + 1 }, out var m) ? m : default;
        Rgb3 RgbAt(int x, int y) { int o = (y * w + x) * 4; return new Rgb3(rgba[o], rgba[o + 1], rgba[o + 2]); }

        var output = new byte[w * S * h * S * 4];
        void Put(int x, int y, int i, int j, in Rgb3 c, int a)
        {
            int o = (((y * S + j) * w * S) + x * S + i) * 4;
            output[o] = U8(c.R); output[o + 1] = U8(c.G); output[o + 2] = U8(c.B); output[o + 3] = (byte)a;
        }

        // the stops and the grid of colours between them (2 x 2 fully soft, else 4 x 4)
        double[] stops = softness >= 1 ? new double[] { 0, 1 } : new[] { 0, softness / 2, 1 - softness / 2, 1 };
        int gn = stops.Length;
        var grid = new Rgb3[gn * gn];
        (int, double) Cell(double t)
        {
            int k = 0;
            while (k < stops.Length - 2 && t > stops[k + 1]) k++;
            double a = stops[k], b = stops[k + 1];
            return (k, b > a ? (t - a) / (b - a) : 0);
        }
        // each sub-texel's cell and fraction: the same for every pixel
        var cells = new (int, double)[S];
        for (int i = 0; i < S; i++) cells[i] = Cell((i + 0.5) / S);

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                if (!Opaque(x, y))
                {
                    var fill = AroundMean(x, y);
                    for (int j = 0; j < S; j++) for (int i = 0; i < S; i++) Put(x, y, i, j, fill, 0);
                    continue;
                }
                var own = RgbAt(x, y);
                Rgb3 c0 = CornerMean(x, y, own), c1 = CornerMean(x + 1, y, own), c2 = CornerMean(x + 1, y + 1, own), c3 = CornerMean(x, y + 1, own);
                if (softness >= 1)
                {
                    grid[0] = c0; grid[1] = c1;
                    grid[2] = c3; grid[3] = c2;
                }
                else
                {
                    Rgb3 e0 = EdgeMean(x, y, x, y - 1, own), e1 = EdgeMean(x, y, x + 1, y, own),
                         e2 = EdgeMean(x, y, x, y + 1, own), e3 = EdgeMean(x, y, x - 1, y, own);
                    grid[0] = c0; grid[1] = e0; grid[2] = e0; grid[3] = c1;
                    grid[4] = e3; grid[5] = own; grid[6] = own; grid[7] = e1;
                    grid[8] = e3; grid[9] = own; grid[10] = own; grid[11] = e1;
                    grid[12] = c3; grid[13] = e2; grid[14] = e2; grid[15] = c2;
                }
                for (int j = 0; j < S; j++)
                {
                    var (jv, fv) = cells[j];
                    for (int i = 0; i < S; i++)
                    {
                        var (iu, fu) = cells[i];
                        Put(x, y, i, j, Rgb3.Lerp(
                            Rgb3.Lerp(grid[jv * gn + iu], grid[jv * gn + iu + 1], fu),
                            Rgb3.Lerp(grid[(jv + 1) * gn + iu], grid[(jv + 1) * gn + iu + 1], fu), fv), 255);
                    }
                }
            }
        }
        return output;
    }

    // clipFrameToBounds: the part of a frame inside the level's rectangle, as a frame of its own
    // with its offsets moved so it is drawn at the same place; the frame itself when it lies
    // entirely inside, null when entirely outside; memoised on the frame and the cut.
    // a frame's cuts by (sx, sy, cw, ch): the "sx,sy,cw,ch" key as numbers
    static readonly ConditionalWeakTable<object, Dictionary<(int, int, int, int), object>> ClippedFrames = new();

    static bool Cut(int w, int h, int offX, int offY, int x, int y, bool flipY, int boundsW, int boundsH,
        out bool inside, out int sx, out int sy, out int cw, out int ch, out int offsetX, out int offsetY, out (int, int, int, int) key)
    {
        int left = x + offX, top = y + offY;
        int x0 = Math.Max(0, left), y0 = Math.Max(0, top);
        int x1 = Math.Min(boundsW, left + w), y1 = Math.Min(boundsH, top + h);
        inside = x0 <= left && y0 <= top && x1 >= left + w && y1 >= top + h;
        sx = sy = cw = ch = offsetX = offsetY = 0; key = default;
        if (inside) return true;
        if (x1 <= x0 || y1 <= y0) return false;
        sx = x0 - left; cw = x1 - x0; ch = y1 - y0;
        sy = flipY ? h - (y1 - top) : y0 - top;
        key = (sx, sy, cw, ch);
        offsetX = offX + sx;
        offsetY = offY + (y0 - top);
        return true;
    }

    public static Frame? ClipFrameToBounds(Frame frame, int x, int y, bool flipY, int boundsW, int boundsH)
    {
        int w = frame.Width, h = frame.Height;
        if (!Cut(w, h, frame.OffsetX, frame.OffsetY, x, y, flipY, boundsW, boundsH,
            out bool inside, out int sx, out int sy, out int cw, out int ch, out int offsetX, out int offsetY, out var key)) return null;
        if (inside) return frame;
        var cuts = ClippedFrames.GetValue(frame, _ => new Dictionary<(int, int, int, int), object>());
        if (cuts.TryGetValue(key, out var hit)) return (Frame)hit;
        var cut = new Frame(cw, ch, offsetX, offsetY);
        for (int r = 0; r < ch; r++)
        {
            int from = (sy + r) * w + sx, to = r * cw;
            Array.Copy(frame.Mask, from, cut.Mask, to, cw);
            Array.Copy(frame.Data, from, cut.Data, to, cw);
        }
        cuts[key] = cut;
        return cut;
    }

    public static SpriteMask? ClipFrameToBounds(SpriteMask mask, int x, int y, bool flipY, int boundsW, int boundsH)
    {
        int w = mask.Width, h = mask.Height;
        if (!Cut(w, h, mask.OffsetX, mask.OffsetY, x, y, flipY, boundsW, boundsH,
            out bool inside, out int sx, out int sy, out int cw, out int ch, out int offsetX, out int offsetY, out var key)) return null;
        if (inside) return mask;
        var cuts = ClippedFrames.GetValue(mask, _ => new Dictionary<(int, int, int, int), object>());
        if (cuts.TryGetValue(key, out var hit)) return (SpriteMask)hit;
        var bits = new sbyte[cw * ch];
        for (int r = 0; r < ch; r++) Array.Copy(mask.Bits, (sy + r) * w + sx, bits, r * cw, cw);
        var cut = new SpriteMask(cw, ch, bits, offsetX, offsetY);
        cuts[key] = cut;
        return cut;
    }

    // ParticleCloud.sync: the captured setPixel calls (x, y, r, g, b per particle) as the point
    // cloud's position and colour buffers, every point on the plane `z`.
    public static (float[] Position, float[] Color) ParticleBuffers(IReadOnlyList<double> flat, double z)
    {
        int count = flat.Count / 5;
        var pos = new float[count * 3];
        var col = new float[count * 3];
        for (int i = 0; i < count; i++)
        {
            pos[i * 3] = (float)flat[i * 5];
            pos[i * 3 + 1] = (float)flat[i * 5 + 1];
            pos[i * 3 + 2] = (float)z;
            col[i * 3] = (float)(flat[i * 5 + 2] / 255);
            col[i * 3 + 1] = (float)(flat[i * 5 + 3] / 255);
            col[i * 3 + 2] = (float)(flat[i * 5 + 4] / 255);
        }
        return (pos, col);
    }

    // ParticleCloud.updateScale: a point the size of a level pixel at the view's zoom
    // (orthographic, `pxPerUnit`) or PARTICLE_SIZE, times the cloud's world scale.
    public static double ParticleSize(double? pxPerUnit, double worldScaleX) =>
        (pxPerUnit is double p && p != 0 ? Math.Max(1, p) : PARTICLE_SIZE) * Math.Abs(worldScaleX);
}

// SpriteGeometryCache: builds (and caches) voxel geometry + material for a game Frame or Mask.
public sealed class SpriteGeometryCache
{
    readonly Dictionary<Frame, SpriteEntry> _byFrame = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<SpriteMask, SpriteEntry> _byMask = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<Frame, SpriteBodyParts?> _partsByFrame = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<(int, int, int), SpriteEntry> _blendedByKey = new();
    readonly Dictionary<Frame, int> _frameIds = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<Frame, SpriteMaterial> _flatByFrame = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<Frame, Dictionary<double, SpriteMaterial>> _softByFrame = new(ReferenceEqualityComparer.Instance);
    int _flatColor = 0xffffff;

    public double BlendSoftness { get; private set; }

    // setColorBlend: the colour blend the surfaces are drawn with from now on, 0 = off
    public void SetColorBlend(double softness)
    {
        double s = double.IsNaN(softness) ? 0 : softness;
        BlendSoftness = Math.Max(0, Math.Min(1, s));
    }

    // flatMaterialFor: a white cut-out of the frame, tinted by the shared colour
    public SpriteMaterial FlatMaterialFor(Frame frame)
    {
        if (_flatByFrame.TryGetValue(frame, out var m)) return m;
        int w = frame.Width, h = frame.Height;
        var mask = frame.Mask;
        var rgba = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
            if (mask[i] != 0) rgba[i * 4] = rgba[i * 4 + 1] = rgba[i * 4 + 2] = rgba[i * 4 + 3] = 255;
        m = new SpriteMaterial { Map = MakeTexture(rgba, w, h), Color = _flatColor, AlphaTest = 0.5 };
        _flatByFrame[frame] = m;
        return m;
    }

    // setFlatColor: the one colour every silhouette wears now
    public void SetFlatColor(int hex)
    {
        if (_flatColor == hex) return;
        _flatColor = hex;
        foreach (var m in _flatByFrame.Values) m.Color = hex;
    }

    static SpriteTexture MakeTexture(byte[] rgba, int w, int h) => new(rgba, w, h, false);

    static SpriteEntry MakeEntry(byte[] rgba, Func<int, int, bool> solidFn, int w, int h)
    {
        var material = new SpriteMaterial { Map = MakeTexture(rgba, w, h), VertexColors = true, AlphaTest = 0.5 };
        var geometry = SpriteBuild.BuildExtrudedSpriteGeometry(solidFn, w, h, SpriteBuild.SPRITE_DEPTH);
        return new SpriteEntry { Material = material, Geometry = geometry, W = w, H = h };
    }

    // _rgbaOf: a frame's pixels as RGBA, its mask written into the alpha
    public static byte[] RgbaOf(Frame frame)
    {
        int w = frame.Width, h = frame.Height;
        var rgba = new byte[w * h * 4];
        Buffer.BlockCopy(frame.Data, 0, rgba, 0, w * h * 4);
        var mask = frame.Mask;
        for (int i = 0; i < w * h; i++) rgba[i * 4 + 3] = (byte)(mask[i] != 0 ? 255 : 0);
        return rgba;
    }

    // forFrame: a Frame's relief and its own texture
    public SpriteEntry ForFrame(Frame frame)
    {
        if (_byFrame.TryGetValue(frame, out var entry)) return entry;
        entry = MakeFrameEntry(frame);
        _byFrame[frame] = entry;
        return entry;
    }

    // (its own method: the lambda's captures would otherwise be allocated on every call, hits too)
    static SpriteEntry MakeFrameEntry(Frame frame)
    {
        int w = frame.Width;
        var mask = frame.Mask;
        return MakeEntry(RgbaOf(frame), (x, y) => mask[y * w + x] != 0, w, frame.Height);
    }

    // blendedMaterialFor: the frame's material with the colour blend baked in at the strength
    // the switch is on; off, the frame's ordinary material
    public SpriteMaterial BlendedMaterialFor(Frame frame)
    {
        double softness = BlendSoftness;
        if (!(softness > 0)) return ForFrame(frame).Material;
        if (!_softByFrame.TryGetValue(frame, out var byStrength)) { byStrength = new(); _softByFrame[frame] = byStrength; }
        if (byStrength.TryGetValue(softness, out var material)) return material;
        int w = frame.Width, h = frame.Height;
        var blended = SpriteBuild.BuildBlendedFrameRgba(RgbaOf(frame), w, h, softness);
        const int S = SpriteBuild.FRAME_BLEND_SCALE;
        material = new SpriteMaterial { Map = new SpriteTexture(blended, w * S, h * S, true), VertexColors = true, AlphaTest = 0.25 };
        byStrength[softness] = material;
        return material;
    }

    // _partsFor: a frame's body and whatever floats loose of it, worked out once
    public SpriteBodyParts? PartsFor(Frame frame)
    {
        if (_partsByFrame.TryGetValue(frame, out var parts)) return parts;
        parts = SpriteBuild.SpriteBodyParts(frame.Mask, frame.Width, frame.Height);
        _partsByFrame[frame] = parts;
        return parts;
    }

    int FrameId(Frame? frame)
    {
        if (frame == null) return -1;
        if (!_frameIds.TryGetValue(frame, out int id)) { id = _frameIds.Count; _frameIds[frame] = id; }
        return id;
    }

    // _bodyReader: a neighbouring slice's body read in `frame`'s own pixel grid
    Func<int, int, bool>? BodyReader(Frame frame, Frame? other)
    {
        if (other == null) return null;
        var parts = PartsFor(other);
        if (parts == null) return null;
        int ow = other.Width, oh = other.Height;
        var body = parts.Body;
        int dx = frame.OffsetX - other.OffsetX;
        int dy = frame.OffsetY - other.OffsetY;
        return (x, y) =>
        {
            int ax = x + dx, ay = y + dy;
            return ax >= 0 && ax < ow && ay >= 0 && ay < oh && body[ay * ow + ax] != 0;
        };
    }

    // forFrameBlended: one slice of a surface, rounded off and blended into the slices either
    // side of it (null for a neighbour at the front or back of the stack)
    public SpriteEntry ForFrameBlended(Frame? prevFrame, Frame frame, Frame? nextFrame)
    {
        var key = (FrameId(prevFrame), FrameId(frame), FrameId(nextFrame));
        if (_blendedByKey.TryGetValue(key, out var entry)) return entry;
        var flat = ForFrame(frame);
        var parts = PartsFor(frame);
        GeometryBuffers? geometry = null;
        if (parts != null)
        {
            geometry = SpriteBuild.BuildBlendedSpriteGeometry(parts.Body, parts.Loose,
                BodyReader(frame, prevFrame), BodyReader(frame, nextFrame), frame.Width, frame.Height, SpriteBuild.SPRITE_DEPTH);
        }
        entry = new SpriteEntry { Material = flat.Material, Geometry = geometry, W = flat.W, H = flat.H };
        _blendedByKey[key] = entry;
        return entry;
    }

    // forMask: a 1-bit stencil, drawn as solid white
    public SpriteEntry ForMask(SpriteMask mask)
    {
        if (_byMask.TryGetValue(mask, out var entry)) return entry;
        int w = mask.Width, h = mask.Height;
        var bits = mask.Bits;
        var rgba = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
            if (bits[i] != 0) rgba[i * 4] = rgba[i * 4 + 1] = rgba[i * 4 + 2] = rgba[i * 4 + 3] = 255;
        entry = MakeEntry(rgba, (x, y) => bits[y * w + x] != 0, w, h);
        _byMask[mask] = entry;
        return entry;
    }
}

// A captured draw: a frame or a mask, where, its layer (-2 behind the slab, -1 low, 0 with the
// lemmings, 1 a decal), and `Off` when nothing of it lies inside the level.
public sealed class CapturedDraw
{
    public Frame? Frame;
    public SpriteMask? Mask;
    public int X, Y;
    public bool FlipY;
    public int Layer;
    public bool OneWay;
    public string? Key;
    public bool Off;
}

// SpriteCapture: the fake display the game's render methods draw into; every draw recorded
// (cut to the level's rectangle once that is known), the particles' setPixel calls kept flat.
public sealed class SpriteCapture
{
    public readonly List<CapturedDraw> Items = new();
    public readonly List<double> Particles = new();
    public object? Tag;
    readonly Dictionary<object, int> _ordinals = new();
    // the draws of the last capture, reused by the next (Begin): the items are the capture's own
    // until the next Begin; and the "tag:n" keys, built once each
    readonly List<CapturedDraw> _spare = new();
    readonly Dictionary<(object, int), string> _keys = new();
    public int BoundsW, BoundsH, BoundsBottom;

    public void SetBounds(int w, int h, int? bottom = null)
    {
        BoundsW = w; BoundsH = h;
        BoundsBottom = bottom ?? BoundsH;
    }

    CapturedDraw Cut(CapturedDraw item)
    {
        if (!(BoundsW > 0 && BoundsH > 0)) return item;
        if (item.Frame != null)
        {
            var cut = SpriteBuild.ClipFrameToBounds(item.Frame, item.X, item.Y, item.FlipY, BoundsW, BoundsBottom);
            if (ReferenceEquals(cut, item.Frame)) return item;
            if (cut == null) item.Off = true; else item.Frame = cut;
        }
        else if (item.Mask != null)
        {
            var cut = SpriteBuild.ClipFrameToBounds(item.Mask, item.X, item.Y, item.FlipY, BoundsW, BoundsBottom);
            if (ReferenceEquals(cut, item.Mask)) return item;
            if (cut == null) item.Off = true; else item.Mask = cut;
        }
        return item;
    }

    public void Begin()
    {
        _spare.AddRange(Items);
        Items.Clear();
        Particles.Clear();
        Tag = null;
        _ordinals.Clear();
    }

    string? NextKey()
    {
        if (Tag == null) return null;
        _ordinals.TryGetValue(Tag, out int n);
        n++;
        _ordinals[Tag] = n;
        if (!_keys.TryGetValue((Tag, n), out var key)) _keys[(Tag, n)] = key = Tag + ":" + n;
        return key;
    }

    CapturedDraw Draw(Frame? frame, SpriteMask? mask, int x, int y, bool flipY, int layer, bool oneWay)
    {
        CapturedDraw d;
        if (_spare.Count > 0) { d = _spare[^1]; _spare.RemoveAt(_spare.Count - 1); }
        else d = new CapturedDraw();
        d.Frame = frame; d.Mask = mask; d.X = x; d.Y = y; d.FlipY = flipY; d.Layer = layer; d.OneWay = oneWay;
        d.Key = NextKey(); d.Off = false;
        return d;
    }

    public void DrawFrame(Frame frame, int x, int y) =>
        Items.Add(Cut(Draw(frame, null, x, y, false, 0, false)));

    // drawFrameFlags with a gadget's drawProperties (gadgetAsObject): noOverwrite -> -2,
    // onlyOverwrite -> 1, low -> -1, else 0
    public void DrawFrameFlags(Frame frame, int x, int y, bool isUpsideDown, bool noOverwrite, bool onlyOverwrite, bool low, bool oneWay) =>
        Items.Add(Cut(Draw(frame, null, x, y, isUpsideDown, noOverwrite ? -2 : onlyOverwrite ? 1 : low ? -1 : 0, oneWay)));

    public void DrawFrameFlags(Frame frame, GadgetObject obj) =>
        DrawFrameFlags(frame, obj.X, obj.Y, false, obj.Behind, obj.Decal, obj.Low, obj.OneWay);

    public void DrawMask(SpriteMask mask, int x, int y) =>
        Items.Add(Cut(Draw(null, mask, x, y, false, 0, false)));

    public void SetPixel(double x, double y, int r, int g, int b)
    {
        Particles.Add(x); Particles.Add(y); Particles.Add(r); Particles.Add(g); Particles.Add(b);
    }
}

// Where BillboardPool.sync puts each captured draw's mesh: hidden (`Off`), or its entry, its
// position (geometry origin = the sprite's top-left corner; z by layer plus a hair per item so
// overlapping sprites keep their order) and its y scale (-1 drawn upside down).
public readonly record struct BillboardPlacement(bool Visible, SpriteEntry? Entry, SpriteMaterial? Material, double X, double Y, double Z, double ScaleY);

public static class BillboardPool
{
    // `blend`: the objects (scenery) wear the colour blend, the lemmings do not; `flat`: clear
    // physics, the shape only in the one colour.
    public static List<BillboardPlacement> Place(IReadOnlyList<CapturedDraw> items, SpriteGeometryCache cache,
        Func<int, double> zFor, bool blend, bool flat)
    {
        var output = new List<BillboardPlacement>(items.Count);
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item.Off) { output.Add(new BillboardPlacement(false, null, null, 0, 0, 0, 1)); continue; }
            var entry = item.Frame != null ? cache.ForFrame(item.Frame) : cache.ForMask(item.Mask!);
            int offX = item.Frame != null ? item.Frame.OffsetX : item.Mask!.OffsetX;
            int offY = item.Frame != null ? item.Frame.OffsetY : item.Mask!.OffsetY;
            var material = flat && item.Frame != null ? cache.FlatMaterialFor(item.Frame)
                : (blend && item.Frame != null) ? cache.BlendedMaterialFor(item.Frame)
                : entry.Material;
            double bx = item.X + offX;
            double by = item.Y + offY + (item.FlipY ? entry.H : 0);
            output.Add(new BillboardPlacement(true, entry, material, bx, by, zFor(item.Layer) + i * 0.02, item.FlipY ? -1 : 1));
        }
        return output;
    }

    // applyInterpolation: a sprite's position between the last two sim ticks (alpha 0..1)
    public static double Lerp(double prev, double cur, double alpha) => prev + (cur - prev) * alpha;
}
