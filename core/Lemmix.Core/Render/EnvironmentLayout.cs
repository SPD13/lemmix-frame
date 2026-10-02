using System.Text.Json;
using Lemmix.Engine;
using Lemmix.Io;
using Lemmix.Util;

namespace Lemmix.Render;

// web/3d/js/environment.js, its data side: what a level's room is built from (the gallery
// context: every terrain piece of the theme style on a contact sheet), which pictures the page
// asks envgen for (fog only while no pictures are made offline - web/3d/env has none), the
// level's wallpaper and backdrop, and the geometry the pictures hang on - rings, drums, the fog
// sphere and the standing pieces - in board pixels round the player's place (y up, the slab's
// back at z = 0). The headset placement (placeForXR: the diorama's yaw, scale and the head's
// position) only moves the root and the centre; the desktop's numbers are PlaceDesktop's.
public static class EnvironmentLayout
{
    public const int ENV_SCENE_COLOR = 0x10141c;
    public const int ENV_BACKDROP_COLOR = 0x05070c;
    public const int ENV_SEGMENTS = 96;
    public static readonly string[] ENV_MODES = { "none", "full" };
    // vr.js VR_PIXEL_SCALE (metres per game pixel); app.js gives the room 1 / VR_PIXEL_SCALE pixels a metre
    public const double VR_PIXEL_SCALE = 0.0025;
    public static readonly double PxPerMetre = 1 / VR_PIXEL_SCALE;
    // the pictures put up first, in the haze, before the rest (_buildGallery)
    public static readonly string[] FirstPlanes = { "sky", "floor0", "bowl0", "ceiling0" };

    // _galleryKey: the gallery a level's environment belongs to, its theme style
    public static string GalleryKey(EnvContext ctx) => "nx:" + (string.IsNullOrEmpty(ctx.ThemeName) ? "default" : ctx.ThemeName);

    // The level's own context (app.js envCtx), as far as the room reads it.
    public static EnvContext LevelContext(Level level, string levelId, EnvProfile? profile) => new()
    {
        Engine = "lemmix", LevelId = levelId, Width = level.Width, Height = level.Height,
        ThemeName = string.IsNullOrEmpty(level.ThemeName) ? null : level.ThemeName, Theme = level.Theme,
        GroundImage = level.GroundImage, GroundMask = level.GroundMask?.GroundMask, Profile = profile,
        LemmixPieces = level.Pieces.Select(p => new EnvPieceSource(p.Drawn.Key, p.Drawn.Image, p.Drawn.Steel, p.Erase)).ToList(),
        BackgroundImage = level.Background.Image, BackgroundName = level.Info.Background,
    };

    // neolemmix/styles/index.json: a style's terrain pieces and which of them are steel
    public sealed record StyleIndexEntry(List<string> Pieces, List<string> Steel);

    public static Dictionary<string, StyleIndexEntry>? ReadStylesIndex(IFileSource io)
    {
        string? text = io.Text(StyleManager.AssetDir + "styles/index.json");
        if (text == null) return null;
        try
        {
            using var doc = JsonDocument.Parse(text);
            var byName = new Dictionary<string, StyleIndexEntry>(StringComparer.Ordinal);
            if (!doc.RootElement.TryGetProperty("styles", out var styles)) return byName;
            foreach (var s in styles.EnumerateArray())
            {
                List<string> L(string n) => s.TryGetProperty(n, out var a) && a.ValueKind == JsonValueKind.Array
                    ? a.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : new List<string>();
                byName[StyleManager.Lower(s.GetProperty("name").GetString())] = new StyleIndexEntry(L("pieces"), L("steel"));
            }
            return byName;
        }
        catch (JsonException) { return null; }
    }

