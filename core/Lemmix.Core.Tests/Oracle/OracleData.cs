using System.Text.Json;
using Lemmix.Engine;
using Lemmix.Io;

namespace Lemmix.Tests.Oracle;

// Where the tests find the oracle output (oracle/out, or ORACLE_DIR in the Linux container) and
// the assets (WEB_ASSETS, by default ../LemmingsJS next to the repo). Tests that need them are
// skipped, not failed, when they are absent (CI without the copyrighted assets).
public static class OracleData
{
    public static string RepoRoot { get; } = FindRepoRoot();

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Makefile"))) dir = dir.Parent;
        return dir?.FullName ?? AppContext.BaseDirectory;
    }

    public static string OracleDir => Environment.GetEnvironmentVariable("ORACLE_DIR") ?? Path.Combine(RepoRoot, "oracle", "out");
    public static string AssetsDir => Environment.GetEnvironmentVariable("WEB_ASSETS") ?? Path.GetFullPath(Path.Combine(RepoRoot, "..", "LemmingsJS"));

    public static bool HasAssets => File.Exists(Path.Combine(AssetsDir, "levels", "index.json"));

    // the file, or its .gz twin (the big oracle files are committed gzipped)
    public static JsonDocument? Load(string relative)
    {
        string p = Path.Combine(OracleDir, relative);
        if (File.Exists(p)) return JsonDocument.Parse(File.ReadAllText(p));
        if (!File.Exists(p + ".gz")) return null;
        using var gz = new System.IO.Compression.GZipStream(File.OpenRead(p + ".gz"), System.IO.Compression.CompressionMode.Decompress);
        return JsonDocument.Parse(gz);
    }

    static StyleManager? _styles;
    static DiskFileSource? _io;
    public static DiskFileSource Io => _io ??= new DiskFileSource(AssetsDir);
    public static StyleManager Styles => _styles ??= new StyleManager(Io);

    public static Level BuildLevel(string id, string url)
    {
        var data = LevelBuilder.ParseLevel(Io.Text(url) ?? throw new FileNotFoundException(url));
        return LevelBuilder.Build(data, Styles, id);
    }
}
