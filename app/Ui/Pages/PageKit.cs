using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using Godot;

namespace Lemmix.App.Ui.Pages;

// The look of the web's desktop pages (setup.html, solutions.html, hotkeys.js's dialog,
// index.html's HUD drawer), restated for a canvas: their CSS colours, and helpers to paint their
// parts - text that wraps like a paragraph, buttons, badges, checkboxes, selects - in canvas
// pixels, S canvas pixels to the CSS pixel (the pages are bigger in the headset than on a screen).
public static class Css
{
    public const string Ground = "#10141c";          // html, body
    public const string Text = "#cdd6e4";            // body
    public const string Bright = "#f0f3f8";          // h2, names
    public const string Dim = "#8fa1bb";             // .dim
    public const string Faint = "#5a6a7c";           // .hk-hint, td.dim
    public const string Green = "#6fce7e";           // h1, .msg.ok, .dot.on
    public const string Red = "#e07a6a";             // .msg.err
    public const string Link = "#7fd6e8";            // a
    public const string LinkHot = "#a9e6f4";         // a:hover
    public const string Yellow = "#ffd866";          // .notice, the dialogs' titles
    public const string NoticeBold = "#ffe9a0";      // .notice b
    public const string Border = "#2a3446";          // .card, .row
    public const string BtnBg = "#1c2432", BtnHot = "#263043", BtnBorder = "#33405a";
    public const string Card = "rgba(10, 14, 22, 0.82)";
    public const string Row = "#161c28";
    public const string Input = "#0b0f16";
    public const string Frame = "#ffd866";           // the VR windows' outline
}

// a run of text with its own font and colour (the spans of a paragraph)
public readonly record struct Run(string Text, string Font, string Color);

// a run placed on a line by Flow
public readonly record struct Placed(string Text, string Font, string Color, float X, int Line);

public sealed class Flowed
{
    public readonly List<Placed> Parts = new();
    public int Lines = 1;
    public float LastWidth;     // where the last line ends
}

// what the beam can press on a page: an id, its rectangle in canvas pixels
public readonly record struct Region(string Id, Rect2 Rect, bool Enabled, bool Overlay);

public static class PageText
{
    static readonly Regex Token = new(@"\S+\s*|\s+", RegexOptions.Compiled);

    /** fmtMB: 4 KB, 7.0 MB, 1.23 GB. */
    public static string Mb(long? n)
    {
        if (n is not long v || v < 0) return "?";
        if (v >= 1e9) return (v / 1e9).ToString("0.00", CultureInfo.InvariantCulture) + " GB";
        if (v >= 1e6) return (v / 1e6).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
        return Math.Max(1, (long)Math.Round(v / 1e3, MidpointRounding.AwayFromZero)) + " KB";
    }

    /** fmtDate: the browser's toLocaleString in en-US, "10/2/2026, 3:19:00 PM". */
    public static string Date(long? ms)
    {
        if (ms is not long t || t == 0) return "";
        var d = DateTimeOffset.FromUnixTimeMilliseconds(t).ToLocalTime();
        return d.ToString("M/d/yyyy, h:mm:ss tt", CultureInfo.InvariantCulture);
    }

    /**
     * Lay runs out like an inline paragraph `width` wide: words go on the line while they fit and
     * break to the next line between words, a word longer than a line by its letters. `indent`
     * starts the first line further in (text that follows something on its line).
     */
    public static Flowed Flow(Canvas2D cx, IEnumerable<Run> runs, float width, float indent = 0)
    {
        var f = new Flowed();
        float x = indent;
        int line = 0;
        void Put(string text, Run r, float w)
        {
            // one placed part per run and line
            if (f.Parts.Count > 0)
            {
                var last = f.Parts[^1];
                if (last.Line == line && last.Font == r.Font && last.Color == r.Color)
                {
                    f.Parts[^1] = last with { Text = last.Text + text };
                    x += w;
                    return;
                }
            }
            f.Parts.Add(new Placed(text, r.Font, r.Color, x, line));
            x += w;
        }
        foreach (var r in runs)
        {
            cx.font = r.Font;
            foreach (Match m in Token.Matches(r.Text))
            {
                string tok = m.Value;
                string word = tok.TrimEnd();
                float ww = cx.measureText(word).width, tw = cx.measureText(tok).width;
                if (word.Length == 0)
                {
                    if (x > 0) Put(tok, r, tw); // spaces at a line's start go
                    continue;
                }
                if (x + ww > width && x > 0) { line++; x = 0; }
                if (ww > width)
                {
                    // a word wider than the line: its letters, line by line
                    string cur = "";
                    foreach (char ch in tok)
                    {
                        float cw = cx.measureText(cur + ch).width;
                        if (x + cw > width && cur.Length > 0) { Put(cur, r, cx.measureText(cur).width); line++; x = 0; cur = ""; }
                        cur += ch;
                    }
                    if (cur.Length > 0) Put(cur, r, cx.measureText(cur).width);
                    continue;
                }
                Put(tok, r, tw);
            }
        }
        f.Lines = line + 1;
        f.LastWidth = x;
        return f;
    }

    /** Paint a flowed paragraph with its first line's top at `top`, lines `lineH` apart. */
    public static void Paint(Canvas2D cx, Flowed f, float left, float top, float lineH)
    {
        cx.textAlign = "left";
        cx.textBaseline = "middle";
        foreach (var p in f.Parts)
        {
            string t = p.Text.TrimEnd();
            if (t.Length == 0) continue;
            cx.font = p.Font;
            cx.fillStyle = p.Color;
            cx.fillText(t, left + p.X, top + p.Line * lineH + lineH / 2);
        }
    }

    /** A label cut to a width with an ellipsis (text-overflow: ellipsis), in the font set. */
    public static string Fit(Canvas2D cx, string text, float maxW)
    {
        if (maxW <= 0) return "";
        if (cx.measureText(text).width <= maxW) return text;
        string cut = text;
        while (cut.Length > 1 && cx.measureText(cut + "…").width > maxW) cut = cut[..^1];
        return cut + "…";
    }

    /** Text with letter-spacing (the headings' and badges' `letter-spacing`): glyph by glyph. */
    public static float Spaced(Canvas2D cx, string text, float x, float y, float spacing, bool paint = true)
    {
        float at = x;
        foreach (char ch in text)
        {
            string s = ch.ToString();
            if (paint) cx.fillText(s, at, y);
            at += cx.measureText(s).width + spacing;
        }
        return at - x - (text.Length > 0 ? spacing : 0);
    }
}

// The popup list a <select> opens: its options under the field, the one chosen marked, scrolled
// by the stick when there are more than fit.
public sealed class PagePopup
{
    public readonly string Id;
    public readonly List<(string Value, string Label)> Options;
    public readonly string? Current;
    public Rect2 Box;           // canvas px, the whole list's window
    public float RowH;
    public float Scroll;
    public int Hover = -1;
    public PagePopup(string id, List<(string, string)> options, string? current) { Id = id; Options = options; Current = current; }
    public int VisibleRows => Math.Max(1, (int)Math.Floor(Box.Size.Y / RowH));
    public float MaxScroll => Math.Max(0, Options.Count * RowH - Box.Size.Y);
}
