using Lemmix.Engine;
using Lemmix.Render;

namespace Lemmix.SceneryGen;

// Keeping every gallery as calm as the first (orig_dirt, the reference): its horizon no
// brighter or more vivid, its strips and ground no brighter once graded, whatever the style's
// own colours (a marble or cloud style is near white, a candy one vivid).
public static class Look
{
    public static double MeanLuma(Bitmap b)
    {
        var d = b.Data; double sum = 0; long n = 0;
        for (int i = 0; i < d.Length; i += 4)
            if (d[i + 3] != 0) { sum += 0.299 * d[i] + 0.587 * d[i + 1] + 0.114 * d[i + 2]; n++; }
        return n == 0 ? 0 : sum / n;
    }

    // the reference's levels, a little over orig_dirt's own (strips ~55, ground ~70, horizon
    // luma 80 and chroma 107), so it is left as it was
    public const double StripLuma = 62, GroundLuma = 75;
    public const double HorizonLuma = 85, HighLuma = 50, ZenithLuma = 30, MaxChroma = 112;

    // a grade that keeps the graded strip (or ground) no brighter than `cap`
    public static double CapGrade(double grade, Bitmap b, double cap)
    {
        double l = MeanLuma(b);
        return l * grade <= cap || l == 0 ? grade : Math.Round(cap / l, 3);
    }

    // a colour no brighter than maxLuma and no more vivid than MaxChroma (pulled toward its grey)
    public static int Calm(int rgb, double maxLuma)
    {
        double r = (rgb >> 16) & 255, g = (rgb >> 8) & 255, b = rgb & 255;
        double l = 0.299 * r + 0.587 * g + 0.114 * b;
        if (l > maxLuma) { double k = maxLuma / l; r *= k; g *= k; b *= k; l = maxLuma; }
        double chroma = Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b));
        if (chroma > MaxChroma) { double k = MaxChroma / chroma; r = l + (r - l) * k; g = l + (g - l) * k; b = l + (b - l) * k; }
        return EnvGen.RgbOf(r, g, b);
    }
}