    // _galleryContext: every terrain piece of the level's theme style, a contact sheet of them
    // standing in for a level's picture (the palette is read from it)
    public static EnvContext GalleryContext(EnvContext ctx, StyleManager? styles, Dictionary<string, StyleIndexEntry>? index)
    {
        var images = new List<Bitmap>();
        List<EnvPieceSource>? pieces = null;
        if (styles != null && !string.IsNullOrEmpty(ctx.ThemeName) && index != null
            && index.TryGetValue(ctx.ThemeName, out var entry) && entry.Pieces.Count > 0)
        {
            var steel = new HashSet<string>(entry.Steel, StringComparer.Ordinal);
            pieces = new List<EnvPieceSource>();
            foreach (string piece in entry.Pieces)
            {
                var meta = styles.Terrain(ctx.ThemeName, piece);
                if (meta == null || meta.Base?.Image == null) continue;
                var image = meta.Base.Image;
                pieces.Add(new EnvPieceSource(ctx.ThemeName + ":" + piece, image, meta.Steel || steel.Contains(piece), false));
                images.Add(image);
            }
        }
        pieces ??= new List<EnvPieceSource>(); // no style to build from: the fog alone
        var sheet = EnvGen.SheetOf(images);
        return new EnvContext
        {
            Engine = ctx.Engine, LevelId = "gallery:" + GalleryKey(ctx), Gallery = true,
            ThemeName = ctx.ThemeName, Theme = ctx.Theme, Profile = ctx.Profile, Dir = ctx.ThemeName,
            Width = sheet.Width, Height = sheet.Height, GroundImage = sheet.Data, GroundMask = null, LemmixPieces = pieces,
        };
    }

    // _galleryWallpaper: the gallery's sky, the wallpaper its profile names
    public static EnvWallpaper? GalleryWallpaper(EnvContext gctx, StyleManager? styles)
    {
        var env = gctx.Profile?.Environment;
        if (string.IsNullOrEmpty(env?.Wallpaper) || styles == null) return null;
        var id = StyleManager.SplitIdentifier(env.Wallpaper.ToLowerInvariant(), string.IsNullOrEmpty(gctx.ThemeName) ? "default" : gctx.ThemeName);
        if (id == null) return null;
        var image = styles.Background(id.Gs, id.Piece);
        if (image == null) return null;
        string key = id.Gs + ":" + id.Piece;
        return new EnvWallpaper(image, key, EnvGen.ClassifyBackground(image, key, gctx.Profile));
    }

    // _wallpaper: the level's own background, for the backdrop behind its slab
    public static EnvWallpaper? LevelWallpaper(EnvContext ctx)
    {
        if (ctx.BackgroundImage == null) return null;
        string key = (ctx.BackgroundName ?? "").ToLowerInvariant();
        if (key != "" && !key.Contains(':')) key = (ctx.ThemeName ?? "null") + ":" + key;
        return new EnvWallpaper(ctx.BackgroundImage, key, EnvGen.ClassifyBackground(ctx.BackgroundImage, key, ctx.Profile));
    }

    // What _buildGallery asks envgen for: with no pictures made offline the gallery is fog only
    // (the sky, floor0, bowl0, ceiling0 in the haze); `full` (the "full" state with pictures to
    // stand on) builds every plane as the collage, and the standing pieces.
    public sealed class Gallery
    {
        public required Room Room;
        public required EnvPalette Palette;
        public EnvWallpaper? Wallpaper;
        public required EnvCollected Collected;
        public bool FogOnly;
        public int Fog;
        public string? CollageMode;
        public readonly Dictionary<string, Bitmap> Pictures = new(StringComparer.Ordinal);
        public List<EnvProp> Props = new();
    }

