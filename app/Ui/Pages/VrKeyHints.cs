using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Lemmix.Input;

namespace Lemmix.App.Ui.Pages;

// web/index.html's key-hints drawer (#hud-keys, the 3D view's lines) and app.js refreshKeyHints:
// the view's fixed keys, the click, and the table's keys for the common functions, named as they
// are bound now - refreshed whenever the table changes (HotkeyManager.OnChange) or the controls
// dialog closes. ButtonTitles are the tooltips refreshKeyHints gives the HUD's buttons.
public sealed class VrKeyHints : VrPage
{
    public const int PAGE_W = 960, PAGE_H = 200;
    public readonly HotkeyManager Manager;

    public VrKeyHints(HotkeyManager manager) : base("hints", PAGE_W, PAGE_H, 1.5f)
    {
        Manager = manager;
    }

    // the functions the line names, in its order (refreshKeyHints' add calls)
    static readonly (string Id, string Label, object? Mod)[] Common =
    {
        ("pause", "pause", null), ("skip", "step", 1.0), ("restart", "restart", null), ("fastforward", "fast forward", null),
        ("previous_skill", "prev skill", null), ("next_skill", "next skill", null), ("quit", "library", null), ("save_replay", "save replay", null),
    };

    /** The table's line: (key, what it does) for each function bound, then the pointer to the dialog. */
    public static List<(string Key, string Label)> HotkeyParts(HotkeyManager m)
    {
        var parts = new List<(string, string)>();
        foreach (var (id, label, mod) in Common)
        {
            string n = mod == null ? m.KeyNameFor(id) : m.KeyNameFor(id, mod);
            if (n != "") parts.Add((n, label));
        }
        return parts;
    }

    public const string Tail = "configure controls (below) sets them all";

    /** The drawer's text, line by line, as the page shows it (innerText). */
    public static List<string> Lines(HotkeyManager m)
    {
        var parts = HotkeyParts(m);
        return new List<string>
        {
            "drag/⇧arrows orbit · right-drag/arrows pan · wheel/PgUp∕PgDn zoom · Home/2×right-click reset",
            "click lemming: assign skill · panel: as in game",
            string.Join(" · ", parts.Select(p => p.Key + " " + p.Label)) + (parts.Count > 0 ? " · " : "") + Tail,
        };
    }

    /** refreshKeyHints' button titles: "previous level (Page Up)" and the like. */
    public static Dictionary<string, string> ButtonTitles(HotkeyManager m)
    {
        string K(string id) { string n = m.KeyNameFor(id); return n != "" ? " (" + n + ")" : ""; }
        return new Dictionary<string, string>
        {
            ["prev"] = "previous level" + K("previous_level"),
            ["next"] = "next level" + K("next_level"),
            ["pause"] = "pause / resume" + K("pause"),
            ["library"] = "world library" + K("quit"),
            ["sound"] = "sound on / off" + K("toggle_sound"),
            ["view"] = "reset the view" + K("reset_view"),
        };
    }

    protected override void OnPress(string id) { }

    protected override void PaintPage()
    {
        float x0 = U(20), w = W - x0 - U(70), lh = U(19.2f);
        var b = Font(12, true);
        var r = Font(12);
        string bc = Css.Text, dc = Css.Dim;
        Run B(string t) => new(t, b, bc);
        Run D(string t) => new(t, r, dc);
        var paras = new List<Run[]>
        {
            new[] { B("drag"), D("/"), B("⇧arrows"), D(" orbit · "), B("right-drag"), D("/"), B("arrows"), D(" pan · "), B("wheel"), D("/"), B("PgUp∕PgDn"), D(" zoom · "), B("Home"), D("/"), B("2×right-click"), D(" reset") },
            new[] { B("click"), D(" lemming: assign skill · panel: as in game") },
        };
        var hk = new List<Run>();
        var parts = HotkeyParts(Manager);
        for (int i = 0; i < parts.Count; i++)
        {
            hk.Add(B(parts[i].Key));
            hk.Add(D(" " + parts[i].Label + " · "));
        }
        hk.Add(B("configure controls"));
        hk.Add(D(" (below) sets them all"));
        paras.Add(hk.ToArray());
        float y = U(14);
        foreach (var p in paras)
        {
            var f = PageText.Flow(cx, p, w);
            // right-aligned, as the drawer is: each line shifted by what it leaves
            for (int line = 0; line < f.Lines; line++)
            {
                var onLine = f.Parts.Where(q => q.Line == line).ToList();
                if (onLine.Count == 0) continue;
                var lastPart = onLine[^1];
                cx.font = lastPart.Font;
                float end = lastPart.X + cx.measureText(lastPart.Text.TrimEnd()).width;
                float shift = w - end;
                foreach (var q in onLine)
                {
                    cx.font = q.Font;
                    cx.fillStyle = q.Color;
                    cx.fillText(q.Text.TrimEnd(), x0 + shift + q.X, y + line * lh + lh / 2);
                }
            }
            y += f.Lines * lh;
        }
        View = new Rect2(0, H, W, 0);
        ContentHeight = 0;
    }
}
