using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Godot;
using Lemmix.App.Ui;
using Lemmix.App.Ui.Windows;

namespace Lemmix.App.Test;

// The web's own windows, as oracle/webshots.js recorded them (app/Test/Fixtures/windows.json): for
// every state its canvas size, the draw calls its paint made, and what it was painted from. Make
// builds the port's window in the same state, with its paint traced.
public static class WindowFixture
{
    static JsonObject? _f;
    public static JsonObject F => _f ??= JsonNode.Parse(Godot.FileAccess.GetFileAsString("res://Test/Fixtures/windows.json"))!.AsObject();
    public static JsonObject Shots => F["shots"]!.AsObject();
    public static JsonObject Layout => F["layout"]!.AsObject();
    public static IEnumerable<string> Names => Shots.Select(kv => kv.Key);
    public static List<string> WebTrace(string name) => Shots[name]!["trace"]!.AsArray().Select(t => t!.GetValue<string>()).ToList();

    // the window in that state: the panel showing it, its traced paint, the node to add or free
    public sealed record Made(Panel3D Panel, List<string> Trace, Node Owner);

    static readonly Dictionary<string, Texture2D> Thumbs = new();
    public static Texture2D Thumb(string levelId)
    {
        if (Thumbs.TryGetValue(levelId, out var t)) return t;
        string url = F["thumbs"]![levelId]!.GetValue<string>();
        var img = new Image();
        img.LoadPngFromBuffer(Convert.FromBase64String(url[(url.IndexOf(',') + 1)..]));
        return Thumbs[levelId] = ImageTexture.CreateFromImage(img);
    }

    public static List<CatalogItem> CatalogItems(JsonObject c) => c["items"]!.AsArray().Select(n =>
    {
        var o = n!.AsObject();
        string? S(string k) => o[k]?.GetValue<string>();
        bool B(string k) => o[k]?.GetValue<bool>() ?? false;
        int I(string k) => o[k]?.GetValue<int>() ?? 0;
        return new CatalogItem
        {
            Kind = S("kind")!, Label = S("label") ?? "", LevelId = S("levelId"), Playable = o["playable"]?.GetValue<bool>() ?? true,
            Name = S("name") ?? "", Set = S("set") ?? "", Best = o["best"] is JsonNode b && b.GetValueKind() == System.Text.Json.JsonValueKind.Number ? b.GetValue<double>() : null,
            Solution = B("solution"), Favorite = B("favorite"), Current = B("current"),
            Engine = S("engine"), Path = S("path"), Count = I("count"), Done = I("done"),
            Thumb = S("thumb") is string id ? Thumb(id) : null,
        };
    }).ToList();

    sealed class FakeEffects : IVrEffects
    {
        readonly JsonObject _r;
        public FakeEffects(JsonObject rows) { _r = rows; }
        public bool Emboss => _r["emboss"]!.GetValue<bool>();
        public bool Doors => _r["doors"]!.GetValue<bool>();
        public bool Smooth => _r["smooth"]!.GetValue<bool>();
        public bool SmoothTerrain => _r["smoothTerrain"]!.GetValue<bool>();
        public string ColorBlend => _r["colorBlend"]!.GetValue<string>();
        public bool SkillBar => _r["skillBar"]!.GetValue<bool>();
        public bool FlatSkills => _r["flatSkills"]!.GetValue<bool>();
        public string Environment => _r["environment"]!.GetValue<string>();
        public void ToggleEmboss() { } public void ToggleDoors() { } public void ToggleSmooth() { } public void ToggleSmoothTerrain() { }
        public void ToggleColorBlend() { } public void ToggleSkillBar() { } public void ToggleFlatSkills() { } public void ToggleEnvironment() { }
        public void Recenter() { }
    }

    static List<string> Traced(Canvas2D c, Action paint)
    {
        var t = new List<string>();
        c.Trace = t;
        paint();
        c.Trace = null;
        return t;
    }

    static List<string> Lines(JsonNode? n) => n!.AsArray().Select(x => x!.GetValue<string>()).ToList();

