using System.Runtime.CompilerServices;
using System.Text.Json;
using Lemmix.Engine;
using Lemmix.Util;

namespace Lemmix.Render;

// web/3d/js/envgen.js: the environment's pictures - a floor, walls and a ceiling in rings round
// the board, drawn in the level's own pixel art. Pure pixels: ambient gradients dithered
// between the level's colours, the style's wallpaper, and a collage of the level's own terrain
// pieces from a generator seeded with the level's id. Method for method, colours as 0xRRGGBB
// ints, every picture a Bitmap (RGBA). Math.sin / exp / hypot go through V8Math, so the
// pictures are the page's bit for bit.
// Left out, as the page never reaches them for a Lemmix level: the classic engine's inputs
// (dosBitmap, a DOS ground palette, a DOS tileset's terraImages) and the decorations gathered
// from a level's own objects (ctx.lemmixObjects) - only a level's own context carries those, the
// page builds every picture from a gallery context (environment.js) which has none, and drawing
// one would throw in the JS (a Frame has no flipHorizontal / tinted).

// The room's sizes (roomFor): its rings, each with its pictures' sizes.
public sealed record PlaneSize(int K, int W, int H);

public sealed class RoomLayer
{
    public int I;
    public double RIn, ROut, Circ, D, RBand, FogBand, Fog, FogNear, Skyline, Swell;
    public bool Last;
    public required PlaneSize Floor, Ceiling, Wall;

    public RoomLayer With(double skyline, double swell) => new()
    {
        I = I, RIn = RIn, ROut = ROut, Circ = Circ, D = D, RBand = RBand, FogBand = FogBand, Fog = Fog, FogNear = FogNear,
        Skyline = skyline, Swell = swell, Last = Last, Floor = Floor, Ceiling = Ceiling, Wall = Wall,
    };
}

public sealed record RoomSphere(int R, int W, int H);

public sealed class Room
{
    public double W, H, P, FloorDrop, WallPx, Reach;
    public (double X, double Z) Center;
    public required List<RoomLayer> Layers;
    public required RoomSphere Sphere;
}

// The profile's environment hints (all optional): a palette, pieces left out, the pieces each
// plane is built from, the collage mode, how a background is used, the gallery's wallpaper.
public sealed class EnvHints
{
    public List<string>? PaletteMaterial;
    public string? PaletteBg, PaletteFog;
    public bool HasPalette;
    public List<string>? Exclude, Floor, Ceiling, Wall;
    public string? Mode;
    public Dictionary<string, string>? Backgrounds;
    public string? Wallpaper;
}

// A loaded profile as far as the 3D layer's data reads it (profile-store.js): the class each
// terrain piece is tagged with, the objects' shapes (portals.js), the environment hints.
public sealed class EnvProfile
{
    public Dictionary<string, string> TerrainById = new(StringComparer.Ordinal);
    public Dictionary<int, PortalProfileEntry>? ObjectsById;
    public EnvHints? Environment;

    static readonly string[] Classes = { "backdrop", "terrain", "relief", "overlay" };

    // ProfileStore.classOf: the class a piece is tagged with, null when left to auto
    public string? ClassOf(string key) =>
        TerrainById.TryGetValue(key, out var c) && Array.IndexOf(Classes, c) >= 0 ? c : null;

    public static EnvProfile Parse(string json)
    {
        var p = new EnvProfile();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("terrain", out var t) && t.ValueKind == JsonValueKind.Object && t.TryGetProperty("byId", out var byId) && byId.ValueKind == JsonValueKind.Object)
            foreach (var e in byId.EnumerateObject()) if (e.Value.ValueKind == JsonValueKind.String) p.TerrainById[e.Name] = e.Value.GetString()!;
        if (root.TryGetProperty("objects", out var o) && o.ValueKind == JsonValueKind.Object && o.TryGetProperty("byId", out var ob) && ob.ValueKind == JsonValueKind.Object)
        {
            p.ObjectsById = new();
            foreach (var e in ob.EnumerateObject())
            {
                if (!int.TryParse(e.Name, out int id) || e.Value.ValueKind != JsonValueKind.Object) continue;
                string? shape = e.Value.TryGetProperty("shape", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
                double? depth = e.Value.TryGetProperty("depth", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : null;
                p.ObjectsById[id] = new PortalProfileEntry(shape, depth);
            }
        }
        if (root.TryGetProperty("environment", out var env) && env.ValueKind == JsonValueKind.Object) p.Environment = ParseHints(env);
        return p;
    }

    static List<string>? Strings(JsonElement e, string name) =>
        e.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString()! : x.ToString()).ToList() : null;

    static EnvHints ParseHints(JsonElement e)
    {
        var h = new EnvHints();
        if (e.TryGetProperty("palette", out var pal) && pal.ValueKind == JsonValueKind.Object)
        {
            h.HasPalette = true;
            h.PaletteMaterial = Strings(pal, "material");
            h.PaletteBg = pal.TryGetProperty("bg", out var bg) ? bg.ToString() : null;
            h.PaletteFog = pal.TryGetProperty("fog", out var fog) ? fog.ToString() : null;
        }
        h.Exclude = Strings(e, "exclude"); h.Floor = Strings(e, "floor"); h.Ceiling = Strings(e, "ceiling"); h.Wall = Strings(e, "wall");
        h.Mode = e.TryGetProperty("mode", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
        if (e.TryGetProperty("backgrounds", out var b) && b.ValueKind == JsonValueKind.Object)
        {
            h.Backgrounds = new(StringComparer.Ordinal);
            foreach (var x in b.EnumerateObject()) if (x.Value.ValueKind == JsonValueKind.String) h.Backgrounds[x.Name] = x.Value.GetString()!;
        }
        h.Wallpaper = e.TryGetProperty("wallpaper", out var w) && w.ValueKind == JsonValueKind.String ? w.GetString() : null;
        return h;
    }

    // ProfileStore.merge: byId maps unioned (a later file wins), the environment hints a later
    // file's per key
    public static EnvProfile Merge(IEnumerable<EnvProfile> profiles)
    {
        var output = new EnvProfile();
        foreach (var p in profiles)
        {
            foreach (var kv in p.TerrainById) output.TerrainById[kv.Key] = kv.Value;
            if (p.ObjectsById != null)
            {
                output.ObjectsById ??= new();
                foreach (var kv in p.ObjectsById) output.ObjectsById[kv.Key] = kv.Value;
            }
            if (p.Environment != null)
            {
                var o = output.Environment ??= new EnvHints();
                var e = p.Environment;
                if (e.HasPalette) { o.HasPalette = true; o.PaletteMaterial = e.PaletteMaterial; o.PaletteBg = e.PaletteBg; o.PaletteFog = e.PaletteFog; }
                if (e.Exclude != null) o.Exclude = e.Exclude;
                if (e.Floor != null) o.Floor = e.Floor;
                if (e.Ceiling != null) o.Ceiling = e.Ceiling;
                if (e.Wall != null) o.Wall = e.Wall;
                if (e.Mode != null) o.Mode = e.Mode;
                if (e.Backgrounds != null) o.Backgrounds = e.Backgrounds;
                if (e.Wallpaper != null) o.Wallpaper = e.Wallpaper;
            }
        }
        return output;
    }

    // ProfileStore.urlsForGroundData for a Lemmix level: one file per style its pieces come from,
    // in the order the pieces' distinct images are first met (3d/profiles/nx-<style>.json)
    public static List<string> UrlsFor(Level level)
    {
        var urls = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in level.Pieces)
        {
            if (!seen.Add(p.Drawn.VariantKey)) continue;
            string key = p.Drawn.Key;
            int colon = key.IndexOf(':');
            if (colon <= 0) continue;
            string style = key[..colon];
            if (!System.Text.RegularExpressions.Regex.IsMatch("nx:" + style, "^nx:[a-z0-9_]+$")) continue;
            string url = "3d/profiles/nx-" + style + ".json";
            if (!urls.Contains(url)) urls.Add(url);
        }
        return urls;
    }

