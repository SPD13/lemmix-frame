using Lemmix.Engine;
using Lemmix.Io;

namespace Lemmix.Ui;

// LemMenuFont.pas TMenuFont: gfx/menu/menu_font.png, one strip of 16 x 19 glyphs for '!' to '~';
// a space (and anything else) is a blank cell.
public sealed class MenuFont
{
    public const int CharW = 16, CharH = 19;
    readonly Bitmap _strip;

    public MenuFont(Bitmap strip) { _strip = strip; }

    public static int Width(string s) => s.Length * CharW;

    /** One line of text, drawn over `dst` (blended) with its top-left at (x, y). */
    public void Draw(Bitmap dst, string s, int x, int y)
    {
        int glyphs = _strip.Width / CharW;
        for (int i = 0; i < s.Length; i++)
        {
            int g = s[i] - '!';
            if (g >= 0 && g < glyphs) TitleArt.Blend(_strip, dst, x + i * CharW, y, g * CharW, 0, CharW, CharH);
        }
    }

    /** DrawTextCentered: the line centred across `dst`. */
    public void DrawCentered(Bitmap dst, string s, int y) => Draw(dst, s, (dst.Width - Width(s)) / 2, y);

    /** The line on its own transparent bitmap. */
    public Bitmap Render(string s)
    {
        var b = new Bitmap(System.Math.Max(1, Width(s)), CharH);
        Draw(b, s, 0, 0);
        return b;
    }
}

// NeoLemmix's title screen (GameMenuScreen.pas, GameBaseMenuScreen.pas, data/title.nxmi), as the
// headset's lobby shows it: the 864 x 500 screen with background.png tiled over it and logo.png
// centred at the top, and the signs held up by lemmings - their key caps taken off (a headset
// has no F1 or Esc), the config sign's music note swapped for a headset, each with the glow
// MakeClickableImageAuto draws round a sign under the mouse. The art is NeoLemmix's own
// (gfx/menu, installed with NeoLemmix); Ok is false when it is not there.
public sealed class TitleArt
{
    // INTERNAL_SCREEN_WIDTH / HEIGHT, and title.nxmi's numbers
    public const int ScreenW = 864, ScreenH = 500;
    public const int LogoCenterY = 72;
    public const int CardsSpacingX = 160, CardsCenterY = 248;
    public const int FooterTextY = 364;
    public const int ScrollerTopY = 456, ScrollerLength = 40, ScrollerLemmingFrames = 16;
    public const int ScrollerWidth = ScrollerLength * MenuFont.CharW;
    // MakeClickableImageAuto's margin and its glow colours
    public const int SignMargin = 5;
    public const uint HoverColor = 0xA0A0A0, ClickColor = 0x404040;

    public const string Dir = StyleManager.AssetDir + "gfx/menu/";

    /** A sign as the screen shows it: padded by SignMargin, plain and lit (the hover's glow). */
    public sealed record Sign(Bitmap Normal, Bitmap Hover);

    public bool Ok;
    public Bitmap? Screen;              // the background tiled, the logo on it
    public MenuFont? Font;
    public Bitmap? ScrollerLemmings, ScrollerSegment;
    public Sign? Play, VrSettings, Quit;

    public static TitleArt Load(IFileSource io)
    {
        var art = new TitleArt();
        Bitmap? Get(string name) => io.Image(Dir + name);
        var bg = Get("background.png");
        var logo = Get("logo.png");
        var font = Get("menu_font.png");
        var play = Get("sign_play.png");
        var config = Get("sign_config.png");
        var quit = Get("sign_quit.png");
        art.ScrollerLemmings = Get("scroller_lemmings.png");
        art.ScrollerSegment = Get("scroller_segment.png");
        if (font != null) art.Font = new MenuFont(font);
        if (bg == null || logo == null || play == null || config == null || quit == null) return art;

        art.Screen = new Bitmap(ScreenW, ScreenH);
        // DrawBackground: the picture tiled from the top-left
        for (int y = 0; y < ScreenH; y += bg.Height)
            for (int x = 0; x < ScreenW; x += bg.Width)
                Blend(bg, art.Screen, x, y, 0, 0, bg.Width, bg.Height);
        // DrawLogo
        Blend(logo, art.Screen, (ScreenW - logo.Width) / 2, LogoCenterY - logo.Height / 2, 0, 0, logo.Width, logo.Height);

        art.Play = MakeSign(RemoveKeyCap(play));
        art.VrSettings = MakeSign(VrSign(RemoveKeyCap(config)));
        art.Quit = MakeSign(RemoveKeyCap(quit));
        art.Ok = true;
        return art;
    }

