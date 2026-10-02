using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;
using Lemmix.Library;

namespace Lemmix.App.Ui.Pages;

// web/solutions.html + web/3d/js/solutions.js in the headset: every Lemmix level as a row, a
// check where a solution exists and what it is worth - saved, skills, the clock, the tier that
// found it - the count in the head, the fuzzy search (typed on the VR keyboard or a Bluetooth
// one), the pack and show filters, the sort by column, "play level" and "▶ play solution". The
// launcher's solver queue (the server row, the checkboxes, the state column) is not the native
// app's and is left out.
public sealed class VrSolutionsPage : VrPage
{
    public const int PAGE_W = 1456, PAGE_H = 1000;
    public readonly ISolutionsBackend Backend;

    public string Query = "", Pack = "", Show = "all";
    public string SortKey = "level";
    public int SortDir = 1;
    IReadOnlyList<SolutionLevel> _levels = Array.Empty<SolutionLevel>();
    List<(string Folder, List<string> Packs)> _folders = new();
    List<string> _packs = new();
    public List<Row> Shown = new();

    public sealed record Row(SolutionLevel L, RowData D, double Score);
    public sealed record RowData(bool Solved, bool NotFound, double Saved, double Skills, double Time, double Tier, SolutionInfo? Rec);

    public VrSolutionsPage(ISolutionsBackend backend) : base("solutions", PAGE_W, PAGE_H, 1.3f)
    {
        Backend = backend;
    }

    public override void Opened() { Load(); Paint(); }

    public void Load()
    {
        _levels = Backend.Levels();
        _folders = new();
        foreach (var l in _levels)
        {
            int i = _folders.FindIndex(f => f.Folder == l.Folder);
            if (i < 0) { _folders.Add((l.Folder, new List<string>())); i = _folders.Count - 1; }
            if (!_folders[i].Packs.Contains(l.Pack)) _folders[i].Packs.Add(l.Pack);
        }
        _packs = _levels.Select(l => l.Pack).Distinct().ToList();
        Render();
    }

    // ---- solutions.js helpers
    /** human: "45s", "1m 10s", "15m". */
    public static string Human(double seconds)
    {
        long s = (long)Math.Max(0, Math.Round(seconds, MidpointRounding.AwayFromZero));
        if (s < 60) return s + "s";
        long m = s / 60, sec = s % 60;
        return sec != 0 ? m + "m " + sec + "s" : m + "m";
    }

    /** mmss: frames (17 a second) as m:ss. */
    public static string MmSs(int frames)
    {
        int s = frames / 17;
        return s / 60 + ":" + (s % 60).ToString("00", CultureInfo.InvariantCulture);
    }

    public RowData DataOf(SolutionLevel l)
    {
        var rec = Backend.Info(l.Id);
        bool nf = rec == null && Backend.NotFound(l.Id);
        return new RowData(rec != null, nf, rec?.Saved ?? -1, rec?.SkillsUsed ?? double.PositiveInfinity,
            rec?.CompletionFrame ?? double.PositiveInfinity, rec != null ? rec.Tier : -Backend.TriedTier(l.Id), rec);
    }

    /** The head's line: how many levels have a solution. */
    public (int Solved, int Total, double Pct, int NotFound) Summary()
    {
        int total = _levels.Count, solved = _levels.Count(l => Backend.Info(l.Id) != null), nf = _levels.Count(l => Backend.NotFound(l.Id));
        double pct = total != 0 ? Math.Round(1000.0 * solved / total, MidpointRounding.AwayFromZero) / 10 : 0;
        return (solved, total, pct, nf);
    }

    public string SummaryText()
    {
        var s = Summary();
        return s.Solved + " of " + s.Total + " levels have a solution (" + Canvas2D.Num(s.Pct) + "%)" + (s.NotFound != 0 ? " · " + s.NotFound + " not found at tier 3" : "") +
            " " + _folders.Count + " level folders, " + _packs.Count + " packs · solutions are those shipped";
    }

