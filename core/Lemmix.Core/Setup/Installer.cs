using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using Lemmix.Index;

namespace Lemmix.Setup;

// web/3d/js/setup.js, the installs: NeoLemmix itself (the "engine" unit), the styles package
// (the "styles" unit) and level packs (one unit per levels/<dir>), unpacked from their zips into
// the asset root (neolemmix/..., levels/...). A unit installed again replaces the previous one;
// a level directory replaces its namesake. The web version tags every stored file with its unit
// (IndexedDB); here units.json keeps each unit's files. After every change the three indexes are
// rebuilt (tools/*-index.js, ported in Index/).
public sealed record ZipEntryName(string Name, long Size);

public sealed class UnitInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Source { get; set; } = "";
    public long InstalledAt { get; set; }        // ms since the epoch, as Date.now()
    public int Files { get; set; }
    public long Bytes { get; set; }
    public List<string> Paths { get; set; } = new(); // asset-root-relative, what an uninstall removes
}

public sealed record InstallPlan(string Kind, string Label, UnitInfo? Replaces, List<string> ClashingDirs, List<string> Dirs);

public sealed class Installer
{
    public const string UnitsFile = "units.json";
    readonly string _root;

    public Installer(string assetRoot) { _root = Path.GetFullPath(assetRoot); Directory.CreateDirectory(_root); }

    public string Root => _root;

    // ---- what a zip is, and where its entries go (setup.js KINDS, detectKind, levelZipMap)

    // A zip entry's name as a repo path: forward slashes, no leading ./ or /.
    public static string NormName(string name)
    {
        string n = name.Replace('\\', '/');
        while (n.StartsWith("./", StringComparison.Ordinal)) n = n[2..];
        return n.TrimStart('/');
    }

    public static readonly Dictionary<string, string> Labels = new()
    {
        ["engine"] = "NeoLemmix", ["styles"] = "the styles package", ["levels"] = "a level pack",
    };

    static readonly Regex NeoLemmixExe = new(@"^neolemmix\.exe$", RegexOptions.IgnoreCase);
    static readonly Regex LevelFile = new(@"\.nxlv$|\.DAT$|(^|/)levels\.nxmi$", RegexOptions.IgnoreCase);

    public static string? DetectKind(IReadOnlyList<ZipEntryName> names)
    {
        var list = names.Select(e => e.Name).ToList();
        if (list.Any(n => NeoLemmixExe.IsMatch(n)) || list.Any(n => n.StartsWith("gfx/panel/", StringComparison.Ordinal))) return "engine";
        if (list.Any(n => n == "styles/styles.ini") ||
            (!list.Any(n => n.StartsWith("gfx/", StringComparison.Ordinal)) && list.Count(n => n.StartsWith("styles/", StringComparison.Ordinal)) > list.Count / 2.0)) return "styles";
        if (list.Any(n => Regex.IsMatch(n, @"\.nxlv$", RegexOptions.IgnoreCase) || Regex.IsMatch(n, @"\.DAT$", RegexOptions.IgnoreCase) || Regex.IsMatch(n, @"(^|/)levels\.nxmi$", RegexOptions.IgnoreCase))) return "levels";
        return null;
    }

    public sealed record Where(string Path, string Unit);

    public static Where? MapEngine(string n)
    {
        var parts = n.Split('/');
        string top = parts[0];
        if (top is "gfx" or "sound" or "music" or "data" or "styles") return new Where("neolemmix/" + n, "engine");
        if (top == "levels" && parts.Length > 2) return new Where(n, "levels/" + parts[1]);
        return null;
    }

    public static Where? MapStyles(string n)
    {
        string top = n.Split('/')[0];
        return top is "styles" or "sound" ? new Where("neolemmix/" + n, "styles") : null;
    }

