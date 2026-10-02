using System.Text.Json;
using Lemmix.Index;
using Lemmix.Input;
using Lemmix.Library;
using Lemmix.Oracle;
using Lemmix.Store;
using Lemmix.Tests.Oracle;

namespace Lemmix.Tests.Store;

// oracle/settings.js replayed: every scenario's steps run on the port in the oracle's order (the
// level tree, like the web's LevelTree, carries over from one scenario to the next), each step's
// result JSON.stringify'd and the whole store hashed, both compared with what the web did. The
// tests of Store/, Input/ and Library/ each assert on their own scenarios.
public sealed class SettingsReplay
{
    public sealed record Failure(string Scenario, int Step, string Op, string Args, string What);

    public JsonDocument Doc { get; }
    public List<Failure> Failures { get; } = new();
    public Dictionary<string, int> StepsRun { get; } = new();

    static SettingsReplay? _instance;
    static readonly object Gate = new();

    // the replay, once per test run; null when the oracle output or the assets are missing
    public static SettingsReplay? Instance
    {
        get
        {
            lock (Gate)
            {
                if (_instance != null) return _instance;
                var doc = OracleData.Load("settings.json");
                if (doc == null || !OracleData.HasAssets) return null;
                _instance = new SettingsReplay(doc);
                _instance.Run();
                return _instance;
            }
        }
    }

    readonly LocalStore _store = new();
    HotkeyManager? _hk;
    HotkeyManager? _setupHk;
    readonly LevelTree _tree = new();
    readonly Solutions _solutions = new();
    LibraryState? _lib;
    string? _nativeIndex, _webIndex;

    SettingsReplay(JsonDocument doc) { Doc = doc; }

    public static string Hash(string text) { var h = new StateHash(); h.Str(text); return h.Hex(); }

    public static string StoreText(LocalStore s) =>
        JsJson.Stringify(new JsArray(s.Entries().Select(e => (object?)new JsArray(new object?[] { e.Key, e.Value }))))!;

    string NativeIndex()
    {
        if (_nativeIndex != null) return _nativeIndex;
        var o = LevelsIndex.Build(new TreeSource(OracleData.AssetsDir));
        o["generated"] = "";
        return _nativeIndex = o.ToJsonString();
    }

    string WebIndex() => _webIndex ??= File.ReadAllText(Path.Combine(OracleData.AssetsDir, "levels", "index.json"));

    void Run()
    {
        var root = Doc.RootElement;
        string from = root.GetProperty("solutions").GetString()!;
        string solPath = from == "assets" ? Path.Combine(OracleData.AssetsDir, "solutions", "index.json") : Path.Combine(OracleData.RepoRoot, "web", "solutions", "index.json");
        _solutions.Load(from == "none" ? "{\"levels\":{}}" : File.ReadAllText(solPath));
        var snapshots = root.GetProperty("snapshots");
        foreach (var sc in root.GetProperty("scenarios").EnumerateArray())
        {
            string name = sc.GetProperty("name").GetString()!;
            int i = 0;
            foreach (var st in sc.GetProperty("steps").EnumerateArray())
            {
                string op = st[0].GetString()!;
                var args = (JsArray)JsJson.Parse(st[1].GetRawText())!;
                string? want = st[2].ValueKind == JsonValueKind.Null ? null : st[2].GetString();
                string wantStore = st[3].GetString()!;
                string? got;
                try { got = JsJson.Stringify(Step(op, args)); }
                catch (JsTypeError) { got = "{\"threw\":\"TypeError\"}"; }
                catch (Exception e) { got = "C# " + e.GetType().Name + ": " + e.Message; }
                string argText = st[1].GetRawText();
                if (got != want) Failures.Add(new Failure(name, i, op, Trim(argText), "result\n    web : " + Trim(want) + "\n    port: " + Trim(got)));
                string storeText = StoreText(_store);
                if (Hash(storeText) != wantStore)
                {
                    string web = snapshots.TryGetProperty(wantStore, out var w) ? w.GetString()! : "?";
                    Failures.Add(new Failure(name, i, op, Trim(argText), "store\n    web : " + Diff(web, storeText)));
                }
                i++;
            }
            StepsRun[name] = i;
        }
    }

    static string Trim(string? s) => s == null ? "(undefined)" : s.Length > 600 ? s[..600] + "…" : s;

    static string Diff(string web, string port)
    {
        int i = 0;
        while (i < web.Length && i < port.Length && web[i] == port[i]) i++;
        int from = Math.Max(0, i - 100);
        return "…" + web.Substring(from, Math.Min(260, web.Length - from)) + "\n    port: …" + port.Substring(from, Math.Min(260, port.Length - from));
    }