    // The merged view the page loads for a level (ProfileFiles.loadAll): `read` returns a file's
    // text, null when there is none (an empty profile).
    public static EnvProfile ForLevel(Level level, Func<string, string?> read) =>
        Merge(UrlsFor(level).Select(u => read(u) is string text ? Parse(text) : new EnvProfile()));

    // A style's own profile, with no level (native: the lobby's room is a style's gallery)
    public static EnvProfile ForStyle(string style, Func<string, string?> read) =>
        read("3d/profiles/nx-" + style + ".json") is string text ? Parse(text) : new EnvProfile();
}

// A terrain piece the context is drawn from: what a level piece (or a gallery's style piece) is.
public sealed record EnvPieceSource(string Key, Bitmap Image, bool Steel, bool Erase);

// The data envgen reads of a level or a gallery (environment.js's ctx / _galleryContext).
public sealed class EnvContext
{
    public string Engine = "lemmix";
    public string? LevelId;
    public bool Gallery;
    public int Width, Height;
    public string? ThemeName;
    public Theme? Theme;
    public byte[]? GroundImage;
    public sbyte[]? GroundMask;
    public IReadOnlyList<IReadOnlyList<(int R, int G, int B)>>? Donors;
    public EnvProfile? Profile;
    public List<EnvPieceSource>? LemmixPieces;
    public Bitmap? BackgroundImage;
    public string? BackgroundName;
    public string? Dir;
}

public sealed class EnvPalette
{
    public required List<int> Material;
    public int Bg, Dark, Light, Accent, Fog;
    public int? Avg;
    public required string Source;
}

public sealed record EnvBox(int X, int Y, int W, int H);

public sealed class EnvPiece
{
    public required string Key;
    public required Bitmap Image;
    public int W, H, Area, Opaque, Count;
    public double Fill;
    public required EnvBox BBox;
    public bool FlatTop, Steel, Excluded;
    public string? Cls;
    internal readonly Dictionary<string, Bitmap> Dressed = new(StringComparer.Ordinal);
}

public sealed class EnvCollected
{
    public required List<EnvPiece> Pieces;
    public required List<EnvPiece> Decor;
}

public sealed class EnvBuckets
{
    public required List<EnvPiece> Floor, Ceiling, Wall, Decor, Ground, Usable;
}

public sealed record EnvWallpaper(Bitmap Image, string Key, string Kind);

public sealed record EnvProp(int Ring, double U, double R, double W, double H, Bitmap Bitmap, int Depth, double Yaw);

public sealed record EnvBackdrop(Bitmap Bitmap, int K);

public sealed class EnvBuildOptions
{
    public required Room Room;
    public EnvPalette? Palette;
    public bool Full, SmoothFog;
    public EnvWallpaper? Wallpaper;
    public int? Fog;
    public EnvCollected? Pieces;
}

public sealed class EnvBuildResult
{
    public required EnvPalette Palette;
    public string? Mode;
    public int Fog;
    public required Dictionary<string, Bitmap> Planes;
    public EnvBackdrop? Backdrop;
    public List<EnvProp>? Props;
    public EnvCollected? Pieces;
}

public sealed record GradientStop(double T, int Rgb);

public static class EnvGen
{
    // ------------------------------------------------------------ the room
    public static class ROOM
    {
        public const double EYE_M = 0.9, CEIL_M = 2.6, FLOOR_DROP_M = 0.5;
        public static readonly double[] RINGS = { 2.8, 4.5, 8.0 };
        public const double SPHERE_M = 17.0;
        public const int NOMINAL_W = 1600, NOMINAL_H = 160;
        public const double CLEAR_M = 0.5, FOG_M = 7.0;
        public static readonly double[] SKYLINE = { 0.38, 0.6, 0.8, 0.95 };
        public static readonly double[] SWELL = { 0.1, 0.18, 0.2, 0.2 };
        public static readonly int[] PROPS = { 6, 10, 14, 16 };
        public static readonly double[][] PROP_M = { new[] { 0.5, 1.1 }, new[] { 0.9, 2.0 }, new[] { 1.6, 3.2 }, new[] { 2.4, 5.0 } };
        public const double BOWL_DENSE = 0.35, BOWL_THIN = 0.72, BOWL_DEPTH_M = 2.0, BOWL_DOME_M = 0.8;
        public const double BAND_IN = 0.18, BAND_SKY = 0.65, THICK = 0.12;
        public const int TEX_FIRST = 4096, TEX_NEAR = 2048, TEX_FAR = 2048, TEX_WALL_ROWS = 1024, TEX_BAND_ROWS = 1024;
        public const double WALL_K = 1.25;
    }

    static double FogAt(double d) => 1 - V8Math.Exp(-d / ROOM.FOG_M);

    // roomFor: the room's sizes in board pixels for a level W x H, round `center` (the player's
    // place; by default EYE_M in front of the board's middle)
    public static Room RoomFor(double W, double H, double pxPerMetre, (double X, double Z)? center = null)
    {
        double P = pxPerMetre;
        double floorDrop = JsMath.Round(ROOM.FLOOR_DROP_M * P);
        double wallPx = JsMath.Round(ROOM.CEIL_M * P) + floorDrop;
        var c = center ?? (W / 2, 16 + ROOM.EYE_M * P);
        double reach = Math.Max(Math.Max(Math.Max(
            V8Math.Hypot(c.X, c.Z), V8Math.Hypot(W - c.X, c.Z)),
            V8Math.Hypot(c.X, c.Z - 16)), V8Math.Hypot(W - c.X, c.Z - 16));
        var radii = new List<double>();
        for (int i = 0; i < ROOM.RINGS.Length; i++)
        {
            double r = ROOM.RINGS[i] * P;
            if (i == 0) r = Math.Max(r, reach + ROOM.CLEAR_M * P);
            else r = Math.Max(r, radii[i - 1] * 1.6);
            radii.Add(JsMath.Round(r));
        }
        var layers = new List<RoomLayer>();
        for (int i = 0; i < radii.Count; i++)
        {
            double rOut = radii[i];
            double rIn = i == 0 ? 0 : radii[i - 1];
            double circ = JsMath.Round(2 * Math.PI * rOut);
            int texW = i == 0 ? ROOM.TEX_FIRST : i == 1 ? ROOM.TEX_NEAR : ROOM.TEX_FAR;
            double d = rOut / P;
            int kBand = (int)Math.Max(1, Math.Ceiling(circ / texW));
            int kWall = (int)Math.Max(1, Math.Ceiling(circ / texW * ROOM.WALL_K));
            double rBand = rOut - ROOM.BAND_IN * (rOut - rIn);
            var band = new PlaneSize(kBand, (int)Math.Ceiling(circ / kBand), (int)Math.Min(ROOM.TEX_BAND_ROWS, Math.Ceiling((rOut - rIn) / kBand)));
            layers.Add(new RoomLayer
            {
                I = i, RIn = rIn, ROut = rOut, Circ = circ, D = d, RBand = rBand, FogBand = FogAt(rBand / P),
                Fog = FogAt(d), FogNear = i == 0 ? 0 : FogAt(rIn / P),
                Skyline = ROOM.SKYLINE[Math.Min(i, ROOM.SKYLINE.Length - 1)],
                Swell = ROOM.SWELL[Math.Min(i, ROOM.SWELL.Length - 1)],
                Last = false,
                Floor = band, Ceiling = band,
                Wall = new PlaneSize(kWall, (int)Math.Ceiling(circ / kWall), (int)Math.Min(ROOM.TEX_WALL_ROWS, Math.Ceiling(wallPx / kWall))),
            });
        }
        return new Room
        {
            W = W, H = H, P = P, FloorDrop = floorDrop, WallPx = wallPx, Center = c, Reach = reach, Layers = layers,
            Sphere = new RoomSphere((int)JsMath.Round(ROOM.SPHERE_M * P), 1024, 512),
        };
    }

