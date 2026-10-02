using Lemmix.Engine;
using Lemmix.Util;

namespace Lemmix.Render;

// web/3d/js/depth.js - depth compositing: a per-pixel depth-class buffer built alongside
// groundImage/groundMask by replaying the terrain piece list in composite order, the
// colour-keyed relief and the sculpt, the surface-blend zones and donors, the colour-blend
// flags. The final reconcile pass forces depth>0 <=> pixel solid.

public static class DepthClass
{
    public const byte EMPTY = 0, BACKDROP = 1, TERRAIN = 2, RELIEF = 3, OVERLAY = 4;

    // DepthClassByName (0 = not a class name)
    public static byte ByName(string? name) => name switch
    {
        "backdrop" => BACKDROP,
        "terrain" => TERRAIN,
        "relief" => RELIEF,
        "overlay" => OVERLAY,
        _ => 0,
    };
}

/// <summary>Z band and front-face shading of a class (DEPTH_BANDS).</summary>
public readonly record struct DepthBand(int Back, int Front, double FrontShade);

/// <summary>One placed terrain piece as depth.js reads it (groundData.lr.terrains[i]).</summary>
public sealed class GroundPiece
{
    public int X, Y, Id;
    public string? Key;
    public bool IsUpsideDown, NoOverwrite, OnlyOverwrite, IsErase;
}

/// <summary>One distinct drawn image (groundData.terraImages[id]): a palette-style frame, 0x80 = transparent.</summary>
public sealed class TerraImage
{
    public int Width, Height;
    public byte[] Frame = Array.Empty<byte>();
    public string? Name;
}

/// <summary>
/// What depth.js and the piece editor read of a level (window.__lem3dGroundData): the placed
/// pieces with an id per distinct drawn image. For a Lemmix level, app.js lemmixEngine.groundData.
/// </summary>
public sealed class GroundData
{
    public int LevelWidth, LevelHeight;
    public List<GroundPiece> Terrains = new();
    public List<TerraImage> TerraImages = new(); // indexed by id (ids are 0..n-1)

    public TerraImage? Image(int id) => id >= 0 && id < TerraImages.Count ? TerraImages[id] : null;

    // app.js:5874 lemmixEngine.groundData
    public static GroundData FromLevel(Level level)
    {
        var gd = new GroundData { LevelWidth = level.Width, LevelHeight = level.Height };
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var p in level.Pieces)
        {
            var d = p.Drawn;
            if (!ids.TryGetValue(d.VariantKey, out int id))
            {
                id = ids.Count;
                ids[d.VariantKey] = id;
                var frame = new byte[d.Width * d.Height];
                var data = d.Image.Data;
                for (int i = 0, a = 3; i < frame.Length; i++, a += 4) frame[i] = data[a] < 128 ? (byte)0x80 : (byte)0;
                gd.TerraImages.Add(new TerraImage { Width = d.Width, Height = d.Height, Frame = frame, Name = d.Key });
            }
            gd.Terrains.Add(new GroundPiece
            {
                X = p.X, Y = p.Y, Id = id, Key = d.Key,
                IsUpsideDown = false, NoOverwrite = p.NoOverwrite, OnlyOverwrite = false, IsErase = p.Erase,
            });
        }
        return gd;
    }
}

/// <summary>buildBlendMap's result: slot+1 per pixel (0 = do not blend), and the donors per slot.</summary>
public sealed class BlendMap
{
    public ushort[] Slot = Array.Empty<ushort>();
    public List<BlendDonor[]> Donors = new();
}

public readonly record struct BlendDonor(int Index, int R, int G, int B);

public static class Depth
{
    public static readonly DepthBand?[] DEPTH_BANDS =
    {
        null,
        new DepthBand(0, 3, 0.62),  // BACKDROP: recessed, dimmed
        new DepthBand(0, 16, 1.0),  // TERRAIN: the main slab
        new DepthBand(0, 22, 1.0),  // RELIEF: proud of the slab
        new DepthBand(0, 18, 1.0),  // OVERLAY: thin decal layer
    };