    public List<Failure> FailuresOf(params string[] scenarios) => Failures.Where(f => scenarios.Contains(f.Scenario)).ToList();

    public static string Report(List<Failure> fs) =>
        fs.Count + " differences:\n" + string.Join("\n", fs.Take(25).Select(f => $"  {f.Scenario}#{f.Step} {f.Op} {f.Args}\n    {f.What}"));

    static object? A(JsArray a, int i) => i < a.Count ? a.Items[i] : Js.Undefined;
    static string S(JsArray a, int i) => (string)a.Items[i]!;

    static JsObject Binding(HotkeyBinding? b) => b?.ToJs()!;

    object? Step(string op, JsArray a)
    {
        switch (op)
        {
            // the store
            case "reset":
                _store.Clear();
                if (A(a, 0) is JsObject seed) foreach (var k in seed.Keys) _store.SetItem(k, (string)seed.Get(k)!);
                return Js.Undefined;
            case "set": _store.SetItem(S(a, 0), S(a, 1)); return Js.Undefined;
            case "remove": _store.RemoveItem(S(a, 0)); return Js.Undefined;
            case "get": return _store.GetItem(S(a, 0));
            // hotkeys
            case "hk.new": _hk = new HotkeyManager(_store); return Js.Undefined;
            case "hk.load": _hk!.Load(); return Js.Undefined;
            case "hk.save": _hk!.Save(); return Js.Undefined;
            case "hk.applyPreset": _hk!.ApplyPreset(A(a, 0) as string, a.Count > 1 ? Js.Truthy(A(a, 1)) : true); return Js.Undefined;
            case "hk.applyVrPreset": _hk!.ApplyVrPreset(a.Count > 0 ? Js.Truthy(A(a, 0)) : true); return Js.Undefined;
            case "hk.fillDefaults": return new JsArray(_hk!.FillDefaults(a.Count > 0 ? Js.Truthy(A(a, 0)) : true));
            case "hk.hasHalf": return _hk!.HasHalf(Js.Truthy(A(a, 0)));
            case "hk.set": _hk!.Set(S(a, 0), A(a, 1) as string, a.Count > 2 ? A(a, 2) : null); return Js.Undefined;
            case "hk.get": return _hk!.Get(S(a, 0)) is { } b ? b.ToJs() : null;
            case "hk.codesFor": return new JsArray(_hk!.CodesFor(S(a, 0), a.Count > 1 ? A(a, 1) : Js.Undefined));
            case "hk.keyNameFor": return _hk!.KeyNameFor(S(a, 0), a.Count > 1 ? A(a, 1) : Js.Undefined);
            case "hk.export": return _hk!.ExportJSON();
            case "hk.import":
                try { return _hk!.ImportJSON(S(a, 0)).ToJs(); }
                catch (HotkeyImportException e) { return new JsObject(("error", e.Message)); }
            case "hk.table": return new JsArray(_hk!.Table.Select(p => (object?)new JsArray(new object?[] { p.Key, p.Value.ToJs() })));
            // config-store.js
            case "cfg.build": return ConfigFiles.Build(_store, S(a, 0));
            case "cfg.apply":
                try { ConfigFiles.Apply(_store, S(a, 0), JsJson.Parse(S(a, 1))); return "ok"; }
                catch (ConfigFileException e) { return new JsObject(("error", e.Message)); }
            // setup.js
            case "setup.load": _setupHk = null; return Js.Undefined;
            case "setup.exportControls": return Download(ConfigFiles.ExportControls(_setupHk ??= new HotkeyManager(_store)));
            case "setup.exportPrefs": return Download(ConfigFiles.ExportPrefs(_store));
            case "setup.exportProgress": return Download(ConfigFiles.ExportProgress(_store));
            case "setup.importControls": return Message(ConfigFiles.ImportControls(_setupHk ??= new HotkeyManager(_store), S(a, 0), S(a, 1)));
            case "setup.importPrefs": return Message(ConfigFiles.ImportPrefs(_store, S(a, 0), S(a, 1)));
            case "setup.importProgress": return Message(ConfigFiles.ImportProgress(_store, S(a, 0), S(a, 1)));
            // preferences
            case "pref.read": return new Preferences(_store, Preferences.ParseParams(A(a, 0) as string)).ReadState();
            case "audio.new": { var p = new Preferences(_store); return new JsObject(("enabled", p.Sound), ("volume", p.Volume)); }
            case "audio.setVolume": return new Preferences(_store).SetVolume(Js.ToNumber(A(a, 0)));
            case "audio.setEnabled": { var p = new Preferences(_store); p.Sound = Js.Truthy(A(a, 0)); return p.Sound; }
            // the tree
            case "tree.load": _tree.Load(S(a, 0) == "web" ? WebIndex() : NativeIndex(), true); return (double)_tree.ById.Count;
            case "tree.next": return _tree.Next(S(a, 0), Js.ToInt32(A(a, 1)));
            case "tree.describe":
                {
                    var d = _tree.Describe(S(a, 0));
                    if (d == null) return null;
                    return new JsObject(("engine", d.Node.Raw.Get("engine")), ("label", d.Label), ("packName", d.PackName), ("title", d.Title),
                        ("node", d.Node.Raw.Get("path")), ("pack", d.Pack != null ? d.Pack.Raw.Get("path") : null));
                }
            case "tree.levelsOf":
                {
                    var ids = LevelTree.LevelsOf(_tree.NodeAt(A(a, 0) as string)!).Select(l => l.Id ?? "").ToList();
                    return new JsObject(("count", ids.Count), ("first", ids.Count > 0 ? ids[0] : null), ("all", Hash(string.Join("\n", ids))));
                }
            case "tree.firstLevelId": return _tree.FirstLevelId();
            case "tree.classicId": return _tree.ClassicId(Js.ToNumber(A(a, 0)), Js.ToNumber(A(a, 1)), Js.ToNumber(A(a, 2)));
            case "tree.nodeAt":
                {
                    var n = _tree.NodeAt(A(a, 0) as string);
                    if (n == null) return null;
                    return new JsObject(("path", n.Raw.Get("path")), ("kind", n.Raw.Get("kind")), ("name", n.Raw.Get("name")),
                        ("parent", n.Parent?.Raw.Get("path")), ("pack", n.Pack?.Raw.Get("path")));
                }
            // progress, recent, favorites, solutions, talismans
            case "prog.all": return new LevelProgress(_store, _tree).All();
            case "prog.record": return new LevelProgress(_store, _tree).Record(S(a, 0), Js.ToNumber(A(a, 1)), A(a, 2));
            case "prog.best": return new LevelProgress(_store, _tree).Best(S(a, 0));
            case "prog.saved": return new LevelProgress(_store, _tree).Saved(S(a, 0));
            case "prog.clearedUnder": return new LevelProgress(_store, _tree).ClearedUnder(_tree.NodeAt(A(a, 0) as string)!);
            case "prog.migrate": new LevelProgress(_store, _tree).Migrate(); return Js.Undefined;
            case "prog.format": return LevelProgress.Format(Js.ToNumber(A(a, 0)));
            case "recent.push": new RecentLevels(_store).Push(A(a, 0)); return Js.Undefined;
            case "recent.list": return new RecentLevels(_store).List();
            case "fav.toggle": return new FavoriteLevels(_store).Toggle(A(a, 0));
            case "fav.has": return new FavoriteLevels(_store).Has(A(a, 0));
            case "fav.list": return new FavoriteLevels(_store).List();
            case "sol.info": return _solutions.Info(S(a, 0));
            case "sol.has": return _solutions.Has(S(a, 0));
            case "sol.url":
                try { return _solutions.Url(S(a, 0), S(a, 1)); }
                catch (JsUriError) { return new JsObject(("error", "URIError")); }
            case "talisman.win": Talismans.RecordWin(_store, S(a, 0), ((JsArray)A(a, 1)!).Items); return Js.Undefined;
            // the library's own
            case "lib.new": _lib = new LibraryState(_store, _tree); return new JsObject(("order", _lib.Order), ("path", _lib.Path));
            case "lib.toggleOrder": return _lib!.ToggleOrder();
            case "lib.navigate": _lib!.Navigate(A(a, 0) as string); return _lib.Path;
            case "lib.setCurrent": _lib!.SetCurrent(A(a, 0) as string); return _lib.CurrentLevelId;
            case "lib.levelName": return _lib!.LevelName(S(a, 0));
            case "lib.search":
                {
                    var r = Search.Run(_tree, S(a, 0));
                    var ids = r.Shown.Select(h => h.Level.Id ?? "").ToList();
                    return new JsObject(("title", r.Title), ("status", r.Status), ("count", ids.Count),
                        ("top", new JsArray(ids.Take(10))), ("all", Hash(string.Join("\n", ids))));
                }
            case "lib.recentView": return View(_lib!.RecentView());
            case "lib.favoritesView": return View(_lib!.FavoritesView());
        }
        throw new InvalidOperationException("unknown op " + op);
    }

    static JsObject Download(ConfigDownload d) => new(("name", d.Name), ("text", d.Text));
    static JsObject Message(ConfigMessage m) => new(("text", m.Text), ("cls", m.ClassName));
    static JsObject View(PickedView v) => new(("title", v.Title), ("ids", new JsArray(v.Hits.Select(h => (object?)h.Level.Id))), ("status", v.Status));
}
