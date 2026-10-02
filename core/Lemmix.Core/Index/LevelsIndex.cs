using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Lemmix.Parse;
using Lemmix.Util;

namespace Lemmix.Index;

// web/tools/levels-index.js - levels/index.json, the tree the level browser navigates. Every
// folder of levels/ is a pack: a NeoLemmix pack (levels.nxmi with its ranks), a folder wrapping
// packs in a levels/ subfolder (collapsed), or loose levels. The classic DOS packs of config.json
// are not part of the native app. Objects keep the JS key order, so the JSON is the web's.
public static class LevelsIndex
{
    public const string LevelsDir = "levels";

    static string Join(params string?[] parts) => string.Join("/", parts.Where(p => !string.IsNullOrEmpty(p)));

    static string Basename(string p, string? ext = null)
    {
        string b = p[(p.LastIndexOf('/') + 1)..];
        if (ext != null && b.ToLowerInvariant().EndsWith(ext.ToLowerInvariant(), StringComparison.Ordinal)) b = b[..^ext.Length];
        return b;
    }

    static string? First(NxSection n, string key) => n.Entries.FirstOrDefault(e => e.Key == key)?.Value;
    static List<string> All(NxSection n, string key) => n.Entries.Where(e => e.Key == key).Select(e => e.Value).ToList();

    // the browser parses with its own simpler parseNx; NxParser.Parse reads the same
    static NxSection ParseNx(string text) => NxParser.Parse(text);

