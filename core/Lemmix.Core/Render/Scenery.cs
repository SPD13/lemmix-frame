using System.Text.Json;
using System.Text.Json.Serialization;
using Lemmix.Io;

namespace Lemmix.Render;

// A gallery's scenery (native only, no web counterpart): pictures made offline for a style by
// tools/scenery-gen, kept under the asset root at 3d/env/<style>/scenery/ with this manifest
// (scenery.json). When a gallery has one, the room is the scenery instead of envgen's rings: a
// ground to the horizon, strips of the style's pieces in rings further and further out, a sky,
// and a haze that thickens with distance and toward the ground, painted at run time (the
// pictures carry none) so the manifest's numbers can be tuned without making them again.
//
// Every ring's strip has the same number of texels round (texels_round), so a texel spans the
// same angle on every ring, close or far: the pixel art keeps one size, as a 2D game's parallax
// layers do. Radii are nominal; the app pushes them out together when a big level needs the
// first ring of the room wider (SceneryLayout.Scale).
public sealed class SceneryManifest
{
    [JsonPropertyName("version")] public int Version { get; set; } = 1;
    [JsonPropertyName("style")] public string Style { get; set; } = "";
    [JsonPropertyName("generator")] public string? Generator { get; set; }
    [JsonPropertyName("texels_round")] public int TexelsRound { get; set; } = 2048;
    [JsonPropertyName("eye_m")] public double EyeM { get; set; } = 1.6;
    [JsonPropertyName("sky")] public ScenerySky Sky { get; set; } = new();
    [JsonPropertyName("fog")] public SceneryFog Fog { get; set; } = new();
    [JsonPropertyName("ground")] public SceneryGround? Ground { get; set; }
    [JsonPropertyName("layers")] public List<SceneryLayer> Layers { get; set; } = new();

    public const string FileName = "scenery.json";
    public static string DirFor(string style) => "3d/env/" + style + "/scenery/";

    static readonly JsonSerializerOptions Options = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static SceneryManifest? Parse(string json)
    {
        try
        {
            var m = JsonSerializer.Deserialize<SceneryManifest>(json, Options);
            return m == null || m.Layers.Count == 0 || m.TexelsRound <= 0 ? null : m;
        }
        catch (JsonException) { return null; }
    }

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    // the manifest of a style's scenery under the asset root, or null
    public static SceneryManifest? Load(IFileSource io, string? style)
    {
        if (string.IsNullOrEmpty(style)) return null;
        string? text = io.Text(DirFor(style) + FileName);
        return text == null ? null : Parse(text);
    }
}

public sealed class ScenerySky
{
    [JsonPropertyName("zenith")] public string Zenith { get; set; } = "#0a0406";
    [JsonPropertyName("high")] public string High { get; set; } = "#2a1010";
    [JsonPropertyName("horizon")] public string Horizon { get; set; } = "#6a3018";
    [JsonPropertyName("below")] public string Below { get; set; } = "#3a1a10";
    [JsonPropertyName("radius_m")] public double RadiusM { get; set; } = 120;
    [JsonPropertyName("dither")] public bool Dither { get; set; } = true;
}

public sealed class SceneryFog
{
    [JsonPropertyName("distance_m")] public double DistanceM { get; set; } = 24;  // 1 - e^(-d / distance) of the sky's colour
    [JsonPropertyName("max")] public double Max { get; set; } = 0.94;
    [JsonPropertyName("mist")] public double Mist { get; set; } = 0.5;          // more of it near the ground, far away
    [JsonPropertyName("mist_m")] public double MistM { get; set; } = 2.5;       // its height at 30 m (it grows with distance)
}

public sealed class SceneryGround
{
    [JsonPropertyName("file")] public string File { get; set; } = "ground.png";
    [JsonPropertyName("tile_m")] public double TileM { get; set; } = 2.56;
    [JsonPropertyName("radius_m")] public double RadiusM { get; set; } = 115;
    [JsonPropertyName("grade")] public double Grade { get; set; } = 0.6;
    [JsonPropertyName("desat")] public double Desat { get; set; } = 0.2;
}

public sealed class SceneryLayer
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("file")] public string File { get; set; } = "";
    [JsonPropertyName("radius_m")] public double RadiusM { get; set; }
    [JsonPropertyName("bottom_m")] public double BottomM { get; set; }       // its lower edge over the floor
    [JsonPropertyName("height_m")] public double HeightM { get; set; }
    [JsonPropertyName("grade")] public double Grade { get; set; } = 1;       // brightness
    [JsonPropertyName("desat")] public double Desat { get; set; }            // 0 the pieces' colours, 1 grey
    [JsonPropertyName("fade_top")] public double FadeTop { get; set; }       // its top this fraction of it melting into the sky
}

// The scenery's colours, the same in the generator's preview and the app's shader
// (SceneryView.ShaderCode): the sky by the sine of the elevation, the haze by distance and height.
public static class SceneryLook
{
    public static (double R, double G, double B) Hex(string hex)
    {
        int v = int.Parse(hex.TrimStart('#'), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
        return (((v >> 16) & 255) / 255.0, ((v >> 8) & 255) / 255.0, (v & 255) / 255.0);
    }

    static double Smooth(double a, double b, double x) { double t = Math.Clamp((x - a) / (b - a), 0, 1); return t * t * (3 - 2 * t); }
    static (double, double, double) Mix((double R, double G, double B) a, (double R, double G, double B) b, double t) =>
        (a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t);

    // the sky's colour toward a direction whose y is `e` (the sine of its elevation)
    public static (double R, double G, double B) Sky(ScenerySky s, double e)
    {
        var hor = Hex(s.Horizon);
        if (e < 0) return Mix(hor, Hex(s.Below), Smooth(0, 0.3, -e));
        return e < 0.28 ? Mix(hor, Hex(s.High), Smooth(0, 0.28, e)) : Mix(Hex(s.High), Hex(s.Zenith), Smooth(0.28, 1, e));
    }

    // how much of the sky's colour is over a thing `distM` away and `heightM` over the floor
    public static double Haze(SceneryFog f, double distM, double heightM)
    {
        double k = Math.Min(f.Max, 1 - Math.Exp(-distM / f.DistanceM));
        double mistH = f.MistM * (0.4 + distM / 30.0);
        double m = f.Mist * (1 - Smooth(0, mistH, heightM)) * Smooth(3, 30, distM);
        return Math.Clamp(k + (1 - k) * m, 0, 1);
    }

    // a texel's colour graded (brightness, toward grey)
    public static (double R, double G, double B) Grade((double R, double G, double B) c, double grade, double desat)
    {
        double l = 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
        return ((l + (c.R - l) * (1 - desat)) * grade, (l + (c.G - l) * (1 - desat)) * grade, (l + (c.B - l) * (1 - desat)) * grade);
    }
}

// Where the rings go for a room: pushed out together so the first stands clear of the room's
// first ring (where the board is kept), 0.9 m past it at least.
public static class SceneryLayout
{
    public static double Scale(SceneryManifest m, double firstRingM)
    {
        double near = m.Layers.Min(l => l.RadiusM);
        return near <= 0 ? 1 : Math.Max(1, (firstRingM + 0.9) / near);
    }
}
