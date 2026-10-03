using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Lemmix.App.Ui.Pages;
using Lemmix.App.Ui.Windows;
using Lemmix.Input;
using Lemmix.Library;
using Lemmix.Setup;
using Lemmix.Store;

namespace Lemmix.App.Test;

// The pages' fakes for the tests and the shots: what the web pages showed when oracle/pageshots.js
// took their pictures (app/Test/Fixtures/pages.json: the solutions page's rows and index), and
// backends whose answers a test sets.
public static class PageFixture
{
    static JsonObject? _f;
    public static JsonObject F => _f ??= JsonNode.Parse(Godot.FileAccess.GetFileAsString("res://Test/Fixtures/pages.json"))!.AsObject();

    public const long ShotDate = 1790954340000; // 10/2/2026, 3:19:00 PM UTC

    // ---- files in memory
    public sealed class MemFiles : IPageFiles
    {
        public string ImportFolder => "/data/import";
        public string ExportFolder => "/data/export";
        public readonly Dictionary<string, string> Texts = new();         // path -> text
        public readonly Dictionary<string, long> Sizes = new();
        public void Put(string folder, string name, string text, long size = -1) { Texts[folder + "/" + name] = text; Sizes[folder + "/" + name] = size >= 0 ? size : text.Length; }
        public IReadOnlyList<ShelfFile> List(string folder, string extension) => Texts.Keys
            .Where(p => p.StartsWith(folder + "/", StringComparison.Ordinal) && p.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .Select(p => new ShelfFile(p[(folder.Length + 1)..], p, Sizes[p], ShotDate)).ToList();
        public string ReadText(string path) => Texts.TryGetValue(path, out var t) ? t : throw new IOException("no such file");
        public string WriteText(string folder, string name, string text) { Put(folder, name, text); return folder + "/" + name; }
    }

    public sealed class Confirms : IPageConfirm
    {
        public readonly List<(string Title, string Verb, string Body, Action Yes)> Asked = new();
        public bool AutoYes;
        public void Ask(string title, string verb, string body, Action yes)
        {
            Asked.Add((title, verb, body, yes));
            if (AutoYes) yes();
        }
        public (string Title, string Verb, string Body, Action Yes) Last => Asked[^1];
    }

    // ---- the setup backend
    public sealed class Setup : ISetupBackend
    {
        public string Version => "1.0.0";
        public readonly MemFiles Mem = new();
        public IPageFiles Files => Mem;
        public readonly Dictionary<string, SetupUnit> Units = new();
        public List<SetupDir> DirList = new();
        public bool Levels = true;
        public (long, long?) Store = (4000, 10_740_000_000);
        public readonly LocalStore Prefs = new();
        public readonly HotkeyManager Hotkeys;
        // a zip's names by path, and what its install does (a test sets them)
        public readonly Dictionary<string, List<ZipEntryName>> Zips = new();
        public Func<string, InstallPlan>? PlanOf;
        public Exception? InstallFails;
        public ManualResetEventSlim? InstallGate;   // held: the install waits (a test sees the bar)
        public readonly List<string> Log = new();
        public string? DownloadTo;
        public Exception? DownloadFails;

        public Setup() { Hotkeys = new HotkeyManager(Prefs); }

