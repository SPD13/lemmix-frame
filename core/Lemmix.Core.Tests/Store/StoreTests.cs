using System.Text.Json;
using Lemmix.Store;

namespace Lemmix.Tests.Store;

// oracle/settings.js: the three configuration files (config-store.js build/apply, setup.js
// downloads and uploads with the progress merge), the preferences as app.js and audio.js read
// them, and the JS primitives the port reimplements; plus the store's own file.
public class StoreTests
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

    [Fact] public void ConfigFilesBuildAndApplyAsTheWeb() => Scenarios("config-build-apply");
    [Fact] public void SetupDownloadsAndUploadsAsTheWeb() => Scenarios("setup-files");
    [Fact] public void ProgressMergeAsTheWeb() => Scenarios("setup-merge-random");
    [Fact] public void PreferencesReadAndWrittenAsTheWeb() => Scenarios("preferences");

    static JsonElement Primitives(string name) => Replay().Doc.RootElement.GetProperty("primitives").GetProperty(name);

    [Fact]
    public void NumbersPrintAsJs()
    {
        var bad = new List<string>();
        foreach (var e in Primitives("numbers").EnumerateArray())
        {
            ulong bits = ((ulong)e[0].GetUInt32() << 32) | e[1].GetUInt32();
            double v = BitConverter.UInt64BitsToDouble(bits);
            string want = e[2].GetString()!, got = Js.NumberToString(v);
            if (got != want) bad.Add($"{bits:x16}: web {want} port {got}");
        }
        Assert.True(bad.Count == 0, bad.Count + " differ:\n" + string.Join("\n", bad.Take(20)));
    }

    [Fact]
    public void JsonParsesAndPrintsAsJs()
    {
        var bad = new List<string>();
        foreach (var e in Primitives("json").EnumerateArray())
        {
            string text = e[0].GetString()!;
            string want = e[1].GetString()!;
            string? want2 = e[2].ValueKind == JsonValueKind.Null ? null : e[2].GetString();
            string got, got2 = null!;
            try
            {
                var v = JsJson.Parse(text);
                got = JsJson.Stringify(v)!;
                got2 = JsJson.Stringify(v, 2)!;
            }
            catch (JsSyntaxError) { got = "error"; }
            if (got != want || want != "error" && got2 != want2) bad.Add($"{JsonSerializer.Serialize(text)}: web {want} | {want2}\n  port {got} | {got2}");
        }
        Assert.True(bad.Count == 0, bad.Count + " differ:\n" + string.Join("\n", bad.Take(20)));
    }

    [Fact]
    public void StringsBecomeNumbersAsJs()
    {
        var bad = new List<string>();
        foreach (var e in Primitives("parseFloat").EnumerateArray())
        {
            string s = e[0].GetString()!;
            string want = e[2].GetString()!, got = Js.NumberToString(Js.ParseFloat(s));
            if (got != want) bad.Add($"parseFloat({JsonSerializer.Serialize(s)}): web {want} port {got}");
        }
        foreach (var e in Primitives("toNumber").EnumerateArray())
        {
            string s = e[0].GetString()!;
            string want = e[1].GetString()!, got = Js.NumberToString(Js.StringToNumber(s));
            if (got != want) bad.Add($"Number({JsonSerializer.Serialize(s)}): web {want} port {got}");
            if (Js.ToInt32(s) != e[2].GetInt32()) bad.Add($"{JsonSerializer.Serialize(s)} | 0: web {e[2].GetInt32()} port {Js.ToInt32(s)}");
        }
        Assert.True(bad.Count == 0, bad.Count + " differ:\n" + string.Join("\n", bad.Take(20)));
    }

    [Fact]
    public void CaseMapsAsJs()
    {
        var bad = new List<string>();
        foreach (var (name, map) in new (string, Func<string, string>)[] { ("lower", Js.ToLower), ("upper", Js.ToUpper) })
        {
            var want = new Dictionary<int, string>();
            foreach (var e in Primitives(name).EnumerateArray()) want[e[0].GetInt32()] = e[1].GetString()!;
            for (int cp = 0; cp <= 0x10FFFF; cp++)
            {
                if (cp is >= 0xD800 and <= 0xDFFF) continue;
                string s = char.ConvertFromUtf32(cp);
                string w = want.TryGetValue(cp, out var x) ? x : s;
                string g = map(s);
                if (g != w) bad.Add($"{name} {cp:x}: web {string.Join(" ", w.EnumerateRunes().Select(r => r.Value.ToString("x")))} port {string.Join(" ", g.EnumerateRunes().Select(r => r.Value.ToString("x")))}");
            }
        }
        foreach (var e in Primitives("lowerStrings").EnumerateArray())
        {
            string s = e[0].GetString()!, w = e[1].GetString()!, g = Js.ToLower(s);
            if (g != w) bad.Add($"lower {JsonSerializer.Serialize(s)}: web {JsonSerializer.Serialize(w)} port {JsonSerializer.Serialize(g)}");
        }
        Assert.True(bad.Count == 0, bad.Count + " differ:\n" + string.Join("\n", bad.Take(200)));
    }

    [Fact]
    public void UriComponentsEncodeAsJs()
    {
        var bad = new List<string>();
        foreach (var e in Primitives("encodeURIComponent").EnumerateArray())
        {
            string s = (string)JsJson.Parse(e[0].GetRawText())!; // lone surrogates: not System.Text.Json's
            string? want = e[1].ValueKind == JsonValueKind.Null ? null : e[1].GetString();
            string? got;
            try { got = Js.EncodeURIComponent(s); } catch (JsUriError) { got = null; }
            if (got != want) bad.Add($"{JsonSerializer.Serialize(s)}: web {want} port {got}");
        }
        Assert.True(bad.Count == 0, string.Join("\n", bad));
    }

    // the store's file: written after the debounce, read back, a bad file kept aside
    [Fact]
    public void LocalStorePersists()
    {
        string dir = Path.Combine(Path.GetTempPath(), "lemmix-store-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(dir, "settings.json");
        try
        {
            var changed = new List<string>();
            using (var s = new LocalStore(path, 50))
            {
                s.Changed += changed.Add;
                s.SetItem("lem3d-emboss", "off");
                s.SetItem("b", "1");
                s.SetItem("10", "x\u2028\"y\ud800");
                s.RemoveItem("b");
                s.RemoveItem("nope");
                Assert.False(File.Exists(path)); // not before the debounce
                Assert.Equal(new[] { "lem3d-emboss", "b", "10", "b" }, changed);
                Assert.Equal(2, s.Length);
                Assert.Equal("lem3d-emboss", s.Key(0));
                Assert.Null(s.Key(5));
                Assert.Null(s.GetItem("b"));
            }
            Assert.True(File.Exists(path));
            using (var s = new LocalStore(path))
            {
                Assert.Equal("off", s.GetItem("lem3d-emboss"));
                Assert.Equal("x\u2028\"y\ud800", s.GetItem("10"));
                Assert.Equal(2, s.Length);
                s.Clear();
                s.Flush();
            }
            Assert.Equal("{}\n", File.ReadAllText(path));
            File.WriteAllText(path, "{ not json");
            using (var s = new LocalStore(path))
            {
                Assert.Equal(0, s.Length);
                Assert.True(File.Exists(path + ".bad"));
            }
            // a debounced save lands without a flush (its timer runs on the thread pool, which the
            // rest of the suite can keep busy for a while: a generous deadline)
            using (var s = new LocalStore(path, 20))
            {
                s.SetItem("k", "v");
                var until = DateTime.UtcNow.AddSeconds(10);
                while (DateTime.UtcNow < until && !File.ReadAllText(path).Contains("\"k\"")) Thread.Sleep(20);
                Assert.Contains("\"k\": \"v\"", File.ReadAllText(path));
            }
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    // the preferences the oracle cannot reach (inside app.js's closure, or vfs.js)
    [Fact]
    public void PreferencesBarAndSetupSeen()
    {
        var s = new LocalStore();
        var p = new Preferences(s);
        Assert.Null(p.Bar);
        p.SaveBar(true, new[] { 0.1, -0.25, 1e-7 }, new[] { 0.0, 0.7071067811865476, 0, 0.7071067811865475 });
        Assert.Equal("{\"locked\":true,\"pos\":[0.1,-0.25,1e-7],\"quat\":[0,0.7071067811865476,0,0.7071067811865475]}", s.GetItem("lem3d-bar"));
        Assert.IsType<JsObject>(p.Bar);
        s.SetItem("lem3d-bar", "{oops");
        Assert.Null(p.Bar);
        Assert.False(p.SetupSeen);
        p.MarkSetupSeen();
        Assert.True(p.SetupSeen);
        Assert.Equal("1", s.GetItem("lem3d-setup-seen"));
        Assert.Equal("level", p.LibOrder);
        p.LibOrder = "world";
        Assert.Equal("world", p.LibOrder);
        Assert.Equal("", p.LibPath);
        p.Emboss = false;
        Assert.Equal("off", s.GetItem("lem3d-emboss"));
        Assert.False(p.Emboss);
        Assert.True(new Preferences(s, Preferences.ParseParams("?emboss=%59es")).Emboss);
        Assert.Equal("lemmings-3d-preferences.json", ConfigFiles.FileOf("prefs")!.Name);
        Assert.Equal("progress", ConfigFiles.KindOfKey("lem3d-talismans"));
        Assert.Null(ConfigFiles.KindOfKey("lem3d-recent"));
    }
}