    // the bands as a plain array for the mesher (index 0 unused)
    internal static readonly int[] Front = { 0, 3, 16, 22, 18 };
    internal static readonly int[] Back = { 0, 0, 0, 0, 0 };
    internal static readonly double[] FrontShade = { 0, 0.62, 1.0, 1.0, 1.0 };

    public const int RELIEF_MAX = 4;
    public const int SCULPT_MAX = 24;
    public const int RELIEF_TOP = 24; // Math.max(RELIEF_MAX, SCULPT_MAX)
    public const int BLEND_PALETTE_MAX = 6;
    public const int BLEND_MERGE = 24;

    /// <summary>pieceKey: what a placed piece is tagged by (its name; a DOS id otherwise).</summary>
    public static string PieceKey(GroundPiece piece) => piece.Key ?? piece.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The key a piece id's tags live under: its image's name, else the id itself.</summary>
    static string KeyOfId(GroundData? gd, int id)
    {
        var img = gd?.Image(id);
        return img != null && img.Name != null ? img.Name : id.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>The largest id the tables cover: 254 or the highest image id.</summary>
    static int MaxId(GroundData? gd)
    {
        int maxId = 254;
        if (gd != null) maxId = Math.Max(maxId, gd.TerraImages.Count - 1);
        return maxId;
    }

    public static byte DepthClassForPiece(GroundPiece piece, DepthProfile? profile)
    {
        var cfg = profile?.Terrain;
        byte o = 0;
        if (cfg != null && cfg.ById.TryGetValue(PieceKey(piece), out var v) && v.IsString) o = DepthClass.ByName(v.Str);
        if (o != 0) return o;
        byte d = cfg != null && cfg.Default.IsString ? DepthClass.ByName(cfg.Default.Str) : (byte)0;
        return d != 0 ? d : DepthClass.TERRAIN;
    }

    static bool Usable(GroundData? gd, int w, int h) => gd != null && gd.LevelWidth == w && gd.LevelHeight == h;

    /// <summary>Piece id per pixel (stored as id+1; 0 = no piece), in the compositor's order.</summary>
    public static ushort[] BuildPieceMap(Level level, GroundData? groundData)
    {
        int W = level.Width, H = level.Height;
        var map = new ushort[W * H];
        if (!Usable(groundData, W, H)) return map;
        foreach (var piece in groundData!.Terrains)
        {
            var src = groundData.Image(piece.Id);
            if (src == null) continue;
            var pixBuf = src.Frame;
            int w = src.Width, h = src.Height;
            for (int y = 0; y < h; y++)
            {
                int outY = y + piece.Y;
                if (outY < 0 || outY >= H) continue;
                int sourceY = piece.IsUpsideDown ? (h - y - 1) : y;
                for (int x = 0; x < w; x++)
                {
                    if ((pixBuf[sourceY * w + x] & 0x80) != 0) continue; // transparent
                    int outX = x + piece.X;
                    if (outX < 0 || outX >= W) continue;
                    int idx = outY * W + outX;
                    if (piece.IsErase) { map[idx] = 0; continue; }
                    if (piece.NoOverwrite && map[idx] != 0) continue;
                    if (piece.OnlyOverwrite && map[idx] == 0) continue;
                    map[idx] = (ushort)((piece.Id + 1) & 0xffff);
                }
            }
        }
        return map;
    }

    /// <summary>embossModeFor: 0 off, 1 normal (lighter raised), 2 invert (darker raised).</summary>
    public static int EmbossModeFor(string pieceId, DepthProfile? profile)
    {
        var value = Lookup(profile?.Emboss, pieceId);
        if (value.IsFalse) return 0;
        if (value.IsString && value.Str == "invert") return 2;
        return 1;
    }

    public static bool EmbossEnabledFor(string pieceId, DepthProfile? profile) => EmbossModeFor(pieceId, profile) != 0;
    public static bool EmbossInvertedFor(string pieceId, DepthProfile? profile) => EmbossModeFor(pieceId, profile) == 2;

    /// <summary>The "3D object" tag: pieces opt in.</summary>
    public static bool SculptFor(string pieceId, DepthProfile? profile) => Lookup(profile?.Sculpt, pieceId).IsTrue;

    public static double SculptRadius(int width) => Math.Min(SCULPT_MAX, width / 2.0);

    /// <summary>Surface blend: pieces opt out.</summary>
    public static bool SurfaceBlendFor(string pieceId, DepthProfile? profile) => !Lookup(profile?.Blend, pieceId).IsFalse;

    /// <summary>Colour blend: pieces opt out.</summary>
    public static bool ColorBlendFor(string pieceId, DepthProfile? profile) => !Lookup(profile?.ColorBlend, pieceId).IsFalse;

    // `hasOwnProperty(byId, id) ? byId[id] : cfg.default`
    static ProfileValue Lookup(ProfileSection? cfg, string pieceId)
    {
        if (cfg == null) return ProfileValue.Undefined;
        return cfg.ById.TryGetValue(pieceId, out var v) ? v : cfg.Default;
    }

    /// <summary>Perceived brightness of a packed 0xRRGGBB colour.</summary>
    public static double BlendLuma(int rgb) => (((rgb >> 16) & 255) * 299 + ((rgb >> 8) & 255) * 587 + (rgb & 255) * 114) / 1000.0;

    /// <summary>Are two packed colours the same to within BLEND_MERGE.</summary>
    public static bool BlendNear(int a, int b) =>
        Math.Abs(((a >> 16) & 255) - ((b >> 16) & 255)) +
        Math.Abs(((a >> 8) & 255) - ((b >> 8) & 255)) +
        Math.Abs((a & 255) - (b & 255)) <= BLEND_MERGE;

    /// <summary>Per-pixel flag: 1 where the pixel's piece takes the colour blend.</summary>
    public static byte[] BuildColorBlendMap(Level level, ushort[]? pieceMap, DepthProfile? profile, bool enabled, GroundData? groundData)
    {
        int W = level.Width, H = level.Height;
        var map = new byte[W * H];
        if (!enabled || pieceMap == null || groundData == null) return map;
        int maxId = MaxId(groundData);
        var onById = new byte[maxId + 2];
        for (int id = 0; id <= maxId; id++)
            if (ColorBlendFor(KeyOfId(groundData, id), profile)) onById[id + 1] = 1;
        var mask = level.GroundMask.GroundMask;
        for (int i = 0; i < W * H; i++)
            if (mask[i] != 0 && onById[pieceMap[i]] != 0) map[i] = 1;
        return map;
    }

    // a palette's colours (lightest first, at most BLEND_PALETTE_MAX of 24 bits), packed as a key
    readonly record struct PaletteKey(int Count, long A, long B, long C);

    /// <summary>
    /// Which colours each pixel of a blend-tagged piece may use down its extrusion, and where in
    /// the level texture each can be sampled from (buildBlendMap). Each zone's palette is worked
    /// out as soon as its flood fill ends - the JS does it in a second pass over the zones in the
    /// same order, from nothing but the zone's own data, so the slots come out the same.
    /// </summary>
    public static BlendMap BuildBlendMap(Level level, ushort[]? pieceMap, DepthProfile? profile, GroundData? groundData)
    {
        int W = level.Width, H = level.Height, N = W * H;
        var slot = new ushort[N];
        var nothing = new BlendMap { Slot = slot };
        if (pieceMap == null || groundData == null) return nothing;

        int maxId = MaxId(groundData);
        var blendById = new byte[maxId + 2];
        for (int id = 0; id <= maxId; id++)
            if (SurfaceBlendFor(KeyOfId(groundData, id), profile)) blendById[id + 1] = 1;

        var img = level.GroundImage;
        var mask = level.GroundMask.GroundMask;
        int RgbAt(int i) { int o = i * 4; return (img[o] << 16) | (img[o + 1] << 8) | img[o + 2]; }
        bool Eligible(int i) => mask[i] != 0 && blendById[pieceMap[i]] != 0;

        var zoneOf = new int[N];
        Array.Fill(zoneOf, -1);
        var slotOfZone = new List<int>();
        var stack = new int[16];
        // the zone's border: colours in first-seen order, a count and the first pixel of each
        var borderIndex = new Dictionary<int, int>();
        var borderRgb = new List<int>(); var borderCount = new List<int>(); var borderAt = new List<int>();
        var order = new List<int>();
        var keptRgb = new int[BLEND_PALETTE_MAX]; var keptIdx = new int[BLEND_PALETTE_MAX]; var keptLuma = new double[BLEND_PALETTE_MAX];
        var donors = new List<BlendDonor[]>();
        var bySignature = new Dictionary<PaletteKey, int>();
        for (int s = 0; s < N; s++)
        {
            if (zoneOf[s] >= 0 || !Eligible(s)) continue;
            // --- the zone: a colour-tolerant flood fill, compared against the seed
            int zid = slotOfZone.Count;
            int pid = pieceMap[s];
            int seed = RgbAt(s);
            borderIndex.Clear(); borderRgb.Clear(); borderCount.Clear(); borderAt.Clear();
            zoneOf[s] = zid;
            int sp = 0;
            stack[sp++] = s;
            while (sp > 0)
            {
                int i = stack[--sp];
                int x = i % W, y = i / W;
                for (int d = 0; d < 4; d++)
                {
                    int nx = x + (d == 0 ? -1 : d == 1 ? 1 : 0);
                    int ny = y + (d == 2 ? -1 : d == 3 ? 1 : 0);
                    if (nx < 0 || nx >= W || ny < 0 || ny >= H) continue;
                    int j = ny * W + nx;
                    if (pieceMap[j] != pid || !Eligible(j)) continue;
                    int c = RgbAt(j);
                    if (BlendNear(c, seed))
                    {
                        if (zoneOf[j] < 0)
                        {
                            zoneOf[j] = zid;
                            if (sp == stack.Length) Array.Resize(ref stack, sp * 2);
                            stack[sp++] = j;
                        }
                        continue;
                    }
                    if (borderIndex.TryGetValue(c, out int bi)) borderCount[bi]++;
                    else { borderIndex[c] = borderRgb.Count; borderRgb.Add(c); borderCount.Add(1); borderAt.Add(j); }
                }
            }

            // --- its palette: its colour, then the touching ones by count (a stable sort)
            order.Clear();
            for (int k = 0; k < borderRgb.Count; k++) order.Add(k);
            for (int a = 1; a < order.Count; a++)
            {
                int v = order[a], b = a - 1;
                while (b >= 0 && borderCount[order[b]] < borderCount[v]) { order[b + 1] = order[b]; b--; }
                order[b + 1] = v;
            }
            int n = 0;
            keptRgb[n] = seed; keptIdx[n] = s; n++;
            foreach (int k in order)
            {
                if (n >= BLEND_PALETTE_MAX) break;
                int c = borderRgb[k];
                bool near = false;
                for (int q = 0; q < n; q++) if (BlendNear(keptRgb[q], c)) { near = true; break; }
                if (near) continue;
                keptRgb[n] = c; keptIdx[n] = borderAt[k]; n++;
            }
            if (n < 2) { slotOfZone.Add(0); continue; } // nothing to blend with
            // lightest first (a stable sort)
            for (int q = 0; q < n; q++) keptLuma[q] = BlendLuma(keptRgb[q]);
            for (int a = 1; a < n; a++)
            {
                int vr = keptRgb[a], vi = keptIdx[a]; double vl = keptLuma[a];
                int b = a - 1;
                while (b >= 0 && keptLuma[b] < vl) { keptRgb[b + 1] = keptRgb[b]; keptIdx[b + 1] = keptIdx[b]; keptLuma[b + 1] = keptLuma[b]; b--; }
                keptRgb[b + 1] = vr; keptIdx[b + 1] = vi; keptLuma[b + 1] = vl;
            }
            var key = new PaletteKey(n,
                ((long)keptRgb[0] << 24) | (uint)keptRgb[1],
                n > 2 ? ((long)keptRgb[2] << 24) | (n > 3 ? (uint)keptRgb[3] : 0) : 0,
                n > 4 ? ((long)keptRgb[4] << 24) | (n > 5 ? (uint)keptRgb[5] : 0) : 0);
            if (!bySignature.TryGetValue(key, out int sl))
            {
                var palette = new BlendDonor[n];
                for (int q = 0; q < n; q++)
                    palette[q] = new BlendDonor(keptIdx[q], (keptRgb[q] >> 16) & 255, (keptRgb[q] >> 8) & 255, keptRgb[q] & 255);
                donors.Add(palette);
                sl = donors.Count; // stored as slot+1
                bySignature[key] = sl;
            }
            slotOfZone.Add(sl);
        }
        if (donors.Count == 0) return nothing;

        for (int i = 0; i < N; i++)
        {
            int z = zoneOf[i];
            if (z >= 0) slot[i] = (ushort)slotOfZone[z];
        }
        return new BlendMap { Slot = slot, Donors = donors };
    }

    /// <summary>Per-pixel relief height: the colour-keyed grain and the sculpted bodies (buildReliefMap).</summary>
    public static byte[] BuildReliefMap(Level level, ushort[] pieceMap, DepthProfile? profile, bool enabled, GroundData? groundData)
    {
        int W = level.Width, H = level.Height;
        var relief = new byte[W * H];
        if (!enabled) return relief;

        int maxId = MaxId(groundData);
        var embossById = new byte[maxId + 2];
        var sculptById = new byte[maxId + 2];
        bool anySculpt = false;
        for (int id = 0; id <= maxId; id++)
        {
            string key = KeyOfId(groundData, id);
            int mode = EmbossModeFor(key, profile);
            embossById[id + 1] = (byte)(mode == 0 ? 0 : mode == 2 ? 2 : 1);
            if (groundData?.Image(id) != null && SculptFor(key, profile))
            {
                sculptById[id + 1] = 1;
                anySculpt = true;
            }
        }

        var img = level.GroundImage;
        var mask = level.GroundMask.GroundMask;
        double Luma(int i) { int o = i * 4; return (img[o] * 299 + img[o + 1] * 587 + img[o + 2] * 114) / 1000.0; }

        // --- the grain
        double lo = 255, hi = 0;
        for (int i = 0; i < W * H; i++)
        {
            int p = pieceMap[i];
            if (mask[i] == 0 || embossById[p] == 0 || sculptById[p] != 0) continue;
            double l = Luma(i);
            if (l < lo) lo = l;
            if (l > hi) hi = l;
        }
        if (hi > lo)
        {
            for (int i = 0; i < W * H; i++)
            {
                int p = pieceMap[i];
                int mode = embossById[p];
                if (mask[i] == 0 || mode == 0 || sculptById[p] != 0) continue;
                double t = (Luma(i) - lo) / (hi - lo);
                relief[i] = (byte)JsMath.Round((mode == 2 ? 1 - t : t) * RELIEF_MAX);
            }
        }

        if (anySculpt) SculptRelief(relief, W, H, pieceMap, mask, Luma, sculptById, groundData);
        return relief;
    }

    /// <summary>The sculpted pieces' heights, written into relief over their pixels (sculptRelief).</summary>
    static void SculptRelief(byte[] relief, int W, int H, ushort[] pieceMap, sbyte[] mask, Func<int, double> luma, byte[] sculptById, GroundData? groundData)
    {
        if (!Usable(groundData, W, H)) return;

        // --- the axis
        int ids = sculptById.Length;
        var dx = new double[ids];
        var dy = new double[ids];
        for (int i = 0; i < W * H; i++)
        {
            int p = pieceMap[i];
            if (mask[i] == 0 || sculptById[p] == 0) continue;
            double l = luma(i);
            int x = i % W;
            if (x + 1 < W && mask[i + 1] != 0 && pieceMap[i + 1] == p) dx[p] += Math.Abs(l - luma(i + 1));
            if (i + W < W * H && mask[i + W] != 0 && pieceMap[i + W] == p) dy[p] += Math.Abs(l - luma(i + W));
        }

        // --- the lathe
        foreach (var piece in groundData!.Terrains)
        {
            int p = piece.Id + 1;
            if (p >= ids || sculptById[p] == 0) continue;
            var src = groundData.Image(piece.Id);
            if (src == null) continue;
            var pixBuf = src.Frame;
            int w = src.Width, h = src.Height;
            bool Solid(int x, int y)
            {
                int sy = piece.IsUpsideDown ? (h - y - 1) : y;
                return (pixBuf[sy * w + x] & 0x80) == 0;
            }
            void Write(int x, int y, byte z)
            {
                int ox = x + piece.X, oy = y + piece.Y;
                if (ox < 0 || ox >= W || oy < 0 || oy >= H) return;
                int idx = oy * W + ox;
                if (mask[idx] != 0 && pieceMap[idx] == p) relief[idx] = z;
            }
            bool acrossX = dx[p] != dy[p] ? dx[p] > dy[p] : h >= w;
            int along = acrossX ? h : w, across = acrossX ? w : h;
            for (int a = 0; a < along; a++)
            {
                bool At(int b) => acrossX ? Solid(b, a) : Solid(a, b);
                for (int b0 = 0; b0 < across;)
                {
                    if (!At(b0)) { b0++; continue; }
                    int b1 = b0;
                    while (b1 + 1 < across && At(b1 + 1)) b1++;
                    int width = b1 - b0 + 1;
                    double r = width / 2.0, amp = SculptRadius(width);
                    double centre = b0 + r;
                    for (int b = b0; b <= b1; b++)
                    {
                        double u = (b + 0.5 - centre) / r;
                        byte z = (byte)JsMath.Round(amp * Math.Sqrt(Math.Max(0, 1 - u * u)));
                        if (acrossX) Write(b, a, z); else Write(a, b, z);
                    }
                    b0 = b1 + 1;
                }
            }
        }
    }

    /// <summary>The depth buffer of a loaded level, reconciled against the collision mask (buildDepthMap).</summary>
    public static byte[] BuildDepthMap(Level level, GroundData? groundData, DepthProfile? profile)
    {
        int W = level.Width, H = level.Height;
        var depth = new byte[W * H];
        if (Usable(groundData, W, H))
        {
            foreach (var piece in groundData!.Terrains)
            {
                var src = groundData.Image(piece.Id);
                if (src == null) continue;
                byte cls = DepthClassForPiece(piece, profile);
                var pixBuf = src.Frame;
                int w = src.Width, h = src.Height;
                for (int y = 0; y < h; y++)
                {
                    int outY = y + piece.Y;
                    if (outY < 0 || outY >= H) continue;
                    int sourceY = piece.IsUpsideDown ? (h - y - 1) : y;
                    for (int x = 0; x < w; x++)
                    {
                        if ((pixBuf[sourceY * w + x] & 0x80) != 0) continue; // transparent
                        int outX = x + piece.X;
                        if (outX < 0 || outX >= W) continue;
                        int idx = outY * W + outX;
                        if (piece.IsErase) depth[idx] = DepthClass.EMPTY;
                        else
                        {
                            if (piece.NoOverwrite && depth[idx] != DepthClass.EMPTY) continue;
                            if (piece.OnlyOverwrite && depth[idx] == DepthClass.EMPTY) continue;
                            depth[idx] = cls;
                        }
                    }
                }
            }
        }

        // authoritative reconcile against the collision mask
        var mask = level.GroundMask.GroundMask;
        for (int i = 0; i < W * H; i++)
            depth[i] = mask[i] != 0 ? (depth[i] != 0 ? depth[i] : DepthClass.TERRAIN) : DepthClass.EMPTY;
        return depth;
    }
}