    /** visible(): the rows to show, filtered and sorted. */
    public List<Row> Visible()
    {
        string q = Query.Trim();
        var outList = new List<Row>();
        foreach (var l in _levels)
        {
            if (Pack.StartsWith("folder:", StringComparison.Ordinal) && l.Folder != Pack[7..]) continue;
            if (Pack.StartsWith("pack:", StringComparison.Ordinal) && l.Pack != Pack[5..]) continue;
            var d = DataOf(l);
            if (Show == "solved" && !d.Solved) continue;
            if (Show == "unsolved" && (d.Solved || d.NotFound)) continue;
            if (Show == "notfound" && !d.NotFound) continue;
            double score = 0;
            if (q != "")
            {
                score = Search.FuzzyScore(q, string.Join(" ", l.Where) + " " + l.Title + " " + l.Id.Replace('_', ' ').Replace('/', ' '));
                if (score < 0) continue;
            }
            outList.Add(new Row(l, d, score));
        }
        int Mark(RowData d) => d.Solved ? 1 : d.NotFound ? -1 : 0;
        Comparison<Row> cmp = (a, b) =>
        {
            if (q != "" && SortKey == "level") return b.Score.CompareTo(a.Score);
            double x, y;
            switch (SortKey)
            {
                case "level": x = a.L.Order; y = b.L.Order; break;
                case "solved": x = Mark(a.D); y = Mark(b.D); break;
                case "saved": x = a.D.Saved; y = b.D.Saved; break;
                case "skills": x = a.D.Skills; y = b.D.Skills; break;
                case "time": x = a.D.Time; y = b.D.Time; break;
                default: x = a.D.Tier; y = b.D.Tier; break;
            }
            if (x == y) return a.L.Order.CompareTo(b.L.Order);
            return (x < y ? -1 : 1) * SortDir;
        };
        // a stable sort, as Array.prototype.sort
        return outList.Select((r, i) => (r, i)).OrderBy(t => t.r, Comparer<Row>.Create(cmp)).ThenBy(t => t.i).Select(t => t.r).ToList();
    }

    public void Render()
    {
        Shown = Visible();
        ScrollTo(Scroll);
    }

    public void SortBy(string key)
    {
        if (SortKey == key) SortDir = -SortDir;
        else { SortKey = key; SortDir = key is "solved" or "saved" ? -1 : 1; }
        Render();
    }

    public List<(string Value, string Label)> PackOptions()
    {
        var o = new List<(string, string)> { ("", "all packs") };
        foreach (var (folder, subs) in _folders)
        {
            o.Add(("folder:" + folder, folder));
            if (subs.Count > 1 || !subs.Contains(folder)) foreach (var p in subs) o.Add(("pack:" + p, "   " + p));
        }
        return o;
    }

    public static readonly List<(string Value, string Label)> ShowOptions = new()
    {
        ("all", "all levels"), ("solved", "solved"), ("unsolved", "unsolved, tiers left to try"), ("notfound", "not found at tier 3"),
    };

    // ---- input
    protected override void OnPress(string id)
    {
        if (id == "back") { Backend.Back(); return; }
        if (id == "search") { Edit("search", Query); return; }
        if (id == "search:clear") { SetQuery(""); return; }
        if (id == "pack" || id == "show")
        {
            var anchor = RegionRect(id)!.Value;
            if (id == "pack") OpenPopup("pack", PackOptions(), Pack, anchor, 18);
            else OpenPopup("show", ShowOptions, Show, anchor);
            return;
        }
        if (id.StartsWith("sort:", StringComparison.Ordinal)) { SortBy(id[5..]); return; }
        if (id.StartsWith("play:", StringComparison.Ordinal)) { Backend.Play(id[5..], false); return; }
        if (id.StartsWith("sol:", StringComparison.Ordinal)) { Backend.Play(id[4..], true); return; }
    }

