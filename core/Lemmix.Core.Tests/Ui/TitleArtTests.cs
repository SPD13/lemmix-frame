using System.IO.Compression;
using Lemmix.Engine;
using Lemmix.Io;
using Lemmix.Tests.Oracle;
using Lemmix.Ui;

namespace Lemmix.Tests.Ui;

// The lobby's NeoLemmix art (TitleArt, MenuFont, TitleScroller) over the installed gfx/menu.
// TITLE_DUMP=<dir> writes the screen, the signs and a scroller frame there as PNGs, to look at.
public class TitleArtTests
{
    static TitleArt Art()
    {
        var art = TitleArt.Load(OracleData.Io);
        Dump(art);
        return art;
    }

    static bool HasMenu => OracleData.HasAssets && OracleData.Io.Image(TitleArt.Dir + "sign_play.png") != null;

    // NeoLemmix's releases draw their signs differently (flat boards, or glossy ones with a
    // gradient and two-line lettering): the assets' own, and TITLE_ALT_MENU=<a gfx/menu folder>
    // from another release when it is set
    sealed class MenuFolder : IFileSource
    {
        readonly string _dir;
        public MenuFolder(string dir) { _dir = dir; }
        string? P(string path) => path.StartsWith(TitleArt.Dir) && File.Exists(Path.Combine(_dir, path[TitleArt.Dir.Length..])) ? Path.Combine(_dir, path[TitleArt.Dir.Length..]) : null;
        public string? Text(string path) => P(path) is { } f ? File.ReadAllText(f) : null;
        public byte[]? Bytes(string path) => P(path) is { } f ? File.ReadAllBytes(f) : null;
        public Bitmap? Image(string path) => Bytes(path) is { } b ? Png.Decode(b) : null;
    }

    static IEnumerable<(string Name, IFileSource Io)> Variants()
    {
        yield return ("assets", OracleData.Io);
        if (Environment.GetEnvironmentVariable("TITLE_ALT_MENU") is { Length: > 0 } alt && Directory.Exists(alt)) yield return ("alt", new MenuFolder(alt));
    }

