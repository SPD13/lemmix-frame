using Lemmix.Engine;
using Lemmix.Io;
using Lemmix.Render;

namespace Lemmix.SceneryGen;

// The view from the player's place, straight ahead +-108 degrees, -35 to +55 degrees of elevation,
// one texel per angular texel of the rings: the sky, the ground and the strips with the haze the
// app paints (SceneryLook), to judge a scenery without the headset.
public static class Preview
{
    public static Bitmap Render(SceneryManifest m, string dir)
    {
        int round = m.TexelsRound;
        double th = 2 * Math.PI / round;
        int x0 = (int)(round * 0.2), w = (int)(round * 0.6);
        int up = (int)(55 * Math.PI / 180 / th), down = (int)(35 * Math.PI / 180 / th);
        var output = new Bitmap(w, up + down);
        var layers = m.Layers.OrderByDescending(l => l.RadiusM)
            .Select(l => (L: l, B: Png.Decode(File.ReadAllBytes(Path.Combine(dir, l.File))))).ToList();
        Bitmap? ground = m.Ground != null && File.Exists(Path.Combine(dir, m.Ground.File)) ? Png.Decode(File.ReadAllBytes(Path.Combine(dir, m.Ground.File))) : null;
        double eye = m.EyeM;
        var d = output.Data;
        for (int py = 0; py < output.Height; py++)
        {
            double elev = (up - py - 0.5) * th;
            double e = Math.Sin(elev), t = Math.Tan(elev);
            var sky = SceneryLook.Sky(m.Sky, e);
            for (int px = 0; px < w; px++)
            {
                double u = (x0 + px + 0.5) / round;
                var c = sky;
                if (elev < 0 && ground != null)
                {
                    double dist = eye / -t;
                    if (dist < m.Ground!.RadiusM)
                    {
                        // the app's angle: u = 0.5 straight ahead (-z), growing to the right (+x)
                        double a = (u - 0.5) * 2 * Math.PI;
                        double gx = dist * Math.Sin(a), gz = -dist * Math.Cos(a);
                        var g = Texel(ground, gx / m.Ground.TileM, gz / m.Ground.TileM);
                        c = Mix(SceneryLook.Grade(g, m.Ground.Grade, m.Ground.Desat), sky, SceneryLook.Haze(m.Fog, dist, 0));
                    }
                }
                foreach (var (l, b) in layers)
                {
                    double h = eye + l.RadiusM * t;
                    if (h < l.BottomM || h >= l.BottomM + l.HeightM) continue;
                    int row = (int)((l.BottomM + l.HeightM - h) / l.HeightM * b.Height);
                    int col = (int)(u * b.Width) % b.Width;
                    int i = (Math.Clamp(row, 0, b.Height - 1) * b.Width + col) * 4;
                    if (b.Data[i + 3] == 0) continue;
                    var tc = (b.Data[i] / 255.0, b.Data[i + 1] / 255.0, b.Data[i + 2] / 255.0);
                    c = Mix(SceneryLook.Grade(tc, l.Grade, l.Desat), sky, SceneryLook.Haze(m.Fog, l.RadiusM, h));
                }
                int o = (py * w + px) * 4;
                d[o] = (byte)Math.Clamp(c.R * 255, 0, 255); d[o + 1] = (byte)Math.Clamp(c.G * 255, 0, 255); d[o + 2] = (byte)Math.Clamp(c.B * 255, 0, 255); d[o + 3] = 255;
            }
        }
        return output;
    }

    static (double R, double G, double B) Texel(Bitmap b, double u, double v)
    {
        int x = (int)Math.Floor((u - Math.Floor(u)) * b.Width) % b.Width, y = (int)Math.Floor((v - Math.Floor(v)) * b.Height) % b.Height;
        int i = (y * b.Width + x) * 4;
        return (b.Data[i] / 255.0, b.Data[i + 1] / 255.0, b.Data[i + 2] / 255.0);
    }

    static (double R, double G, double B) Mix((double R, double G, double B) a, (double R, double G, double B) b, double t) =>
        (a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t);
}
