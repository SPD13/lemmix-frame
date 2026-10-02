using Lemmix.Engine;
using Lemmix.Io;
using Lemmix.Oracle;
using Lemmix.Tests.Oracle;
using Lemmix.Ui;

namespace Lemmix.Tests.Ui;

// oracle/cursor.js: the six cursor pictures, 16 and 32 px, with the assets as they are, without
// one cursor-hr file and without one cursor file.
public class CursorTests
{
    // the assets with some files missing
    sealed class Without : IFileSource
    {
        readonly IFileSource _io; readonly HashSet<string> _missing;
        public Without(IFileSource io, IEnumerable<string> missing) { _io = io; _missing = new HashSet<string>(missing, StringComparer.Ordinal); }
        public string? Text(string path) => _missing.Contains(path) ? null : _io.Text(path);
        public byte[]? Bytes(string path) => _missing.Contains(path) ? null : _io.Bytes(path);
        public Bitmap? Image(string path) => _missing.Contains(path) ? null : _io.Image(path);
    }

    static readonly Dictionary<string, string[]> Scenarios = new()
    {
        ["assets"] = Array.Empty<string>(),
        ["no-hr"] = new[] { "neolemmix/gfx/cursor-hr/direction_right.png" },
        ["no-cursor"] = new[] { "neolemmix/gfx/cursor/focused.png" },
    };

    [Fact]
    public void CursorPicturesMatchTheWebPage()
    {
        using var doc = OracleData.Load("cursor.json");
        Assert.SkipWhen(doc == null || !OracleData.HasAssets, "no oracle output or assets");
        var failures = new List<string>();
        foreach (var (name, missing) in Scenarios)
        {
            var want = doc!.RootElement.GetProperty("scenarios").GetProperty(name);
            var cursor = CursorImages.Load(new Without(OracleData.Io, missing));
            if (cursor.Ok != want.GetProperty("ok").GetBoolean()) { failures.Add($"{name}: ok={cursor.Ok}"); continue; }
            var keys = want.GetProperty("keys").EnumerateObject().ToList();
            if (keys.Count != cursor.Small.Count) failures.Add($"{name}: {cursor.Small.Count} pictures, web {keys.Count}");
            foreach (var k in keys)
            {
                var h = new StateHash();
                foreach (var c in new[] { cursor.Small.GetValueOrDefault(k.Name), cursor.Large.GetValueOrDefault(k.Name) }) UiHash.Bitmap(h, c);
                if (h.Hex() != k.Value.GetString()) failures.Add($"{name}: {k.Name} differs");
                bool focused = k.Name.StartsWith("focused", StringComparison.Ordinal);
                int dx = k.Name.EndsWith("-left", StringComparison.Ordinal) ? -1 : k.Name.EndsWith("-right", StringComparison.Ordinal) ? 1 : 0;
                if (CursorImages.Key(focused, dx) != k.Name) failures.Add($"key {k.Name}");
            }
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }
}