    public static Made Make(string name)
    {
        var s = Shots[name]!.AsObject();
        if (name.StartsWith("icon-", StringComparison.Ordinal))
        {
            string bare = name[5..].Replace("-on", "").Replace("-hover", "");
            var b = new IconButton(bare, BarIcons.ByName(bare)!) { Visible = true };
            var st = s["state"]!;
            var t = Traced(b.Canvas, () => { b.State.On = st["on"]!.GetValue<bool>(); b.State.Hovered = st["hovered"]!.GetValue<bool>(); b.Repaint(); });
            return new Made(b, t, b);
        }
        if (name.StartsWith("volume-", StringComparison.Ordinal))
        {
            var bar = new VrToolbar();
            var t = Traced(bar.Volume.Canvas, () => bar.PaintVolume(s["level"]!.GetValue<float>(), s["hovered"]!.GetValue<bool>()));
            bar.Volume.Visible = true;
            return new Made(bar.Volume, t, bar.GuiRoot);
        }
        if (name.StartsWith("modal-", StringComparison.Ordinal))
        {
            var m = new VrModal();
            var t = Traced(m.Panel.Canvas, () => m.Ask(s["title"]!.GetValue<string>(), s["body"]?.GetValue<string>()));
            m.Root.Visible = true;
            return new Made(m.Panel, t, m.Root);
        }
        if (name.StartsWith("status-", StringComparison.Ordinal))
        {
            var st = new VrStatusStrip();
            st.Status = new StatusModel(s["name"]!.GetValue<string>(), s["meta"]!.GetValue<string>(), s["note"]!.GetValue<string>(), s["kind"]!.GetValue<string>());
            var t = Traced(st.Panel.Canvas, st.Paint);
            st.Panel.Visible = true;
            return new Made(st.Panel, t, st.Root);
        }
        if (name.StartsWith("leveltext-", StringComparison.Ordinal))
        {
            var w = new VrLevelText();
            var lines = Lines(s["lines"]);
            bool hot = s["hot"]!.GetValue<bool>();
            if (hot) w.Paint(lines); // the web lit the OK of a window already showing the text
            w.OkHot = hot;
            var t = Traced(w.Panel.Canvas, () => w.Paint(lines));
            w.Root.Visible = true;
            return new Made(w.Panel, t, w.Root);
        }
        if (name.StartsWith("settings", StringComparison.Ordinal))
        {
            var w = new VrSettings(VrSettings.Rows(new FakeEffects(s["rows"]!.AsObject())));
            w.Hover = s["hover"]!.GetValue<int>();
            var t = Traced(w.Panel.Canvas, w.Paint);
            w.Root.Visible = true;
            return new Made(w.Panel, t, w.Root);
        }
        if (name.StartsWith("tip-", StringComparison.Ordinal))
        {
            var tip = new VrTooltip(_ => null);
            var t = Traced(tip.Panel.Canvas, () => tip.Paint(s["text"]!.GetValue<string>()));
            tip.Panel.Visible = true;
            return new Made(tip.Panel, t, tip.Panel);
        }
        if (name == "replay-badge")
        {
            var b = new VrReplayBadge();
            b.Panel.Visible = true;
            return new Made(b.Panel, new List<string>(), b.Panel);
        }
        if (name.StartsWith("catalog-", StringComparison.Ordinal))
        {
            var c = F["catalog"]![s["catalog"]!.GetValue<string>()]!.AsObject();
            var cat = new VrCatalog();
            cat.SetList(CatalogItems(c), c["heading"]!.GetValue<string>(), c["note"]!.GetValue<string>());
            switch (c["scroll"]!.GetValue<string>())
            {
                case "reveal": cat.RevealItem(cat.Items.FindIndex(it => it.Current)); break;
                case "end": cat.ScrollTo(1e6f); break;
            }
            cat.Hover = c["hover"]!.GetValue<int>();
            var t = Traced(cat.Panel.Canvas, cat.Paint);
            cat.Root.Visible = true;
            return new Made(cat.Panel, t, cat.Root);
        }
        throw new ArgumentException("no such window state: " + name);
    }

