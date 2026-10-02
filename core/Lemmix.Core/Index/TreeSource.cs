namespace Lemmix.Index;

// The io the index builders read folders through (levels-index.js nodeIO): "/"-joined paths
// relative to the asset root; listings without dot entries, in natural order.
public sealed class TreeSource
{
    readonly string _root;
    public TreeSource(string root) { _root = Path.GetFullPath(root); }

    string Abs(string p) => Path.Combine(_root, Path.Combine(p.Split('/', StringSplitOptions.RemoveEmptyEntries)));

    public string ReadText(string p) => Io.DiskFileSource.DecodeUtf8(File.ReadAllBytes(Abs(p)));
    public bool Exists(string p) => File.Exists(Abs(p)) || Directory.Exists(Abs(p));
    public bool IsDir(string p) => Directory.Exists(Abs(p));

    public List<string> ListDirs(string p) => !IsDir(p) ? new() : NaturalCompare.Sort(
        Directory.EnumerateDirectories(Abs(p)).Select(Path.GetFileName).OfType<string>().Where(n => !n.StartsWith('.')));

    public List<string> ListFiles(string p, string? ext = null) => !IsDir(p) ? new() : NaturalCompare.Sort(
        Directory.EnumerateFiles(Abs(p)).Select(Path.GetFileName).OfType<string>()
            .Where(n => ext == null || n.ToLowerInvariant().EndsWith(ext, StringComparison.Ordinal)));
}
