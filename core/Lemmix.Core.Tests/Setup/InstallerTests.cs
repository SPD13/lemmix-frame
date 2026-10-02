using System.Text.Json.Nodes;
using Lemmix.Index;
using Lemmix.Setup;
using Lemmix.Tests.Oracle;

namespace Lemmix.Tests.Setup;

public class InstallerTests
{
    static List<ZipEntryName> Names(params string[] n) => n.Select(x => new ZipEntryName(x, 1)).ToList();

    [Fact]
    public void KindsAreDetectedAsTheSetupPageDoes()
    {
        Assert.Equal("engine", Installer.DetectKind(Names("NeoLemmix.exe", "gfx/panel/empty_slot.png", "styles/default/theme.nxtm")));
        Assert.Equal("engine", Installer.DetectKind(Names("gfx/panel/x.png")));
        Assert.Equal("styles", Installer.DetectKind(Names("styles/styles.ini", "styles/a/terrain/b.png")));
        Assert.Equal("styles", Installer.DetectKind(Names("styles/a/x.png", "styles/a/y.png", "readme.txt")));
        Assert.Equal("levels", Installer.DetectKind(Names("Pack/levels.nxmi", "Pack/Fun/a.nxlv")));
        Assert.Null(Installer.DetectKind(Names("readme.txt")));
    }

    [Fact]
    public void LevelZipsLandUnderTheirOneFolderOrTheZipsName()
    {
        var (dirs, map) = Installer.LevelZipMap(Names("Pack/levels.nxmi", "Pack/Fun/a.nxlv"), "whatever.zip");
        Assert.Equal(new[] { "Pack" }, dirs);
        Assert.Equal("levels/Pack/Fun/a.nxlv", map("Pack/Fun/a.nxlv")!.Path);
        var (dirs2, map2) = Installer.LevelZipMap(Names("levels/A/levels.nxmi", "music/x.ogg"), "LemmingsPlus_All_20201114.zip");
        Assert.Equal(new[] { "LemmingsPlus_All_20201114" }, dirs2);
        Assert.Equal("levels/LemmingsPlus_All_20201114/levels/A/levels.nxmi", map2("levels/A/levels.nxmi")!.Path);
        var (dirs3, _) = Installer.LevelZipMap(Names("a.nxlv"), "My Pack (v2)!.zip");
        Assert.Equal(new[] { "My Pack _v2_" }, dirs3);
        Assert.Equal("neolemmix/gfx/a.png", Installer.MapEngine("gfx/a.png")!.Path);
        Assert.Equal("levels/X", Installer.MapEngine("levels/X/a.nxlv")!.Unit);
        Assert.Null(Installer.MapEngine("NeoLemmix.exe"));
        Assert.Null(Installer.MapStyles("gfx/a.png"));
    }

    // The real downloads installed into an empty folder: the packs and styles they bring index as
    // they do in the web version's installed copy (LEMMIX_DOWNLOAD_TEST=1; ~120 MB, cached in build/).
    [Fact]
    public async Task TheOfficialZipsInstallLikeTheWebCopy()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("LEMMIX_DOWNLOAD_TEST") != "1" || !OracleData.HasAssets, "set LEMMIX_DOWNLOAD_TEST=1");
        string cache = Path.Combine(OracleData.RepoRoot, "build", "downloads");
        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("LemmixFrame/0.1");
        var zips = new List<string>();
        foreach (var o in new[] { Downloads.Engine, Downloads.Styles, Downloads.Packs })
        {
            var existing = Directory.Exists(cache) ? Directory.EnumerateFiles(cache, "*.zip").FirstOrDefault(f => Installer.DetectKind(Installer.ZipNames(f)) == (o.Key == "packs" ? "levels" : o.Key)) : null;
            zips.Add(existing ?? await Downloads.FetchAsync(http, o, cache, cancel: TestContext.Current.CancellationToken));
        }
        string root = Path.Combine(Path.GetTempPath(), "lemmix-install-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var inst = new Installer(root);
            foreach (var z in zips) inst.Install(z, inst.Plan(z));
            var units = inst.Units();
            Assert.True(units.ContainsKey("engine") && units.ContainsKey("styles"), "engine and styles units");
            Assert.Matches(@"^V?\d+\.\d+", units["engine"].Version);
            // the web copy's index nodes for the packs we installed must be ours
            var web = JsonNode.Parse(File.ReadAllText(Path.Combine(OracleData.OracleDir, "index", "levels.json")))!.AsObject();
            var mine = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "levels", "index.json")))!.AsObject();
            foreach (var node in mine["children"]!.AsArray())
            {
                string path = node!["path"]!.GetValue<string>();
                var twin = web["children"]!.AsArray().FirstOrDefault(n => n!["path"]!.GetValue<string>() == path);
                Assert.True(twin != null, "the web copy has " + path);
                Assert.Equal(twin!.ToJsonString(), node.ToJsonString());
            }
            var webStyles = JsonNode.Parse(File.ReadAllText(Path.Combine(OracleData.OracleDir, "index", "styles.json")))!["styles"]!.AsArray();
            var myStyles = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "neolemmix", "styles", "index.json")))!["styles"]!.AsArray();
            // the styles package grows (301 styles on 2 Oct 2026 against 280 in the web copy, installed
            // earlier): every style of the web copy must be there; the builder itself is checked by IndexTests
            var mineNames = myStyles.Select(x => x!["name"]!.GetValue<string>()).ToHashSet();
            // (a style the player added by hand, like turrican_special in the web copy, is not in the package)
            var inPackage = zips.SelectMany(z => Installer.ZipNames(z)).Select(e => e.Name.Split('/'))
                .Where(p => p.Length > 2 && p[0] == "styles").Select(p => p[1].ToLowerInvariant()).ToHashSet();
            var missing = webStyles.Select(x => x!["name"]!.GetValue<string>()).Where(n => inPackage.Contains(n) && !mineNames.Contains(n)).ToList();
            Assert.True(missing.Count == 0, "styles missing after install: " + string.Join(", ", missing));
            // uninstall a pack: its folder and unit go, the index follows
            string dir = mine["children"]!.AsArray()[0]!["path"]!.GetValue<string>();
            inst.DeleteDir(dir);
            Assert.False(Directory.Exists(Path.Combine(root, "levels", dir)));
            Assert.DoesNotContain("levels/" + dir, inst.Units().Keys);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