    // ---- comparing two traces: the same calls in the same order, numbers within `eps`
    //
    // Fonts differ (Chrome's monospace on the Mac is Menlo, 0.602 em a glyph; the port draws Noto
    // Sans Mono, 0.6 em, as Android's monospace is): with fontSlack a label trimmed to fit may keep
    // one glyph more or less ("NeoLemmix …" / "NeoLemmix I…"), and a canvas sized to its text (the
    // tooltip) may be up to 0.6% narrower; `accepted` collects those.
    public static List<string> Compare(List<string> web, List<string> port, float eps = 0.01f, bool fontSlack = false, List<string>? accepted = null)
    {
        var diffs = new List<string>();
        int n = Math.Max(web.Count, port.Count);
        for (int i = 0; i < n; i++)
        {
            string? a = i < web.Count ? web[i] : null, b = i < port.Count ? port[i] : null;
            if (a == b) continue;
            if (a != null && b != null && Same(a, b, eps)) continue;
            if (a != null && b != null && Trimmed(a, b)) { accepted?.Add($"#{i}: web {a} | port {b}"); continue; }
            if (fontSlack && a != null && b != null && Same(a, b, eps, 0.006)) { accepted?.Add($"#{i}: web {a} | port {b}"); continue; }
            diffs.Add($"#{i}: web {a ?? "(none)"} | port {b ?? "(none)"}");
        }
        return diffs;
    }

    // two fillTexts of one label cut to fit at different glyphs
    static bool Trimmed(string a, string b)
    {
        var (na, xa) = Split(a);
        var (nb, xb) = Split(b);
        if (na != "fillText" || nb != "fillText" || xa.Count != xb.Count) return false;
        string ta = xa[0].Trim('"'), tb = xb[0].Trim('"');
        if (!ta.EndsWith("…") && !tb.EndsWith("…")) return false;
        string pa = ta.TrimEnd('…'), pb = tb.TrimEnd('…');
        if (!(pa.StartsWith(pb) || pb.StartsWith(pa)) || Math.Abs(pa.Length - pb.Length) > 1) return false;
        for (int i = 1; i < xa.Count; i++) if (xa[i] != xb[i]) return false;
        return true;
    }

    static bool Same(string a, string b, float eps, double relative = 0)
    {
        var (na, xa) = Split(a);
        var (nb, xb) = Split(b);
        if (na != nb || xa.Count != xb.Count) return false;
        for (int i = 0; i < xa.Count; i++)
        {
            if (xa[i] == xb[i]) continue;
            if (double.TryParse(xa[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var da) &&
                double.TryParse(xb[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var db) &&
                Math.Abs(da - db) <= Math.Max(eps, relative * Math.Abs(da) + (relative > 0 ? 0.5 : 0))) continue;
            return false;
        }
        return true;
    }

    // "name(1,\"a,b\",2)" -> ("name", ["1", "\"a,b\"", "2"]); "lineWidth=3" -> ("lineWidth", ["3"])
    static (string, List<string>) Split(string call)
    {
        int eq = call.IndexOf('='), par = call.IndexOf('(');
        if (eq >= 0 && (par < 0 || eq < par)) return (call[..eq], new List<string> { call[(eq + 1)..] });
        if (par < 0) return (call, new List<string>());
        var args = new List<string>();
        var cur = new System.Text.StringBuilder();
        bool inStr = false;
        for (int i = par + 1; i < call.Length - 1; i++)
        {
            char ch = call[i];
            if (inStr && ch == '\\') { cur.Append(ch).Append(call[++i]); continue; }
            if (ch == '"') inStr = !inStr;
            if (ch == ',' && !inStr) { args.Add(cur.ToString()); cur.Clear(); continue; }
            cur.Append(ch);
        }
        if (cur.Length > 0 || args.Count > 0) args.Add(cur.ToString());
        return (call[..par], args);
    }
}