    public static Gallery BuildGallery(EnvContext gctx, StyleManager? styles, bool hasFiles, string mode = "full")
    {
        var room = EnvGen.CanonicalRoom(PxPerMetre);
        var palette = EnvGen.DerivePalette(gctx);
        var wallpaper = GalleryWallpaper(gctx, styles);
        var collected = EnvGen.CollectPieces(gctx);
        bool fogOnly = !hasFiles || !collected.Pieces.Any(p => !p.Excluded);
        bool full = mode == "full" && !fogOnly;
        var g = new Gallery { Room = room, Palette = palette, Wallpaper = wallpaper, Collected = collected, FogOnly = fogOnly };
        foreach (string name in FirstPlanes)
        {
            var ambient = EnvGen.Build(gctx, new EnvBuildOptions { Room = room, Palette = palette, Wallpaper = wallpaper, Full = false, SmoothFog = fogOnly }, new[] { name });
            g.Fog = ambient.Fog;
            g.Pictures[name] = ambient.Planes[name];
        }
        if (fogOnly) return g;
        foreach (string name in EnvGen.PlaneNames(room))
        {
            if (name == "backdrop" || name == "sky") continue;
            if (!full && FirstPlanes.Contains(name)) continue;
            var built = EnvGen.Build(gctx, new EnvBuildOptions { Room = room, Palette = palette, Wallpaper = wallpaper, Full = full, Pieces = collected }, new[] { name });
            g.Pictures[name] = built.Planes[name];
            if (built.Mode != null) g.CollageMode = built.Mode;
        }
        if (full) g.Props = EnvGen.Build(gctx, new EnvBuildOptions { Room = room, Palette = palette, Wallpaper = wallpaper, Full = true, Pieces = collected }, new[] { "props" }).Props ?? new();
        return g;
    }

    // _applyScene: past the last ring there is only the fog
    public static int SceneColour(Gallery? g) => g == null ? ENV_SCENE_COLOR : g.Fog;

    // _applyBackdrop: the slab's backdrop - the level's wallpaper tiled 1:1 behind the terrain
    // (tinted 0x999999), a prop placed once (0xb0b0b0), or the gallery's background dimmed
    public sealed record Backdrop(int Colour, EnvWallpaper? Tiled, double RepeatX, double RepeatY, EnvBackdrop? Prop);

    public static Backdrop BackdropFor(EnvContext levelCtx, Gallery? g, EnvWallpaper? wp)
    {
        if (g == null) return new Backdrop(ENV_BACKDROP_COLOR, null, 0, 0, null);
        if (wp != null && wp.Kind == "wallpaper")
            return new Backdrop(0x999999, wp, levelCtx.Width / (double)wp.Image.Width, levelCtx.Height / (double)wp.Image.Height, null);
        if (wp != null && wp.Kind == "prop")
            return new Backdrop(0xb0b0b0, null, 0, 0, EnvGen.PropBackdrop(levelCtx, wp.Image, g.Palette.Bg));
        return new Backdrop(EnvGen.Scale(g.Palette.Bg, 0.7), null, 0, 0, null);
    }

    // ------------------------------------------------------------ geometry
    // Environment.ringGeometry: a flat ring at height y round c, u round, v from the inner edge
    // (0) to the rim (1), its height raised by `profile(r)` where given
    public static GeometryBuffers RingGeometry((double X, double Z) c, double rIn, double rOut, double y, int rows, Func<double, double>? profile)
    {
        if (rows == 0) rows = 6;
        int segs = ENV_SEGMENTS;
        var pos = new List<double>(); var uv = new List<double>(); var idx = new List<int>();
        for (int j = 0; j <= rows; j++)
        {
            double v = (double)j / rows, r = rIn + (rOut - rIn) * v;
            double yy = y + (profile != null ? profile(r) : 0);
            for (int i = 0; i <= segs; i++)
            {
                double u = (double)i / segs, th = u * Math.PI * 2;
                pos.Add(c.X + r * V8Math.Sin(th)); pos.Add(yy); pos.Add(c.Z + r * V8Math.Cos(th));
                uv.Add(u); uv.Add(v);
            }
        }
        for (int j = 0; j < rows; j++)
            for (int i = 0; i < segs; i++)
            {
                int a = j * (segs + 1) + i, b = a + segs + 1;
                idx.Add(a); idx.Add(b); idx.Add(a + 1); idx.Add(a + 1); idx.Add(b); idx.Add(b + 1);
            }
        return new GeometryBuffers { Position = GeometryBuffers.ToFloat(pos), Uv = GeometryBuffers.ToFloat(uv), Index = idx.ToArray() };
    }