    static readonly Regex TerrainBlock = new(@"^\s*\$TERRAIN\s*$([\s\S]*?)^\s*\$END\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.ECMAScript);
    static readonly Regex StyleLine = new(@"^\s*STYLE\s+(.+?)\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.ECMAScript);
    static readonly Regex SectionStart = new(@"^\s*\$", RegexOptions.Multiline | RegexOptions.ECMAScript);

    // The styles a level's terrain pieces come from, lowercased, sorted, once each ("*group" left out).
    public static List<string> TerrainStyles(string text)
    {
        var styles = new List<string>();
        foreach (Match m in TerrainBlock.Matches(text))
        {
            var s = StyleLine.Match(m.Groups[1].Value);
            if (!s.Success) continue;
            string style = s.Groups[1].Value.ToLowerInvariant();
            if (style != "" && style[0] != '*' && !styles.Contains(style)) styles.Add(style);
        }
        styles.Sort(StringComparer.Ordinal);
        return styles;
    }

    // parseInt(v, 10): leading spaces, a sign, the digits as far as they go; NaN (null) otherwise
    static int? ParseInt(string? v)
    {
        if (v == null) return null;
        string t = JsString.Trim(v);
        int i = 0;
        bool neg = false;
        if (i < t.Length && (t[i] == '+' || t[i] == '-')) { neg = t[i] == '-'; i++; }
        int start = i;
        while (i < t.Length && t[i] >= '0' && t[i] <= '9') i++;
        if (i == start) return null;
        long n = long.Parse(t[start..i], System.Globalization.CultureInfo.InvariantCulture);
        return (int)(neg ? -n : n);
    }

    static JsonNode? Num(int? v) => v is int n ? JsonValue.Create(n) : null;
    static JsonNode? Str(string? s) => s == null ? null : JsonValue.Create(s);

    // The header of a .nxlv: the keys before the first section, plus what the browser lists.
    static JsonObject ReadLevelHeader(TreeSource io, string file)
    {
        string text = io.ReadText(file);
        var cut = SectionStart.Match(text);
        var nx = ParseNx(cut.Success ? text[..cut.Index] : text);
        int? N(string k) { var v = First(nx, k); return v == null ? null : ParseInt(v); }
        string title = First(nx, "TITLE") is { Length: > 0 } t ? t : Basename(file, ".nxlv").Replace('_', ' ');
        return new JsonObject
        {
            ["title"] = title,
            ["theme"] = Str(First(nx, "THEME") is { Length: > 0 } th ? th : null),
            ["styles"] = new JsonArray(TerrainStyles(text).Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()),
            ["nxId"] = Str(First(nx, "ID") is { Length: > 0 } id ? id : null),
            ["width"] = Num(N("WIDTH")), ["height"] = Num(N("HEIGHT")),
            ["lemmings"] = Num(N("LEMMINGS")), ["save"] = Num(N("SAVE_REQUIREMENT")),
        };
    }

    // Every file under `dir`, as paths relative to it ("sub/name.ext"), in natural order.
    public static List<string> ListFilesBelow(TreeSource io, string dir)
    {
        var output = new List<string>();
        void Walk(string d, string rel)
        {
            foreach (string f in io.ListFiles(d)) output.Add(rel + f);
            foreach (string sub in io.ListDirs(d)) Walk(d + "/" + sub, rel + sub + "/");
        }
        if (io.IsDir(dir)) Walk(dir, "");
        return output;
    }

    static int Count(JsonArray children) => children.Sum(c => c!["count"]!.GetValue<int>());

    // One NeoLemmix pack: info.nxmi for its name, levels.nxmi for its ranks.
    static JsonObject LemmixPack(TreeSource io, string dir, string logicalPath, string? musicDir)
    {
        var nx = ParseNx(io.ReadText(Join(dir, "levels.nxmi")));
        var info = io.Exists(Join(dir, "info.nxmi")) ? ParseNx(io.ReadText(Join(dir, "info.nxmi"))) : null;
        var music = io.Exists(Join(dir, "music.nxmi")) ? All(ParseNx(io.ReadText(Join(dir, "music.nxmi"))), "TRACK") : new List<string>();
        var children = new JsonArray();
        foreach (var group in nx.Sections.Where(s => s.Name == "GROUP" || s.Name == "RANK"))
        {
            string? rankName = First(group, "NAME") is { Length: > 0 } n ? n : First(group, "FOLDER");
            string? folder = First(group, "FOLDER") is { Length: > 0 } f ? f : rankName;
            if (string.IsNullOrEmpty(folder)) continue;
            string rankDir = Join(dir, folder);
            if (!io.IsDir(rankDir)) continue;
            var rank = LemmixLevels(io, rankDir, logicalPath + "/" + folder, rankName ?? "");
            if (rank["count"]!.GetValue<int>() > 0) children.Add(rank);
        }
        // levels listed straight in the pack (a pack without ranks)
        if (All(nx, "LEVEL").Count > 0)
        {
            var own = LemmixLevels(io, dir, logicalPath, Basename(dir));
            if (own["count"]!.GetValue<int>() > 0) children.Add(own);
        }
        var pack = new JsonObject
        {
            ["kind"] = "pack", ["engine"] = "lemmix",
            ["name"] = info != null && First(info, "TITLE") is { Length: > 0 } title ? title : Basename(dir).Replace('_', ' '),
            ["path"] = logicalPath,
            ["dir"] = dir,
            ["count"] = Count(children),
            ["children"] = children,
        };
        if (info != null && First(info, "AUTHOR") is { Length: > 0 } author) pack["author"] = author;
        if (info != null && First(info, "VERSION") is { Length: > 0 } version) pack["version"] = version;
        if (io.Exists(Join(dir, "logo.png"))) pack["logo"] = Join(dir, "logo.png");
        if (io.Exists(Join(dir, "skill_panels.png"))) pack["panel"] = true;
        if (music.Count > 0) pack["musicRotation"] = new JsonArray(music.Select(m => (JsonNode?)JsonValue.Create(m)).ToArray());
        if (musicDir != null)
        {
            pack["musicDir"] = musicDir;
            pack["musicFiles"] = new JsonArray(ListFilesBelow(io, musicDir).Select(m => (JsonNode?)JsonValue.Create(m)).ToArray());
        }
        return pack;
    }

    // A folder of .nxlv files, in the order its levels.nxmi lists them.
    static JsonObject LemmixLevels(TreeSource io, string dir, string logicalPath, string name)
    {
        List<string>? files = null;
        if (io.Exists(Join(dir, "levels.nxmi")))
            files = All(ParseNx(io.ReadText(Join(dir, "levels.nxmi"))), "LEVEL").Where(f => io.Exists(Join(dir, f))).ToList();
        if (files == null || files.Count == 0) files = io.ListFiles(dir, ".nxlv");
        var levels = new JsonArray();
        foreach (string file in files)
        {
            string url = Join(dir, file);
            var level = new JsonObject { ["id"] = logicalPath + "/" + file, ["file"] = file, ["url"] = url };
            foreach (var kv in ReadLevelHeader(io, url).ToList()) { var v = kv.Value; kv.Value?.Parent?.AsObject().Remove(kv.Key); level[kv.Key] = v; }
            levels.Add(level);
        }
        return new JsonObject { ["kind"] = "dir", ["engine"] = "lemmix", ["name"] = name, ["path"] = logicalPath, ["count"] = levels.Count, ["levels"] = levels };
    }

    // Any folder under levels/: a pack, a wrapper of packs, or loose levels.
    static JsonObject? LemmixNode(TreeSource io, string dir, string logicalPath, string? musicDir)
    {
        string name = Basename(dir);
        if (io.Exists(Join(dir, "levels.nxmi"))) return LemmixPack(io, dir, logicalPath, musicDir);
        var dirs = io.ListDirs(dir);
        var nxlv = io.ListFiles(dir, ".nxlv");
        // the wrapper a downloaded collection ships as: levels/ (and music/) only
        if (nxlv.Count == 0 && dirs.Contains("levels") && !dirs.Any(d => d != "levels" && d != "music"))
        {
            string inner = Join(dir, "levels");
            string? music = io.IsDir(Join(dir, "music")) ? Join(dir, "music") : musicDir;
            var wrapped = new JsonArray();
            foreach (string d in io.ListDirs(inner))
            {
                var n = LemmixNode(io, Join(inner, d), logicalPath + "/" + d, music);
                if (n != null && n["count"]!.GetValue<int>() > 0) wrapped.Add(n);
            }
            return new JsonObject
            {
                ["kind"] = "dir", ["engine"] = "lemmix", ["name"] = name.Replace('_', ' '), ["path"] = logicalPath,
                ["count"] = Count(wrapped), ["children"] = wrapped,
            };
        }
        if (nxlv.Count > 0 && dirs.Count == 0) return LemmixLevels(io, dir, logicalPath, name);
        var children = new JsonArray();
        if (nxlv.Count > 0) children.Add(LemmixLevels(io, dir, logicalPath, name));
        foreach (string d in dirs)
        {
            var n = LemmixNode(io, Join(dir, d), logicalPath + "/" + d, musicDir);
            if (n != null && n["count"]!.GetValue<int>() > 0) children.Add(n);
        }
        if (children.Count == 0) return null;
        return new JsonObject
        {
            ["kind"] = "dir", ["engine"] = "lemmix", ["name"] = name.Replace('_', ' '), ["path"] = logicalPath,
            ["count"] = Count(children), ["children"] = children,
        };
    }

    // The whole tree (buildIndex with no classic packs).
    public static JsonObject Build(TreeSource io, DateTime? generated = null)
    {
        var children = new JsonArray();
        if (io.IsDir(LevelsDir))
            foreach (string d in io.ListDirs(LevelsDir))
            {
                var node = LemmixNode(io, Join(LevelsDir, d), d, null);
                if (node != null && node["count"]!.GetValue<int>() > 0) children.Add(node);
            }
        return new JsonObject
        {
            ["version"] = 1,
            ["generated"] = (generated ?? DateTime.UtcNow).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture),
            ["count"] = Count(children),
            ["children"] = children,
        };
    }
}