    // canonicalRoom: the room every gallery's pictures are drawn for (a classic level's)
    public static Room CanonicalRoom(double pxPerMetre) => RoomFor(ROOM.NOMINAL_W, ROOM.NOMINAL_H, pxPerMetre);

    // sheetOf: the pieces side by side on a transparent sheet, a picture to read a palette from
    public static Bitmap SheetOf(IEnumerable<Bitmap?> images)
    {
        var list = images.Where(i => i != null && i.Width != 0 && i.Height != 0).Select(i => i!).ToList();
        if (list.Count == 0) return new Bitmap(1, 1);
        int cols = (int)Math.Ceiling(Math.Sqrt(list.Count));
        int cw = list.Max(i => i.Width), ch = list.Max(i => i.Height);
        int rows = (int)Math.Ceiling(list.Count / (double)cols);
        var sheet = new Bitmap(cols * cw, rows * ch);
        for (int i = 0; i < list.Count; i++)
            Pixels.Blit(sheet, (i % cols) * cw, (i / cols) * ch, list[i], 0, 0, list[i].Width, list[i].Height, Pixels.CombineGadget);
        return sheet;
    }

    // planeNames: sky, bowl0, floor0, wall0, band0, ceiling0, floor1, ..., backdrop
    public static List<string> PlaneNames(Room room)
    {
        var output = new List<string> { "sky", "bowl0" };
        foreach (var l in room.Layers) { output.Add("floor" + l.I); output.Add("wall" + l.I); output.Add("band" + l.I); output.Add("ceiling" + l.I); }
        output.Add("backdrop");
        return output;
    }

    // bandToWorld: a band picture's pixel (u across, v down: rim to inner edge) round the centre
    public static (double X, double Z, double R, double Th) BandToWorld(Room room, RoomLayer layer, double u, double v, double w, double h)
    {
        double r = layer.ROut - (layer.ROut - layer.RIn) * (v / Math.Max(1, h));
        double th = (u / w) * Math.PI * 2;
        return (room.Center.X + r * V8Math.Sin(th), room.Center.Z + r * V8Math.Cos(th), r, th);
    }

    // ------------------------------------------------------------- colours
    static int Clamp255(double v) => JsMath.ToInt32(v < 0 ? 0 : v > 255 ? 255 : v);
    public static int RgbOf(double r, double g, double b) => (Clamp255(r) << 16) | (Clamp255(g) << 8) | Clamp255(b);
    static int R(int c) => (c >> 16) & 255;
    static int G(int c) => (c >> 8) & 255;
    static int B(int c) => c & 255;
    public static int Scale(int c, double f) => RgbOf(R(c) * f, G(c) * f, B(c) * f);
    public static int Mix(int a, int b, double t) => RgbOf(R(a) + (R(b) - R(a)) * t, G(a) + (G(b) - G(a)) * t, B(a) + (B(b) - B(a)) * t);
    static int Dist2(int a, int b)
    {
        int dr = R(a) - R(b), dg = G(a) - G(b), db = B(a) - B(b);
        return dr * dr * 2 + dg * dg * 4 + db * db * 3;
    }
    // depth.js blendLuma / blendNear (BLEND_MERGE 24)
    public static double Luma(int rgb) => (((rgb >> 16) & 255) * 299 + ((rgb >> 8) & 255) * 587 + (rgb & 255) * 114) / 1000.0;
    static bool BlendNear(int a, int b) =>
        Math.Abs(((a >> 16) & 255) - ((b >> 16) & 255)) + Math.Abs(((a >> 8) & 255) - ((b >> 8) & 255)) + Math.Abs((a & 255) - (b & 255)) <= 24;

    static int? ParseHex(string? s)
    {
        var m = System.Text.RegularExpressions.Regex.Match(JsString.Trim(s ?? ""), "^#?([0-9a-fA-F]{6})$");
        return m.Success ? Convert.ToInt32(m.Groups[1].Value, 16) : null;
    }

    // distinct: the colours of a list that are BLEND_MERGE apart, in order
    static List<int> Distinct(IEnumerable<int> list)
    {
        var output = new List<int>();
        foreach (int c in list) if (!output.Any(o => BlendNear(o, c))) output.Add(c);
        return output;
    }

    // derivePalette: the level's colours (lightest first), its background, dark and light ends,
    // accent, fog, and where they came from
    public static EnvPalette DerivePalette(EnvContext ctx)
    {
        var env = ctx.Profile?.Environment;
        var theme = ctx.Theme?.Colors;
        var material = new List<int>();
        string source = "fallback";
        if (env != null && env.HasPalette && env.PaletteMaterial != null)
        {
            material = env.PaletteMaterial.Select(ParseHex).Where(c => c != null).Select(c => c!.Value).ToList();
            if (material.Count > 0) source = "profile";
        }
        if (material.Count < 3 && ctx.Donors != null && ctx.Donors.Count > 0)
        {
            var all = new List<int>();
            foreach (var slot in ctx.Donors) foreach (var d in slot) all.Add(RgbOf(d.R, d.G, d.B));
            material = Distinct(material.Concat(all));
            if (material.Count >= 3) source = "donors";
        }
        if (material.Count < 3 && ctx.GroundImage != null && ctx.Width != 0 && ctx.Height != 0)
        {
            material = Distinct(material.Concat(Histogram(ctx)));
            if (material.Count >= 3) source = "histogram";
        }
        if (material.Count < 3) material = Distinct(material.Concat(new[] { 0x3a4658, 0x2a3140, 0x1c2230 }));
        material = material.Where(c => Luma(c) > 6).Take(12).ToList();
        if (material.Count == 0) material = new List<int> { 0x3a4658, 0x2a3140, 0x1c2230 };
        material = material.OrderByDescending(Luma).ToList(); // stable, as Array.prototype.sort

        int light = material[0];
        int dark = material.AsEnumerable().Reverse().Cast<int?>().FirstOrDefault(c => Luma(c!.Value) >= 10) ?? material[^1];
        int themeBg = (env != null && env.HasPalette ? ParseHex(env.PaletteBg) : null)
            ?? (theme != null && theme.TryGetValue("BACKGROUND", out int tb) ? tb : 0);
        int bg = Luma(themeBg) > 4 ? themeBg : Scale(dark, 0.45);
        int accent = theme != null && theme.TryGetValue("MASK", out int tm) ? tm : theme != null && theme.TryGetValue("MINIMAP", out int tmm) ? tmm : dark;
        int? avg = ctx.GroundImage != null && ctx.Width != 0 && ctx.Height != 0 ? MeanColor(ctx.GroundImage) : null;
        int fog = (env != null && env.HasPalette ? ParseHex(env.PaletteFog) : null)
            ?? (avg != null && Luma(avg.Value) > 6 ? Scale(Mix(avg.Value, bg, 0.2), 0.8) : Mix(Scale(bg, 0.8), Mix(dark, light, 0.5), 0.55));
        return new EnvPalette { Material = material, Bg = bg, Dark = dark, Light = light, Accent = accent, Fog = fog, Avg = avg, Source = source };
    }

    // histogram: the most common colours of the level's solid pixels, 5 bits a channel
    static List<int> Histogram(EnvContext ctx)
    {
        var img = ctx.GroundImage!;
        var mask = ctx.GroundMask;
        int w = ctx.Width, h = ctx.Height;
        var bins = new Dictionary<int, int>();
        var order = new List<int>();
        int step = (int)Math.Max(1, Math.Floor(Math.Sqrt((double)w * h / 200000)));
        for (int y = 0; y < h; y += step)
        {
            for (int x = 0; x < w; x += step)
            {
                int i = y * w + x, p = i * 4;
                if (mask != null ? mask[i] == 0 : img[p + 3] < Pixels.AlphaCutoff) continue;
                int key = ((img[p] >> 3) << 10) | ((img[p + 1] >> 3) << 5) | (img[p + 2] >> 3);
                if (bins.TryGetValue(key, out int n)) bins[key] = n + 1; else { bins[key] = 1; order.Add(key); }
            }
        }
        return order.OrderByDescending(k => bins[k]).Take(8)
            .Select(k => RgbOf(((k >> 10) & 31) << 3, ((k >> 5) & 31) << 3, (k & 31) << 3)).ToList();
    }

