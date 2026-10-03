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
// has no F1 or Esc), the config and levels signs' boards cleared and drawn on again - a gear and
// a headset (the VR settings), a download (the setup: downloads and installs) - each with the glow
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
    public int LogoBottom;              // the screen row under the logo's foot (the app's subtitle goes below)
    public MenuFont? Font;
    public Bitmap? ScrollerLemmings, ScrollerSegment;
    public Sign? Play, Setup, VrSettings, Quit;

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
        var levels = Get("sign_level_select.png");
        art.ScrollerLemmings = Get("scroller_lemmings.png");
        art.ScrollerSegment = Get("scroller_segment.png");
        if (font != null) art.Font = new MenuFont(font);
        if (bg == null || logo == null || play == null || config == null || quit == null || levels == null) return art;

        art.Screen = new Bitmap(ScreenW, ScreenH);
        // DrawBackground: the picture tiled from the top-left
        for (int y = 0; y < ScreenH; y += bg.Height)
            for (int x = 0; x < ScreenW; x += bg.Width)
                Blend(bg, art.Screen, x, y, 0, 0, bg.Width, bg.Height);
        // DrawLogo (the wordmark alone: the app puts its own subtitle under it)
        logo = WithoutSubtitle(logo);
        Blend(logo, art.Screen, (ScreenW - logo.Width) / 2, LogoCenterY - logo.Height / 2, 0, 0, logo.Width, logo.Height);
        art.LogoBottom = LogoCenterY - logo.Height / 2 + logo.Height;

        art.Play = MakeSign(RemoveKeyCap(play));
        art.Setup = MakeSign(SetupSign(RemoveKeyCap(levels)));
        art.VrSettings = MakeSign(VrSign(RemoveKeyCap(config)));
        art.Quit = MakeSign(RemoveKeyCap(quit));
        art.Ok = true;
        return art;
    }

    /**
     * The logo without a subtitle line of its own (the older releases' COMMUNITY EDITION under the
     * wordmark): cut at the first row in its lower half with nothing solid on it (the wordmark's
     * soft shadow may run on into the subtitle) that has solid picture under it.
     */
    public static Bitmap WithoutSubtitle(Bitmap logo)
    {
        int w = logo.Width, h = logo.Height;
        bool Clear(int y) { for (int x = 0; x < w; x++) if (logo.Data[(y * w + x) * 4 + 3] >= 128) return false; return true; }
        for (int y = h / 2; y < h; y++)
        {
            if (!Clear(y)) continue;
            for (int below = y + 1; below < h; below++)
                if (!Clear(below)) return logo.Crop(0, 0, w, y);
            return logo;
        }
        return logo;
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

    // ---- the board under the art
    // the board's inside (clear of its frame), as a share of the 120 x 87 sign: the lettering and
    // the pictures are within it, in every NeoLemmix release's signs (flat or glossy)
    public static (int X0, int Y0, int X1, int Y1) Inside(Bitmap s) => (s.Width * 9 / 120, s.Height * 30 / 87, s.Width * 111 / 120, s.Height * 75 / 87);

    static int Sat(byte[] d, int i) => Math.Max(d[i], Math.Max(d[i + 1], d[i + 2])) - Math.Min(d[i], Math.Min(d[i + 1], d[i + 2]));
    // a lemming (its skin, light or shaded): reddish, never a board's red, yellow or green
    static bool Lemming(byte[] d, int i) => Opaque(d, i) && d[i] >= 120 && d[i + 1] >= 60 && d[i] - d[i + 1] >= 45 && d[i] - d[i + 2] >= 25;
    static int Dist(byte[] d, int i, (int R, int G, int B) c) => Math.Abs(d[i] - c.R) + Math.Abs(d[i + 1] - c.G) + Math.Abs(d[i + 2] - c.B);

    // the lemming's pixels and two round them (its outline, its shading's edge): never the board's
    static bool[] Protected(Bitmap s)
    {
        int w = s.Width, h = s.Height;
        var p = new bool[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                if (!Lemming(s.Data, (y * w + x) * 4)) continue;
                for (int dy = -2; dy <= 2; dy++)
                    for (int dx = -2; dx <= 2; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx >= 0 && ny >= 0 && nx < w && ny < h) p[ny * w + nx] = true;
                    }
            }
        return p;
    }

    // a row's board colour inside the board: the median of its board-like pixels (saturated, not the lemming)
    static (int R, int G, int B)? RowModel(Bitmap s, int y, bool[] prot, bool[]? skip = null)
    {
        var (x0, _, x1, _) = Inside(s);
        var r = new List<int>(); var g = new List<int>(); var b = new List<int>();
        for (int x = x0; x < x1; x++)
        {
            int k = y * s.Width + x, i = k * 4;
            if (!Opaque(s.Data, i) || prot[k] || (skip != null && skip[k]) || Sat(s.Data, i) < 40) continue;
            r.Add(s.Data[i]); g.Add(s.Data[i + 1]); b.Add(s.Data[i + 2]);
        }
        if (r.Count == 0) return null;
        r.Sort(); g.Sort(); b.Sort();
        return (r[r.Count / 2], g[g.Count / 2], b[b.Count / 2]);
    }

    // the rows' board colours down the inside, each the median of its own and its six nearest
    // rows' (a row under a lettering's shadow band reads darker than the board is; the board's own
    // gradient changes slowly down it)
    static (int R, int G, int B)?[] RowModels(Bitmap s, bool[] prot, bool[]? skip = null)
    {
        var (_, y0, _, y1) = Inside(s);
        var raw = new (int R, int G, int B)?[s.Height];
        for (int y = y0; y < y1; y++) raw[y] = RowModel(s, y, prot, skip);
        var smooth = new (int R, int G, int B)?[s.Height];
        for (int y = y0; y < y1; y++)
        {
            var near = new List<(int R, int G, int B)>();
            for (int n = Math.Max(y0, y - 3); n <= Math.Min(y1 - 1, y + 3); n++) if (raw[n] is { } m) near.Add(m);
            if (near.Count == 0) continue;
            int Med(Func<(int R, int G, int B), int> f) { var v = near.Select(f).OrderBy(c => c).ToList(); return v[v.Count / 2]; }
            smooth[y] = (Med(c => c.R), Med(c => c.G), Med(c => c.B));
        }
        return smooth;
    }

    /**
     * The board with nothing on it: whatever stands on its inside that is not the board (the
     * lettering, the pictures, their shadows and soft edges) found row by row against the row's
     * own colour, the lemming left alone, and painted again as the board round it - each pixel
     * the row's colour (a glossy board's gradient runs down it, not across), with the board's own
     * grain taken from the row's clean pixels.
     */
    public static Bitmap ClearBoard(Bitmap sign)
    {
        var s = sign.Clone();
        var d = s.Data;
        int w = s.Width;
        var (x0, y0, x1, y1) = Inside(s);
        var prot = Protected(s);
        var gone = new bool[w * s.Height];
        var models = RowModels(s, prot);
        for (int y = y0; y < y1; y++)
        {
            if (models[y] is not { } m) continue;
            for (int x = x0; x < x1; x++)
            {
                int k = y * w + x;
                if (!prot[k] && Opaque(d, k * 4) && Dist(d, k * 4, m) > 60) gone[k] = true;
            }
        }
        // and two pixels round what goes: the soft edges and shadows the lettering leaves on the board
        for (int pass = 0; pass < 2; pass++)
        {
            var grown = (bool[])gone.Clone();
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                {
                    int k = y * w + x;
                    if (!gone[k]) continue;
                    foreach (var (nx, ny) in new[] { (x - 1, y), (x + 1, y), (x, y - 1), (x, y + 1) })
                        if (nx >= x0 && ny >= y0 && nx < x1 && ny < y1 && !prot[ny * w + nx]) grown[ny * w + nx] = true;
                }
            gone = grown;
        }
        models = RowModels(s, prot, gone);
        for (int y = y0; y < y1; y++)
        {
            if (models[y] is not { } m) continue;
            var clean = new List<int>();
            for (int x = x0; x < x1; x++)
            {
                int k = y * w + x;
                if (!gone[k] && !prot[k] && Opaque(d, k * 4) && Dist(d, k * 4, m) <= 60) clean.Add(x);
            }
            for (int x = x0; x < x1; x++)
            {
                int k = y * w + x;
                if (!gone[k]) continue;
                // the grain: a clean pixel's difference from the row's colour, picked by place
                var g = m;
                if (clean.Count > 0) { int i = (y * w + clean[(int)((uint)(x * 73856093 ^ y * 19349663) % (uint)clean.Count)]) * 4; g = (d[i], d[i + 1], d[i + 2]); }
                int C(int cm, int cg) => Math.Clamp((int)MathF.Round(cm + (cg - cm) * 0.6f), 0, 255);
                Set(d, k * 4, (uint)(C(m.R, g.R) << 16 | C(m.G, g.G) << 8 | C(m.B, g.B)));
            }
        }
        return s;
    }

    /**
     * The sign without its key cap (the grey plate with F1, F3, Esc on the board's top-left
     * corner): the plate's greys found in the top-left, and what they cover painted as the
     * board's other corner is - its top-right, mirrored (the board, its rounded corner and
     * frame, the air round it) - or, where a lemming stands there, as the row goes on just right
     * of the plate (the board's top edge, the board), else the air; a black outline kept where a
     * hand meets what was the plate.
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
        // the plate's own black outline (two rows thick along its top)
        x0 = Math.Max(0, x0 - 1); y0 = Math.Max(0, y0 - 3); x1 = Math.Min(w - 1, x1 + 1); y1 = Math.Min(h - 1, y1 + 1);
        var src = sign.Data;
        var prot = Protected(sign);
        var replaced = new List<(int X, int Y)>();
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                int i = (y * w + x) * 4;
                // the plate: its greys, its label, its outline, its soft edge on the board
                if (!Opaque(d, i) || Sat(d, i) > 40 || Lemming(d, i)) continue;
                int mi = (y * w + (w - 1 - x)) * 4;
                if (!prot[y * w + (w - 1 - x)]) { d[i] = src[mi]; d[i + 1] = src[mi + 1]; d[i + 2] = src[mi + 2]; d[i + 3] = src[mi + 3]; }
                else
                {
                    // the row just right of the plate, if it is the board (or its edge) rather than a lemming
                    int ri = (y * w + Math.Min(w - 1, x1 + 2)) * 4;
                    if (Opaque(src, ri) && !prot[y * w + Math.Min(w - 1, x1 + 2)]) { d[i] = src[ri]; d[i + 1] = src[ri + 1]; d[i + 2] = src[ri + 2]; d[i + 3] = 255; }
                    else Set(d, i, 0, 0);
                }
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

    // where the pictures stand: the board's middle, a little below its centre (as the words do)
    static int PictureCentreY(Bitmap s) => s.Height * 54 / 87;

    /**
     * The config sign made the VR settings' one: the board cleared (the gears, the music note, its
     * staff), and a gear and a headset drawn on it, in greys with a black outline, side by side.
     */
    public static Bitmap VrSign(Bitmap config)
    {
        var s = ClearBoard(config);
        int k = Math.Max(1, s.Width / 120);
        var (gear, headset) = VrPictures(s);
        DrawPicture(s, gear.X, gear.Y, k, InGear, GearColor);
        DrawPicture(s, headset.X, headset.Y, k, InHeadset, HeadsetColor);
        return s;
    }

    // where the VR sign's gear and headset go: side by side, centred on the board
    public static ((int X, int Y, int W, int H) Gear, (int X, int Y, int W, int H) Headset) VrPictures(Bitmap s)
    {
        int k = Math.Max(1, s.Width / 120), gap = 6 * k;
        int total = GearSize * k + gap + HeadsetW * k, left = (s.Width - total) / 2, cy = PictureCentreY(s);
        return ((left, cy - GearSize * k / 2, GearSize * k, GearSize * k),
                (left + GearSize * k + gap, cy - HeadsetH * k / 2, HeadsetW * k, HeadsetH * k));
    }

    /**
     * The levels sign made the setup's one: the board cleared of its lettering, and a download
     * drawn on it - an arrow down into a tray, white with a black outline, as the signs' words are.
     */
    public static Bitmap SetupSign(Bitmap levels)
    {
        var s = ClearBoard(levels);
        int k = Math.Max(1, s.Width / 120);
        var at = SetupPicture(s);
        DrawPicture(s, at.X, at.Y, k, InDownload, (x, y) => y >= DownloadH - 3 ? 0xCFD3EBu : 0xFFFFFFu);
        return s;
    }

    // where the setup sign's download goes: centred on the board
    public static (int X, int Y, int W, int H) SetupPicture(Bitmap s)
    {
        int k = Math.Max(1, s.Width / 120);
        return ((s.Width - DownloadW * k) / 2, PictureCentreY(s) - DownloadH * k / 2, DownloadW * k, DownloadH * k);
    }

    // ---- a download, 34 x 28: the arrow's shaft and head, the tray it points into
    public const int DownloadW = 34, DownloadH = 28;

    static bool InDownload(int x, int y)
    {
        if (x < 0 || y < 0 || x >= DownloadW || y >= DownloadH) return false;
        float dx = MathF.Abs(x - 16.5f);
        if (y <= 12 && dx <= 4) return true;                          // the shaft
        if (y >= 13 && y <= 22 && dx <= 22 - y + 1.5f) return true;   // the head
        if (y >= 16 && (x <= 3 || x >= DownloadW - 4)) return true;  // the tray's sides
        return y >= 24;                                               // its bottom
    }

    // ---- a gear, 24 x 24: eight teeth round a ring with a hole; lit from above
    public const int GearSize = 24;

    static bool InGear(int x, int y)
    {
        if (x < 0 || y < 0 || x >= GearSize || y >= GearSize) return false;
        float dx = x - 11.5f, dy = y - 11.5f, r = MathF.Sqrt(dx * dx + dy * dy);
        float a = MathF.Atan2(dy, dx);
        float outer = MathF.Cos(8 * a + 0.39f) > 0.15f ? 11.6f : 8.6f;
        return r <= outer && r > 3.6f;
    }

    static uint GearColor(int x, int y) => y <= 6 ? 0xE5E5E5u : y >= 17 ? 0x969696u : 0xC0C0C0u;

    // a picture given as its shape and its colours: outlined in black where it meets the outside
    static void DrawPicture(Bitmap b, int ox, int oy, int k, Func<int, int, bool> inside, Func<int, int, uint> color)
    {
        for (int y = -1; y <= 64; y++)
            for (int x = -1; x <= 64; x++)
            {
                if (!inside(x, y)) continue;
                bool edge = !inside(x - 1, y) || !inside(x + 1, y) || !inside(x, y - 1) || !inside(x, y + 1);
                uint c = edge ? 0 : color(x, y);
                for (int sy = 0; sy < k; sy++)
                    for (int sx = 0; sx < k; sx++)
                    {
                        int px = ox + x * k + sx, py = oy + y * k + sy;
                        if (px >= 0 && py >= 0 && px < b.Width && py < b.Height) Set(b.Data, (py * b.Width + px) * 4, c);
                    }
            }
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
// every 6 ms; Strip is the whole row (lemming, reel, lemming), redrawn after a step. The reel is
// taller than NeoLemmix's (its plain middle row repeated ReelPad times), so the text stands clear
// of its dashed edges rather than touching them; the text and the lemmings are centred on it.
public sealed class TitleScroller
{
    public const int MsPerUpdate = 6;
    public const int TextFreezeBase = 333, TextFreezeWidthDiv = 3;
    // after a long stop (the lobby hidden), the reel picks up where it was rather than catching up
    const int MaxStepsPerUpdate = 50;
    public const int ReelPad = 12;

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
        var tall = Taller(segment, ReelPad);
        _reel = new Bitmap(TitleArt.ScrollerWidth + segment.Width, tall.Height);
        for (int x = 0; x < _reel.Width; x += segment.Width) TitleArt.Blend(tall, _reel, x, 0, 0, 0, tall.Width, tall.Height);
        Strip = new Bitmap(TitleArt.ScrollerWidth + LemmingW * 2, Math.Max(tall.Height, LemmingH));
        if (_lines.Count(l => l.Trim() != "") > 0) NextText();
        else Disabled = true;
        Draw();
    }

    // the segment with its middle row (between the dashed edges) repeated `pad` more times
    static Bitmap Taller(Bitmap seg, int pad)
    {
        var t = new Bitmap(seg.Width, seg.Height + pad);
        int mid = seg.Height / 2, row = seg.Width * 4;
        for (int y = 0; y < t.Height; y++)
        {
            int from = y <= mid ? y : y <= mid + pad ? mid : y - pad;
            Array.Copy(seg.Data, from * row, t.Data, y * row, row);
        }
        return t;
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
        Text = new Bitmap(MenuFont.Width(s), MenuFont.CharH);
        _font.Draw(Text, s, 0, 0);
        TextPos = TitleArt.ScrollerWidth;
    }

    // DrawScroller: the reel, the text over it clipped to the reel, the two lemmings
    public void Draw()
    {
        Array.Clear(Strip.Data);
        int left = LemmingW;
        TitleArt.Blend(_reel, Strip, left, 0, ReelFrame % _segment.Width, 0, TitleArt.ScrollerWidth, _reel.Height);
        int sx = Math.Max(0, -TextPos), dx = left + Math.Max(0, TextPos);
        int tw = Math.Min(Text.Width - sx, TitleArt.ScrollerWidth - Math.Max(0, TextPos));
        if (tw > 0) TitleArt.Blend(Text, Strip, dx, (_reel.Height - Text.Height) / 2, sx, 0, tw, Text.Height);
        int frame = ReelFrame / 4 % TitleArt.ScrollerLemmingFrames;
        int ly = (Strip.Height - LemmingH) / 2;
        TitleArt.Blend(_lemmings, Strip, 0, ly, 0, frame * LemmingH, LemmingW, LemmingH);
        TitleArt.Blend(_lemmings, Strip, left + TitleArt.ScrollerWidth, ly, LemmingW, frame * LemmingH, LemmingW, LemmingH);
    }
}
