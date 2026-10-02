using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Godot;
using Lemmix.Input;
using Lemmix.Store;

namespace Lemmix.App.Ui.Pages;

// web/3d/js/hotkeys.js HotkeyDialog in the headset: the Keyboard and VR tabs, the key list with
// each key's function and its tag (Lemmix, 3D view, VR), the editor for the selected key - its
// function, and the detail that function needs (the skill, the frames, hold, the special skip) -
// the three keyboard layouts and the controllers' default, "show unassigned keys", "find key"
// (the next key pressed on a Bluetooth keyboard, or a controller button, is the one selected),
// export and import (the export and import folders stand in for the download and the file
// picker). Closing saves the table, as there.
public sealed class VrControlsDialog : VrPage
{
    public const int PAGE_W = 1290, PAGE_H = 890;
    public readonly HotkeyManager Manager;
    public readonly IPageFiles Files;

    public string? Selected;          // the code being edited
    public bool ShowAll;              // unassigned keys too
    public bool Finding;              // the next key pressed is the one to edit
    public string Tab = "keyboard";   // or "vr"
    public string StatusText = "";
    public bool StatusBad;
    public List<string> Codes = new();  // the list's rows, in order

    public VrControlsDialog(HotkeyManager manager, IPageFiles files) : base("controls", PAGE_W, PAGE_H, 1.5f)
    {
        Manager = manager;
        Files = files;
    }

    // ---- HotkeyDialog, method for method
    public void Open(string? tab = null)
    {
        SetStatus("");
        if (tab != null) ShowTab(tab); else Refresh();
    }

    public override void Opened() { Refresh(); Paint(); }

    public void ShowTab(string tab)
    {
        Tab = tab == "vr" ? "vr" : "keyboard";
        SetFinding(false);
        Selected = null;
        Scroll = 0;
        Refresh();
    }

    /** close(): the table saved ("By clicking on Close you save all hotkey assignments"). */
    public void CloseDialog()
    {
        SetFinding(false);
        Manager.Save();
        Closed?.Invoke();
    }

    public void SetStatus(string text, bool bad = false) { StatusText = text ?? ""; StatusBad = bad; }

    public void ExportFile()
    {
        try
        {
            string path = Files.WriteText(Files.ExportFolder, Hotkeys.ExportFile, Manager.ExportJSON());
            SetStatus("exported as " + path);
        }
        catch (Exception e) { SetStatus(e.Message, true); }
    }

    public HotkeyImport? ImportText(string text, string? name)
    {
        try
        {
            var r = Manager.ImportJSON(text);
            Selected = null;
            Refresh();
            SetStatus(Hotkeys.ImportStatus(r, name));
            return r;
        }
        catch (HotkeyImportException e)
        {
            SetStatus((string.IsNullOrEmpty(name) ? "" : name + ": ") + e.Message, true);
            return null;
        }
    }

    public void SetFinding(bool on) => Finding = on;

    /** find(): the key pressed is selected, listed even when unassigned. */
    public void Find(string code)
    {
        SetFinding(false);
        if (!Hotkeys.KeyByCode.ContainsKey(code)) return;
        if (Manager.Get(code) == null && !ShowAll) ShowAll = true;
        Refresh();
        Select(code);
        RevealSelected();
    }

    /** A controller button while finding: the VR tab, that input selected (the headset's own
     *  "find key"; the web's finds keyboard keys only). */
    public void FindVr(string code)
    {
        SetFinding(false);
        if (!Hotkeys.VrKeyByCode.ContainsKey(code)) return;
        if (Tab != "vr") ShowTab("vr");
        Select(code);
        RevealSelected();
    }

    /** refresh(): the list - every key with a function, all of them when asked. */
    public void Refresh()
    {
        Codes = new();
        bool vr = Tab == "vr";
        if (vr) foreach (var k in Hotkeys.VrKeys) Codes.Add(k.Code); // the headset's few inputs are always listed
        else foreach (var k in Hotkeys.Keys) if (Manager.Get(k.Code) != null || ShowAll) Codes.Add(k.Code);
        if (Selected != null && !Codes.Contains(Selected)) Selected = null;
    }

