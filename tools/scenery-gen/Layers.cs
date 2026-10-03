using Lemmix.Engine;
using Lemmix.Render;

namespace Lemmix.SceneryGen;

// One ring of the scenery: a strip round the player at radius_m, drawn from the style's pieces.
//   ridge   a skyline filled with masses (and spires standing out of it), standing on the ground
//   hang    the same drawn upside down and hung from above: an overhang, stalactites, roots
// Heights are metres at the ring; the skyline wobbles between skyLo and skyHi and is held down to
// calmFloor of itself straight ahead (|u - 0.5| < calmInner), where the board is.
public sealed record LayerSpec(
    string Name, string Kind, double RadiusM, double HeightM,
    double SkyLoM, double SkyHiM, int FMin, int FMax, double Rough,
    double CalmInner, double CalmOuter, double CalmFloor,
    int Spires = 0, double SpireLoM = 0, double SpireHiM = 0,
    double TuftEvery = 0, double HangP = 0, bool Small = false,
    double BottomM = 0, double Grade = 1, double Desat = 0, double FadeTop = 0);

public static class Layers
{
    public const int Round = 2048;                         // texels round every ring: one angular pixel size
    public static double Texel(double radiusM) => 2 * Math.PI * radiusM / Round;   // metres per texel at a ring

    // The recipe every style goes through (tested on orig_dirt): rubble at the foot of the play
    // space, pillars, an overhang framing the top of the view, then ridges further and further
    // into the haze. Radii are the room's nominal; the app pushes them out together when a big
    // level needs the first ring wider.
    public static readonly LayerSpec[] Recipe =
    {
        new("rubble",   "ridge", 3.8,  0.7,  -0.15, 0.5,  6, 40, 0.6, 0.10, 0.22, 0.0, TuftEvery: 70, Small: true, Grade: 0.5, Desat: 0.35),
        new("pillars",  "ridge", 6.5,  5.2,  -0.2,  0.8,  5, 30, 0.8, 0.12, 0.24, 0.0, Spires: 9, SpireLoM: 1.8, SpireHiM: 4.6, TuftEvery: 55, HangP: 0.22, Grade: 0.42, Desat: 0.4),
        new("overhang", "hang",  9.5,  8.0,   1.2,  4.8,  3, 24, 0.9, 0.08, 0.30, 0.3,  Spires: 14, SpireLoM: 1.2, SpireHiM: 3.6, TuftEvery: 40, HangP: 0.35, BottomM: 5.0, Grade: 0.38, Desat: 0.4, FadeTop: 0.45),
        new("outcrops", "ridge", 14.0, 8.0,   0.6,  5.0,  3, 16, 1.1, 0.05, 0.18, 0.35, Spires: 6, SpireLoM: 3.0, SpireHiM: 7.0, TuftEvery: 45, Grade: 0.5, Desat: 0.45),
        new("ridge",    "ridge", 25.0, 11.0,  1.5,  8.5,  2, 12, 1.2, 0.04, 0.16, 0.45, Spires: 4, SpireLoM: 6, SpireHiM: 10.5, TuftEvery: 40, Grade: 0.6, Desat: 0.45),
        new("far",      "ridge", 44.0, 16.0,  3.0,  13.0, 2, 10, 1.3, 0.03, 0.14, 0.55, Grade: 0.7, Desat: 0.5),
        new("horizon",  "ridge", 78.0, 16.0,  2.0,  11.0, 3, 9,  1.3, 0.0,  0.1,  0.7,  Grade: 0.8, Desat: 0.5),
    };