    protected override void Choose(string popupId, string value)
    {
        if (popupId == "pack") Pack = value;
        if (popupId == "show") Show = value;
        Scroll = 0;
        Render();
    }

    public void SetQuery(string q)
    {
        Query = q;
        Scroll = 0;
        Render();
        Paint();
    }

    public override void TextChanged(string field, string text) { if (field == "search") SetQuery(text); }
    public override void TextEscape(string field) { if (field == "search") SetQuery(""); }   // Escape clears

    public override bool OnKey(string code, string? text)
    {
        if (code == "Escape" && Popup != null) { Popup = null; Paint(); return true; }
        return false;
    }

    // ---- painting
    const float Pad = 18, ContentW = 1044;
    // the columns: left edges and widths, CSS px from the content's left
    static readonly (string Key, string Head, float X, float W, bool Num)[] Cols =
    {
        ("level", "level", 0, 430, false), ("solved", "✔", 430, 30, false), ("saved", "saved", 460, 100, true),
        ("skills", "skills", 560, 56, true), ("time", "time", 616, 50, true), ("tier", "found at", 666, 140, false), ("", "", 806, 238, false),
    };

    float HeadH;

    protected override void PaintPage()
    {
        float x0 = U(Pad);
        // ---- the head, on its ground
        HeadH = U(14) + U(24) + U(20.8f) + U(8) + U(31.2f) + U(10);
        cx.fillStyle = "rgba(10, 14, 22, 0.82)";
        cx.fillRect(4, 4, W - 8, HeadH - 4);
        float y = U(14);
        cx.font = Font(15, true);
        cx.fillStyle = Css.Green;
        PageText.Spaced(cx, "SOLUTIONS", x0, y + U(12), U(1.2f));
        // ‹ Back to the game, top right (left of the window's close)
        cx.font = Font(12);
        string back = "‹ Back to the game";
        float bw = cx.measureText(back).width, bx = x0 + U(ContentW) - bw;
        cx.fillStyle = Hot("back") ? Css.LinkHot : Css.Link;
        cx.fillText(back, bx, y + U(12));
        Hit("back", bx - U(4), y, bw + U(8), U(24));
        // the summary
        var s = Summary();
        float sy = y + U(24) + U(10.4f);
        float sx = x0;
        void Part(string t, string font, string color) { cx.font = font; cx.fillStyle = color; cx.fillText(t, sx, sy); sx += cx.measureText(t).width; }
        string f13 = Font(13), f13b = Font(13, true);
        Part(s.Solved.ToString(), f13b, Css.Green);
        Part(" of ", f13, Css.Bright);
        Part(s.Total.ToString(), f13b, Css.Green);
        Part(" levels have a solution ", f13, Css.Bright);
        Part("(" + Canvas2D.Num(s.Pct) + "%)", f13, Css.Yellow);
        if (s.NotFound != 0) { Part(" · ", f13, Css.Bright); Part(s.NotFound + " not found at tier 3", f13, Css.Red); }
        sx += U(10);
        Part(_folders.Count + " level folders, " + _packs.Count + " packs · solutions are those shipped", Font(11), Css.Dim);
        // the tools: search, pack, show, how many are shown
        float ty = sy + U(10.4f) + U(8), th = U(31.2f), tx = x0;
        TextInput("search", Query, "search levels (fuzzy)", tx, ty, U(260), th);
        tx += U(268);
        cx.font = Font(11);
        cx.fillStyle = Css.Dim;
        cx.fillText("pack", tx, ty + th / 2);
        tx += cx.measureText("pack").width + U(5);
        string packLabel = PackOptions().FirstOrDefault(o => o.Value == Pack).Label ?? "all packs";
        Select("pack", packLabel.Trim(' '), tx, ty, U(230), th);
        tx += U(238);
        cx.font = Font(11);
        cx.fillStyle = Css.Dim;
        cx.fillText("show", tx, ty + th / 2);
        tx += cx.measureText("show").width + U(5);
        Select("show", ShowOptions.First(o => o.Value == Show).Label, tx, ty, U(230), th);
        tx += U(238);
        cx.font = Font(11);
        cx.fillStyle = Css.Dim;
        cx.fillText(Shown.Count + " shown", tx, ty + th / 2);
        cx.fillStyle = Css.Border;
        cx.fillRect(4, HeadH, W - 8, Math.Max(1, U(1)));

        // ---- the table's head row (sticky)
        float hy = HeadH + U(1), hh = U(32);
        cx.fillStyle = Css.Ground;
        cx.fillRect(4, hy, W - 8, hh);
        foreach (var c in Cols)
        {
            if (c.Key == "") continue;
            bool sorted = SortKey == c.Key;
            cx.font = Font(11, true);
            cx.fillStyle = sorted ? Css.Bright : Css.Dim;
            float cxl = x0 + U(c.X) + U(8), cw = U(c.W) - U(16);
            if (c.Num) { cx.textAlign = "right"; cx.fillText(c.Head, cxl + cw, hy + U(20)); cx.textAlign = "left"; }
            else if (c.Key == "solved") { cx.textAlign = "center"; cx.fillText(c.Head, cxl + cw / 2, hy + U(20)); cx.textAlign = "left"; }
            else cx.fillText(c.Head, cxl, hy + U(20));
            if (Hot("sort:" + c.Key)) { cx.fillStyle = "rgba(255,255,255,0.05)"; cx.fillRect(x0 + U(c.X), hy, U(c.W), hh); }
            Hit("sort:" + c.Key, x0 + U(c.X), hy, U(c.W), hh);
        }
        cx.fillStyle = Css.Border;
        cx.fillRect(x0, hy + hh, U(ContentW), Math.Max(1, U(1)));
        View = new Rect2(4, hy + hh + U(1), W - 8, H - (hy + hh + U(1)) - 6);

        // ---- the rows
        BeginScroll();
        float rh = U(30);
        if (Shown.Count == 0)
        {
            cx.font = Font(12);
            cx.fillStyle = Css.Dim;
            cx.fillText("No level matches.", x0, U(20) + U(9.6f));
        }
        int first = Math.Max(0, (int)Math.Floor(Scroll / rh) - 1), last = Math.Min(Shown.Count, first + (int)(View.Size.Y / rh) + 3);
        for (int i = first; i < last; i++) PaintRow(Shown[i], x0, i * rh, rh);
        EndScroll(Shown.Count == 0 ? U(60) : Shown.Count * rh + U(18));
    }

