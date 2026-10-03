using Lemmix.Engine;
using Lemmix.Io;
using Lemmix.Render;

namespace Lemmix.Tests.Render;

// The scenery's manifest and look (native only, no oracle): the manifest read back as written,
// a broken one refused, the rings pushed out for a wide room, the haze growing with distance
// and toward the ground, and the sky one gradient through the horizon.
public class SceneryTests
{
    sealed class MemoryFiles : IFileSource
    {
        public readonly Dictionary<string, string> Files = new(StringComparer.Ordinal);
        public string? Text(string path) => Files.TryGetValue(path, out var t) ? t : null;
        public byte[]? Bytes(string path) => null;
        public Bitmap? Image(string path) => null;
    }

    static SceneryManifest Sample() => new()
    {
        Style = "orig_dirt",
        Ground = new SceneryGround { File = "ground.png", TileM = 1.28 },
        Layers =
        {
            new SceneryLayer { Name = "near", File = "layer-1.png", RadiusM = 3.8, HeightM = 0.7, Grade = 0.5 },
            new SceneryLayer { Name = "hang", File = "layer-2.png", RadiusM = 9.5, BottomM = 5, HeightM = 8, FadeTop = 0.45 },
        },
    };

    [Fact]
    public void ManifestRoundTrips()
    {
        var back = SceneryManifest.Parse(Sample().ToJson());
        Assert.NotNull(back);
        Assert.Equal("orig_dirt", back!.Style);
        Assert.Equal(2, back.Layers.Count);
        Assert.Equal(0.45, back.Layers[1].FadeTop);
        Assert.Equal(1.28, back.Ground!.TileM);
        Assert.Equal(2048, back.TexelsRound);
    }

    [Fact]
    public void BrokenOrEmptyManifestsAreRefused()
    {
        Assert.Null(SceneryManifest.Parse("{ not json"));
        Assert.Null(SceneryManifest.Parse("{\"style\": \"x\", \"layers\": []}"));
        var io = new MemoryFiles();
        Assert.Null(SceneryManifest.Load(io, "orig_dirt"));
        Assert.Null(SceneryManifest.Load(io, null));
        io.Files["3d/env/orig_dirt/scenery/scenery.json"] = Sample().ToJson();
        Assert.NotNull(SceneryManifest.Load(io, "orig_dirt"));
    }

    [Fact]
    public void RingsArePushedOutForAWideRoom()
    {
        var m = Sample();
        Assert.Equal(1, SceneryLayout.Scale(m, 2.8));               // 2.8 + 0.9 < 3.8: as drawn
        Assert.Equal((5.0 + 0.9) / 3.8, SceneryLayout.Scale(m, 5.0), 9);
    }

    [Fact]
    public void HazeGrowsWithDistanceAndTowardTheGround()
    {
        var f = new SceneryFog();
        double prev = -1;
        foreach (double d in new[] { 0.0, 2, 5, 10, 20, 40, 80, 200 })
        {
            double h = SceneryLook.Haze(f, d, 3);
            Assert.True(h >= prev, $"haze at {d} m");
            prev = h;
        }
        Assert.True(SceneryLook.Haze(f, 40, 0) > SceneryLook.Haze(f, 40, 10), "thicker near the ground");
        Assert.True(SceneryLook.Haze(f, 1000, 50) <= 1);
        Assert.Equal(0, SceneryLook.Haze(f, 0, 0), 9);
    }

    [Fact]
    public void SkyIsOneGradient()
    {
        var s = new ScenerySky();
        var hor = SceneryLook.Hex(s.Horizon);
        Assert.Equal(hor, SceneryLook.Sky(s, 0));
        Assert.Equal(hor, SceneryLook.Sky(s, -1e-9));
        Assert.Equal(SceneryLook.Hex(s.High), SceneryLook.Sky(s, 0.28));
        Assert.Equal(SceneryLook.Hex(s.Zenith), SceneryLook.Sky(s, 1));
        Assert.Equal(SceneryLook.Hex(s.Below), SceneryLook.Sky(s, -0.5));
    }
}