    public void Select(string code) => Selected = code;

    void RevealSelected() { if (Selected != null) RevealCode(Selected); }

    /** Scroll the list to a key's row. */
    public void RevealCode(string code)
    {
        int i = Codes.IndexOf(code);
        if (i >= 0) Reveal(i * U(RowCss), U(RowCss));
    }

    /** What the editor shows for the selected key: "editing: …", the function, the detail row. */
    public string EditingText() => Selected != null ? "editing: " + Hotkeys.KeyName(Selected) : (Tab == "vr" ? "click an input in the list" : "click a key in the list");
    public string? DetailKind() => Selected != null && Manager.Get(Selected) is HotkeyBinding b ? Hotkeys.ActionOf(b.Action)?.Mod : null;

    /** _apply(): the editor's state into the table. `action` "" clears the key. */
    public void Apply(string? action, string? skill = null, string? frames = null, bool? hold = null, int? special = null)
    {
        string? code = Selected;
        if (code == null) return;
        action = string.IsNullOrEmpty(action) ? null : action;
        var a = Hotkeys.ActionOf(action);
        var was = Manager.Get(code);
        bool same = was != null && was.Action == action;
        object mod = 0.0;
        if (a?.Mod == "skill") mod = skill ?? (same && was!.Mod is string s ? s : Hotkeys.Skills[0]);
        else if (a?.Mod == "frames")
        {
            string txt = frames ?? (was != null ? Js.ToInt32(was.Mod).ToString(CultureInfo.InvariantCulture) : "");
            mod = same && ParseIntJs(txt) is double n ? n : 1.0;
        }
        else if (a?.Mod == "hold") mod = same && (hold ?? Js.Truthy(was!.Mod)) ? 1.0 : 0.0;
        else if (a?.Mod == "special") mod = same ? (double)(special ?? Js.ToInt32(was!.Mod)) : 0.0;
        Manager.Set(code, action, mod);
        Refresh();
        Select(code);
    }

    // parseInt(text, 10), NaN as null
    static double? ParseIntJs(string text)
    {
        string t = text.Trim();
        int i = 0;
        if (i < t.Length && (t[i] == '-' || t[i] == '+')) i++;
        int start = i;
        while (i < t.Length && char.IsAsciiDigit(t[i])) i++;
        if (i == start) return null;
        return double.Parse(t[..i], CultureInfo.InvariantCulture);
    }

    public static string Capitalize(string s) => s.Length > 0 ? char.ToUpperInvariant(s[0]) + s[1..] : s;
    static string TagSuffix(string? tag) => tag == "lemmix" ? " [Lemmix]" : tag == "view" ? " [3D view]" : tag == "vr" ? " [VR]" : "";

    /** The function select's options: only what this input can take. */
    public List<(string Value, string Label)> FunctionOptions()
    {
        var o = new List<(string, string)> { ("", "(none)") };
        foreach (var a in Hotkeys.Actions)
            if (Selected == null || Hotkeys.AllowedOn(Selected, a.Id)) o.Add((a.Id, a.Label + TagSuffix(a.Tag)));
        return o;
    }

    public static List<(string Value, string Label)> SkillOptions() =>
        Hotkeys.Skills.Select(s => (s, Capitalize(s) + (Hotkeys.DosSkills.Contains(s) ? "" : " [Lemmix]"))).ToList();

    public static List<(string Value, string Label)> SpecialOptions() =>
        Hotkeys.SpecialSkips.Select((s, i) => (i.ToString(CultureInfo.InvariantCulture), s)).ToList();

