namespace Lemmix.Index;

// The io the index builders read folders through (levels-index.js nodeIO): "/"-joined paths
// relative to the asset root; listings without dot entries, in natural order.
// The web version runs these builders on macOS or in the browser, where "terrain" finds a style's
// "Terrain" folder; the Frame's filesystem is case-sensitive, so a path that does not exist as
// written is resolved one segment at a time, ignoring case (as Io.DiskFileSource does).
public sealed class TreeSource
{
    readonly string _root;
    readonly Dictionary<string, string?> _resolved = new(StringComparer.Ordinal);

    public TreeSource(string root) { _root = Path.GetFullPath(root); }

    string Abs(string p)
    {
        var parts = p.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string direct = Path.Combine(_root, Path.Combine(parts));
        if (File.Exists(direct) || Directory.Exists(direct)) return direct;
        if (_resolved.TryGetValue(p, out var hit)) return hit ?? direct;
        string current = _root;
        foreach (string part in parts)
        {
            string next = Path.Combine(current, part);
            if (!File.Exists(next) && !Directory.Exists(next))
            {
                if (!Directory.Exists(current)) { _resolved[p] = null; return direct; }
                string? match = Directory.EnumerateFileSystemEntries(current)
                    .FirstOrDefault(e => string.Equals(Path.GetFileName(e), part, StringComparison.OrdinalIgnoreCase));
                if (match == null) { _resolved[p] = null; return direct; }
                next = match;
            }
            current = next;
        }
        _resolved[p] = current;
        return current;
    }

    public string ReadText(string p) => Io.DiskFileSource.DecodeUtf8(File.ReadAllBytes(Abs(p)));
    public bool Exists(string p) { string a = Abs(p); return File.Exists(a) || Directory.Exists(a); }
    public bool IsDir(string p) => Directory.Exists(Abs(p));

    public List<string> ListDirs(string p) => !IsDir(p) ? new() : NaturalCompare.Sort(
        Directory.EnumerateDirectories(Abs(p)).Select(Path.GetFileName).OfType<string>().Where(n => !n.StartsWith('.')));

    public List<string> ListFiles(string p, string? ext = null) => !IsDir(p) ? new() : NaturalCompare.Sort(
        Directory.EnumerateFiles(Abs(p)).Select(Path.GetFileName).OfType<string>()
            .Where(n => ext == null || n.ToLowerInvariant().EndsWith(ext, StringComparison.Ordinal)));
}
