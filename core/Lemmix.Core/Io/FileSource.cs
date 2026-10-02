using StbImageSharp;
using Lemmix.Engine;

namespace Lemmix.Io;

// The io the engine reads through (web: styles.js browserIO / tools/lemmix-node.js nodeIO):
// files relative to the asset root ("neolemmix/styles/…", "levels/…"), null when absent.
public interface IFileSource
{
    string? Text(string path);
    byte[]? Bytes(string path);
    Bitmap? Image(string path);
}

// Files on disk under one root. The Frame's filesystem is case-sensitive while the styles
// package mixes `Objects/` and `objects/` and the engine asks in lower case, so lookups that
// miss are retried through a case-insensitive map of the tree, built once per directory.
public sealed class DiskFileSource : IFileSource
{
    readonly string _root;
    readonly Dictionary<string, Dictionary<string, string>?> _dirs = new(StringComparer.Ordinal);

    public DiskFileSource(string root) { _root = Path.GetFullPath(root); }

    public string Root => _root;

    public string? Resolve(string path)
    {
        string direct = Path.Combine(_root, path);
        if (File.Exists(direct)) return direct;
        string current = _root;
        foreach (string part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var entries = Entries(current);
            if (entries == null || !entries.TryGetValue(part.ToLowerInvariant(), out var real)) return null;
            current = real;
        }
        return File.Exists(current) ? current : null;
    }

    Dictionary<string, string>? Entries(string dir)
    {
        lock (_dirs)
        {
            if (_dirs.TryGetValue(dir, out var cached)) return cached;
            Dictionary<string, string>? map = null;
            if (Directory.Exists(dir))
            {
                map = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (string e in Directory.EnumerateFileSystemEntries(dir))
                    map.TryAdd(Path.GetFileName(e).ToLowerInvariant(), e);
            }
            _dirs[dir] = map;
            return map;
        }
    }

    public byte[]? Bytes(string path)
    {
        string? p = Resolve(path);
        return p == null ? null : File.ReadAllBytes(p);
    }

    // fs.readFileSync(…, "utf8"): a BOM stays in the text (the parser's trim removes it)
    public string? Text(string path)
    {
        byte[]? b = Bytes(path);
        return b == null ? null : DecodeUtf8(b);
    }

    public Bitmap? Image(string path)
    {
        byte[]? b = Bytes(path);
        return b == null ? null : Png.Decode(b);
    }

    // Node's utf8 decoding keeps U+FEFF and replaces invalid sequences with U+FFFD, as .NET's does.
    public static string DecodeUtf8(byte[] b) => new System.Text.UTF8Encoding(false, false).GetString(b);
}

public static class Png
{
    // RGBA 8-bit, the layout pngjs gives the oracle (tools/lemmix-node.js nodeIO.image).
    public static Bitmap Decode(byte[] png)
    {
        var img = ImageResult.FromMemory(png, ColorComponents.RedGreenBlueAlpha);
        return new Bitmap(img.Width, img.Height, img.Data);
    }
}