    // ---- presses
    protected override void OnPress(string id)
    {
        switch (id)
        {
            case "export": ExportFile(); return;
            case "import":
            {
                var files = Files.List(Files.ImportFolder, ".json");
                if (files.Count == 0) { SetStatus("no .json file in " + Files.ImportFolder + " - copy one there first", true); return; }
                var anchor = RegionRect("import")!.Value;
                anchor = new Rect2(anchor.End.X - U(300), anchor.Position.Y, U(300), anchor.Size.Y);
                OpenPopup("import", files.Select(f => (f.Path, f.Name)).ToList(), null, anchor);
                return;
            }
            case "x": case "done": CloseDialog(); return;
            case "tab:keyboard": ShowTab("keyboard"); return;
            case "tab:vr": ShowTab("vr"); return;
            case "unassigned": ShowAll = !ShowAll; Refresh(); return;
            case "find": SetFinding(!Finding); return;
            case "vr-reset": Manager.ApplyVrPreset(); Refresh(); return;
            case "func": OpenPopup("func", FunctionOptions(), Selected != null ? Manager.Get(Selected)?.Action ?? "" : "", RegionRect("func")!.Value, 18); return;
            case "skill": OpenPopup("skill", SkillOptions(), Selected != null ? Manager.Get(Selected)?.Mod as string : null, RegionRect("skill")!.Value, 21); return;
            case "special": OpenPopup("special", SpecialOptions(), Selected != null ? Js.ToInt32(Manager.Get(Selected)?.Mod).ToString(CultureInfo.InvariantCulture) : null, RegionRect("special")!.Value); return;
            case "frames": if (Selected != null) Edit("frames", Js.ToInt32(Manager.Get(Selected)?.Mod).ToString(CultureInfo.InvariantCulture), true); return;
            case "hold": if (Selected != null) Apply(Manager.Get(Selected)?.Action, hold: !Js.Truthy(Manager.Get(Selected)?.Mod)); return;
        }
        if (id.StartsWith("preset:", StringComparison.Ordinal)) { Manager.ApplyPreset(id[7..]); Refresh(); return; }
        if (id.StartsWith("key:", StringComparison.Ordinal)) { Select(id[4..]); return; }
    }

    protected override void Choose(string popupId, string value)
    {
        switch (popupId)
        {
            case "import":
                try { ImportText(Files.ReadText(value), Path.GetFileName(value)); }
                catch (Exception e) { SetStatus(e.Message, true); }
                return;
            case "func": Apply(value); return;
            case "skill": Apply(Manager.Get(Selected ?? "")?.Action, skill: value); return;
            case "special": Apply(Manager.Get(Selected ?? "")?.Action, special: int.Parse(value, CultureInfo.InvariantCulture)); return;
        }
    }

    public override void TextChanged(string field, string text)
    {
        // the frames field applies as it is typed (its "input" event)
        if (field == "frames" && Selected != null) { Apply(Manager.Get(Selected)?.Action, frames: text); Paint(); }
    }

    /** A key on a Bluetooth keyboard: while finding, the key to select; else Escape closes. */
    public override bool OnKey(string code, string? text)
    {
        if (Finding) { Find(Hotkeys.NormalizeCode(code)); Paint(); return true; }
        if (code == "Escape")
        {
            if (Popup != null) { Popup = null; Paint(); return true; }
            CloseDialog();
            return true;
        }
        return true; // while open the dialog owns the keyboard
    }

    public override bool OnVrButton(string code)
    {
        if (!Finding) return false;
        FindVr(code);
        Paint();
        return true;
    }

    // ---- painting
    const float RowCss = 21, X0Css = 20, InnerW = 760;
    float ListX, ListW;
    protected override float BarX => ListX + ListW - PAGE_BAR_W - U(3);