    public static Bitmap Build(LayerSpec s, PieceSet set, string seed)
    {
        double ts = Texel(s.RadiusM);
        int H = (int)Math.Ceiling(s.HeightM / ts);
        var c = new Canvas(Round, H);
        var rng = EnvGen.SeededRandom(seed + ":" + s.Name);
        var wobble = Shapes.Wobble(rng, s.FMin, s.FMax, s.Rough);
        var calm = Shapes.Calm(s.CalmInner, s.CalmOuter, s.CalmFloor);
        // the skyline in rows from the bottom
        double Sky(double u) => Math.Max(0, (s.SkyLoM + (s.SkyHiM - s.SkyLoM) * (0.5 + 0.5 * wobble(u))) * calm(u)) / ts;
        var masses = s.Small ? set.Rubble.Concat(set.Mass.Where(p => p.H <= 40)).Where(p => p.H >= p.W * 0.5).ToList() : set.Mass;
        if (masses.Count == 0) masses = set.Mass.Count > 0 ? set.Mass : set.Rubble;
        bool hang = s.Kind == "hang";

        // spires first, so the ridge packs round their feet (stamps never overwrite)
        var spireAt = new List<double>();
        for (int k = 0, tries = 0; k < s.Spires && tries < s.Spires * 20; tries++)
        {
            double u = rng();
            if (Shapes.Away(u) < s.CalmInner + 0.04) continue;
            if (spireAt.Any(v => Math.Min(Math.Abs(v - u), 1 - Math.Abs(v - u)) < 0.025)) continue;
            spireAt.Add(u);
            double hm = s.SpireLoM + (s.SpireHiM - s.SpireLoM) * rng();
            hm *= 0.6 + 0.4 * calm(u);
            Spire(c, set, masses, (int)(u * Round), (int)(hm / ts), rng, hang);
            k++;
        }
        Ridge(c, masses, Sky, rng, hang);
        if (!s.Small) c.FillBelowTop(set.Darkest, 10);
        c.Shade(0.55, Math.Max(12, H / 2), foot: Math.Min(24, H / 4), footLow: 0.7);
        if (s.TuftEvery > 0) c.Tufts(set.Tuft, rng, s.TuftEvery);
        if (s.HangP > 0 && !hang) c.Hangs(set.Hang, rng, s.HangP);
        if (!hang) return c.Bmp;
        // hung from above: the strip turned over, roots and moss then hang from its lower edge
        var turned = new Canvas(Round, H);
        Pixels.Blit(turned.Bmp, 0, 0, c.Flipped(), 0, 0, Round, H, Pixels.CombineGadget);
        turned.Hangs(set.Hang.Concat(set.Tuft.Where(t => t.H > t.W)).ToList(), rng, s.HangP, 4);
        turned.FadeTop(H * 0.4, 0.5);
        return turned.Bmp;
    }

    // masses packed in rows from the ground up, each kept if it does not stand far over the
    // skyline; a later (higher) row goes behind the rows under it
    static void Ridge(Canvas c, List<Piece> masses, Func<double, double> sky, Func<double> rng, bool flipV)
    {
        if (masses.Count == 0) return;
        double mh = masses.Select(p => p.H).OrderBy(h => h).ElementAt(masses.Count / 2);
        double step = Math.Max(3, mh * 0.42);
        for (double bottom = c.H + rng() * 4; bottom > -mh; bottom -= step)
        {
            double x = -rng() * 40;
            while (x < c.W)
            {
                var p = Pick(masses, rng);
                double cx = x + p.W / 2.0;
                double sk = sky(((cx % c.W) + c.W) % c.W / c.W), limit = c.H - sk;
                double top = bottom - p.H - rng() * 3;
                // nothing where the skyline is down to the ground (no half-buried band of tops)
                if (sk >= p.H * 0.35 && top + p.H * 0.4 >= limit) c.Stamp(p, (int)x, (int)top, rng() < 0.5, flipV, under: true);
                x += p.W * (0.45 + 0.2 * rng());
            }
        }
    }

