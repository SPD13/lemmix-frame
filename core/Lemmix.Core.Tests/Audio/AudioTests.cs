using Lemmix.Audio;
using Lemmix.Tests.Oracle;

namespace Lemmix.Tests.Audio;

public class AudioTests
{
    [Fact]
    public void TrackerMusicRendersAndLoops()
    {
        string dir = Path.Combine(OracleData.AssetsDir, "neolemmix", "music");
        Assert.SkipWhen(!Directory.Exists(dir), "no assets");
        string file = Directory.EnumerateFiles(dir, "*.it").OrderBy(f => f, StringComparer.Ordinal).First();
        var data = File.ReadAllBytes(file);
        ulong Render()
        {
            using var mod = new TrackerModule(data);
            var buf = new float[2048];
            ulong h = 1469598103934665603UL;
            int total = 0;
            double seconds = Math.Min(mod.DurationSeconds + 5, 600);
            while (total < 48000 * seconds)
            {
                int n = mod.Read(48000, buf, 1024);
                Assert.True(n > 0, "a looping module never ends");
                for (int i = 0; i < n * 2; i++) { h ^= (ulong)BitConverter.SingleToInt32Bits(buf[i]); h *= 1099511628211UL; }
                total += n;
            }
            return h;
        }
        Assert.Equal(Render(), Render()); // deterministic
        Assert.False(string.IsNullOrEmpty(OpenMpt.LibraryVersion));
    }

    [Fact]
    public void MusicNamesFollowTheLevelThenTheRotation()
    {
        var names = MusicResolver.Names(" !orig_01 ; ?random;; lemmings2 ", new[] { "a", "b", "c" }, 4);
        Assert.Equal(new[] { "orig_01", "lemmings2", "b" }, names);
        Assert.Equal(new[] { "c" }, MusicResolver.Names("", new[] { "a", "b", "c" }, -1));
    }

    [Fact]
    public void CandidatesUseKnownFilesInExtensionOrder()
    {
        var dirs = new[]
        {
            new MusicResolver.MusicDir("levels/pack/music", new[] { "Track.IT", "track.ogg", "other.mp3", "track.txt" }),
            new MusicResolver.MusicDir("neolemmix/music", null),
        };
        var c = MusicResolver.Candidates(new[] { "track" }, dirs);
        Assert.Equal("levels/pack/music/track.ogg", c[0]);
        Assert.Equal("levels/pack/music/Track.IT", c[1]);
        Assert.Equal("neolemmix/music/track.ogg", c[2]);
        Assert.Equal(2 + MusicResolver.Extensions.Length, c.Count);
    }
}