        public SetupUnit? Unit(string kind) => Units.GetValueOrDefault(kind);
        public IReadOnlyList<SetupDir> Dirs() => DirList;
        public bool HasLevels => Levels;
        public (long Used, long? Available) Storage() => Store;
        public List<ZipEntryName> ZipNames(string zipPath) => Zips.TryGetValue(zipPath, out var z) ? z : throw new InvalidDataException("End of Central Directory record could not be found.");
        public InstallPlan Plan(string zipPath) => PlanOf!(zipPath);
        public UnitInfo Install(string zipPath, InstallPlan plan, string sourceName, Action<double, string> progress)
        {
            Log.Add("install " + sourceName + " as " + plan.Kind);
            for (int i = 1; i <= 4; i++)
            {
                progress(i / 5.0, "unpacking " + sourceName);
                if (i == 2) InstallGate?.Wait(10000);
            }
            if (InstallFails != null) throw InstallFails;
            progress(1, "unpacking " + sourceName);
            if (plan.Kind == "levels")
                foreach (var d in plan.Dirs) { DirList.RemoveAll(x => x.Dir == d); DirList.Add(new SetupDir(d, d.Replace('_', ' '), "lemmix", 30, 31, 2_000_000, sourceName, ShotDate)); }
            else Units[plan.Kind] = new SetupUnit(1489, 7_000_000, ShotDate, plan.Kind == "engine" ? "V12.14.0" : "");
            return new UnitInfo { Id = plan.Kind, Files = 1489, Bytes = 7_000_000 };
        }
        public async Task<string> Download(Downloads.Official what, Action<long, long?> progress, CancellationToken cancel)
        {
            Log.Add("download " + what.Key);
            await Task.Yield();
            progress(3_200_000, 7_000_000);
            if (DownloadFails != null) throw DownloadFails;
            return DownloadTo!;
        }
        public void DeleteDir(string dir) { Log.Add("delete " + dir); DirList.RemoveAll(d => d.Dir == dir); }
        public UploadServerState UploadValue = UploadServerState.Off;
        public UploadServerState Upload => UploadValue;
        public void SetUpload(UploadMode mode)
        {
            Log.Add("upload " + mode.ToString().ToLowerInvariant());
            UploadValue = mode != UploadMode.Off ? new UploadServerState(mode, true, new[] { "http://192.168.1.42:8642/" }, null, "") : UploadServerState.Off;
        }
        public ConfigDownload Export(string kind) => kind switch
        {
            "controls" => ConfigFiles.ExportControls(Hotkeys),
            "prefs" => ConfigFiles.ExportPrefs(Prefs),
            _ => ConfigFiles.ExportProgress(Prefs),
        };
        public ConfigMessage Import(string kind, string text, string name) => kind switch
        {
            "controls" => ConfigFiles.ImportControls(Hotkeys, text, name),
            "prefs" => ConfigFiles.ImportPrefs(Prefs, text, name),
            _ => ConfigFiles.ImportProgress(Prefs, text, name),
        };
        public void Play() => Log.Add("play");
    }

    /** The setup page as the web's shot had it: NeoLemmix and the styles in, three Lemmix packs. */
    public static Setup ShotSetup()
    {
        var s = new Setup();
        s.Units["engine"] = new SetupUnit(1489, 7_043_210, ShotDate, "V12.14.0");
        s.Units["styles"] = new SetupUnit(9817, 91_712_000, ShotDate, "");
        s.DirList = new()
        {
            new("Lemmings_Redux", "Lemmings Redux", "lemmix", 160, 168, 2_412_000, "Lemmings_Redux.zip", ShotDate),
            new("LemmingsPlus_All_20201114", "LemmingsPlus All 20201114", "lemmix", 796, 820, 21_870_000, "LemmingsPlus_All_20201114.zip", ShotDate),
            new("NeoLemmix_Introduction_Pack", "NeoLemmix Introduction Pack", "lemmix", 120, 133, 1_204_000, "NeoLemmix_V12.14.0.zip", ShotDate),
        };
        s.Store = (125_000_000, 10_740_000_000);
        return s;
    }

    // ---- the solutions backend: the web's rows
    public sealed class Sols : ISolutionsBackend
    {
        readonly List<SolutionLevel> _levels = new();
        readonly Dictionary<string, JsonArray> _index = new();
        public readonly List<string> Log = new();
        public Sols()
        {
            var s = F["solutions"]!;
            int order = 0;
            foreach (var n in s["levels"]!.AsArray())
            {
                var a = n!.AsArray();
                var where = a[2]!.AsArray().Select(x => x!.GetValue<string>()).ToList();
                string Num(JsonNode? v) => v == null ? "" : v.ToJsonString();
                _levels.Add(new SolutionLevel(a[0]!.GetValue<string>(), a[1]!.GetValue<string>(), where, where.Count > 0 ? where[0] : "",
                    a[3]!.GetValue<string>(), a[4]!.GetValue<int>(), order++, Num(a[5]), Num(a[6])));
            }
            foreach (var (id, v) in s["index"]!.AsObject()) _index[id] = v!.AsArray();
        }
        public IReadOnlyList<SolutionLevel> Levels() => _levels;
        static int I(JsonNode? n) => n == null ? 0 : (int)n.GetValue<double>();
        public SolutionInfo? Info(string id)
        {
            if (!_index.TryGetValue(id, out var r) || r[0]!.GetValue<string>() != "solved") return null;
            return new SolutionInfo(I(r[2]), I(r[3]), I(r[4]), I(r[5]), I(r[6]), I(r[1]), r[7] == null ? 0 : r[7]!.GetValue<double>());
        }
        public int TriedTier(string id) => _index.TryGetValue(id, out var r) ? I(r[1]) : 0;
        public bool NotFound(string id) => _index.TryGetValue(id, out var r) && r[0]!.GetValue<string>() != "solved" && I(r[1]) >= 3;
        public void Play(string id, bool solution) => Log.Add((solution ? "solution " : "play ") + id);
        public void Back() => Log.Add("back");
    }