    protected override void PaintPage()
    {
        float x0 = U(X0Css), right = x0 + U(InnerW), y = U(16);
        // ---- the head: title, note, export, import, ×
        cx.font = Font(15, true);
        cx.fillStyle = Css.Yellow;
        float tw = PageText.Spaced(cx, "CONFIGURE CONTROLS", x0, y + U(10), U(0.9f));
        float xb = right - U(22);
        Button("x", "×", xb, y - U(2), kind: "bare", css: 13, width: U(22));
        float iw = ButtonW("import"), ew = ButtonW("export");
        xb -= iw + U(12);
        Button("import", "import", xb, y - U(2));
        xb -= ew + U(12);
        Button("export", "export", xb, y - U(2));
        var note = PageText.Flow(cx, new[] { new Run("click a key or a controller input, give it a function; saved on this headset", Font(11), Css.Dim) }, xb - U(12) - (x0 + tw + U(12)));
        PageText.Paint(cx, note, x0 + tw + U(12), y + U(10) - U(8), U(15));
        y += Math.Max(U(26), note.Lines * U(15)) + U(8);
        if (StatusText != "")
        {
            cx.font = Font(11);
            cx.fillStyle = StatusBad ? Css.Red : Css.Green;
            cx.fillText(PageText.Fit(cx, StatusText, right - x0), x0, y + U(4));
            y += U(17);
        }
        // ---- the tabs
        float tabH = U(26);
        float tx = x0;
        foreach (var (t, label) in new[] { ("keyboard", "Keyboard"), ("vr", "VR") })
        {
            cx.font = Font(12);
            float w = cx.measureText(label).width + U(28);
            bool on = Tab == t, hot = Hot("tab:" + t);
            if (on)
            {
                cx.fillStyle = Css.Row;
                cx.beginPath();
                cx.roundRect(tx, y, w, tabH + U(1), U(4));
                cx.fill();
                cx.strokeStyle = Css.Border;
                cx.lineWidth = Math.Max(1, U(1));
                cx.stroke();
            }
            cx.fillStyle = on ? Css.Yellow : hot ? Css.Bright : Css.Dim;
            cx.textAlign = "center";
            cx.fillText(label, tx + w / 2, y + tabH / 2);
            cx.textAlign = "left";
            Hit("tab:" + t, tx, y, w, tabH);
            tx += w;
        }
        cx.fillStyle = Css.Border;
        cx.fillRect(x0, y + tabH, right - x0, Math.Max(1, U(1)));
        y += tabH + U(10);
        // ---- the tools
        bool vr = Tab == "vr";
        if (!vr)
        {
            float bx = x0, rowH = ButtonH;
            cx.font = Font(12);
            cx.fillStyle = Css.Text;
            cx.fillText("layout:", bx, y + rowH / 2);
            bx += cx.measureText("layout:").width + U(12);
            foreach (var p in new[] { "traditional", "functional", "minimal" }) bx += Button("preset:" + p, p, bx, y) + U(12);
            bx += Checkbox("unassigned", "show unassigned keys", ShowAll, bx, y, rowH, labelColor: Css.Text) + U(16);
            Checkbox("layoutnames", "names from my keyboard", false, bx, y, rowH, enabled: false, labelColor: Css.Text);
            y += rowH + U(6);
            Button("find", Finding ? "press a key…" : "find key", x0, y, kind: Finding ? "on" : "");
            y += rowH + U(10);
        }
        else
        {
            Button("vr-reset", "back to the default controls", x0, y);
            y += ButtonH + U(6);
            cx.font = Font(11);
            cx.fillStyle = Css.Faint;
            cx.fillText("the pointing hand is the one with the beam; a trigger pull on the other hand moves it there", x0, y + U(8));
            if (Finding)
            {
                cx.fillStyle = Css.Yellow;
            }
            y += U(16) + U(6);
        }
        // ---- the body: the list, the editor
        float bodyTop = y, editW = U(250);
        ListX = x0; ListW = right - x0 - editW - U(14);
        float listH = U(400);
        if (bodyTop + listH > H - U(44)) listH = H - U(44) - bodyTop;
        cx.strokeStyle = Css.Border;
        cx.lineWidth = Math.Max(1, U(1));
        cx.beginPath();
        cx.roundRect(ListX, bodyTop, ListW, listH, U(4));
        cx.stroke();
        // the head row (sticky)
        float hh = U(23);
        cx.fillStyle = Css.Row;
        cx.fillRect(ListX + 1, bodyTop + 1, ListW - 2, hh - 1);
        float keyW = vr ? U(248) : U(127), tagW = U(82);
        cx.font = Font(12, true);
        cx.fillStyle = Css.Dim;
        cx.fillText(vr ? "input" : "key", ListX + U(8), bodyTop + hh / 2);
        cx.fillText("function", ListX + keyW + U(8), bodyTop + hh / 2);
        View = new Rect2(ListX + 1, bodyTop + hh, ListW - 2, listH - hh - 1);
        BeginScroll();
        float rh = U(RowCss);
        for (int i = 0; i < Codes.Count; i++)
        {
            float ry = i * rh;
            if (!InView(ry, rh)) continue;
            string code = Codes[i];
            var b = Manager.Get(code);
            bool sel = code == Selected, hot = Hot("key:" + code);
            if (sel || hot) { cx.fillStyle = sel ? "#263043" : "#1a2231"; cx.fillRect(ListX + 1, ry, ListW - 2, rh); }
            cx.font = Font(12);
            cx.fillStyle = b == null ? Css.Faint : sel ? Css.Bright : Css.Text;
            cx.fillText(PageText.Fit(cx, Hotkeys.KeyName(code), keyW - U(12)), ListX + U(8), ry + rh / 2);
            cx.fillText(PageText.Fit(cx, b != null ? Hotkeys.Describe(b) : "(none)", ListW - keyW - tagW - U(12)), ListX + keyW + U(8), ry + rh / 2);
            string tag = Hotkeys.TagOf(b);
            if (tag != "") TagChip(tag, ListX + ListW - tagW - U(4), ry + rh / 2);
            Hit("key:" + code, ListX + 1, ry, ListW - 2, rh);
        }
        EndScroll(Codes.Count * rh + U(2));
        // ---- the editor
        float ex = right - editW, ey = bodyTop + U(2);
        cx.font = Font(12, true);
        cx.fillStyle = Css.Bright;
        cx.fillText(PageText.Fit(cx, EditingText(), editW), ex, ey + U(8));
        ey += U(16) + U(8);
        var bnd = Selected != null ? Manager.Get(Selected) : null;
        var act = bnd != null ? Hotkeys.ActionOf(bnd.Action) : null;
        float selH = U(24);
        Label("function", ex, ey);
        ey += U(15) + U(3);
        Select("func", bnd != null && act != null ? act.Label + TagSuffix(act.Tag) : "(none)", ex, ey, editW, selH, bg: Css.BtnBg, color: Css.Text, enabled: Selected != null);
        ey += selH + U(8);
        switch (act?.Mod)
        {
            case "skill":
                Label("skill", ex, ey);
                ey += U(18);
                string sk = bnd!.Mod as string ?? "";
                Select("skill", Capitalize(sk) + (Hotkeys.DosSkills.Contains(sk) ? "" : " [Lemmix]"), ex, ey, editW, selH, bg: Css.BtnBg, color: Css.Text);
                break;
            case "frames":
                Label("frames", ex, ey);
                ey += U(18);
                TextInput("frames", Js.ToInt32(bnd!.Mod).ToString(CultureInfo.InvariantCulture), "", ex, ey, editW, selH);
                ey += selH + U(3);
                var hint = PageText.Flow(cx, new[] { new Run("17 frames = 1 second; negative goes back", Font(11), Css.Faint) }, editW);
                PageText.Paint(cx, hint, ex, ey, U(15));
                break;
            case "hold":
                Checkbox("hold", "hold: on while the key is down", Js.Truthy(bnd!.Mod), ex, ey, U(20), labelColor: Css.Dim);
                break;
            case "special":
                Label("skip to", ex, ey);
                ey += U(18);
                Select("special", Hotkeys.SpecialSkips[Math.Clamp(Js.ToInt32(bnd!.Mod), 0, 1)], ex, ey, editW, selH, bg: Css.BtnBg, color: Css.Text);
                break;
        }
        // the legend, at the editor's foot
        var legend = new List<(string? Tag, string Text)>();
        if (!vr)
        {
            legend.Add(("lemmix", " NeoLemmix levels only, nothing on a DOS level"));
            legend.Add(("view", " this page's own, not in NeoLemmix"));
            legend.Add((null, "fixed: arrows pan, shift+arrows orbit; Escape closes what is open"));
        }
        else
        {
            legend.Add(("lemmix", " NeoLemmix levels only, nothing on a DOS level"));
            legend.Add(("vr", " the headset's own; "));
            legend.Add(("view+", " this page's own"));
            legend.Add((null, "fixed: the trigger clicks, held and moved it drags the board; a grip drags, both grips scale; the buttons on the bar do the rest"));
        }
        PaintLegend(legend, ex, bodyTop + listH, editW);
        // ---- close
        float cw = ButtonW("close");
        Button("done", "close", right - cw, H - U(14) - ButtonH - U(4));
    }

