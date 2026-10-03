using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Lemmix.Input;
using Lemmix.Setup;
using Lemmix.Store;

namespace Lemmix.App.Ui.Pages;

// NeoLemmix or the styles package as the setup page shows it (setup.js units(): files, bytes,
// installedAt, version; null when it is not installed)
public sealed record SetupUnit(int? Files, long? Bytes, long? InstalledAt, string Version);

// a level directory (setup.js dirs() joined with the index's node of the same name)
public sealed record SetupDir(string Dir, string? Name, string? Engine, int? Count, int? Files, long? Bytes, string Source, long? InstalledAt);

// The level upload server's switch: off (the default), on until the app quits, or on at every start.
public enum UploadMode { Off, Session, Always }

// The level upload server as the setup page shows it: its switch, whether it runs, the addresses to
// type in a browser, why it could not start, the last thing a computer did through it.
public sealed record UploadServerState(UploadMode Mode, bool On, IReadOnlyList<string> Urls, string? Error, string Activity)
{
    public static readonly UploadServerState Off = new(UploadMode.Off, false, Array.Empty<string>(), null, "");
}

// What the setup page reads and does (web/3d/js/setup.js over vfs.js and config-store.js). The
// slow ones - Download, Install, DeleteDir - are called off the frame.
public interface ISetupBackend
{
    string Version { get; }                           // the app's (the page head's v1.0.0)
    IPageFiles Files { get; }
    SetupUnit? Unit(string kind);                     // "engine" or "styles"
    IReadOnlyList<SetupDir> Dirs();
    bool HasLevels { get; }                           // levelCount(index) > 0
    (long Used, long? Available) Storage();
    List<ZipEntryName> ZipNames(string zipPath);      // throws on a file that is not a zip
    InstallPlan Plan(string zipPath);
    UnitInfo Install(string zipPath, InstallPlan plan, string sourceName, Action<double, string> progress);
    Task<string> Download(Downloads.Official what, Action<long, long?> progress, CancellationToken cancel);
    void DeleteDir(string dir);
    ConfigDownload Export(string kind);               // "controls", "prefs", "progress"
    ConfigMessage Import(string kind, string text, string name);
    void Play();                                      // the head's PLAY: the library, to choose a level
    UploadServerState Upload => UploadServerState.Off; // the level upload server (native only)
    void SetUpload(UploadMode mode) { }
}

// The native backend: Installer over <user data>/assets (neolemmix/, levels/, the indexes),
// Downloads into the import folder, the configuration files of the store and the live key table.
public sealed class SetupBackend : ISetupBackend
{
    readonly Installer _installer;
    readonly IStorage _store;
    readonly HotkeyManager _hotkeys;
    readonly HttpClient _http;
    readonly Action _play;
    public string Version { get; }
    public IPageFiles Files { get; }

    readonly Func<UploadServerState>? _upload;
    readonly Action<UploadMode>? _setUpload;
    public UploadServerState Upload => _upload?.Invoke() ?? UploadServerState.Off;
    public void SetUpload(UploadMode mode) => _setUpload?.Invoke(mode);

    public SetupBackend(string assetRoot, IPageFiles files, IStorage store, HotkeyManager hotkeys, string version, Action play, HttpClient? http = null,
        Func<UploadServerState>? upload = null, Action<UploadMode>? setUpload = null)
    {
        _upload = upload;
        _setUpload = setUpload;
        _installer = new Installer(assetRoot);
        Files = files;
        _store = store;
        _hotkeys = hotkeys;
        Version = version;
        _play = play;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
    }

    public SetupUnit? Unit(string kind)
    {
        var u = _installer.Units().GetValueOrDefault(kind);
        return u == null ? null : new SetupUnit(u.Files, u.Bytes, u.InstalledAt == 0 ? null : u.InstalledAt, u.Version);
    }

    // the index's top nodes by their directory: name, engine, count
    Dictionary<string, (string? Name, string? Engine, int? Count)> IndexNodes()
    {
        var map = new Dictionary<string, (string?, string?, int?)>(StringComparer.Ordinal);
        string p = Path.Combine(_installer.Root, "levels", "index.json");
        if (!File.Exists(p)) return map;
        try
        {
            if (JsonNode.Parse(File.ReadAllText(p))?["children"] is JsonArray ch)
                foreach (var n in ch)
                {
                    string? path = n?["path"]?.GetValue<string>();
                    if (path == null) continue;
                    string dir = path.Split('/')[0];
                    if (map.ContainsKey(dir)) continue;
                    map[dir] = (n!["name"]?.GetValue<string>(), n["engine"]?.GetValue<string>(), n["count"]?.GetValue<int>());
                }
        }
        catch (Exception) { /* no index: the directories by their names */ }
        return map;
    }

    public IReadOnlyList<SetupDir> Dirs()
    {
        var nodes = IndexNodes();
        return _installer.Dirs().Select(u =>
        {
            string dir = u.Id["levels/".Length..];
            var has = nodes.TryGetValue(dir, out var n);
            return new SetupDir(dir, has ? n.Name : null, has ? n.Engine : null, has ? n.Count : null,
                u.Files < 0 ? null : u.Files, u.Bytes < 0 ? null : u.Bytes, u.Source, u.InstalledAt == 0 ? null : u.InstalledAt);
        }).ToList();
    }

    public bool HasLevels => IndexNodes().Values.Sum(n => n.Count ?? 0) > 0;

    public (long Used, long? Available) Storage()
    {
        long? free = null;
        try { free = new DriveInfo(Path.GetPathRoot(_installer.Root) ?? "/").AvailableFreeSpace; } catch (Exception) { }
        return (_installer.BytesUsed(), free);
    }

    public List<ZipEntryName> ZipNames(string zipPath) => Installer.ZipNames(zipPath);
    public InstallPlan Plan(string zipPath) => _installer.Plan(zipPath);
    public UnitInfo Install(string zipPath, InstallPlan plan, string sourceName, Action<double, string> progress) =>
        _installer.Install(zipPath, plan, sourceName, progress);
    public Task<string> Download(Downloads.Official what, Action<long, long?> progress, CancellationToken cancel) =>
        Downloads.FetchAsync(_http, what, Files.ImportFolder, progress, cancel: cancel);
    public void DeleteDir(string dir) => _installer.DeleteDir(dir);

    public ConfigDownload Export(string kind) => kind switch
    {
        "controls" => ConfigFiles.ExportControls(_hotkeys),
        "prefs" => ConfigFiles.ExportPrefs(_store),
        _ => ConfigFiles.ExportProgress(_store),
    };

    public ConfigMessage Import(string kind, string text, string name) => kind switch
    {
        "controls" => ConfigFiles.ImportControls(_hotkeys, text, name),
        "prefs" => ConfigFiles.ImportPrefs(_store, text, name),
        _ => ConfigFiles.ImportProgress(_store, text, name),
    };

    public void Play() => _play();
}
