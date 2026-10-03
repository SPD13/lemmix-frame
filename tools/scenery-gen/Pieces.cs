using Lemmix.Engine;
using Lemmix.Render;

namespace Lemmix.SceneryGen;

// A style's terrain pieces sorted by the part they can play in a landscape, by measurement only
// (size, fill, shape, colour), so any style goes through the same recipe:
//   mass    big solid lumps - ridges and mountains are packed from them
//   spire   tall solid pieces - stacked into pillars, hung upside down as stalactites
//   tuft    small greenish pieces - grass on the tops, moss under the overhangs
//   hang    thin pieces taller than wide - roots and creepers under the overhangs
//   rubble  the small solid rest - scattered low along the ground
// Steel and see-through pieces (bridges, chains of circles) play no part.
public sealed record Piece(string Name, Bitmap Image, int W, int H, int Opaque, double Fill, int Mean, bool Green);

public sealed class PieceSet
{
    public readonly List<Piece> Mass = new(), Spire = new(), Tuft = new(), Hang = new(), Rubble = new();
    public readonly List<string> Skipped = new();
    public int Darkest = 0x200008;   // the darkest common colour, for crevices

    public static PieceSet Load(StyleManager styles, string style, IEnumerable<string> names)
    {
        var set = new PieceSet();
        var darks = new List<int>();
        var measured = new List<Piece>();
        foreach (string name in names)
        {
            var meta = styles.Terrain(style, name);
            var raw = meta?.Base?.Image;
            if (raw == null) continue;
            if (meta!.Steel) { set.Skipped.Add(name + " (steel)"); continue; }
            // made things, not ground: a name says so in most styles
            if (System.Text.RegularExpressions.Regex.IsMatch(name, "bridge|sign|chain|rope|ladder|arrow|brick|plank", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            { set.Skipped.Add(name + " (made)"); continue; }
            var p = Measure(name, raw);
            if (p == null) { set.Skipped.Add(name + " (empty)"); continue; }
            measured.Add(p);
        }
        // pieces far greyer than the style's own colours are something else (bones, metal)
        var sats = measured.Select(p => Saturation(p.Mean)).OrderBy(v => v).ToList();
        double typical = sats.Count > 0 ? sats[sats.Count / 2] : 0;
        foreach (var p in measured)
        {
            string name = p.Name;
            double aspect = p.H / (double)p.W;
            int area = p.W * p.H;
            if (typical > 0.25 && Saturation(p.Mean) < typical * 0.4) { set.Skipped.Add(name + " (grey)"); continue; }
            if (aspect < 0.3) { set.Skipped.Add(name + " (flat)"); continue; }
            if (p.Green && Math.Max(p.W, p.H) <= 40) (aspect > 1.4 ? set.Hang : set.Tuft).Add(p);
            else if (p.Fill < 0.42 && aspect > 0.9 && Math.Max(p.W, p.H) <= 48) set.Hang.Add(p);
            else if (p.Fill < 0.42) set.Skipped.Add(name + " (open)");
            else if (aspect >= 1.45 && p.H >= 40) set.Spire.Add(p);
            else if (area >= 900) set.Mass.Add(p);
            else if (area >= 60) set.Rubble.Add(p);
            else set.Skipped.Add(name + " (tiny)");
            if (!p.Green && p.Fill >= 0.42) darks.Add(DarkOf(p.Image));
        }
        if (darks.Count > 0) set.Darkest = darks.OrderBy(EnvGen.Luma).ElementAt(darks.Count / 4);
        return set;
    }

    static double Saturation(int c)
    {
        int r = (c >> 16) & 255, g = (c >> 8) & 255, b = c & 255;
        int mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b));
        return mx == 0 ? 0 : (mx - mn) / (double)mx;
    }

    // the piece cropped to what it draws, and its numbers
    static Piece? Measure(string name, Bitmap raw)
    {
        var box = EnvGen.Measure(raw);
        if (box == null) return null;
        var b = box.Value.BBox;
        var img = raw.Crop(b.X, b.Y, b.W, b.H);
        var d = img.Data;
        long r = 0, g = 0, bl = 0; int n = 0;
        for (int i = 0; i < d.Length; i += 4)
        {
            if (d[i + 3] < 128) { d[i + 3] = 0; continue; }
            d[i + 3] = 255;
            r += d[i]; g += d[i + 1]; bl += d[i + 2]; n++;
        }
        if (n == 0) return null;
        int mean = EnvGen.RgbOf(r / (double)n, g / (double)n, bl / (double)n);
        bool green = g > r * 1.05 && g > bl;
        return new Piece(name, img, b.W, b.H, n, n / (double)(b.W * b.H), mean, green);
    }

    // the darkest quarter's mean of a piece: what its shadows are
    static int DarkOf(Bitmap img)
    {
        var lum = new List<(double L, int C)>();
        var d = img.Data;
        for (int i = 0; i < d.Length; i += 4)
            if (d[i + 3] != 0) { int c = (d[i] << 16) | (d[i + 1] << 8) | d[i + 2]; lum.Add((EnvGen.Luma(c), c)); }
        var q = lum.OrderBy(x => x.L).Take(Math.Max(1, lum.Count / 4)).ToList();
        return EnvGen.RgbOf(q.Average(x => (x.C >> 16) & 255), q.Average(x => (x.C >> 8) & 255), q.Average(x => x.C & 255));
    }

    public void Print(TextWriter w)
    {
        void L(string k, List<Piece> list) => w.WriteLine($"  {k,-7} {string.Join(", ", list.Select(p => $"{p.Name} {p.W}x{p.H}"))}");
        L("mass", Mass); L("spire", Spire); L("tuft", Tuft); L("hang", Hang); L("rubble", Rubble);
        w.WriteLine($"  skipped {string.Join(", ", Skipped)}");
        w.WriteLine($"  crevice #{Darkest:x6}");
    }
}
