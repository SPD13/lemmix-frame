using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lemmix.Index;
using Lemmix.Oracle;
using Lemmix.Tests.Oracle;

namespace Lemmix.Tests.Index;

// oracle/indexes.js: the three indexes as the web's builders make them, JSON text for JSON text
// (`generated` blanked), and localeCompare(numeric, base) on 6000 pairs of file-like names.
public class IndexTests
{
    static readonly JsonSerializerOptions Js = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    static string Text(JsonObject o) { o["generated"] = ""; return o.ToJsonString(Js); }

    static string Hash(string text) { var h = new StateHash(); h.Str(text); return h.Hex(); }

    [Fact]
    public void IndexesAreTheWebBuildersOutput()
    {
        using var doc = OracleData.Load("indexes.json");
        Assert.SkipWhen(doc == null || !OracleData.HasAssets, "no oracle output or assets");
        var io = new TreeSource(OracleData.AssetsDir);
        var built = new Dictionary<string, string>
        {
            ["levels"] = Text(LevelsIndex.Build(io)),
            ["styles"] = Text(StylesIndex.Build(io)),
            ["music"] = Text(MusicIndex.Build(io)),
        };
        var wrong = new List<string>();
        foreach (var (k, text) in built)
        {
            if (Hash(text) == doc!.RootElement.GetProperty("hashes").GetProperty(k).GetString()) continue;
            // say where: the first differing character against the full JSON, when it is there
            string full = Path.Combine(OracleData.OracleDir, "index", k + ".json");
            string where = "";
            if (File.Exists(full))
            {
                string web = File.ReadAllText(full);
                int i = 0;
                while (i < web.Length && i < text.Length && web[i] == text[i]) i++;
                int from = Math.Max(0, i - 120);
                where = $"\n  web : …{web.Substring(from, Math.Min(240, web.Length - from))}\n  port: …{text.Substring(from, Math.Min(240, text.Length - from))}";
            }
            wrong.Add(k + where);
        }
        Assert.True(wrong.Count == 0, "differ: " + string.Join("\n", wrong));
    }

    [Fact]
    public void NaturalCompareIsLocaleCompareNumericBase()
    {
        using var doc = OracleData.Load("indexes.json");
        Assert.SkipWhen(doc == null, "no oracle output");
        var wrong = new List<string>();
        foreach (var p in doc!.RootElement.GetProperty("collation").EnumerateArray())
        {
            string a = p[0].GetString()!, b = p[1].GetString()!;
            int expected = p[2].GetInt32(), got = Math.Sign(NaturalCompare.Instance.Compare(a, b));
            if (expected != got) wrong.Add($"\"{a}\" vs \"{b}\": web {expected}, port {got}");
        }
        Assert.True(wrong.Count == 0, $"{wrong.Count} pairs differ:\n" + string.Join("\n", wrong.Take(25)));
    }
}