    // ----------------------------------------------------------- gradients
    static readonly int[][] BAYER =
    {
        new[] { 0, 32, 8, 40, 2, 34, 10, 42 }, new[] { 48, 16, 56, 24, 50, 18, 58, 26 },
        new[] { 12, 44, 4, 36, 14, 46, 6, 38 }, new[] { 60, 28, 52, 20, 62, 30, 54, 22 },
        new[] { 3, 35, 11, 43, 1, 33, 9, 41 }, new[] { 51, 19, 59, 27, 49, 17, 57, 25 },
        new[] { 15, 47, 7, 39, 13, 45, 5, 37 }, new[] { 63, 31, 55, 23, 61, 29, 53, 21 },
    };

    static double Smoothstep(double a, double b, double x)
    {
        double t = Math.Min(1, Math.Max(0, (x - a) / (b - a)));
        return t * t * (3 - 2 * t);
    }

    // stopColor: the colour a gradient wants at t, between its stops
    static int StopColor(IReadOnlyList<GradientStop> stops, double t)
    {
        if (t <= stops[0].T) return stops[0].Rgb;
        for (int i = 1; i < stops.Count; i++)
        {
            if (t <= stops[i].T)
            {
                var a = stops[i - 1]; var b = stops[i];
                return Mix(a.Rgb, b.Rgb, (t - a.T) / Math.Max(1e-6, b.T - a.T));
            }
        }
        return stops[^1].Rgb;
    }

    static uint Word(int c) => 0xff000000u | ((uint)B(c) << 16) | ((uint)G(c) << 8) | (uint)R(c);

    // paintGradient: a gradient quantised to the palette with an ordered dither
    public static Bitmap PaintGradient(Bitmap bmp, IReadOnlyList<GradientStop> stops, string axis, IReadOnlyList<int>? palette, int? dark, double vignette = 0, int cell = 1)
    {
        int w = bmp.Width, h = bmp.Height;
        var d = bmp.Data;
        var pal = QuantPalette(palette ?? Array.Empty<int>(), dark);
        double vig = vignette;
        cell = Math.Max(1, cell);
        bool alongV = axis != "u";
        var cache = new Dictionary<int, (int A, int B, double Frac)>();
        (int A, int B, double Frac) Pick(int c)
        {
            if (!cache.TryGetValue(c, out var q)) { q = NearestPair(pal, c); cache[c] = q; }
            return q;
        }
        if (alongV && vig == 0)
        {
            var words = bmp.Words();
            var pattern = new uint[8];
            for (int y = 0; y < h; y++)
            {
                double v = h > 1 ? (double)y / (h - 1) : 0;
                var q = Pick(StopColor(stops, v));
                var row = BAYER[(y / cell) & 7];
                for (int i = 0; i < 8; i++)
                {
                    int c = q.Frac > row[i] / 64.0 ? q.B : q.A;
                    pattern[i] = Word(c);
                }
                int bas = y * w;
                for (int x = 0; x < w; x++) words[bas + x] = pattern[(x / cell) & 7];
            }
            return bmp;
        }
        for (int y = 0; y < h; y++)
        {
            double v = h > 1 ? (double)y / (h - 1) : 0;
            for (int x = 0; x < w; x++)
            {
                double u = w > 1 ? (double)x / (w - 1) : 0;
                int c = StopColor(stops, alongV ? v : u);
                if (vig != 0)
                {
                    double e = 2 * Math.Max(Math.Abs(u - 0.5), Math.Abs(v - 0.5));
                    c = Scale(c, 1 - vig * Smoothstep(0.55, 1, e));
                }
                var q = Pick(c);
                double th = BAYER[(y / cell) & 7][(x / cell) & 7] / 64.0;
                int output = q.Frac > th ? q.B : q.A;
                int p = (y * w + x) * 4;
                d[p] = (byte)R(output); d[p + 1] = (byte)G(output); d[p + 2] = (byte)B(output); d[p + 3] = 255;
            }
        }
        return bmp;
    }

    // paintSmooth: a gradient painted as it is, no palette, no dither (the fog)
    public static Bitmap PaintSmooth(Bitmap bmp, IReadOnlyList<GradientStop> stops)
    {
        int w = bmp.Width, h = bmp.Height;
        var words = bmp.Words();
        for (int y = 0; y < h; y++)
        {
            int c = StopColor(stops, h > 1 ? (double)y / (h - 1) : 0);
            words.Slice(y * w, w).Fill(Word(c));
        }
        return bmp;
    }

    // quantPalette: the level's colours and darker rungs of its dark one, down to black
    public static List<int> QuantPalette(IReadOnlyList<int> material, int? dark)
    {
        int bas = dark ?? (material.Count > 0 && material[^1] != 0 ? material[^1] : 0x202020);
        return Distinct(material.Concat(new[] { Scale(bas, 0.66), Scale(bas, 0.4), Scale(bas, 0.2), 0x000000 }));
    }

    static (int A, int B, double Frac) NearestPair(List<int> pal, int c)
    {
        int a = pal[0], b = pal[0];
        double da = double.PositiveInfinity, db = double.PositiveInfinity;
        foreach (int p in pal)
        {
            double dd = Dist2(p, c);
            if (dd < da) { b = a; db = da; a = p; da = dd; }
            else if (dd < db) { b = p; db = dd; }
        }
        double frac = da + db > 0 ? da / (da + db) : 0;
        return (a, b, frac);
    }

    // darken: colours times f over a rectangle, an optional per-pixel weight of how much applies
    public static void Darken(Bitmap bmp, double f, double x0, double y0, double x1, double y1, Func<int, int, double>? weight)
    {
        int w = bmp.Width;
        var d = bmp.Data;
        int ix0 = Math.Max(0, JsMath.ToInt32(x0)), iy0 = Math.Max(0, JsMath.ToInt32(y0));
        int ix1 = Math.Min(w, JsMath.ToInt32(x1)), iy1 = Math.Min(bmp.Height, JsMath.ToInt32(y1));
        for (int y = iy0; y < iy1; y++)
        {
            for (int x = ix0; x < ix1; x++)
            {
                double k = weight != null ? 1 - (1 - f) * weight(x, y) : f;
                int p = (y * w + x) * 4;
                d[p] = JsMath.ClampU8(JsMath.ToInt32(d[p] * k));
                d[p + 1] = JsMath.ClampU8(JsMath.ToInt32(d[p + 1] * k));
                d[p + 2] = JsMath.ClampU8(JsMath.ToInt32(d[p + 2] * k));
            }
        }
    }

    // -------------------------------------------------------- backgrounds
    public static readonly HashSet<string> PROPS = new(StringComparer.Ordinal) { "orig_dirt:nessy", "davidz_fortune_ex:object_20", "davidz_fortune_ex:object_21" };

    // opaqueFraction: the fraction of a bitmap's pixels opaque enough to count
    public static double OpaqueFraction(Bitmap bmp)
    {
        var d = bmp.Data;
        int n = 0;
        for (int p = 3; p < d.Length; p += 4) if (d[p] >= Pixels.AlphaCutoff) n++;
        return (double)n / (bmp.Width * bmp.Height);
    }

