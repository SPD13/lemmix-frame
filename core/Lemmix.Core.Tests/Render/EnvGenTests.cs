using System.Collections.Concurrent;
using System.Text.Json;
using Lemmix.Engine;
using Lemmix.Oracle;
using Lemmix.Render;
using Lemmix.Tests.Oracle;

namespace Lemmix.Tests.Render;

// oracle/envgen.js: the room round the board (web/3d/js/envgen.js, environment.js) for one
// level of each of the first themes - gallery, palette, pieces, the runtime's fog pictures, the
// full collage and its standing pieces, the level's backdrop, the rooms and the rings' geometry -
// plus V8's Math.sin / cos / exp / hypot, bit for bit.
public class EnvGenTests
{
    static void Bitmap(StateHash h, Bitmap? b)
    {
        if (b == null) { h.Null(); return; }
        h.Word(b.Width); h.Word(b.Height); h.Bytes(b.Data);
    }

    static string RoomHash(Room room)
    {
        var h = new StateHash();
        foreach (double v in new[] { room.W, room.H, room.P, room.FloorDrop, room.WallPx, room.Center.X, room.Center.Z, room.Reach }) RenderHash.F64(h, v);
        h.Word(room.Layers.Count);
        foreach (var l in room.Layers)
        {
            foreach (double v in new[] { l.I, l.RIn, l.ROut, l.Circ, l.D, l.RBand, l.FogBand, l.Fog, l.FogNear, l.Skyline, l.Swell }) RenderHash.F64(h, v);
            foreach (var p in new[] { l.Floor, l.Ceiling, l.Wall }) { h.Word(p.K); h.Word(p.W); h.Word(p.H); }
        }
        h.Word(room.Sphere.R); h.Word(room.Sphere.W); h.Word(room.Sphere.H);
        return h.Hex();
    }

    static string PaletteHash(EnvPalette p)
    {
        var h = new StateHash();
        h.Word(p.Material.Count); foreach (int c in p.Material) h.Word(c);
        foreach (int c in new[] { p.Bg, p.Dark, p.Light, p.Accent, p.Fog }) h.Word(c);
        h.Int(p.Avg); h.Str(p.Source);
        return h.Hex();
    }

    static string PiecesHash(EnvCollected c)
    {
        var h = new StateHash();
        h.Word(c.Pieces.Count);
        foreach (var p in c.Pieces)
        {
            h.Str(p.Key); h.Word(p.W); h.Word(p.H); h.Word(p.Area); h.Word(p.Opaque); RenderHash.F64(h, p.Fill);
            h.Word(p.BBox.X); h.Word(p.BBox.Y); h.Word(p.BBox.W); h.Word(p.BBox.H);
            h.Bool(p.FlatTop); h.Str(p.Cls); h.Bool(p.Steel); h.Word(p.Count); h.Bool(p.Excluded);
        }
        h.Word(c.Decor.Count);
        return h.Hex();
    }

    static string Str(JsonElement row, string name) =>
        row.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v.ToString() : "null";