    // Environment.drumGeometry: an open drum of radius r round c from y0 up to y1
    public static GeometryBuffers DrumGeometry((double X, double Z) c, double r, double y0, double y1)
    {
        int segs = ENV_SEGMENTS;
        var pos = new List<double>(); var uv = new List<double>(); var idx = new List<int>();
        for (int j = 0; j <= 1; j++)
        {
            double y = j != 0 ? y1 : y0;
            for (int i = 0; i <= segs; i++)
            {
                double u = (double)i / segs, th = u * Math.PI * 2;
                pos.Add(c.X + r * V8Math.Sin(th)); pos.Add(y); pos.Add(c.Z + r * V8Math.Cos(th));
                uv.Add(u); uv.Add(j);
            }
        }
        for (int i = 0; i < segs; i++) { idx.Add(i); idx.Add(i + 1); idx.Add(i + segs + 1); idx.Add(i + 1); idx.Add(i + segs + 2); idx.Add(i + segs + 1); }
        return new GeometryBuffers { Position = GeometryBuffers.ToFloat(pos), Uv = GeometryBuffers.ToFloat(uv), Index = idx.ToArray() };
    }

    // THREE.SphereGeometry (r147), position / uv / index (the normals are not needed: the fog
    // sphere is unlit)
    public static GeometryBuffers SphereGeometry(double radius, int widthSegments = 32, int heightSegments = 16,
        double phiStart = 0, double phiLength = Math.PI * 2, double thetaStart = 0, double thetaLength = Math.PI)
    {
        widthSegments = Math.Max(3, widthSegments);
        heightSegments = Math.Max(2, heightSegments);
        double thetaEnd = Math.Min(thetaStart + thetaLength, Math.PI);
        int index = 0;
        var grid = new List<int[]>();
        var vertices = new List<double>(); var uvs = new List<double>(); var indices = new List<int>();
        for (int iy = 0; iy <= heightSegments; iy++)
        {
            var row = new int[widthSegments + 1];
            double v = (double)iy / heightSegments;
            double uOffset = 0;
            if (iy == 0 && thetaStart == 0) uOffset = 0.5 / widthSegments;
            else if (iy == heightSegments && thetaEnd == Math.PI) uOffset = -0.5 / widthSegments;
            for (int ix = 0; ix <= widthSegments; ix++)
            {
                double u = (double)ix / widthSegments;
                double x = -radius * V8Math.Cos(phiStart + u * phiLength) * V8Math.Sin(thetaStart + v * thetaLength);
                double y = radius * V8Math.Cos(thetaStart + v * thetaLength);
                double z = radius * V8Math.Sin(phiStart + u * phiLength) * V8Math.Sin(thetaStart + v * thetaLength);
                vertices.Add(x); vertices.Add(y); vertices.Add(z);
                uvs.Add(u + uOffset); uvs.Add(1 - v);
                row[ix] = index++;
            }
            grid.Add(row);
        }
        for (int iy = 0; iy < heightSegments; iy++)
            for (int ix = 0; ix < widthSegments; ix++)
            {
                int a = grid[iy][ix + 1], b = grid[iy][ix], c = grid[iy + 1][ix], d = grid[iy + 1][ix + 1];
                if (iy != 0 || thetaStart > 0) { indices.Add(a); indices.Add(b); indices.Add(d); }
                if (iy != heightSegments - 1 || thetaEnd < Math.PI) { indices.Add(b); indices.Add(c); indices.Add(d); }
            }
        return new GeometryBuffers { Position = GeometryBuffers.ToFloat(vertices), Uv = GeometryBuffers.ToFloat(uvs), Index = indices.ToArray() };
    }