    // MakePosition(h, 0): a card's centre on the screen, h cards right of the middle
    public static (int X, int Y) CardCentre(float h) => (ScreenW / 2 + (int)System.MathF.Round(h * CardsSpacingX), CardsCenterY);

    // ------------------------------------------------------------ pixels
    static bool Opaque(byte[] d, int i) => d[i + 3] != 0;
    static bool Neutral(byte[] d, int i) => d[i] == d[i + 1] && d[i + 1] == d[i + 2];
    static uint Rgb(byte[] d, int i) => (uint)(d[i] << 16 | d[i + 1] << 8 | d[i + 2]);
    // a lemming's skin (the hands round the board): pink, never the board's red
    static bool Skin(byte[] d, int i) => Opaque(d, i) && d[i] >= 180 && d[i + 1] >= 100 && d[i] - d[i + 1] >= 50;

    static void Set(byte[] d, int i, uint rgb, byte a = 255)
    {
        d[i] = (byte)(rgb >> 16); d[i + 1] = (byte)(rgb >> 8); d[i + 2] = (byte)rgb; d[i + 3] = a;
    }

    /** `src`'s rectangle (sx, sy, w, h) blended over `dst` at (dx, dy), clipped to `dst`. */
    public static void Blend(Bitmap src, Bitmap dst, int dx, int dy, int sx, int sy, int w, int h)
    {
        for (int y = 0; y < h; y++)
        {
            int ty = dy + y, fy = sy + y;
            if (ty < 0 || ty >= dst.Height || fy < 0 || fy >= src.Height) continue;
            for (int x = 0; x < w; x++)
            {
                int tx = dx + x, fx = sx + x;
                if (tx < 0 || tx >= dst.Width || fx < 0 || fx >= src.Width) continue;
                Pixels.MergeOver(src.Data, (fy * src.Width + fx) * 4, dst.Data, (ty * dst.Width + tx) * 4);
            }
        }
    }

    /** The colour most of the sign's board is: its middle's commonest opaque colour. */
    public static uint BoardColor(Bitmap s)
    {
        var count = new Dictionary<uint, int>();
        for (int y = s.Height * 35 / 100; y < s.Height * 90 / 100; y++)
            for (int x = s.Width / 6; x < s.Width * 5 / 6; x++)
            {
                int i = (y * s.Width + x) * 4;
                if (!Opaque(s.Data, i)) continue;
                uint c = Rgb(s.Data, i);
                count[c] = count.GetValueOrDefault(c) + 1;
            }
        return count.Count == 0 ? 0 : count.MaxBy(kv => kv.Value).Key;
    }

