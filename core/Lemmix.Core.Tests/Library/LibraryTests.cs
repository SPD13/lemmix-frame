using System.Text.Json;
using Lemmix.Library;
using Lemmix.Store;
using Lemmix.Tests.Store;

namespace Lemmix.Tests.Library;

// oracle/settings.js: library.js's data side over the real level tree (LevelsIndex.Build of the
// assets, and the web install's own index with its classic packs): next/previous level, the
// tree's lookups, progress recorded and read, the recent and favorites lists and views, the
// solutions, the talismans as app.js records them, the library's order and path, and the fuzzy
// search (WorldLibrary._renderSearch run on a fake DOM) for 64 queries.
public class LibraryTests
{
    static SettingsReplay Replay()
    {
        var r = SettingsReplay.Instance;
        Assert.SkipWhen(r == null, "no oracle output (oracle/out/settings.json.gz) or assets");
        return r!;
    }

    static void Scenarios(params string[] names)
    {
        var r = Replay();
        foreach (var n in names) Assert.True(r.StepsRun.ContainsKey(n), "scenario " + n + " not in the oracle output");
        var fs = r.FailuresOf(names);
        Assert.True(fs.Count == 0, SettingsReplay.Report(fs));
    }

    [Fact] public void TreeProgressRecentFavoritesAsTheWeb() => Scenarios("library");
    [Fact] public void SearchAsTheWeb() => Scenarios("search");
    [Fact] public void ClassicTreeAndMigrationAsTheWeb() => Scenarios("web-tree");

    [Fact]
    public void FuzzyScoresAsTheWeb()
    {
        var bad = new List<string>();
        foreach (var e in Replay().Doc.RootElement.GetProperty("primitives").GetProperty("fuzzy").EnumerateArray())
        {
            string q = (string)JsJson.Parse(e[0].GetRawText())!, t = (string)JsJson.Parse(e[1].GetRawText())!; // lone surrogates
            double want = e[2].GetDouble(), got = Search.FuzzyScore(q, t);
            if (BitConverter.DoubleToInt64Bits(got) != BitConverter.DoubleToInt64Bits(want) && !(got == 0 && want == 0))
                bad.Add($"{JsonSerializer.Serialize(q)} in {JsonSerializer.Serialize(t)}: web {want:R} port {got:R}");
        }
        Assert.True(bad.Count == 0, bad.Count + " differ:\n" + string.Join("\n", bad.Take(20)));
    }

    // a small tree by hand: the pieces the oracle's real tree does not have
    [Fact]
    public void SmallTree()
    {
        var tree = new LevelTree();
        tree.Load("{\"children\":[{\"kind\":\"pack\",\"engine\":\"lemmix\",\"name\":\"P\",\"path\":\"p\",\"children\":[" +
                  "{\"kind\":\"dir\",\"name\":\"R\",\"path\":\"p/r\",\"levels\":[{\"id\":\"p/r/a\",\"title\":\"Alpha\"},{\"id\":\"p/r/b\"}]}]}," +
                  "{\"kind\":\"dir\",\"name\":\"Loose\",\"path\":\"l\",\"levels\":[{\"id\":\"l/x\",\"theme\":\"dirt\"}]}]}");
        Assert.Equal("p/r/b", tree.Next("p/r/a", 1));
        Assert.Equal("p/r/a", tree.Next("p/r/a", 2));
        Assert.Equal("p/r/b", tree.Next("l/x", -1)); // no pack: the whole tree
        Assert.Throws<JsTypeError>(() => tree.Next("p/r/a", -3));
        var d = tree.Describe("p/r/b")!;
        Assert.Equal("R 2", d.Label);
        Assert.Equal("P", d.PackName);
        Assert.Null(d.Title);
        Assert.Equal("", tree.Describe("l/x")!.PackName);
        var store = new LocalStore();
        var lib = new LibraryState(store, tree);
        lib.Navigate("p/r");
        lib.Up();
        Assert.Equal("p", lib.Path);
        lib.Up();
        Assert.Equal("", lib.Path);
        Assert.Equal("Alpha", lib.LevelName("p/r/a"));
        var r = Search.Run(tree, "alp");
        Assert.Equal("matching “alp” · 1 level", r.Title);
        Assert.Equal("Alpha P R 1 ", Search.TextOf(tree, tree.ById["p/r/a"]));
        Assert.Equal("no level matches", Search.Run(tree, "zzz").Status);
        var p = new LevelProgress(store, tree);
        Assert.True(p.Record("p/r/a", 75, 3.0));
        Assert.False(p.Record("p/r/a", 80, null));
        Assert.Equal(75, p.Best("p/r/a"));
        Assert.Equal(1, p.ClearedUnder(tree.Root!));
        Assert.Equal("1:15", LevelProgress.Format(75));
        Talismans.RecordWin(store, "p/r/a", new object?[] { "t1" });
        Assert.Equal(new[] { "t1" }, Talismans.Of(store, "p/r/a"));
    }
}