    // light or grey pixels with no colour (lettering, the plate, the gears) on the board's inside,
    // outside the given boxes and away from the lemming
    static int Lettering(Bitmap b, params (int X, int Y, int W, int H)[] boxes)
    {
        var (x0, y0, x1, y1) = TitleArt.Inside(b);
        int n = 0;
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                if (boxes.Any(r => x >= r.X - 1 && x <= r.X + r.W && y >= r.Y - 1 && y <= r.Y + r.H)) continue;
                // (the inside's corners: a glossy board's corner glint, the board's own)
                if ((x < x0 + 3 || x >= x1 - 3) && (y < y0 + 3 || y >= y1 - 3)) continue;
                int i = (y * b.Width + x) * 4;
                int mx = Math.Max(b.Data[i], Math.Max(b.Data[i + 1], b.Data[i + 2])), mn = Math.Min(b.Data[i], Math.Min(b.Data[i + 1], b.Data[i + 2]));
                if (mx - mn < 30 && mx >= 120) n++;
            }
        return n;
    }

    // the plate's greys (35..107, r = g = b) anywhere in the top-left third
    static int PlatePixels(Bitmap b)
    {
        int n = 0;
        for (int y = 0; y < b.Height * 6 / 10; y++)
            for (int x = 0; x < b.Width / 3; x++)
            {
                int i = (y * b.Width + x) * 4;
                byte r = b.Data[i], g = b.Data[i + 1], bl = b.Data[i + 2];
                if (b.Data[i + 3] != 0 && r == g && g == bl && r >= 30 && r <= 110) n++;
            }
        return n;
    }

    [Fact]
    public void TheScreenAndTheSignsAreBuiltFromTheMenuGraphics()
    {
        Assert.SkipWhen(!HasMenu, "no NeoLemmix menu graphics");
        var art = Art();
        Assert.True(art.Ok);
        Assert.Equal(TitleArt.ScreenW, art.Screen!.Width);
        Assert.Equal(TitleArt.ScreenH, art.Screen.Height);
        // the logo's middle is drawn over the background: not the tile's pixel there
        var bg = OracleData.Io.Image(TitleArt.Dir + "background.png")!;
        int li = (TitleArt.LogoCenterY * TitleArt.ScreenW + TitleArt.ScreenW / 2) * 4;
        int bi = ((TitleArt.LogoCenterY % bg.Height) * bg.Width + (TitleArt.ScreenW / 2) % bg.Width) * 4;
        Assert.NotEqual(bg.Data.AsSpan(bi, 4).ToArray(), art.Screen.Data.AsSpan(li, 4).ToArray());
        // the screen's bottom-right corner is the tile, repeated
        int cx = TitleArt.ScreenW - 1, cy = TitleArt.ScreenH - 1;
        Assert.Equal(bg.Data.AsSpan(((cy % bg.Height) * bg.Width + cx % bg.Width) * 4, 4).ToArray(), art.Screen.Data.AsSpan((cy * TitleArt.ScreenW + cx) * 4, 4).ToArray());
    }

    [Fact]
    public void TheKeyCapsAreTakenOffTheSigns()
    {
        Assert.SkipWhen(!HasMenu, "no NeoLemmix menu graphics");
        foreach (var (variant, io) in Variants())
            foreach (var name in new[] { "sign_play.png", "sign_config.png", "sign_quit.png", "sign_level_select.png" })
            {
                var sign = io.Image(TitleArt.Dir + name)!;
                Assert.True(PlatePixels(sign) > 50, variant + " " + name + " has its key cap");
                var bare = TitleArt.RemoveKeyCap(sign);
                Assert.True(PlatePixels(bare) == 0, variant + " " + name + ": plate left " + PlatePixels(bare));
                Assert.Equal(sign.Width, bare.Width);
                Dump(variant + "-bare-" + name, bare);
            }
    }

    [Fact]
    public void TheVrSignIsClearedForAGearAndAHeadset()
    {
        Assert.SkipWhen(!HasMenu, "no NeoLemmix menu graphics");
        foreach (var (variant, io) in Variants())
        {
            var config = io.Image(TitleArt.Dir + "sign_config.png")!;
            var vr = TitleArt.VrSign(TitleArt.RemoveKeyCap(config));
            Dump(variant + "-vr.png", vr);
            // the headset's visor, which the sign's own art has nowhere
            int visor = 0;
            for (int i = 0; i < vr.Data.Length; i += 4)
                if (vr.Data[i] == 0x30 && vr.Data[i + 1] == 0x30 && vr.Data[i + 2] == 0x30) visor++;
            Assert.True(visor > 50, variant + ": the headset's visor " + visor);
            // the sign's own gears and note gone: no grey or light left on the board but the drawn pictures
            var (gear, headset) = TitleArt.VrPictures(vr);
            Assert.True(Lettering(config) > 100, variant + ": the art has its pictures");
            Assert.True(Lettering(vr, gear, headset) == 0, variant + ": left of the old pictures " + Lettering(vr, gear, headset));
        }
    }

    [Fact]
    public void TheSetupSignIsClearedForADownload()
    {
        Assert.SkipWhen(!HasMenu, "no NeoLemmix menu graphics");
        foreach (var (variant, io) in Variants())
        {
            var levels = io.Image(TitleArt.Dir + "sign_level_select.png")!;
            var sign = TitleArt.SetupSign(TitleArt.RemoveKeyCap(levels));
            Dump(variant + "-setup.png", sign);
            Assert.True(Lettering(levels) > 100, variant + ": the art has its lettering");
            Assert.True(Lettering(sign, TitleArt.SetupPicture(sign)) == 0, variant + ": lettering left " + Lettering(sign, TitleArt.SetupPicture(sign)));
            // the download's white
            var at = TitleArt.SetupPicture(sign);
            int white = 0;
            for (int y = at.Y; y < at.Y + at.H; y++)
                for (int x = at.X; x < at.X + at.W; x++)
                    if (sign.Data[(y * sign.Width + x) * 4] == 255 && sign.Data[(y * sign.Width + x) * 4 + 2] == 255) white++;
            Assert.True(white > 150, variant + ": the download's white " + white);
        }
    }

    [Fact]
    public void AHoveredSignGlowsRoundItsEdge()
    {
        Assert.SkipWhen(!HasMenu, "no NeoLemmix menu graphics");
        var art = Art();
        var s = art.Play!;
        int m = TitleArt.SignMargin;
        Assert.Equal(s.Normal.Width, s.Hover.Width);
        // one pixel left of the sign's left edge, mid-height: clear when plain, the glow when lit
        int y = s.Normal.Height / 2, x = 0;
        while (s.Normal.Data[(y * s.Normal.Width + x + 1) * 4 + 3] == 0) x++;
        int i = (y * s.Normal.Width + x) * 4;
        Assert.Equal(0, s.Normal.Data[i + 3]);
        Assert.True(s.Hover.Data[i + 3] > 100, "glow alpha " + s.Hover.Data[i + 3]);
        Assert.Equal(0xA0, s.Hover.Data[i]);
        // the far corner of the margin stays clear
        Assert.Equal(0, s.Hover.Data[3]);
    }

    [Fact]
    public void TheMenuFontDrawsItsGlyphsAndSkipsSpaces()
    {
        Assert.SkipWhen(!HasMenu, "no NeoLemmix menu graphics");
        var font = Art().Font!;
        var b = font.Render("A B");
        Assert.Equal(3 * MenuFont.CharW, b.Width);
        static int Ink(Bitmap b, int x0) { int n = 0; for (int y = 0; y < b.Height; y++) for (int x = x0; x < x0 + MenuFont.CharW; x++) if (b.Data[(y * b.Width + x) * 4 + 3] > 0) n++; return n; }
        Assert.True(Ink(b, 0) > 20);
        Assert.Equal(0, Ink(b, MenuFont.CharW));
        Assert.True(Ink(b, 2 * MenuFont.CharW) > 20);
    }

    [Fact]
    public void TheScrollerSlidesALineInAndHoldsItCentred()
    {
        Assert.SkipWhen(!HasMenu, "no NeoLemmix menu graphics");
        var art = Art();
        var sc = new TitleScroller(art.Font!, art.ScrollerLemmings!, art.ScrollerSegment!, new[] { "HELLO", "", "WORLD" });
        Assert.Equal(TitleArt.ScrollerWidth + art.ScrollerLemmings!.Width, sc.Strip.Width);
        // the reel taller than NeoLemmix's 28 px, the text clear of its dashed edges
        Assert.Equal(art.ScrollerSegment!.Height + TitleScroller.ReelPad, sc.Strip.Height);
        Assert.Equal(TitleArt.ScrollerWidth, sc.TextPos);
        Assert.True(!sc.Update(0) && !sc.Update(5), "nothing before a whole step");
        Assert.True(sc.Update(12));
        Assert.Equal(TitleArt.ScrollerWidth - 2, sc.TextPos);
        Assert.Equal(2, sc.ReelFrame);
        // to the centre, then held there
        int centre = (TitleArt.ScrollerWidth - MenuFont.Width("HELLO")) / 2;
        while (sc.TextPos > centre) sc.Step();
        sc.Draw();
        Dump("scroller-centred.png", sc.Strip);
        // the text's rows: nothing in the top or bottom 5 rows, where the dashes run
        int left = sc.LemmingW + centre, first = int.MaxValue, last = -1;
        for (int y = 0; y < sc.Strip.Height; y++)
            for (int x = left; x < left + MenuFont.Width("HELLO"); x++)
            {
                int i = (y * sc.Strip.Width + x) * 4;
                if (sc.Strip.Data[i + 2] > 150 && sc.Strip.Data[i] < 120) { first = Math.Min(first, y); last = Math.Max(last, y); }
            }
        Assert.True(first >= 6 && last <= sc.Strip.Height - 7, $"text rows {first}..{last} of {sc.Strip.Height}");
        Assert.Equal(TitleScroller.TextFreezeBase + MenuFont.Width("HELLO") / TitleScroller.TextFreezeWidthDiv, sc.Freeze);
        int frame = sc.ReelFrame;
        sc.Step();
        Assert.Equal(frame, sc.ReelFrame);
        // off the left: the next line with text on it
        while (sc.TextIndex == 0) sc.Step();
        Assert.Equal(2, sc.TextIndex);
        Assert.Equal(TitleArt.ScrollerWidth, sc.TextPos);
        Dump("scroller.png", sc.Strip);
    }

    // ------------------------------------------------------------ TITLE_DUMP
    static void Dump(TitleArt art)
    {
        if (!art.Ok) return;
        Dump("screen.png", art.Screen!);
        Dump("play.png", art.Play!.Normal);
        Dump("play-hover.png", art.Play.Hover);
        Dump("vr.png", art.VrSettings!.Normal);
        Dump("vr-hover.png", art.VrSettings.Hover);
        Dump("quit.png", art.Quit!.Normal);
        Dump("setup.png", art.Setup!.Normal);
    }

    static void Dump(string name, Bitmap b)
    {
        var dir = Environment.GetEnvironmentVariable("TITLE_DUMP");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, name), EncodePng(b));
    }

    // an uncompressed-filter RGBA PNG, enough to look at
    static byte[] EncodePng(Bitmap b)
    {
        var raw = new MemoryStream();
        for (int y = 0; y < b.Height; y++) { raw.WriteByte(0); raw.Write(b.Data, y * b.Width * 4, b.Width * 4); }
        var z = new MemoryStream();
        using (var zs = new ZLibStream(z, CompressionLevel.Fastest, true)) raw.WriteTo(zs);
        var png = new MemoryStream();
        png.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        void Chunk(string type, byte[] data)
        {
            var t = System.Text.Encoding.ASCII.GetBytes(type);
            png.Write(BE((uint)data.Length));
            png.Write(t);
            png.Write(data);
            png.Write(BE(Crc(t.Concat(data).ToArray())));
        }
        var ihdr = new MemoryStream();
        ihdr.Write(BE((uint)b.Width)); ihdr.Write(BE((uint)b.Height));
        ihdr.Write(new byte[] { 8, 6, 0, 0, 0 });
        Chunk("IHDR", ihdr.ToArray());
        Chunk("IDAT", z.ToArray());
        Chunk("IEND", Array.Empty<byte>());
        return png.ToArray();
    }

    static byte[] BE(uint v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };

    static uint Crc(byte[] data)
    {
        uint c = 0xFFFFFFFF;
        foreach (byte x in data)
        {
            c ^= x;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        }
        return c ^ 0xFFFFFFFF;
    }
}