    /**
     * The sign without its key cap (the grey plate with F1, F3, Esc in the board's top-left
     * corner): the plate's greys found in the top-left, and the box round them painted as the
     * board under it would be - nothing above the board's top edge, its black edge, the board's
     * colour - with a black outline kept where a hand meets what was the plate.
     */
    public static Bitmap RemoveKeyCap(Bitmap sign)
    {
        var s = sign.Clone();
        var d = s.Data;
        int w = s.Width, h = s.Height;
        int x0 = w, y0 = h, x1 = -1, y1 = -1;
        for (int y = 0; y < h * 6 / 10; y++)
            for (int x = 0; x < w / 3; x++)
            {
                int i = (y * w + x) * 4;
                if (!Opaque(d, i) || !Neutral(d, i) || d[i] < 30 || d[i] > 110) continue;
                x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y);
            }
        if (x1 < 0) return s;
        // the plate's own black outline
        x0 = Math.Max(0, x0 - 1); y0 = Math.Max(0, y0 - 1); x1 = Math.Min(w - 1, x1 + 1); y1 = Math.Min(h - 1, y1 + 1);
        uint board = BoardColor(s);
        // the board's top edge: just right of the plate, the black row with the board under it
        int edge = -1, cx = Math.Min(w - 1, x1 + 2);
        for (int y = 0; y + 1 < h && edge < 0; y++)
        {
            int i = (y * w + cx) * 4, below = ((y + 1) * w + cx) * 4;
            if (Opaque(d, i) && Rgb(d, i) == 0 && Opaque(d, below) && Rgb(d, below) == board) edge = y;
        }
        if (edge < 0) edge = y0;

        var replaced = new List<(int X, int Y)>();
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                int i = (y * w + x) * 4;
                if (!Opaque(d, i) || !Neutral(d, i)) continue;
                if (y < edge) Set(d, i, 0, 0);
                else Set(d, i, y == edge ? 0u : board);
                replaced.Add((x, y));
            }
        // where a hand now meets the board (or the air), its outline
        foreach (var (x, y) in replaced)
        {
            bool nextToHand = false;
            foreach (var (nx, ny) in new[] { (x - 1, y), (x + 1, y), (x, y - 1), (x, y + 1) })
                if (nx >= 0 && ny >= 0 && nx < w && ny < h && Skin(d, (ny * w + nx) * 4)) nextToHand = true;
            if (nextToHand) Set(d, (y * w + x) * 4, 0);
        }
        return s;
    }

    /**
     * The config sign made the VR settings' one: the gear stays, the music note and its staff
     * (the board's right half) go, and a headset takes their place, in the gear's greys.
     */
    public static Bitmap VrSign(Bitmap config)
    {
        var s = config.Clone();
        int w = s.Width, h = s.Height;
        uint board = BoardColor(s);
        // the note's box, as a share of the 120 x 87 sign
        int nx0 = w * 63 / 120, nx1 = w * 105 / 120, ny0 = h * 36 / 87, ny1 = h * 73 / 87; // (the head's chin is above)
        for (int y = ny0; y < ny1; y++)
            for (int x = nx0; x < nx1; x++) Set(s.Data, (y * w + x) * 4, board);
        int k = Math.Max(1, w / 120);
        DrawHeadset(s, nx0 + (nx1 - nx0 - HeadsetW * k) / 2, h * 51 / 87 - HeadsetH * k / 2, k);
        return s;
    }

    // ---- a headset, front on, as a 36 x 18 picture: the body with its visor, the straps off its
    // sides, the nose's notch; black outline, the gear's greys (229 light, 206, 192, 150 shade)
    public const int HeadsetW = 36, HeadsetH = 18;

    static bool InHeadset(int x, int y)
    {
        if (x < 0 || y < 0 || x >= HeadsetW || y >= HeadsetH) return false;
        bool strap = y >= 5 && y <= 10;
        bool body = x >= 4 && x <= 31 && y <= 16;
        if (body)
        {
            // rounded corners, radius 3
            int cxp = x < 7 ? 7 : x > 28 ? 28 : x, cyp = y < 3 ? 3 : y > 13 ? 13 : y;
            int dx = x - cxp, dy = y - cyp;
            if (dx * dx + dy * dy > 10) body = false;
            // the nose's notch, up from the bottom edge
            float nx = (x - 17.5f) / 4.5f, ny = (y - 17f) / 6f;
            if (nx * nx + ny * ny <= 1) body = false;
        }
        return body || strap;
    }

    static uint HeadsetColor(int x, int y)
    {
        // the visor: a dark band across the front, its own outline, a glint along its top
        if (x >= 8 && x <= 27 && y >= 4 && y <= 10)
        {
            if (x == 8 || x == 27 || y == 4 || y == 10) return 0x000000;
            return y == 5 ? 0x515151u : 0x303030u;
        }
        if (x < 4 || x > 31) return y == 6 ? 0xC0C0C0u : 0x969696u; // the straps
        return y <= 1 ? 0xE5E5E5u : y == 2 ? 0xCECECEu : y >= 12 ? 0x969696u : 0xC0C0C0u;
    }

    static void DrawHeadset(Bitmap b, int ox, int oy, int k)
    {
        for (int y = 0; y < HeadsetH; y++)
            for (int x = 0; x < HeadsetW; x++)
            {
                if (!InHeadset(x, y)) continue;
                bool edge = !InHeadset(x - 1, y) || !InHeadset(x + 1, y) || !InHeadset(x, y - 1) || !InHeadset(x, y + 1);
                uint c = edge ? 0 : HeadsetColor(x, y);
                for (int sy = 0; sy < k; sy++)
                    for (int sx = 0; sx < k; sx++)
                    {
                        int px = ox + x * k + sx, py = oy + y * k + sy;
                        if (px >= 0 && py >= 0 && px < b.Width && py < b.Height) Set(b.Data, (py * b.Width + px) * 4, c);
                    }
            }
    }

    // ------------------------------------------------------------ the glow
    /**
     * MakeClickableImageAuto: the sign on a canvas SignMargin larger all round, over a glow of
     * `rgb` that spreads its outline out by one pixel per pass (each pass a weighted sum of the
     * eight neighbours' alpha: straights twice, diagonals once, x 2 / 12); rgb null: no glow.
     */
    public static Bitmap Padded(Bitmap sign, uint? rgb, int margin = SignMargin)
    {
        int w = sign.Width + margin * 2, h = sign.Height + margin * 2;
        var outp = new Bitmap(w, h);
        if (rgb is uint color)
        {
            var a = new int[w * h];
            for (int y = 0; y < sign.Height; y++)
                for (int x = 0; x < sign.Width; x++) a[(y + margin) * w + x + margin] = sign.Data[(y * sign.Width + x) * 4 + 3];
            int At(int[] src, int x, int y) => x < 0 || y < 0 || x >= w || y >= h ? 0 : src[y * w + x];
            for (int n = 0; n < margin; n++)
            {
                var next = new int[w * h];
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int sum = At(a, x - 1, y - 1) + At(a, x + 1, y - 1) + At(a, x - 1, y + 1) + At(a, x + 1, y + 1)
                                + (At(a, x - 1, y) + At(a, x + 1, y) + At(a, x, y - 1) + At(a, x, y + 1)) * 2;
                        // Delphi's Round: halves to even
                        next[y * w + x] = Math.Min((int)Math.Round(sum / 12.0 * 2, MidpointRounding.ToEven), 255);
                    }
                a = next;
            }
            for (int i = 0; i < w * h; i++) Set(outp.Data, i * 4, color, (byte)a[i]);
        }
        Blend(sign, outp, margin, margin, 0, 0, sign.Width, sign.Height);
        return outp;
    }

    static Sign MakeSign(Bitmap s) => new(Padded(s, null), Padded(s, HoverColor));
}