    // ---- the replays
    public sealed class Replays : IReplayFilesBackend
    {
        public string Folder { get; set; } = "";
        public bool CanSave { get; set; } = true;
        public string LevelName { get; set; } = "Just Nuke Them!";
        public string? LevelReplayId { get; set; } = "x7A3F00D2C9E1B044";
        public string SaveText() => "# NeoLemmix Replay File\nTITLE Just Nuke Them!\nID x7A3F00D2C9E1B044\n\nASSIGNMENT\n  FRAME 12\n  ACTION DIGGER\n";
        public readonly List<string> Loaded = new();
        public void Load(Lemmix.Engine.ParsedReplay replay, string name) => Loaded.Add(name + " " + replay.Meta.Id);
    }

    // ---- the catalog's library, over the fixture's levels (for the search)
    public sealed class Lib : ICatalogLibrary
    {
        public CatalogNode Root { get; } = new() { Name = "levels", Path = "" };
        readonly Dictionary<string, CatalogNode> _byPath = new() , _byLevel = new();
        readonly Dictionary<string, (string Title, string Pack)> _info = new();
        public readonly List<SolutionLevel> Levels;
        public Lib()
        {
            _byPath[""] = Root;
            Levels = new Sols().Levels().ToList();
            foreach (var l in Levels)
            {
                var node = Root;
                foreach (var w in l.Where)
                {
                    string path = (node.Path == "" ? "" : node.Path + "/") + w;
                    if (!_byPath.TryGetValue(path, out var child))
                    {
                        child = new CatalogNode { Name = w, Path = path, Engine = "lemmix", Parent = node };
                        node.Children.Add(child);
                        _byPath[path] = child;
                    }
                    node = child;
                }
                node.Levels.Add(l.Id);
                _byLevel[l.Id] = node;
                _info[l.Id] = (l.Title, l.Pack);
            }
        }
        CatalogNode _cur => Root;
        public CatalogNode? CurrentNode() => Root;
        public void Navigate(string path) { }
        public void Up() { }
        public bool Locked => false;
        public string? CurrentLevelId => null;
        public CatalogNode? NodeOf(string id) => _byLevel.GetValueOrDefault(id);
        public bool CanLoad(string? engine) => engine == "lemmix";
        public string LevelName(string id) => _info[id].Title;
        public string WorldOf(string id) => "";
        public double? Best(string id) => null;
        public int ClearedUnder(CatalogNode n) => 0;
        public bool HasSolution(string id) => new Sols().Info(id) != null;
        public bool IsFavorite(string id) => false;
        public IReadOnlyList<string> Recent() => Array.Empty<string>();
        public IReadOnlyList<string> Favorites() => Array.Empty<string>();
        public void EnsureNames(CatalogNode n) { }

        /** The web's search text for a level: its name, the directories above it, its rank and number. */
        public (List<string>, int) Search(string q)
        {
            var hits = new List<(double, string)>();
            foreach (var l in Levels)
            {
                var n = _byLevel[l.Id];
                string text = string.Join(" ", new[] { l.Title }.Concat(l.Where.Take(l.Where.Count - 1)).Append(n.Name + " " + l.Ordinal));
                double sc = Lemmix.Library.Search.FuzzyScore(q, text);
                if (sc >= 0) hits.Add((sc, l.Id));
            }
            var sorted = hits.Select((h, i) => (h, i)).OrderByDescending(t => t.h.Item1).ThenBy(t => t.i).Select(t => t.h.Item2).ToList();
            return (sorted.Take(Lemmix.Library.Search.SearchMax).ToList(), sorted.Count);
        }
    }

    public sealed class PagesHost : IVrPagesHost
    {
        public bool Presenting { get; set; } = true;
        public Transform3D? HeadPose { get; set; } = Transform3D.Identity;
        public readonly HashSet<string> Held = new();
        public void HoldSim(string who) => Held.Add(who);
        public void ReleaseSim(string who) => Held.Remove(who);
    }
}