    // classifyBackground: a style background as a wallpaper (tiled) or a prop (placed once)
    public static string ClassifyBackground(Bitmap bmp, string? key, EnvProfile? profile)
    {
        var env = profile?.Environment;
        string k = key ?? "";
        string name = k.Split(':')[^1];
        if (env?.Backgrounds != null && (env.Backgrounds.TryGetValue(k, out var byKey) || env.Backgrounds.TryGetValue(name, out byKey)) && !string.IsNullOrEmpty(byKey))
            return byKey;
        if (PROPS.Contains(k)) return "prop";
        double opaque = OpaqueFraction(bmp);
        if (opaque < 0.85) return "prop";
        if (bmp.Width >= 4 * bmp.Height && bmp.Width >= 320 && opaque < 0.97) return "prop";
        return "wallpaper";
    }

    // --------------------------------------------------------------- pieces
    // measure: a piece's opaque box, how full it is, whether it has a flat top
    public static (EnvBox BBox, int Opaque, double Fill, bool FlatTop)? Measure(Bitmap img)
    {
        int w = img.Width, h = img.Height;
        var d = img.Data;
        int x0 = w, y0 = h, x1 = -1, y1 = -1, opaque = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                if (d[(y * w + x) * 4 + 3] < Pixels.AlphaCutoff) continue;
                opaque++;
                if (x < x0) x0 = x; if (x > x1) x1 = x;
                if (y < y0) y0 = y; if (y > y1) y1 = y;
            }
        if (x1 < 0) return null;
        var bbox = new EnvBox(x0, y0, x1 - x0 + 1, y1 - y0 + 1);
        int top = 0;
        for (int x = x0; x <= x1; x++) if (d[(y0 * w + x) * 4 + 3] >= Pixels.AlphaCutoff) top++;
        return (bbox, opaque, (double)opaque / (bbox.W * bbox.H), (double)top / bbox.W >= 0.7);
    }

    // collectPieces: one entry per piece key with its measurements, class and placements
    public static EnvCollected CollectPieces(EnvContext ctx)
    {
        var env = ctx.Profile?.Environment;
        var exclude = new HashSet<string>(env?.Exclude ?? new List<string>(), StringComparer.Ordinal);
        var byKey = new Dictionary<string, (Bitmap Image, bool Steel, int Count, List<(Bitmap Img, int N)> Variants)>(StringComparer.Ordinal);
        var keys = new List<string>();
        if (ctx.LemmixPieces != null)
        {
            foreach (var p in ctx.LemmixPieces)
            {
                if (p.Erase) continue;
                var image = p.Image;
                if (image == null || image.Width == 0 || image.Height == 0) continue;
                if (!byKey.TryGetValue(p.Key, out var e)) { e = (image, p.Steel, 0, new List<(Bitmap, int)>()); keys.Add(p.Key); }
                e.Count++;
                int vi = e.Variants.FindIndex(v => ReferenceEquals(v.Img, image));
                if (vi >= 0) e.Variants[vi] = (image, e.Variants[vi].N + 1); else e.Variants.Add((image, 1));
                byKey[p.Key] = e;
            }
        }
        var pieces = new List<EnvPiece>();
        foreach (string key in keys)
        {
            var e = byKey[key];
            var image = e.Image; int best = -1;
            foreach (var (img, n) in e.Variants) if (n > best) { best = n; image = img; }
            var m = Measure(image);
            if (m == null) continue;
            string? cls = ctx.Profile?.ClassOf(key);
            int area = m.Value.BBox.W * m.Value.BBox.H;
            bool excluded = e.Steel || cls == "overlay" || area < 16 || exclude.Contains(key);
            pieces.Add(new EnvPiece
            {
                Key = key, Image = image, W = image.Width, H = image.Height, Area = area, Opaque = m.Value.Opaque, Fill = m.Value.Fill,
                BBox = m.Value.BBox, FlatTop = m.Value.FlatTop, Cls = cls, Steel = e.Steel, Count = e.Count, Excluded = excluded,
            });
        }
        return new EnvCollected { Pieces = pieces, Decor = new List<EnvPiece>() };
    }

    // buckets: the pieces sorted into what the collage draws with
    public static EnvBuckets Buckets(EnvCollected collected, EnvHints? env)
    {
        var usable = collected.Pieces.Where(p => !p.Excluded).ToList();
        List<EnvPiece> Listed(List<string>? names) => (names ?? new List<string>())
            .Select(n => usable.FirstOrDefault(p => p.Key == n)).Where(p => p != null).Select(p => p!).ToList();
        var ground = usable.Where(p => p.Fill >= 0.45 && Math.Max(p.BBox.W, p.BBox.H) >= 24 && p.Cls != "backdrop")
            .OrderByDescending(p => p.Area).ToList();
        if (ground.Count == 0) ground = usable.OrderByDescending(p => p.Area).ToList();
        var decor = usable.Where(p => p.Cls == "backdrop").Concat(collected.Decor).ToList();
        return new EnvBuckets
        {
            Floor = Listed(env?.Floor) is { Count: > 0 } f ? f : ground,
            Ceiling = Listed(env?.Ceiling) is { Count: > 0 } c ? c : ground,
            Wall = Listed(env?.Wall) is { Count: > 0 } w ? w : ground,
            Decor = decor, Ground = ground, Usable = usable,
        };
    }

    // chooseMode: "tile" for a style of small blocks of one size, else "clump"
    public static string ChooseMode(List<EnvPiece> pieces, EnvProfile? profile)
    {
        var env = profile?.Environment;
        if (env?.Mode == "tile" || env?.Mode == "clump") return env.Mode!;
        var usable = pieces.Where(p => !p.Excluded).ToList();
        if (usable.Count == 0) return "clump";
        int largest = usable.Max(p => Math.Max(p.W, p.H));
        var sizes = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var p in usable) { string s = p.W + "x" + p.H; sizes[s] = (sizes.TryGetValue(s, out int n) ? n : 0) + 1; }
        int shared = sizes.Values.Max();
        return largest < 48 && shared >= 3 ? "tile" : "clump";
    }

    // ------------------------------------------------------------ randomness
    // seededRandom: level.js's generator, seeded from a string
    public static Func<double> SeededRandom(string seed)
    {
        uint s = 0;
        foreach (char ch in seed) s = unchecked(s * 31 + ch);
        if (s == 0) s = 1;
        return () =>
        {
            s ^= s << 13;
            s ^= s >> 17;
            s ^= s << 5;
            return s / 4294967296.0;
        };
    }

    // noiseFn: a smooth wobble in -1..1 along u, three sines with seeded phases
    public static Func<double, double> NoiseFn(Func<double> rng)
    {
        double[] f = { 2, 5, 9 }, a = { 0.5, 0.3, 0.2 };
        var ph = new double[3];
        for (int i = 0; i < 3; i++) ph[i] = rng() * Math.PI * 2;
        return u =>
        {
            double s = 0;
            for (int i = 0; i < 3; i++) s = s + a[i] * V8Math.Sin(2 * Math.PI * f[i] * u + ph[i]);
            return s;
        };
    }

    static T PickWeighted<T>(List<T> list, Func<double> rng, Func<T, double> weight)
    {
        double total = 0;
        foreach (var p in list) total += weight(p);
        double r = rng() * total;
        foreach (var p in list) { r -= weight(p); if (r <= 0) return p; }
        return list[^1];
    }

    // dressed: a piece's picture, flipped as asked and dimmed, cached per piece
    static Bitmap Dressed(EnvPiece piece, bool flipH, bool flipV, int tint)
    {
        string key = (flipH ? "h" : "") + (flipV ? "v" : "") + tint.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (!piece.Dressed.TryGetValue(key, out var img))
        {
            img = piece.Image;
            if (flipH) img = img.FlipHorizontal();
            if (flipV) img = img.FlipVertical();
            if (tint != 0xffffff) img = img.Tinted(tint);
            piece.Dressed[key] = img;
        }
        return img;
    }

    // stamp: a piece onto a picture that wraps round
    static void Stamp(Bitmap dst, Bitmap img, double x, double y)
    {
        double rx = JsMath.Round(x), ry = JsMath.Round(y);
        int W = dst.Width;
        int ix = (int)(((rx % W) + W) % W);
        int iy = (int)ry;
        Pixels.Blit(dst, ix, iy, img, 0, 0, img.Width, img.Height, Pixels.CombineTerrainDefault);
        if (ix + img.Width > W) Pixels.Blit(dst, ix - W, iy, img, 0, 0, img.Width, img.Height, Pixels.CombineTerrainDefault);
    }

    static readonly ConditionalWeakTable<Bitmap, Dictionary<double, Bitmap>> StretchCache = new();

    // stretched: a piece stretched sideways by fx, kept to a twentieth
    static Bitmap Stretched(Bitmap img, double fx)
    {
        if (Math.Abs(fx - 1) < 0.05) return img;
        fx = JsMath.Round(fx * 20) / 20;
        var cache = StretchCache.GetValue(img, _ => new Dictionary<double, Bitmap>());
        if (cache.TryGetValue(fx, out var hit)) return hit;
        var output = StretchedNow(img, fx);
        cache[fx] = output;
        return output;
    }

    static Bitmap StretchedNow(Bitmap img, double fx)
    {
        int w = (int)Math.Max(1, JsMath.Round(img.Width * fx)), h = img.Height;
        var output = new Bitmap(w, h);
        var s = img.Words(); var d = output.Words();
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++) d[y * w + x] = s[y * img.Width + Math.Min(img.Width - 1, JsMath.ToInt32(x / fx))];
        return output;
    }

    // -------------------------------------------------------------- collage
    public static class TINT
    {
        public const int floor = 0xb4b4b4, ceiling = 0x8c8c8c, wall = 0x7a7a7a, prop = 0x666666, wallpaper = 0x909090;
    }

    // meanColor: the mean colour of a picture's opaque pixels
    public static int MeanColor(byte[] d)
    {
        double r = 0, g = 0, b = 0; int n = 0;
        for (int p = 0; p < d.Length; p += 4)
        {
            if (d[p + 3] < Pixels.AlphaCutoff) continue;
            r += d[p]; g += d[p + 1]; b += d[p + 2]; n++;
        }
        return n != 0 ? RgbOf(r / n, g / n, b / n) : 0;
    }

    public static int MeanColor(Bitmap bmp) => MeanColor(bmp.Data);

    // fogBlend: every opaque pixel blended toward `fog`, fFar at row 0 to fNear at the last
    public static void FogBlend(Bitmap bmp, int fog, double fFar, double fNear)
    {
        int w = bmp.Width, h = bmp.Height;
        var d = bmp.Data;
        int fr = R(fog), fg = G(fog), fb = B(fog);
        for (int y = 0; y < h; y++)
        {
            double f = h > 1 ? fFar + (fNear - fFar) * ((double)y / (h - 1)) : fFar;
            int fi = (int)JsMath.Round(f * 256);
            if (fi <= 0) continue;
            int end = (y + 1) * w * 4;
            for (int p = y * w * 4; p < end; p += 4)
            {
                if (d[p + 3] == 0) continue;
                d[p] = JsMath.ClampU8(d[p] + (((fr - d[p]) * fi) >> 8));
                d[p + 1] = JsMath.ClampU8(d[p + 1] + (((fg - d[p + 1]) * fi) >> 8));
                d[p + 2] = JsMath.ClampU8(d[p + 2] + (((fb - d[p + 2]) * fi) >> 8));
            }
        }
    }

    // bandStretch: how much wider a piece is drawn on row y of a band, to keep its size on the ground
    static Func<double, double> BandStretch(RoomLayer layer, double h) =>
        y => layer.ROut / Math.Max(1, layer.ROut - (layer.ROut - layer.RIn) * (y / Math.Max(1, h)));

    static double Away(double u) => Math.Min(Math.Abs(u - 0.5), 1 - Math.Abs(u - 0.5));

    // drawFloor: the first floor a lattice of rocks over the bowl, the further ones ground all over
    static void DrawFloor(Bitmap bmp, Room room, RoomLayer layer, EnvBuckets bk, string mode, Func<double> rng)
    {
        int W = bmp.Width, H = bmp.Height;
        var noise = NoiseFn(rng);
        var stretch = BandStretch(layer, H);
        if (layer.I == 0)
        {
            double Hl(double u) => H * (ROOM.BOWL_DENSE + 0.08 * noise(u));
            if (mode == "tile") DrawTiles(bmp, bk, Hl, rng, TINT.floor, false, 0.08, 0.04, stretch);
            else DrawClumps(bmp, bk.Floor, Hl, rng, TINT.floor, false, 0, stretch);
            var list = bk.Floor.Count > 0 ? bk.Floor : bk.Usable;
            int n = (int)JsMath.Round((double)W * H / 3000);
            double Thinning(double y) => 1 - Smoothstep(ROOM.BOWL_DENSE, ROOM.BOWL_THIN, y / H);
            ScatterStretched(bmp, list, n, rng, TINT.floor, (x, y) => y > Hl(x / W) - 8 && rng() < Thinning(y), stretch);
            Scatter(bmp, bk.Decor, (int)JsMath.Round((double)W * H / 20000), rng, TINT.floor,
                (x, y) => y > Hl(x / W) && y < H * ROOM.BOWL_THIN && !(Away(x / W) < 0.14 && y < H * 0.75));
        }
        else
        {
            double Hl(double u) => H + 64;
            if (mode == "tile") DrawTiles(bmp, bk, Hl, rng, TINT.floor, false, 0.05, 0.04, stretch);
            else DrawClumps(bmp, bk.Floor, Hl, rng, TINT.floor, false, 0, stretch);
        }
    }

    // drawWall: a band of pieces standing along the floor line under a wavy skyline
    static void DrawWall(Bitmap bmp, Room room, RoomLayer layer, EnvBuckets bk, string mode, Func<double> rng)
    {
        int W = bmp.Width, H = bmp.Height;
        var noise = NoiseFn(rng);
        var sheet = new Bitmap(W, H);
        double top = layer.Skyline, swell = layer.Swell;
        Func<double, double> dip = layer.I == 0 ? u => 1 - 0.55 * V8Math.Exp(-V8Math.Square(Away(u) / 0.16)) : _ => 1;
        double Hl(double u) => H * Math.Min(1, (top + swell * noise(u)) * dip(u));
        if (mode == "tile") DrawTiles(sheet, bk, Hl, rng, TINT.wall, true, 0.06, 0.04, null);
        else DrawClumps(sheet, bk.Wall.Count > 0 ? bk.Wall : bk.Ground, Hl, rng, TINT.wall, true, 0, null);
        var up = sheet.FlipVertical();
        Pixels.Blit(bmp, 0, 0, up, 0, 0, W, H, Pixels.CombineTerrainDefault);
        if (layer.I == 0) Scatter(bmp, bk.Decor, (int)JsMath.Round(W / 300.0), rng, TINT.wall, (x, y) => y > H - Hl(x / W) - 8);
    }

    // drawClumps: pieces packed in rows from row 0 up to the wavy limit hl(u)
    static void DrawClumps(Bitmap bmp, List<EnvPiece> list, Func<double, double> hl, Func<double> rng, int tint, bool flipV, double narrowing, Func<double, double>? stretch)
    {
        if (list.Count == 0) return;
        int W = bmp.Width, H = bmp.Height;
        var fxAt = stretch ?? (_ => 1);
        var heights = list.Select(p => p.BBox.H).OrderBy(v => v).ToList();
        int mhRaw = heights[heights.Count >> 1];
        double mh = mhRaw != 0 ? mhRaw : 8;
        double step = Math.Max(2, mh * 0.6);
        double rowY = 0; int row = 0;
        while (rowY < H)
        {
            double a = -rng() * 32;
            double x = a + row * narrowing * rng();
            while (x < W)
            {
                var p = PickWeighted(list, rng, q => q.Area * (1 + q.Count));
                double top = rowY - rng() * 8;
                double limit = hl(Math.Min(1, Math.Max(0, (x + p.BBox.W / 2.0) / W)));
                double fx = fxAt(Math.Max(0, Math.Min(H - 1, top + p.BBox.H / 2.0)));
                if (top + p.BBox.H * 0.5 <= limit)
                {
                    var img = Stretched(Dressed(p, rng() < 0.5, flipV, tint), fx);
                    double bx = (flipV ? p.W - p.BBox.X - p.BBox.W : p.BBox.X) * fx;
                    double by = flipV ? p.H - p.BBox.Y - p.BBox.H : p.BBox.Y;
                    Stamp(bmp, img, x - bx, top - by);
                }
                x += p.BBox.W * fx * (0.55 + 0.15 * rng());
            }
            rowY += step;
            row++;
        }
    }

    // tileSet: the most-placed pieces of the dominant size
    static List<EnvPiece> TileSet(EnvBuckets bk)
    {
        var sizes = new Dictionary<string, int>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var p in bk.Usable)
        {
            string s = p.W + "x" + p.H;
            if (sizes.TryGetValue(s, out int n)) sizes[s] = n + p.Count; else { sizes[s] = p.Count; order.Add(s); }
        }
        string? bestSize = null; int best = -1;
        foreach (string s in order) if (sizes[s] > best) { best = sizes[s]; bestSize = s; }
        return bk.Usable.Where(p => p.W + "x" + p.H == bestSize).OrderByDescending(p => p.Count).Take(5).ToList();
    }

    // drawTiles: blocks on a grid under hl(u), a few cells left empty, a few turned
    static void DrawTiles(Bitmap bmp, EnvBuckets bk, Func<double, double> hl, Func<double> rng, int tint, bool flipV, double emptyP, double turnP, Func<double, double>? stretch)
    {
        var tiles = TileSet(bk);
        if (tiles.Count == 0) return;
        int cw = tiles[0].W, ch = tiles[0].H, W = bmp.Width;
        var fxAt = stretch ?? (_ => 1);
        for (int y = 0; y < bmp.Height; y += ch)
        {
            double fx = fxAt(y + ch / 2.0), step = Math.Max(1, cw * fx);
            for (double x = 0; x < W; x += step)
            {
                if (y + ch > hl((x + step / 2) / W)) continue;
                if (rng() < emptyP) continue;
                var t = PickWeighted(tiles, rng, p => p.Count);
                var img = Dressed(t, rng() < 0.5, flipV, tint);
                if (cw == ch && rng() < turnP) img = img.Rotate90();
                Stamp(bmp, Stretched(img, fx), x, y);
            }
        }
    }

    // scatterStretched: n pieces dropped where ok(x, y) allows, stretched for their row
    static void ScatterStretched(Bitmap bmp, List<EnvPiece> list, int n, Func<double> rng, int tint, Func<double, double, bool> ok, Func<double, double>? stretch)
    {
        if (list.Count == 0 || n <= 0) return;
        for (int i = 0, tries = 0; i < n && tries < n * 4; tries++)
        {
            var p = PickWeighted(list, rng, q => q.Area);
            double x = rng() * bmp.Width;
            double y = rng() * bmp.Height;
            if (!ok(x, y)) continue;
            double fx = stretch != null ? stretch(y) : 1;
            var img = Stretched(Dressed(p, rng() < 0.5, false, tint), fx);
            Stamp(bmp, img, x - p.BBox.X * fx - p.BBox.W * fx / 2, y - p.BBox.Y - p.BBox.H / 2.0);
            i++;
        }
    }

    // scatter: n decorations dropped where ok(x, y) allows
    static void Scatter(Bitmap bmp, List<EnvPiece> decor, int n, Func<double> rng, int tint, Func<double, double, bool> ok)
    {
        if (decor.Count == 0 || n <= 0) return;
        for (int i = 0, tries = 0; i < n && tries < n * 6; tries++)
        {
            var p = decor[JsMath.ToInt32(rng() * decor.Count)];
            double x = rng() * bmp.Width;
            double y = rng() * bmp.Height;
            if (!ok(x, y)) continue;
            var img = Dressed(p, rng() < 0.5, false, tint);
            Stamp(bmp, img, x - p.BBox.X - p.BBox.W / 2.0, y - p.BBox.Y - p.BBox.H);
            i++;
        }
    }

    // buildProps: the pieces standing on each ring's floor between the walls
    public static List<EnvProp> BuildProps(EnvContext ctx, Room room, EnvCollected collected, int fog)
    {
        double P = room.P;
        var bk = Buckets(collected, ctx.Profile?.Environment);
        var list = bk.Floor.Count > 0 ? bk.Floor : bk.Usable;
        if (list.Count == 0) return new List<EnvProp>();
        var tall = list.Where(p => p.BBox.H >= p.BBox.W * 0.6).ToList();
        var pool = tall.Count >= 3 ? tall : list;
        var rng = SeededRandom("props:" + (string.IsNullOrEmpty(ctx.LevelId) ? "level" : ctx.LevelId));
        var output = new List<EnvProp>();
        for (int i = 0; i < room.Layers.Count; i++)
        {
            var layer = room.Layers[i];
            int n = ROOM.PROPS[Math.Min(i, ROOM.PROPS.Length - 1)];
            double hLo = ROOM.PROP_M[Math.Min(i, ROOM.PROP_M.Length - 1)][0], hHi = ROOM.PROP_M[Math.Min(i, ROOM.PROP_M.Length - 1)][1];
            double rLo = layer.RIn + (layer.ROut - layer.RIn) * (i == 0 ? 0.62 : 0.15);
            double rHi = layer.RIn + (layer.ROut - layer.RIn) * 0.9;
            for (int k = 0, tries = 0; k < n && tries < n * 4; tries++)
            {
                double u = rng();
                double r = rLo + (rHi - rLo) * rng();
                if (i == 0 && Math.Abs(u - 0.5) < 0.2) continue;
                var p = PickWeighted(pool, rng, q => q.Area);
                double hPx = (hLo + (hHi - hLo) * rng()) * P;
                double scale = hPx / p.BBox.H;
                var img = Dressed(p, rng() < 0.5, false, TINT.floor);
                var bmp = img.Crop(p.BBox.X, p.BBox.Y, p.BBox.W, p.BBox.H);
                FogBlend(bmp, fog, FogAt(r / P), FogAt(r / P));
                int depth = (int)Math.Max(2, JsMath.Round(p.BBox.W * ROOM.THICK));
                double yaw = (rng() - 0.5) * Math.PI / 3.6;
                output.Add(new EnvProp(i, u, r, p.BBox.W * scale, hPx, bmp, depth, yaw));
                k++;
            }
        }
        return output;
    }

    // ------------------------------------------------------------- the build
    // parsePlane: { kind: floor | wall | band | ceiling | bowl | sky | backdrop, i }
    public static (string Kind, int I) ParsePlane(string name)
    {
        var m = System.Text.RegularExpressions.Regex.Match(name, "^(floor|wall|band|ceiling|bowl)([0-9]+)$");
        if (name == "sky") return ("sky", 0);
        return m.Success ? (m.Groups[1].Value, int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture)) : (name, 0);
    }

    // build: the pictures for a context; `only` limits the planes drawn (null: every one, props too)
    public static EnvBuildResult Build(EnvContext ctx, EnvBuildOptions opts, IReadOnlyCollection<string>? only)
    {
        var room = opts.Room;
        var palette = opts.Palette ?? DerivePalette(ctx);
        var env = ctx.Profile?.Environment;
        int dark = palette.Dark, bg = palette.Bg;
        var wp = opts.Wallpaper;
        int fog = opts.Fog ?? (wp != null && wp.Kind == "wallpaper" ? Scale(MeanColor(wp.Image), 0.85) : palette.Fog);
        bool Want(string name) => only == null || only.Contains(name);
        var planes = new Dictionary<string, Bitmap>(StringComparer.Ordinal);
        EnvCollected? collected = null; EnvBuckets? bk = null; string? mode = null;
        if (opts.Full)
        {
            collected = opts.Pieces ?? CollectPieces(ctx);
            bk = Buckets(collected, env);
            mode = ChooseMode(collected.Pieces, ctx.Profile);
        }
        Func<double> Seed(string name) => SeededRandom(name + ":" + (string.IsNullOrEmpty(ctx.LevelId) ? "level" : ctx.LevelId));
        GradientStop S(double t, int rgb) => new(t, rgb);

        if (Want("bowl0"))
        {
            var layer = room.Layers[0];
            var bmp = new Bitmap(layer.Floor.W, layer.Floor.H);
            if (opts.SmoothFog) PaintSmooth(bmp, new[] { S(0, Scale(fog, 0.6)), S(0.5, Scale(fog, 0.25)), S(1, 0x000000) });
            else PaintGradient(bmp, new[] { S(0, Scale(dark, 0.45)), S(0.5, Scale(dark, 0.18)), S(1, 0x000000) }, "v", palette.Material, dark, 0, 1);
            if (opts.Full)
            {
                var rng = Seed("bowl0");
                int H = bmp.Height, W = bmp.Width;
                var list = bk!.Floor.Count > 0 ? bk.Floor : bk.Usable;
                ScatterStretched(bmp, list, (int)JsMath.Round((double)W * H / 6000), rng, 0x6a6a6a, (x, y) => rng() < 1 - Smoothstep(0.2, 0.85, y / H), BandStretch(layer, H));
                Darken(bmp, 0, 0, H * 0.7, W, H, (x, y) => Smoothstep(0.7, 1, (double)y / H));
            }
            planes["bowl0"] = bmp;
        }
        foreach (var layer in room.Layers)
        {
            int i = layer.I;
            if (Want("floor" + i))
            {
                var bmp = new Bitmap(layer.Floor.W, layer.Floor.H);
                var stops = i == 0
                    ? new[] { S(0, Scale(dark, 0.55)), S(0.45, Scale(dark, 0.75)), S(1, Scale(dark, 0.3)) }
                    : new[] { S(0, Scale(dark, 0.5)), S(1, Scale(dark, 0.6)) };
                if (opts.SmoothFog) PaintSmooth(bmp, new[] { S(0, Scale(fog, 0.8)), S(1, Scale(fog, 0.55)) });
                else if (!(i == 0 && opts.Full)) PaintGradient(bmp, stops, "v", palette.Material, dark, 0, 1);
                if (opts.Full) DrawFloor(bmp, room, layer, bk!, mode!, Seed("floor" + i));
                FogBlend(bmp, fog, layer.Fog, layer.FogNear);
                planes["floor" + i] = bmp;
            }
            if (Want("ceiling" + i))
            {
                var bmp = new Bitmap(layer.Ceiling.W, layer.Ceiling.H);
                int overhead = Scale(fog, i == 0 ? 0.55 : 0.8);
                PaintSmooth(bmp, new[] { S(0, fog), S(1, overhead) });
                FogBlend(bmp, fog, layer.Fog, layer.FogNear);
                planes["ceiling" + i] = bmp;
            }
            if (Want("band" + i))
            {
                var bmp = new Bitmap(layer.Wall.W, layer.Wall.H);
                var inner = layer.With(layer.Skyline * ROOM.BAND_SKY, layer.Swell * 0.7);
                if (opts.Full) DrawWall(bmp, room, inner, bk!, mode!, Seed("band" + i));
                FogBlend(bmp, fog, layer.FogBand, layer.FogBand);
                planes["band" + i] = bmp;
            }
            if (Want("wall" + i))
            {
                var bmp = new Bitmap(layer.Wall.W, layer.Wall.H);
                if (opts.Full) DrawWall(bmp, room, layer, bk!, mode!, Seed("wall" + i));
                FogBlend(bmp, fog, layer.Fog, layer.Fog);
                planes["wall" + i] = bmp;
            }
        }
        if (Want("sky"))
        {
            var sp = room.Sphere;
            var bmp = new Bitmap(sp.W, sp.H);
            PaintSmooth(bmp, new[] { S(0, Scale(fog, 0.5)), S(0.42, Scale(fog, 0.9)), S(0.5, fog), S(0.58, Scale(fog, 0.9)), S(1, Scale(fog, 0.45)) });
            if (wp != null && wp.Kind == "wallpaper")
            {
                var band = new Bitmap(sp.W, (int)JsMath.Round(sp.H * 0.24));
                Pixels.DrawNineSlice(band, 0, 0, band.Width, band.Height, wp.Image.Tinted(TINT.wallpaper), new Pixels.Margins(0, 0, 0, 0), Pixels.CombineGadget);
                FogBlend(band, fog, 0.85, 0.85);
                Pixels.Blit(bmp, 0, (int)JsMath.Round(sp.H * 0.38), band, 0, 0, band.Width, band.Height, Pixels.CombineGadget);
            }
            planes["sky"] = bmp;
        }
        EnvBackdrop? backdrop = null;
        if (Want("backdrop") && wp != null && wp.Kind == "prop") backdrop = PropBackdrop(ctx, wp.Image, bg);
        var props = opts.Full && Want("props") ? BuildProps(ctx, room, collected!, fog) : null;
        return new EnvBuildResult { Palette = palette, Mode = mode, Fog = fog, Planes = planes, Backdrop = backdrop, Props = props, Pieces = collected };
    }

    // drawWallpaper: the style's background on the far wall, a sky tiled or a prop placed once
    public static void DrawWallpaper(Bitmap bmp, Room room, RoomLayer layer, EnvWallpaper wp, int fog)
    {
        int k = layer.Wall.K;
        int f = (int)Math.Max(1, JsMath.Round(k / 2.0));
        var img = f > 1 ? Shrink(wp.Image, f) : wp.Image;
        if (wp.Kind == "prop")
        {
            int x = JsMath.ToInt32(bmp.Width / 2.0 - img.Width / 2.0);
            double y = (room.WallPx - room.FloorDrop) / k - img.Height;
            Pixels.Blit(bmp, x, JsMath.ToInt32(y), img.Tinted(TINT.prop), 0, 0, img.Width, img.Height, Pixels.CombineGadget);
            return;
        }
        var tinted = img.Tinted(TINT.wallpaper);
        Pixels.DrawNineSlice(bmp, 0, 0, bmp.Width, bmp.Height, tinted, new Pixels.Margins(0, 0, 0, 0), Pixels.CombineGadget);
    }

    // propBackdrop: a prop background once on the background colour, centred, on the bottom edge
    public static EnvBackdrop PropBackdrop(EnvContext ctx, Bitmap img, int bg)
    {
        int W = ctx.Width, H = ctx.Height;
        int k = W > 2048 || H > 2048 ? 2 : 1;
        var output = new Bitmap((int)Math.Ceiling(W / (double)k), (int)Math.Ceiling(H / (double)k));
        var d = output.Data;
        for (int p = 0; p < d.Length; p += 4) { d[p] = (byte)R(bg); d[p + 1] = (byte)G(bg); d[p + 2] = (byte)B(bg); d[p + 3] = 255; }
        var src = k == 1 ? img : Shrink(img, k);
        Pixels.Blit(output, JsMath.ToInt32((output.Width - src.Width) / 2.0), output.Height - src.Height, src, 0, 0, src.Width, src.Height, Pixels.CombineGadget);
        return new EnvBackdrop(output, k);
    }

    // shrink: a bitmap at 1/k, nearest pixel
    public static Bitmap Shrink(Bitmap img, int k)
    {
        int w = (int)Math.Ceiling(img.Width / (double)k), h = (int)Math.Ceiling(img.Height / (double)k);
        var output = new Bitmap(w, h);
        var s = img.Words(); var d = output.Words();
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++) d[y * w + x] = s[Math.Min(img.Height - 1, y * k) * img.Width + Math.Min(img.Width - 1, x * k)];
        return output;
    }
}