// GameMenuScreen.pas's scroller: a reel of scroller_segment.png turned by a worker lemming at
// each end (scroller_lemmings.png: 16 frames, the left lemming in the left half), the lines of
// text sliding across it right to left, each held still for a while when it is centred. A step
// every 6 ms; Strip is the whole row (lemming, reel, lemming), redrawn after a step.
public sealed class TitleScroller
{
    public const int MsPerUpdate = 6;
    public const int TextFreezeBase = 333, TextFreezeWidthDiv = 3;
    // after a long stop (the lobby hidden), the reel picks up where it was rather than catching up
    const int MaxStepsPerUpdate = 50;

    readonly MenuFont _font;
    readonly Bitmap _lemmings, _segment;
    readonly List<string> _lines;
    readonly Bitmap _reel;
    public readonly int LemmingW, LemmingH;
    public int ReelFrame, TextPos, TextIndex = -1, Freeze;
    public bool Disabled;
    public Bitmap Text = new(1, 1);
    public readonly Bitmap Strip;
    double _last = double.NaN;

    public TitleScroller(MenuFont font, Bitmap lemmings, Bitmap segment, IEnumerable<string> lines)
    {
        _font = font;
        _lemmings = lemmings;
        _segment = segment;
        _lines = lines.ToList();
        LemmingW = lemmings.Width / 2;
        LemmingH = lemmings.Height / TitleArt.ScrollerLemmingFrames;
        // LoadScrollerGraphics: the segment tiled one segment past the reel's length
        _reel = new Bitmap(TitleArt.ScrollerWidth + segment.Width, segment.Height);
        for (int x = 0; x < _reel.Width; x += segment.Width) TitleArt.Blend(segment, _reel, x, 0, 0, 0, segment.Width, segment.Height);
        Strip = new Bitmap(TitleArt.ScrollerWidth + LemmingW * 2, Math.Max(segment.Height, LemmingH));
        if (_lines.Count(l => l.Trim() != "") > 0) NextText();
        else Disabled = true;
        Draw();
    }