    static List<string> Check(JsonElement row, StyleManager styles, Dictionary<string, EnvironmentLayout.StyleIndexEntry>? index)
    {
        var bad = new List<string>();
        void Is(string what, string got, string? want) { if (got != want) bad.Add(what); }
        var level = RenderHash.Build(row, styles);
        string id = row.GetProperty("id").GetString()!;
        var profile = EnvProfile.ForLevel(level, url => File.Exists(Path.Combine(OracleData.RepoRoot, "web", url)) ? File.ReadAllText(Path.Combine(OracleData.RepoRoot, "web", url)) : null);
        var ctx = EnvironmentLayout.LevelContext(level, id, profile);
        Is("gallery", EnvironmentLayout.GalleryKey(ctx), row.GetProperty("gallery").GetString());
        var gctx = EnvironmentLayout.GalleryContext(ctx, styles, index);
        Is("sheet", RenderHash.Hex(h => { h.Word(gctx.Width); h.Word(gctx.Height); h.Bytes(gctx.GroundImage!); }), row.GetProperty("sheet").GetString());
        var room = EnvGen.CanonicalRoom(EnvironmentLayout.PxPerMetre);
        Is("canonical room", RoomHash(room), row.GetProperty("canonicalRoom").GetString());
        var palette = EnvGen.DerivePalette(gctx);
        Is("palette", PaletteHash(palette), row.GetProperty("palette").GetString());
        var wallpaper = EnvironmentLayout.GalleryWallpaper(gctx, styles);
        Is("gallery wallpaper", wallpaper != null ? wallpaper.Key + ":" + wallpaper.Kind : "null", Str(row, "galleryWallpaper"));
        var collected = EnvGen.CollectPieces(gctx);
        Is("pieces", PiecesHash(collected), row.GetProperty("pieces").GetString());
        Is("mode", EnvGen.ChooseMode(collected.Pieces, gctx.Profile), row.GetProperty("mode").GetString());
        int fog = 0;
        foreach (string name in EnvironmentLayout.FirstPlanes)
        {
            var ambient = EnvGen.Build(gctx, new EnvBuildOptions { Room = room, Palette = palette, Wallpaper = wallpaper, Full = false, SmoothFog = true }, new[] { name });
            fog = ambient.Fog;
            Is("fog " + name, RenderHash.Hex(h => Bitmap(h, ambient.Planes[name])), row.GetProperty("fog").GetProperty(name).GetString());
        }
        Is("fog colour", fog.ToString(), row.GetProperty("fogColour").ToString());
        foreach (string name in EnvGen.PlaneNames(room))
        {
            if (name == "backdrop") continue;
            var built = EnvGen.Build(gctx, new EnvBuildOptions { Room = room, Palette = palette, Wallpaper = wallpaper, Full = true, Pieces = collected }, new[] { name });
            Is("collage " + name, RenderHash.Hex(h => Bitmap(h, built.Planes[name])), row.GetProperty("collage").GetProperty(name).GetString());
        }
        var props = EnvGen.Build(gctx, new EnvBuildOptions { Room = room, Palette = palette, Wallpaper = wallpaper, Full = true, Pieces = collected }, new[] { "props" }).Props ?? new();
        var wantProps = row.GetProperty("props").EnumerateArray().Select(e => e.GetString()).ToList();
        if (props.Count != wantProps.Count) bad.Add($"props: {props.Count}, oracle {wantProps.Count}");
        else for (int i = 0; i < props.Count; i++)
        {
            var p = props[i];
            string got = RenderHash.Hex(h =>
            {
                h.Word(p.Ring); RenderHash.F64(h, p.U); RenderHash.F64(h, p.R); RenderHash.F64(h, p.W); RenderHash.F64(h, p.H); h.Word(p.Depth); RenderHash.F64(h, p.Yaw);
                Bitmap(h, p.Bitmap);
                RenderHash.Geometry(h, EnvironmentLayout.PieceGeometry(p));
            });
            if (got != wantProps[i]) { bad.Add("prop " + i); break; }
        }
        var wp = EnvironmentLayout.LevelWallpaper(ctx);
        Is("wallpaper", wp != null ? wp.Key + ":" + wp.Kind : "null", Str(row, "wallpaper"));
        if (wp != null && wp.Kind == "prop")
        {
            var prop = EnvGen.PropBackdrop(ctx, wp.Image, palette.Bg);
            Is("prop backdrop", RenderHash.Hex(h => { h.Word(prop.K); Bitmap(h, prop.Bitmap); }), row.GetProperty("propBackdrop").GetString());
        }
        if (ctx.BackgroundImage != null)
        {
            var image = ctx.BackgroundImage;
            string got = RenderHash.Hex(h =>
            {
                var prop = EnvGen.PropBackdrop(ctx, image, palette.Bg);
                h.Word(prop.K); Bitmap(h, prop.Bitmap);
                var sky = EnvGen.Build(gctx, new EnvBuildOptions { Room = room, Palette = palette, Wallpaper = new EnvWallpaper(image, "x", "wallpaper"), Full = false, SmoothFog = true }, new[] { "sky" });
                h.Word(sky.Fog); Bitmap(h, sky.Planes["sky"]);
                foreach (string kind in new[] { "prop", "wallpaper" })
                {
                    var layer = room.Layers[2];
                    var bmp = new Bitmap(layer.Wall.W, layer.Wall.H);
                    EnvGen.DrawWallpaper(bmp, room, layer, new EnvWallpaper(image, "x", kind), fog);
                    Bitmap(h, bmp);
                }
            });
            Is("background paths", got, Str(row, "backgroundPaths"));
        }
        else Is("background paths", "null", Str(row, "backgroundPaths"));
        var gallery = new EnvironmentLayout.Gallery { Room = room, Palette = palette, Collected = collected, Fog = fog };
        Is("backdrop colour", EnvironmentLayout.BackdropFor(ctx, gallery, wp).Colour.ToString(), row.GetProperty("backdropColour").ToString());
        Is("scene colour", EnvironmentLayout.SceneColour(gallery).ToString(), row.GetProperty("sceneColour").ToString());
        var levelRoom = EnvGen.RoomFor(ctx.Width, ctx.Height, EnvironmentLayout.PxPerMetre);
        Is("room", RoomHash(levelRoom), row.GetProperty("room").GetString());
        var (yF, yC) = EnvironmentLayout.DesktopHeights(EnvironmentLayout.PxPerMetre);
        var layout = EnvironmentLayout.Layout(levelRoom, null, yF, yC);
        foreach (var plane in row.GetProperty("layout").EnumerateObject())
        {
            if (plane.Name == "_heights")
            {
                Is("heights", RenderHash.Hex(h => { RenderHash.F64(h, yF); RenderHash.F64(h, yC); RenderHash.F64(h, yC - yF); }), plane.Value.GetString());
                continue;
            }
            Is("layout " + plane.Name, RenderHash.Hex(h => RenderHash.Geometry(h, layout[plane.Name])), plane.Value.GetString());
        }
        return bad;
    }

