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
        foreach (var name in new[] { "sign_play.png", "sign_config.png", "sign_quit.png" })
        {
            var sign = OracleData.Io.Image(TitleArt.Dir + name)!;
            Assert.True(PlatePixels(sign) > 50, name + " has its key cap");
            var bare = TitleArt.RemoveKeyCap(sign);
            Assert.Equal(0, PlatePixels(bare));
            Assert.Equal(sign.Width, bare.Width);
            // the board's colour fills the corner where the plate was
            uint board = TitleArt.BoardColor(sign);
            int i = (36 * bare.Width + 20) * 4;
            Assert.Equal(board, (uint)(bare.Data[i] << 16 | bare.Data[i + 1] << 8 | bare.Data[i + 2]));
        }
    }

    [Fact]
    public void TheVrSignHasAHeadsetWhereTheNoteWas()
    {
        Assert.SkipWhen(!HasMenu, "no NeoLemmix menu graphics");
        var config = OracleData.Io.Image(TitleArt.Dir + "sign_config.png")!;
        var vr = TitleArt.VrSign(TitleArt.RemoveKeyCap(config));
        // the visor's dark grey, which neither the gear nor the note has
        int visor = 0;
        for (int i = 0; i < vr.Data.Length; i += 4)
            if (vr.Data[i] == 0x30 && vr.Data[i + 1] == 0x30 && vr.Data[i + 2] == 0x30) visor++;
        Assert.True(visor > 50, "the headset's visor: " + visor);
        // the staff's darker yellow is gone from the right half
        uint staff = 0xC6B925;
        for (int y = vr.Height * 36 / 87; y < vr.Height * 73 / 87; y++)
            for (int x = vr.Width / 2 + 5; x < vr.Width * 7 / 8; x++)
            {
                int i = (y * vr.Width + x) * 4;
                Assert.NotEqual(staff, (uint)(vr.Data[i] << 16 | vr.Data[i + 1] << 8 | vr.Data[i + 2]));
            }
        // the gear is still there: its light grey on the left half
        bool gear = false;
        for (int y = 40; y < 65 && !gear; y++)
            for (int x = 35; x < 60; x++)
                if (vr.Data[(y * vr.Width + x) * 4] == 192 && vr.Data[(y * vr.Width + x) * 4 + 1] == 192) gear = true;
        Assert.True(gear);
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
        Assert.Equal(TitleArt.ScrollerWidth, sc.TextPos);
        Assert.True(!sc.Update(0) && !sc.Update(5), "nothing before a whole step");
        Assert.True(sc.Update(12));
        Assert.Equal(TitleArt.ScrollerWidth - 2, sc.TextPos);
        Assert.Equal(2, sc.ReelFrame);
        // to the centre, then held there
        int centre = (TitleArt.ScrollerWidth - MenuFont.Width("HELLO")) / 2;
        while (sc.TextPos > centre) sc.Step();
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