    /** UpdateReel: the steps due since the last call; true when the strip changed. */
    public bool Update(double nowMs)
    {
        if (double.IsNaN(_last)) { _last = nowMs; return false; }
        int steps = (int)((nowMs - _last) / MsPerUpdate);
        if (steps <= 0) return false;
        _last += steps * (double)MsPerUpdate;
        if (steps > MaxStepsPerUpdate) { steps = MaxStepsPerUpdate; _last = nowMs; }
        if (Disabled) return false;
        for (int n = 0; n < steps && !Disabled; n++) Step();
        Draw();
        return true;
    }

    /** UpdateReelIteration (no forced direction: the mouse cannot grab it here). */
    public void Step()
    {
        if (Freeze > 0) { Freeze--; return; }
        ReelFrame++;
        TextPos--;
        if (Text.Width <= TitleArt.ScrollerWidth && TextPos == (TitleArt.ScrollerWidth - Text.Width) / 2)
            Freeze = TextFreezeBase + Text.Width / TextFreezeWidthDiv;
        if (TextPos <= -Text.Width || TextPos >= TitleArt.ScrollerWidth) NextText();
    }

    // PrepareNextReelText: the next line with something on it, entering at the right
    void NextText()
    {
        string? s = null;
        for (int i = 1; i <= _lines.Count; i++)
        {
            int real = (TextIndex + i) % _lines.Count;
            if (_lines[real].Trim() == "") continue;
            // (NeoLemmix stops at a list of one line; here that line goes round again)
            s = _lines[real].Trim();
            TextIndex = real;
            break;
        }
        if (s == null) { Disabled = true; return; }
        Text = new Bitmap(MenuFont.Width(s), MenuFont.CharH + 4);
        _font.Draw(Text, s, 0, 4);
        TextPos = TitleArt.ScrollerWidth;
    }

    // DrawScroller: the reel, the text over it clipped to the reel, the two lemmings
    void Draw()
    {
        Array.Clear(Strip.Data);
        int left = LemmingW;
        TitleArt.Blend(_reel, Strip, left, 0, ReelFrame % _segment.Width, 0, TitleArt.ScrollerWidth, _reel.Height);
        int sx = Math.Max(0, -TextPos), dx = left + Math.Max(0, TextPos);
        int tw = Math.Min(Text.Width - sx, TitleArt.ScrollerWidth - Math.Max(0, TextPos));
        if (tw > 0) TitleArt.Blend(Text, Strip, dx, 0, sx, 0, tw, Text.Height);
        int frame = ReelFrame / 4 % TitleArt.ScrollerLemmingFrames;
        TitleArt.Blend(_lemmings, Strip, 0, 0, 0, frame * LemmingH, LemmingW, LemmingH);
        TitleArt.Blend(_lemmings, Strip, left + TitleArt.ScrollerWidth, 0, LemmingW, frame * LemmingH, LemmingW, LemmingH);
    }
}
