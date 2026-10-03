using Lemmix.Engine;
using Lemmix.Render;

namespace Lemmix.SceneryGen;

// Drawing on a panorama strip: x wraps round (the strip closes on itself), row 0 is the top,
// the bottom row stands on the ground. Pixels are opaque or empty, as the pieces are.
public sealed class Canvas
{
    public readonly Bitmap Bmp;
    public readonly int W, H;
    public readonly bool WrapY;
    public Canvas(int w, int h, bool wrapY = false) { W = w; H = h; WrapY = wrapY; Bmp = new Bitmap(w, h); }

    public bool Opaque(int x, int y) => y >= 0 && y < H && Bmp.Data[(y * W + Mod(x, W)) * 4 + 3] != 0;
    static int Mod(int a, int n) => ((a % n) + n) % n;

    // a piece with its top-left at (x, y), optionally mirrored; `under` keeps what is there
    public void Stamp(Piece p, int x, int y, bool flipH = false, bool flipV = false, bool under = false)
    {
        var s = p.Image.Data; var d = Bmp.Data;
        for (int j = 0; j < p.H; j++)
        {
            int yy = y + j;
            if (WrapY) yy = Mod(yy, H); else if (yy < 0 || yy >= H) continue;
            int sj = flipV ? p.H - 1 - j : j;
            for (int i = 0; i < p.W; i++)
            {
                int si = ((sj * p.W) + (flipH ? p.W - 1 - i : i)) * 4;
                if (s[si + 3] == 0) continue;
                int di = (yy * W + Mod(x + i, W)) * 4;
                if (under && d[di + 3] != 0) continue;
                d[di] = s[si]; d[di + 1] = s[si + 1]; d[di + 2] = s[si + 2]; d[di + 3] = 255;
            }
        }
    }

    // the first opaque row of a column (H when empty)
    public int Top(int x)
    {
        for (int y = 0; y < H; y++) if (Opaque(x, y)) return y;
        return H;
    }

    // crevices: the empty pixels more than `skin` rows under their column's top with rock on
    // both sides within `reach` (gaps inside a mass, not the open air beside a leaning rock)
    public void FillBelowTop(int crevice, int skin, int reach = 20)
    {
        var d = Bmp.Data;
        var fill = new List<int>();
        var tops = new int[W];
        for (int x = 0; x < W; x++) tops[x] = Top(x);
        for (int x = 0; x < W; x++)
            for (int y = tops[x] + skin; y < H; y++)
            {
                if (Opaque(x, y)) continue;
                bool left = false, right = false;
                for (int k = 1; k <= reach && !(left && right); k++)
                {
                    left |= Opaque(x - k, y);
                    right |= Opaque(x + k, y);
                }
                if (left && right) fill.Add(y * W + x);
            }
        foreach (int p in fill)
        {
            int i = p * 4;
            d[i] = (byte)(crevice >> 16); d[i + 1] = (byte)(crevice >> 8); d[i + 2] = (byte)crevice; d[i + 3] = 255;
        }
    }

    // ambient occlusion: each pixel darker the further it is under its column's top, down to
    // `low` at `depth` rows; the bottom `foot` rows darker still (where the ground's shadow is)
    public void Shade(double low, int depth, int foot = 0, double footLow = 1)
    {
        var d = Bmp.Data;
        for (int x = 0; x < W; x++)
        {
            int top = Top(x);
            for (int y = top; y < H; y++)
            {
                int i = (y * W + x) * 4;
                if (d[i + 3] == 0) continue;
                double t = Math.Min(1, (y - top) / (double)Math.Max(1, depth));
                double k = 1 - (1 - low) * t;
                if (foot > 0 && y > H - foot) k *= 1 - (1 - footLow) * ((y - (H - foot)) / (double)foot);
                d[i] = (byte)(d[i] * k); d[i + 1] = (byte)(d[i + 1] * k); d[i + 2] = (byte)(d[i + 2] * k);
            }
        }
    }

    // greenery on the tops: a tuft where a column's top has open sky over it
    public void Tufts(List<Piece> tufts, Func<double> rng, double every, Func<int, bool>? where = null)
    {
        if (tufts.Count == 0) return;
        for (double x = rng() * every; x < W; x += every * (0.5 + rng()))
        {
            int xi = (int)x;
            if (where != null && !where(xi)) continue;
            int top = Top(xi);
            if (top >= H - 2) continue;
            var p = tufts[(int)(rng() * tufts.Count)];
            Stamp(p, xi - p.W / 2, top - p.H + 3 + (int)(rng() * 2), rng() < 0.5, false, under: true);
        }
    }

    // roots and moss from the undersides: where an opaque pixel has `clear` empty rows under it
    public void Hangs(List<Piece> hangs, Func<double> rng, double p, int clear = 6)
    {
        if (hangs.Count == 0) return;
        var spots = new List<(int X, int Y)>();
        for (int x = 0; x < W; x += 3)
            for (int y = 1; y < H - clear; y++)
            {
                if (!Opaque(x, y) || Opaque(x, y + 1)) continue;
                bool open = true;
                for (int k = 2; k <= clear && open; k++) open = !Opaque(x, y + k);
                if (open && Opaque(x, y - 1)) spots.Add((x, y));
            }
        foreach (var (x, y) in spots)
        {
            if (rng() >= p) continue;
            var h = hangs[(int)(rng() * hangs.Count)];
            Stamp(h, x - h.W / 2, y - 1, rng() < 0.5);
        }
    }

    public Bitmap Flipped() => Bmp.FlipVertical();

    // darker toward the top edge: `low` at row 0 up to full at `rows` (an overhang going up into
    // the dark of the vault)
    public void FadeTop(double rows, double low)
    {
        var d = Bmp.Data;
        for (int y = 0; y < Math.Min(H, (int)rows); y++)
        {
            double t = y / rows, k = low + (1 - low) * t * t;
            for (int x = 0; x < W; x++)
            {
                int i = (y * W + x) * 4;
                d[i] = (byte)(d[i] * k); d[i + 1] = (byte)(d[i + 1] * k); d[i + 2] = (byte)(d[i + 2] * k);
            }
        }
    }
}

// The shapes the strips are drawn to: seamless round the circle (u in 0..1, 0.5 straight ahead,
// where the board is).
public static class Shapes
{
    public static double Smoothstep(double a, double b, double x)
    {
        double t = Math.Clamp((x - a) / (b - a), 0, 1);
        return t * t * (3 - 2 * t);
    }

    // how far u is from straight ahead, 0..0.5
    public static double Away(double u) { double a = Math.Abs(u - 0.5); return Math.Min(a, 1 - a); }

    // 1 away from the board, `floor` straight ahead: the calm the board stands against
    public static Func<double, double> Calm(double inner, double outer, double floor) =>
        u => floor + (1 - floor) * Smoothstep(inner, outer, Away(u));

    // a periodic wobble in -1..1: whole-number frequencies so it closes round the circle
    public static Func<double, double> Wobble(Func<double> rng, int fMin, int fMax, double rough)
    {
        var parts = new List<(int F, double A, double Ph)>();
        double total = 0;
        for (int f = fMin; f <= fMax; f++)
        {
            double a = Math.Pow(f / (double)fMin, -rough) * (0.6 + 0.8 * rng());
            parts.Add((f, a, rng() * Math.PI * 2));
            total += a;
        }
        return u => parts.Sum(p => p.A * Math.Sin(2 * Math.PI * p.F * u + p.Ph)) / total * 1.8;
    }
}