    // Environment.pieceGeometry: a standing piece extruded like a sprite on the board, its
    // origin at its bottom centre
    public static GeometryBuffers? PieceGeometry(EnvProp p)
    {
        var bmp = p.Bitmap;
        int w = bmp.Width, h = bmp.Height;
        var d = bmp.Data;
        int depth = p.Depth != 0 ? p.Depth : 2;
        var g = SpriteBuild.BuildExtrudedSpriteGeometry((x, y) => d[(y * w + x) * 4 + 3] >= 0x80, w, h, depth);
        if (g == null) return null;
        g.Translate(-w / 2.0, -h, -depth / 2.0);
        return g;
    }

    // Where a standing piece goes (_placeProps): on its ring's floor, its face to the centre
    // turned by its yaw, scaled from its picture's pixels (y down) to its height
    public static (double X, double Y, double Z, double RotY, double Scale) PlaceProp(EnvProp p, (double X, double Z) c, double yFloor)
    {
        double th = p.U * Math.PI * 2;
        double x = c.X + p.R * V8Math.Sin(th), z = c.Z + p.R * V8Math.Cos(th);
        double k = p.H / p.Bitmap.Height;
        return (x, yFloor, z, Math.Atan2(c.X - x, c.Z - z) + p.Yaw, k);
    }

    // placeDesktop: the floor a little below the board, the ceiling CEIL_M over it
    public static (double YFloor, double YCeil) DesktopHeights(double pxPerMetre)
    {
        double yFloor = -EnvGen.ROOM.FLOOR_DROP_M * pxPerMetre;
        return (yFloor, yFloor + EnvGen.ROOM.CEIL_M * pxPerMetre);
    }

    // _layout: every plane's geometry for a room round `center` (the room's own centre unless
    // the headset placed it elsewhere), floor at yF and ceiling at yC
    public static Dictionary<string, GeometryBuffers> Layout(Room room, (double X, double Z)? center, double yF, double yC)
    {
        var c = center ?? room.Center;
        double P = room.P;
        var output = new Dictionary<string, GeometryBuffers>(StringComparer.Ordinal);
        foreach (var l in room.Layers)
        {
            bool first = l.I == 0;
            output["floor" + l.I] = RingGeometry(c, l.RIn, l.ROut, yF, 6, null);
            output["ceiling" + l.I] = RingGeometry(c, l.RIn, l.ROut, yC, first ? 16 : 6,
                first ? r => EnvGen.ROOM.BOWL_DOME_M * P * (1 - V8Math.Square(r / l.ROut)) : null);
            output["wall" + l.I] = DrumGeometry(c, l.ROut, yF, yC);
            output["band" + l.I] = DrumGeometry(c, l.RBand, yF, yC);
        }
        var l0 = room.Layers[0];
        output["bowl0"] = RingGeometry(c, 0, l0.ROut, yF - 1, 32, r => -EnvGen.ROOM.BOWL_DEPTH_M * P * (1 - V8Math.Square(r / l0.ROut)));
        var sphere = SphereGeometry(room.Sphere.R, 48, 32);
        sphere.Translate(c.X, yF + 1.6 * P, c.Z);
        output["sky"] = sphere;
        return output;
    }

    // _wallRepeat: the wall's picture is drawn for a nominal height; the plane shows that many
    // rows from the floor up (texture repeat y)
    public static double WallRepeatY(Room room, double wallHeight) => wallHeight / room.WallPx;

    // Environment.fileFor: the file a plane's offline picture is kept in (floor.png, floor-1.png, ...)
    public static string FileFor(string name)
    {
        var (kind, i) = EnvGen.ParsePlane(name);
        return kind + (i != 0 ? "-" + i : "");
    }
}
