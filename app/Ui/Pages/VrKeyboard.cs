using System;
using System.Collections.Generic;
using Godot;
using static Lemmix.App.Xr.VrManager;

namespace Lemmix.App.Ui.Pages;

// The headset's text keyboard: a field showing what is typed and a board of keys the beam
// presses - for the library's search (web/3d/js/library.js: the query as typed, Enter plays the
// first match, Escape clears it, or closes when it is already empty) and any other field a page
// has (the solutions search, the controls' frames). A Bluetooth keyboard types into the same field
// (OnKey). The owner hears every change (Changed), Enter (Entered) and Escape on an empty field
// (Escaped); Escape on a field with text clears it, as the search does.
public sealed class VrKeyboard : VrPage
{
    public const int PAGE_W = 1100, PAGE_H = 470;
    public string Text = "";
    public string Label = "";
    public string Placeholder = "";
    public bool Numeric;
    public bool Caps;
    public Action<string>? Changed;
    public Action<string>? Entered;
    public Action? Escaped;

    static readonly string[][] Letters =
    {
        new[] { "1", "2", "3", "4", "5", "6", "7", "8", "9", "0", "-", "⌫" },
        new[] { "q", "w", "e", "r", "t", "y", "u", "i", "o", "p", "'", "!" },
        new[] { "a", "s", "d", "f", "g", "h", "j", "k", "l", ".", "↵" },
        new[] { "⇧", "z", "x", "c", "v", "b", "n", "m", ",", "?", "&" },
        new[] { "esc", "space", "clear" },
    };
    static readonly string[][] Digits =
    {
        new[] { "7", "8", "9", "⌫" },
        new[] { "4", "5", "6", "-" },
        new[] { "1", "2", "3", "↵" },
        new[] { "esc", "0", "clear" },
    };

    public VrKeyboard() : base("keyboard", PAGE_W, PAGE_H, 1f)
    {
        Panel.RenderPriority = GUI_ORDER_PAGE_BTN;
    }

    public override float CentreY => VR_GUI_Y + 0.07f;
    public override float PlaneZ => VR_MODAL_Z + 0.12f;

    /** Open on a field: its text, what it is, digits only or the full board. */
    public void Open(string text, string label, string placeholder = "", bool numeric = false)
    {
        Text = text;
        Label = label;
        Placeholder = placeholder;
        Numeric = numeric;
        Caps = false;
    }

    // ---- what a key does
    public void Type(string s)
    {
        Text += Caps ? s.ToUpperInvariant() : s;
        Changed?.Invoke(Text);
    }

    public void Backspace()
    {
        if (Text.Length == 0) return;
        Text = Text[..^1];
        Changed?.Invoke(Text);
    }

    public void Enter() => Entered?.Invoke(Text);

    public void Escape()
    {
        if (Text != "") { Text = ""; Changed?.Invoke(Text); return; }
        Escaped?.Invoke();
    }

    public void Key(string k)
    {
        switch (k)
        {
            case "⌫": Backspace(); break;
            case "↵": Enter(); break;
            case "esc": Escape(); break;
            case "clear": if (Text != "") { Text = ""; Changed?.Invoke(Text); } break;
            case "space": Type(" "); break;
            case "⇧": Caps = !Caps; break;
            default: Type(k); break;
        }
    }

    protected override void OnPress(string id)
    {
        if (id.StartsWith("k:", StringComparison.Ordinal)) Key(id[2..]);
    }

    /** A Bluetooth keyboard: the same field. */
    public override bool OnKey(string code, string? text)
    {
        switch (code)
        {
            case "Backspace": Backspace(); break;
            case "Enter": case "NumpadEnter": Enter(); break;
            case "Escape": Escape(); break;
            default:
                if (string.IsNullOrEmpty(text) || char.IsControl(text[0])) return false;
                if (Numeric && !(char.IsAsciiDigit(text[0]) || text == "-")) return true;
                Text += text;
                Changed?.Invoke(Text);
                break;
        }
        Paint();
        return true;
    }

    // ---- painting: the VR windows' look (the settings', the catalog's)
    protected override void PaintPage()
    {
        float pad = 26;
        // the field
        cx.font = "20px monospace";
        cx.fillStyle = "#8fa1bb";
        cx.fillText(Label, pad + 6, 34);
        float fy = 52, fh = 58, fw = W - 2 * pad - 90;
        cx.fillStyle = "#0b0f16";
        cx.beginPath();
        cx.roundRect(pad, fy, fw, fh, 10);
        cx.fill();
        cx.strokeStyle = "#6fce7e";
        cx.lineWidth = 3;
        cx.stroke();
        cx.font = "28px monospace";
        string shown = Text == "" ? Placeholder : Text;
        cx.fillStyle = Text == "" ? "#6b7686" : "#f0f3f8";
        // the end of a long text stays in view
        while (shown.Length > 1 && cx.measureText(shown).width > fw - 40) shown = shown[1..];
        cx.fillText(shown, pad + 16, fy + fh / 2);
        float caret = pad + 16 + (Text == "" ? 0 : cx.measureText(shown).width) + 2;
        cx.fillStyle = "#f0f3f8";
        cx.fillRect(caret, fy + 14, 3, fh - 28);
        // the keys
        var rows = Numeric ? Digits : Letters;
        float top = fy + fh + 18, gap = 8;
        float rowH = (H - top - pad - gap * (rows.Length - 1)) / rows.Length;
        float unit = Numeric ? 150 : (W - 2 * pad - gap * 11) / 12;
        for (int r = 0; r < rows.Length; r++)
        {
            var row = rows[r];
            float y = top + r * (rowH + gap);
            // the width of each key: space is wide, the bottom row's ends too
            var widths = new List<float>();
            foreach (var k in row) widths.Add(k == "space" ? unit * 6 + gap * 5 : (k is "esc" or "clear") && !Numeric ? unit * 2 + gap : unit);
            float total = 0;
            foreach (var w0 in widths) total += w0;
            total += gap * (row.Length - 1);
            float x = (W - total) / 2;
            for (int i = 0; i < row.Length; i++)
            {
                string k = row[i];
                float w = widths[i];
                string id = "k:" + k;
                bool hot = Hot(id), special = k.Length > 1 || k is "⌫" or "↵" or "⇧";
                cx.fillStyle = hot ? "#2b3548" : special ? "#151b26" : "#19202c";
                if (k == "⇧" && Caps) cx.fillStyle = "#4a4326";
                cx.beginPath();
                cx.roundRect(x, y, w, rowH, 10);
                cx.fill();
                if (hot) { cx.strokeStyle = "#ffffff"; cx.lineWidth = 3; cx.stroke(); }
                cx.fillStyle = k == "↵" ? "#6fce7e" : special ? "#ffd866" : "#f0f3f8";
                cx.font = special && k.Length > 1 ? "22px monospace" : "bold 28px monospace";
                cx.textAlign = "center";
                string face = k.Length == 1 && Caps ? k.ToUpperInvariant() : k;
                cx.fillText(face, x + w / 2, y + rowH / 2);
                cx.textAlign = "left";
                Hit(id, x, y, w, rowH);
                x += w + gap;
            }
        }
        View = new Rect2(0, H, W, 0);
        ContentHeight = 0;
    }
}
