using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Lemmix.Index;

// web/tools/styles-index.js - neolemmix/styles/index.json: every style's terrain pieces, which
// have a .nxmt and which are steel, its theme's sprite set and which optional files it has (the
// engine's StyleManager reads the flags and `metas` to ask only for files that exist).
public static class StylesIndex
{
    public const string StylesDir = "neolemmix/styles";
    public const string IndexFile = "index.json";

    // The display names of styles.ini: [folder] sections with a Name= line.
    public static Dictionary<string, string> ReadStylesIni(string? text)
    {
        var titles = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(text)) return titles;
        string? current = null;
        foreach (string raw in Regex.Split(text, "\r?\n"))
        {
            string line = Util.JsString.Trim(raw);
            if (line == "" || line[0] == ';' || line[0] == '#') continue;
            var m = Regex.Match(line, @"^\[(.+)\]$", RegexOptions.ECMAScript);
            if (m.Success) { current = Util.JsString.Trim(m.Groups[1].Value).ToLowerInvariant(); continue; }
            int eq = line.IndexOf('=');
            if (current != null && eq > 0 && Util.JsString.Trim(line[..eq]).ToLowerInvariant() == "name")
                titles[current] = Util.JsString.Trim(line[(eq + 1)..]);
        }
        return titles;
    }

    static string ReadOr(TreeSource io, string p, string dflt)
    {
        try { return io.Exists(p) ? io.ReadText(p) : dflt; } catch (Exception) { return dflt; }
    }

    static string Ext(string f) { int i = f.LastIndexOf('.'); return i < 0 ? "" : f[i..].ToLowerInvariant(); }
    static string Stem(string f) { int i = f.LastIndexOf('.'); return (i < 0 ? f : f[..i]).ToLowerInvariant(); }
    static readonly Regex Steel = new(@"^\s*STEEL\b", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.ECMAScript);
    static readonly Regex Lemmings = new(@"^\s*LEMMINGS\s+(.+?)\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.ECMAScript);

    public static (List<string> Pieces, List<string> Steel, List<string> Metas) ListPieces(TreeSource io, string styleDir)
    {
        string dir = styleDir + "/terrain";
        if (!io.IsDir(dir)) return (new(), new(), new());
        var names = new List<string>();
        var metas = new Dictionary<string, string>(StringComparer.Ordinal); // lowercased stem -> the .nxmt file name
        foreach (string f in io.ListFiles(dir))
        {
            string e = Ext(f);
            if (e == ".png") { if (!names.Contains(Stem(f))) names.Add(Stem(f)); }
            else if (e == ".nxmt") metas[Stem(f)] = f;
        }
        var pieces = NaturalCompare.Sort(names);
        var withMeta = pieces.Where(metas.ContainsKey).ToList();
        var steel = withMeta.Where(n => Steel.IsMatch(ReadOr(io, dir + "/" + metas[n], ""))).ToList();
        return (pieces, steel, withMeta);
    }

    public static string ReadTheme(TreeSource io, string styleDir)
    {
        string? text = io.Exists(styleDir + "/theme.nxtm") ? ReadOr(io, styleDir + "/theme.nxtm", "") : null;
        if (text == null) return "default";
        var m = Lemmings.Match(text);
        return m.Success ? m.Groups[1].Value.ToLowerInvariant() : "default";
    }

    static JsonArray Arr(IEnumerable<string> items) => new(items.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());

    public static JsonObject Build(TreeSource io, DateTime? generated = null)
    {
        var styles = new JsonArray();
        int pieceCount = 0;
        if (io.IsDir(StylesDir))
        {
            var titles = ReadStylesIni(ReadOr(io, StylesDir + "/styles.ini", ""));
            foreach (string name in io.ListDirs(StylesDir))
            {
                string dir = StylesDir + "/" + name;
                var (pieces, steel, metas) = ListPieces(io, dir);
                styles.Add(new JsonObject
                {
                    ["name"] = name.ToLowerInvariant(),
                    ["title"] = titles.TryGetValue(name.ToLowerInvariant(), out var t) && t != "" ? t : name,
                    ["theme"] = ReadTheme(io, dir),
                    ["hasTheme"] = io.Exists(dir + "/theme.nxtm"),
                    ["hasAlias"] = io.Exists(dir + "/alias.nxmi"),
                    ["pieces"] = Arr(pieces), ["steel"] = Arr(steel), ["metas"] = Arr(metas), ["count"] = pieces.Count,
                });
                pieceCount += pieces.Count;
            }
        }
        return new JsonObject
        {
            ["version"] = 2,
            ["generated"] = (generated ?? DateTime.UtcNow).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture),
            ["count"] = styles.Count,
            ["pieceCount"] = pieceCount,
            ["styles"] = styles,
        };
    }
}

// web/tools/music-index.js - neolemmix/music/index.json: the music packs' files, natural order.
public static class MusicIndex
{
    public const string MusicDir = "neolemmix/music";

    public static JsonObject Build(TreeSource io, DateTime? generated = null)
    {
        var files = LevelsIndex.ListFilesBelow(io, MusicDir).Where(f => f != StylesIndex.IndexFile).ToList();
        return new JsonObject
        {
            ["version"] = 1,
            ["generated"] = (generated ?? DateTime.UtcNow).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture),
            ["count"] = files.Count,
            ["files"] = new JsonArray(files.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()),
        };
    }
}