    // Where a level zip's entries go: under the one folder they all share, or under a folder named
    // after the zip. A collection wrapping packs in levels/ (and music/) keeps that layout.
    public static (List<string> Dirs, Func<string, Where?> Map) LevelZipMap(IReadOnlyList<ZipEntryName> names, string fileName)
    {
        var list = names.Select(e => e.Name).ToList();
        var tops = list.Select(n => n.Split('/')[0]).Distinct().ToList();
        bool nested = list.All(n => n.Contains('/'));
        if (tops.Count == 1 && nested && tops[0] != "levels" && tops[0] != "music")
        {
            string d = tops[0];
            return (new() { d }, n => n.StartsWith(d + "/", StringComparison.Ordinal) ? new Where("levels/" + n, "levels/" + d) : null);
        }
        string dir = Regex.Replace(Regex.Replace(fileName, @"\.zip$", "", RegexOptions.IgnoreCase), @"[^\w.\- ]+", "_", RegexOptions.ECMAScript);
        if (dir == "") dir = "levels";
        return (new() { dir }, n => new Where("levels/" + dir + "/" + n, "levels/" + dir));
    }

    public static List<string> LevelDirsIn(IEnumerable<string> list) =>
        list.Where(n => n.StartsWith("levels/", StringComparison.Ordinal) && n.Split('/').Length > 2).Select(n => n.Split('/')[1]).Distinct().ToList();

    // ---- the zip

    public static List<ZipEntryName> ZipNames(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        return zip.Entries.Select(e => new ZipEntryName(NormName(e.FullName), e.Length))
            .Where(e => e.Name != "" && !e.Name.EndsWith('/')).ToList();
    }

    // ---- units (setup.js browserStore: units, deleteUnit, recordUnit, dirs, deleteDir)

    public Dictionary<string, UnitInfo> Units()
    {
        string p = Path.Combine(_root, UnitsFile);
        if (!File.Exists(p)) return new();
        try { return JsonSerializer.Deserialize<Dictionary<string, UnitInfo>>(File.ReadAllText(p)) ?? new(); }
        catch (JsonException) { return new(); }
    }