    void PaintRow(Row r, float x0, float y, float rh)
    {
        var l = r.L;
        var d = r.D;
        string id = l.Id;
        bool hot = Hover != null && (Hover == "row:" + id || Hover == "play:" + id || Hover == "sol:" + id);
        if (hot) { cx.fillStyle = Css.Row; cx.fillRect(x0, y, U(ContentW), rh); }
        Hit("row:" + id, x0, y, U(ContentW), rh);
        cx.fillStyle = "#1a2231";
        cx.fillRect(x0, y + rh - Math.Max(1, U(1)), U(ContentW), Math.Max(1, U(1)));
        float mid = y + rh / 2;
        // level: where › … number, title
        float lx = x0 + U(8), lw = U(430 - 16);
        string where = string.Join(" › ", l.Where) + " ";
        cx.font = Font(12);
        float ww = cx.measureText(where).width;
        cx.fillStyle = Css.Dim;
        cx.fillText(PageText.Fit(cx, where, lw), lx, mid);
        if (ww < lw)
        {
            cx.font = Font(12, true);
            cx.fillStyle = Css.Bright;
            string n = l.Ordinal.ToString();
            cx.fillText(n, lx + ww, mid);
            float nw = cx.measureText(n).width;
            bool hasTitle = l.Title != "" && l.Title != (l.Where.Count > 0 ? l.Where[^1] : "");
            if (hasTitle)
            {
                cx.font = Font(11);
                cx.fillStyle = Css.Dim;
                float tx = lx + ww + nw + U(8);
                cx.fillText(PageText.Fit(cx, l.Title, lx + lw - tx), tx, mid);
            }
        }
        // the mark
        float mx = x0 + U(430 + 15);
        cx.textAlign = "center";
        cx.font = Font(12, true);
        if (d.Solved) { cx.fillStyle = Css.Green; cx.fillText("✔", mx, mid); }
        else if (d.NotFound) { cx.fillStyle = Css.Red; cx.fillText("✘", mx, mid); }
        cx.textAlign = "right";
        var rec = d.Rec;
        // saved
        float sr = x0 + U(560 - 8);
        if (rec != null)
        {
            cx.font = Font(12);
            cx.fillStyle = "#5a6a7c";
            string need = " (" + rec.Needed + ")";
            cx.fillText(need, sr, mid);
            float nw = cx.measureText(need).width;
            cx.font = Font(12, true);
            cx.fillStyle = Css.Text;
            cx.fillText(rec.Saved + " / " + rec.Count, sr - nw, mid);
            cx.font = Font(12);
            cx.fillText(rec.SkillsUsed.ToString(), x0 + U(616 - 8), mid);
            cx.fillText(MmSs(rec.CompletionFrame), x0 + U(666 - 8), mid);
        }
        else
        {
            cx.font = Font(12);
            cx.fillStyle = "#5a6a7c";
            cx.fillText(l.Lemmings + " (" + l.Save + ")", sr, mid);
        }
        cx.textAlign = "left";
        // found at
        float fx = x0 + U(666 + 8);
        int tried = Backend.TriedTier(id);
        if (rec != null)
        {
            cx.font = Font(12, true);
            cx.fillStyle = Css.Yellow;
            string t = "tier " + rec.Tier;
            cx.fillText(t, fx, mid);
            float tw = cx.measureText(t).width;
            cx.font = Font(12);
            cx.fillStyle = "#5a6a7c";
            cx.fillText(" · " + Human(rec.ElapsedMs / 1000), fx + tw, mid);
        }
        else if (d.NotFound) { cx.font = Font(12); cx.fillStyle = Css.Red; cx.fillText("not found", fx, mid); }
        else if (tried > 0) { cx.font = Font(12); cx.fillStyle = "#5a6a7c"; cx.fillText("tried at tier " + tried, fx, mid); }
        // the actions, right-aligned
        float right = x0 + U(ContentW) - U(8), bh = U(21);
        if (d.Solved) right -= LinkButton("sol:" + id, "▶ play solution", right, mid - bh / 2, bh, true) + U(4);
        LinkButton("play:" + id, "play level", right, mid - bh / 2, bh, false);
    }

    /** a.btn: a small link button, right edge at `right`; its width. */
    float LinkButton(string id, string label, float right, float y, float h, bool primary)
    {
        cx.font = Font(10);
        float w = cx.measureText(label).width + U(18);
        float x = right - w;
        bool hot = Hot(id);
        cx.fillStyle = primary ? (hot ? "#26485c" : "#173442") : (hot ? Css.BtnHot : Css.BtnBg);
        cx.beginPath();
        cx.roundRect(x, y, w, h, U(4));
        cx.fill();
        cx.strokeStyle = primary ? Css.Link : Css.BtnBorder;
        cx.lineWidth = Math.Max(1, U(1));
        cx.stroke();
        cx.fillStyle = primary ? Css.Link : (hot ? Css.Bright : Css.Text);
        cx.textAlign = "center";
        cx.fillText(label, x + w / 2, y + h / 2);
        cx.textAlign = "left";
        Hit(id, x, y, w, h);
        return w;
    }
}
