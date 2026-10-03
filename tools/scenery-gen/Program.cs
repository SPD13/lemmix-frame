using Lemmix.Engine;
using Lemmix.Io;
using Lemmix.Render;
using Lemmix.SceneryGen;

// scenery-gen: a gallery's scenery (core/Lemmix.Core/Render/Scenery.cs) made from its style's pieces.
//   scenery-gen gen <style> [--out <dir>]      the strips, the ground and scenery.json, by default into
//                                              <assets>/3d/env/<style>/scenery/, and preview.png beside them
//   scenery-gen sheet <style> <out.png>        a contact sheet of the style's terrain pieces
// The asset root is WEB_ASSETS (default ../LemmingsJS from lemmix-frame).
string assets = Environment.GetEnvironmentVariable("WEB_ASSETS") ?? Path.GetFullPath("../LemmingsJS");
var io = new DiskFileSource(assets);
var styles = new StyleManager(io);
string cmd = args.Length > 0 ? args[0] : "";

List<string> PieceNames(string style)
{
    var dir = Path.Combine(assets, "neolemmix/styles", style, "terrain");
    return Directory.GetFiles(dir, "*.png").Select(f => Path.GetFileNameWithoutExtension(f)!).OrderBy(n => n, StringComparer.Ordinal).ToList();
}

if (cmd == "sheet" && args.Length >= 3)
{
    string style = args[1];
    var pics = PieceNames(style).Select(n => (n, styles.Terrain(style, n)?.Base?.Image)).Where(p => p.Item2 != null).ToList();
    int cell = 132, cols = 8, rows = (pics.Count + cols - 1) / cols;
    var sheet = new Bitmap(cols * cell, rows * cell);
    sheet.Words().Fill(0xff302828);
    for (int i = 0; i < pics.Count; i++)
    {
        var img = pics[i].Item2!;
        int f = Math.Max(1, (int)Math.Ceiling(Math.Max(img.Width, img.Height) / 128.0));
        var s = f > 1 ? EnvGen.Shrink(img, f) : img;
        Pixels.Blit(sheet, (i % cols) * cell + 2, (i / cols) * cell + 2, s, 0, 0, s.Width, s.Height, Pixels.CombineGadget);
    }
    PngWriter.Write(args[2], sheet);
    return 0;
}

if (cmd == "gen" && args.Length >= 2)
{
    string style = args[1];
    int o = Array.IndexOf(args, "--out");
    string outDir = o > 0 ? args[o + 1] : Path.Combine(assets, SceneryManifest.DirFor(style));
    Directory.CreateDirectory(outDir);
    var set = PieceSet.Load(styles, style, PieceNames(style));
    Console.WriteLine($"[scenery] {style}: pieces");
    set.Print(Console.Out);

    // the colours: the gallery's palette (envgen's, from every piece of the style) - the haze
    // warmed toward its brightest material, the sky over it darkening to the background's
    var gctx = EnvironmentLayout.GalleryContext(new EnvContext { ThemeName = style }, styles, EnvironmentLayout.ReadStylesIndex(io));
    var pal = EnvGen.DerivePalette(gctx);
    int bright = pal.Material.OrderByDescending(EnvGen.Luma).First();
    int horizon = EnvGen.Mix(pal.Fog, bright, 0.36);
    string Hex(int c) => "#" + c.ToString("x6");
    var m = new SceneryManifest
    {
        Style = style, Generator = "tools/scenery-gen", TexelsRound = Layers.Round,
        Sky = new ScenerySky
        {
            Horizon = Hex(horizon), High = Hex(EnvGen.Mix(horizon, pal.Bg, 0.62)),
            Zenith = Hex(EnvGen.Scale(pal.Bg, 0.45)), Below = Hex(EnvGen.Scale(horizon, 0.8)),
        },
        Fog = new SceneryFog(),
        Ground = new SceneryGround { File = "ground.png", TileM = 512 * 0.0025, Grade = 0.4, Desat = 0.35 },
    };
    PngWriter.Write(Path.Combine(outDir, "ground.png"), Layers.Ground(set, style, 512, 0.72));
    int n = 0;
    foreach (var spec in Layers.Recipe)
    {
        var bmp = Layers.Build(spec, set, style);
        string file = $"layer-{++n}-{spec.Name}.png";
        PngWriter.Write(Path.Combine(outDir, file), bmp);
        double heightM = bmp.Height * Layers.Texel(spec.RadiusM);
        m.Layers.Add(new SceneryLayer
        {
            Name = spec.Name, File = file, RadiusM = spec.RadiusM, BottomM = spec.BottomM,
            HeightM = Math.Round(heightM, 3), Grade = spec.Grade, Desat = spec.Desat, FadeTop = spec.FadeTop,
        });
        Console.WriteLine($"[scenery] {file} {bmp.Width}x{bmp.Height} r={spec.RadiusM} m");
    }
    File.WriteAllText(Path.Combine(outDir, SceneryManifest.FileName), m.ToJson());
    var preview = Preview.Render(m, outDir);
    PngWriter.Write(Path.Combine(outDir, "preview.png"), preview);
    Console.WriteLine($"[scenery] wrote {outDir}");
    return 0;
}

Console.Error.WriteLine("usage: scenery-gen gen <style> [--out <dir>] | sheet <style> <out.png>");
return 1;