    void SaveUnits(Dictionary<string, UnitInfo> units)
    {
        string p = Path.Combine(_root, UnitsFile);
        File.WriteAllText(p + ".tmp", JsonSerializer.Serialize(units, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(p + ".tmp", p, true);
    }

    // the level directories on disk, with what is known of them
    public List<UnitInfo> Dirs()
    {
        var units = Units();
        string levels = Path.Combine(_root, "levels");
        if (!Directory.Exists(levels)) return new();
        return NaturalCompare.Sort(Directory.EnumerateDirectories(levels).Select(Path.GetFileName).OfType<string>().Where(d => !d.StartsWith('.')))
            .Select(d => units.TryGetValue("levels/" + d, out var u) ? u : new UnitInfo { Id = "levels/" + d, Name = d, Files = -1, Bytes = -1 })
            .ToList();
    }

    void DeleteUnit(string id)
    {
        var units = Units();
        if (units.TryGetValue(id, out var u))
        {
            foreach (string p in u.Paths) { string abs = Abs(p); if (File.Exists(abs)) File.Delete(abs); }
            units.Remove(id);
            SaveUnits(units);
            PruneEmptyDirs();
        }
    }

    public void DeleteDir(string dir)
    {
        DeleteUnit("levels/" + dir);
        string abs = Abs("levels/" + dir);
        if (Directory.Exists(abs)) Directory.Delete(abs, true); // whatever else landed there
        RebuildIndexes();
    }

    void PruneEmptyDirs()
    {
        foreach (string top in new[] { "neolemmix", "levels" })
        {
            string d = Path.Combine(_root, top);
            if (!Directory.Exists(d)) continue;
            foreach (string sub in Directory.EnumerateDirectories(d, "*", SearchOption.AllDirectories).OrderByDescending(s => s.Length))
                if (!Directory.EnumerateFileSystemEntries(sub).Any()) Directory.Delete(sub);
        }
    }

    string Abs(string rel)
    {
        string full = Path.GetFullPath(Path.Combine(_root, rel));
        if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new InvalidDataException("outside the asset folder: " + rel);
        return full;
    }

    // ---- installs

    // What installing this zip would do - what it replaces is for the player to confirm.
    public InstallPlan Plan(string zipPath, string? expected = null)
    {
        var names = ZipNames(zipPath);
        string kind = DetectKind(names) ?? throw new InvalidDataException(Path.GetFileName(zipPath) + " does not look like NeoLemmix, its styles package or a level pack");
        var units = Units();
        var have = new HashSet<string>(Dirs().Select(d => d.Name), StringComparer.Ordinal);
        List<string> dirs = kind == "engine" ? LevelDirsIn(names.Select(e => e.Name)) : kind == "levels" ? LevelZipMap(names, Path.GetFileName(zipPath)).Dirs : new();
        return new InstallPlan(kind, Labels[kind], kind == "levels" ? null : units.GetValueOrDefault(kind),
            dirs.Where(have.Contains).ToList(), dirs);
    }

    // Install a planned zip; `progress(fraction, label)` as it unpacks. The source name (the zip's
    // file name) carries the engine's version, as NeoLemmix_V12.14.0.zip does.
    public UnitInfo Install(string zipPath, InstallPlan plan, string? sourceName = null, Action<double, string>? progress = null)
    {
        string source = sourceName ?? Path.GetFileName(zipPath);
        var names = ZipNames(zipPath);
        Func<string, Where?> map = plan.Kind switch
        {
            "engine" => MapEngine,
            "styles" => MapStyles,
            _ => LevelZipMap(names, source).Map,
        };
        if (plan.Kind != "levels" && plan.Replaces != null) DeleteUnit(plan.Kind);
        foreach (string d in plan.Dirs) { DeleteUnit("levels/" + d); string a = Abs("levels/" + d); if (Directory.Exists(a)) Directory.Delete(a, true); }

        var byUnit = new Dictionary<string, UnitInfo>(StringComparer.Ordinal);
        long total = names.Sum(n => n.Size), done = 0;
        using (var zip = ZipFile.OpenRead(zipPath))
        {
            foreach (var e in zip.Entries)
            {
                string name = NormName(e.FullName);
                if (name == "" || name.EndsWith('/')) continue;
                var where = map(name);
                if (where == null) continue;
                string abs = Abs(where.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
                e.ExtractToFile(abs, true);
                if (!byUnit.TryGetValue(where.Unit, out var u)) byUnit[where.Unit] = u = new UnitInfo { Id = where.Unit };
                u.Paths.Add(where.Path);
                u.Files++;
                u.Bytes += e.Length;
                done += e.Length;
                progress?.Invoke(total == 0 ? 1 : done / (double)total, "unpacking " + source);
            }
        }
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        string version = plan.Kind == "engine" ? Regex.Match(source, @"(V?\d+\.\d+(\.\d+)?(-\w+)?)", RegexOptions.IgnoreCase) is { Success: true } m ? m.Groups[1].Value : "" : "";
        var units = Units();
        foreach (var (id, u) in byUnit)
        {
            u.Name = id.StartsWith("levels/", StringComparison.Ordinal) ? id["levels/".Length..] : Labels[plan.Kind];
            u.Source = source;
            u.InstalledAt = now;
            u.Version = id == "engine" ? version : "";
            units[id] = u;
        }
        // a level dir counted whole, as recordLevelDir does: everything now under it
        foreach (var id in byUnit.Keys.Where(k => k.StartsWith("levels/", StringComparison.Ordinal)))
        {
            var files = Directory.EnumerateFiles(Abs(id), "*", SearchOption.AllDirectories).ToList();
            units[id].Files = files.Count;
            units[id].Bytes = files.Sum(f => new FileInfo(f).Length);
        }
        SaveUnits(units);
        RebuildIndexes();
        return byUnit.TryGetValue(plan.Kind, out var main) ? main : byUnit.Values.FirstOrDefault() ?? new UnitInfo { Id = plan.Kind };
    }

    // setup.js rebuildIndexes: the three indexes from what is on disk now
    public void RebuildIndexes()
    {
        var io = new TreeSource(_root);
        var js = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        void Write(string rel, System.Text.Json.Nodes.JsonObject index)
        {
            string abs = Abs(rel);
            if (!Directory.Exists(Path.GetDirectoryName(abs))) return;
            File.WriteAllText(abs, index.ToJsonString(js) + "\n");
        }
        Directory.CreateDirectory(Abs("levels"));
        Write("levels/index.json", LevelsIndex.Build(io));
        Write(StylesIndex.StylesDir + "/index.json", StylesIndex.Build(io));
        Write(MusicIndex.MusicDir + "/index.json", MusicIndex.Build(io));
    }

    // what the setup panel shows as storage used
    public long BytesUsed() => Units().Values.Sum(u => Math.Max(0, u.Bytes));
}