    // a pillar: a footing of masses, then a shaft of spire pieces and narrower masses stacked up
    // to `rows`, leaning a little as it goes; some end in a wide cap (a hoodoo), some in a point
    static void Spire(Canvas c, PieceSet set, List<Piece> masses, int x0, int rows, Func<double> rng, bool flipV)
    {
        var shaft = set.Spire.Concat(masses.Where(m => m.H >= m.W * 0.7)).ToList();
        if (shaft.Count == 0) shaft = masses;
        double lean = (rng() - 0.5) * 0.5;
        for (int k = 0; k < 2 + (int)(rng() * 3); k++)
        {
            var b = Pick(masses, rng);
            c.Stamp(b, (int)(x0 + (rng() - 0.5) * b.W * 1.4 - b.W / 2.0), (int)(c.H - b.H * (0.6 + 0.4 * rng())), rng() < 0.5, flipV, under: true);
        }
        double y = c.H - 4, x = x0;
        bool cap = rng() < 0.35;
        while (c.H - y < rows)
        {
            double left = rows - (c.H - y);
            var pool = shaft.Where(p => p.H <= left + 30).ToList();
            var p = Pick(pool.Count > 0 ? pool : shaft, rng);
            if (cap && left < p.H * 0.9 && masses.Count > 0) p = masses.OrderByDescending(m => m.W).ElementAt((int)(rng() * Math.Min(3, masses.Count)));
            int top = (int)(y - p.H);
            c.Stamp(p, (int)(x - p.W / 2.0), top, rng() < 0.5, flipV, under: true);
            y = top + p.H * (0.2 + 0.15 * rng());
            x += lean * p.H * 0.3 + (rng() - 0.5) * p.W * 0.25;
        }
    }

    static Piece Pick(List<Piece> list, Func<double> rng)
    {
        double total = list.Sum(p => Math.Sqrt(p.Opaque));
        double r = rng() * total;
        foreach (var p in list) { r -= Math.Sqrt(p.Opaque); if (r <= 0) return p; }
        return list[^1];
    }

    // The ground: a seamless tile packed with the masses and rubble, crevices dark, its contrast
    // taken down so the floor stays quiet under the board.
    public static Bitmap Ground(PieceSet set, string seed, int size, double calm)
    {
        var c = new Canvas(size, size, wrapY: true);
        var rng = EnvGen.SeededRandom(seed + ":ground");
        var list = set.Mass.Concat(set.Rubble).ToList();
        if (list.Count == 0) return c.Bmp;
        double mh = list.Select(p => p.H).OrderBy(h => h).ElementAt(list.Count / 2);
        for (double y = 0; y < size; y += mh * 0.4)
            for (double x = -rng() * 30; x < size; )
            {
                var p = Pick(list, rng);
                c.Stamp(p, (int)x, (int)(y - rng() * 6), rng() < 0.5, rng() < 0.5, under: true);
                x += p.W * (0.45 + 0.25 * rng());
            }
        var d = c.Bmp.Data;
        for (int i = 0; i < d.Length; i += 4)
            if (d[i + 3] == 0) { d[i] = (byte)(set.Darkest >> 16); d[i + 1] = (byte)(set.Darkest >> 8); d[i + 2] = (byte)set.Darkest; d[i + 3] = 255; }
        int mean = EnvGen.MeanColor(c.Bmp);
        for (int i = 0; i < d.Length; i += 4)
        {
            d[i] = (byte)(d[i] + (((mean >> 16) & 255) - d[i]) * calm);
            d[i + 1] = (byte)(d[i + 1] + (((mean >> 8) & 255) - d[i + 1]) * calm);
            d[i + 2] = (byte)(d[i + 2] + ((mean & 255) - d[i + 2]) * calm);
        }
        // grass here and there
        for (int k = 0; k < size * size / 30000 && set.Tuft.Count > 0; k++)
        {
            var t = set.Tuft[(int)(rng() * set.Tuft.Count)];
            c.Stamp(t, (int)(rng() * size), (int)(rng() * size), rng() < 0.5);
        }
        return c.Bmp;
    }
}