    void Label(string text, float x, float y)
    {
        cx.font = Font(12);
        cx.fillStyle = Css.Dim;
        cx.fillText(text, x, y + U(8));
    }

    float TagChip(string tag, float x, float mid)
    {
        var (bg, fg, text) = tag switch
        {
            "lemmix" => ("#2f6b3d", "#d7f5dd", "Lemmix"),
            "view" => ("#26485c", "#d5eef7", "3D view"),
            _ => ("#4a3a6b", "#e6dcf7", "VR"),
        };
        return Chip(text, x, mid, bg, fg, 10, spacing: 0.4f);
    }

    /** The legend: chips and their text, flowed, a break after each entry that ends a line;
     *  laid out from the bottom up to `bottom` (the web's margin-top: auto). */
    void PaintLegend(List<(string? Tag, string Text)> parts, float x, float bottom, float w)
    {
        float lh = U(18.7f);
        var lines = new List<List<(string? Tag, string Text, float X)>> { new() };
        float at = 0;
        foreach (var (tag0, text) in parts)
        {
            bool brk = tag0 != "vr";                 // "VR the headset's own; 3D view …" share a line
            string? tag = tag0 == "view+" ? "view" : tag0;
            if (tag != null)
            {
                cx.font = Font(10);
                float cwid = PageText.Spaced(cx, tag == "lemmix" ? "Lemmix" : tag == "view" ? "3D view" : "VR", 0, 0, U(0.4f), false) + U(10);
                if (at + cwid > w && at > 0) { lines.Add(new()); at = 0; }
                lines[^1].Add((tag, "", at));
                at += cwid;
            }
            var f = PageText.Flow(cx, new[] { new Run(text, Font(11), Css.Dim) }, w, at);
            int line = 0;
            foreach (var p in f.Parts)
            {
                while (line < p.Line) { lines.Add(new()); line++; }
                lines[^1].Add((null, p.Text, p.X));
            }
            at = f.LastWidth;
            if (brk) { lines.Add(new()); at = 0; }
        }
        if (lines[^1].Count == 0) lines.RemoveAt(lines.Count - 1);
        float top = bottom - lines.Count * lh;
        for (int i = 0; i < lines.Count; i++)
        {
            float mid = top + i * lh + lh / 2;
            foreach (var (tag, text, px) in lines[i])
            {
                if (tag != null) { TagChip(tag, x + px, mid); continue; }
                cx.font = Font(11);
                cx.fillStyle = Css.Dim;
                cx.fillText(text.TrimEnd(), x + px, mid);
            }
        }
    }
}