    [Fact]
    public void TheRoomIsDrawnAsTheWebDrawsIt()
    {
        using var doc = OracleData.Load("envgen.json");
        Assert.SkipWhen(doc == null || !OracleData.HasAssets, "no oracle output or assets");
        Assert.Equal(EnvironmentLayout.PxPerMetre, doc!.RootElement.GetProperty("pxPerMetre").GetDouble());
        var rows = doc.RootElement.GetProperty("levels").EnumerateArray().Where(r => !r.TryGetProperty("error", out _)).Select(r => r.Clone()).ToList();
        var index = EnvironmentLayout.ReadStylesIndex(OracleData.Io);
        var failures = new ConcurrentBag<string>();
        Parallel.ForEach(rows, () => new StyleManager(OracleData.Io), (row, _, styles) =>
        {
            string id = row.GetProperty("id").GetString()!;
            try
            {
                var bad = Check(row, styles, index);
                if (bad.Count > 0) failures.Add($"{id} ({row.GetProperty("theme")}): {string.Join(", ", bad.Take(8))}");
            }
            catch (Exception e) { failures.Add($"{id}: {e}"); }
            return styles;
        }, _ => { });
        Assert.True(failures.IsEmpty, $"{rows.Count} levels; {failures.Count} differ:\n" + string.Join("\n", failures.Take(20)));
    }

    [Fact]
    public void MathIsV8s()
    {
        using var doc = OracleData.Load("envgen.json");
        Assert.SkipWhen(doc == null, "no oracle output");
        var h = new StateHash();
        uint s = 7;
        double Rnd() { s = unchecked(s * 1103515245u + 12345u); return s / 4294967296.0; }
        for (int i = 0; i < 100000; i++)
        {
            double x = (Rnd() * 2 - 1) * (i % 3 == 0 ? 8 : i % 3 == 1 ? 70 : 1e4);
            double e = -Rnd() * 60 * (i % 2 != 0 ? 1 : 0.05);
            double a = Rnd() * 3000 - 1500, b = Rnd() * 3000;
            RenderHash.F64(h, V8Math.Sin(x)); RenderHash.F64(h, V8Math.Cos(x)); RenderHash.F64(h, V8Math.Exp(e)); RenderHash.F64(h, V8Math.Hypot(a, b));
        }
        Assert.Equal(doc!.RootElement.GetProperty("math").GetString(), h.Hex());
    }
}
