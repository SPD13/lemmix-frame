using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Godot;
using static Lemmix.App.Xr.VrManager;

namespace Lemmix.App.Ui.Windows;

// web/3d/js/app.js "level text (VR)": a level's own text - the opening text, or the closing text
// once it is won - in a window in the dialogs' plane, word-wrapped to its width, with an OK to
// close it. The canvas is as tall as the text needs.
public sealed class VrLevelText
{
    public const int VR_DETAIL_W = 768;
    public const float VR_DETAIL_PAD = 36;                  // canvas px
    public const float VR_DETAIL_LINE = 34;
    public const string VR_DETAIL_FONT = "26px monospace";
    public const int VR_DETAIL_MAX_LINES = 16;
    public const float VR_DETAIL_OK_W = 168, VR_DETAIL_OK_H = 58; // the OK button
    public const float VR_DETAIL_WIDTH = 0.5f;              // metres

    public readonly Node3D Root = new() { Name = "vr-detail", Visible = false };
    public readonly Panel3D Panel;
    public bool OkHot;
    public Rect2 OkRect;
    public List<string> Lines = new();     // as wrapped

    public VrLevelText()
    {
        Panel = new Panel3D(VR_DETAIL_W, 256, 1f) { Name = "vr-detailpanel" };
        Panel.NoDepthTest = true;
        Panel.RenderPriority = IconButton.GUI_ORDER_MODAL;
        Root.AddChild(Panel);
        Paint(Array.Empty<string>());
    }

    /**
     * flowLevelText: a level's text comes hard-wrapped for the game's own 320-px screen; run the
     * lines together into paragraphs - a blank line is a break - so they flow to the window.
     */
    public static List<string> Flow(IEnumerable<string> lines)
    {
        var paragraphs = new List<string>();
        string cur = "";
        foreach (var raw in lines)
        {
            string line = JsTrim(raw);
            if (line == "") { if (cur != "") { paragraphs.Add(cur); cur = ""; } continue; }
            cur = cur != "" ? cur + " " + line : line;
        }
        if (cur != "") paragraphs.Add(cur);
        return paragraphs;
    }

    static readonly char[] JsSpace = { ' ', '\t', '\n', '\r', '\v', '\f', (char)0xa0, (char)0xfeff, (char)0x2028, (char)0x2029 };
    static string JsTrim(string s) => s.Trim(JsSpace);

    /** Break the author's lines at words to the window's width. */
    List<string> Wrap(IEnumerable<string> lines)
    {
        var cx = Panel.Canvas;
        cx.font = VR_DETAIL_FONT;
        float max = VR_DETAIL_W - 2 * VR_DETAIL_PAD;
        var output = new List<string>();
        foreach (var line in lines)
        {
            string cur = "";
            foreach (var word in Regex.Split(line, @"\s+"))
            {
                string next = cur != "" ? cur + " " + word : word;
                if (cur != "" && cx.measureText(next).width > max) { output.Add(cur); cur = word; }
                else cur = next;
            }
            output.Add(cur);
        }
        if (output.Count > VR_DETAIL_MAX_LINES)
        {
            output.RemoveRange(VR_DETAIL_MAX_LINES, output.Count - VR_DETAIL_MAX_LINES);
            output[VR_DETAIL_MAX_LINES - 1] += " …";
        }
        return output;
    }

    public int CanvasHeight => Panel.Canvas.Height;

    public void Paint(IEnumerable<string>? lines)
    {
        // the wrap measures in the font it is shown in, on the canvas as it is
        Lines = Wrap(lines ?? Array.Empty<string>());
        float textTop = 40;
        float okTop = textTop + Lines.Count * VR_DETAIL_LINE + 24;
        int h = (int)(okTop + VR_DETAIL_OK_H + 30);
        if (Panel.Canvas.Height != h) Panel.Resize(VR_DETAIL_W, h); // resizing clears the canvas
        var cx = Panel.Canvas;
        cx.clearRect(0, 0, VR_DETAIL_W, h);
        cx.fillStyle = "rgba(10, 14, 22, 0.96)";
        cx.beginPath();
        cx.roundRect(2, 2, VR_DETAIL_W - 4, h - 4, 16);
        cx.fill();
        cx.strokeStyle = "#ffd866";
        cx.lineWidth = 4;
        cx.stroke();
        cx.fillStyle = "#cdd6e4";
        cx.font = VR_DETAIL_FONT;
        cx.textAlign = "left";
        cx.textBaseline = "alphabetic";
        for (int i = 0; i < Lines.Count; i++)
            cx.fillText(Lines[i], VR_DETAIL_PAD, textTop + (i + 1) * VR_DETAIL_LINE - 8);
        OkRect = new Rect2((VR_DETAIL_W - VR_DETAIL_OK_W) / 2, okTop, VR_DETAIL_OK_W, VR_DETAIL_OK_H);
        var r = OkRect;
        cx.fillStyle = OkHot ? "#1d5030" : "#12331d";
        cx.beginPath();
        cx.roundRect(r.Position.X, r.Position.Y, r.Size.X, r.Size.Y, 12);
        cx.fill();
        cx.strokeStyle = OkHot ? "#ffffff" : "#6fce7e";
        cx.lineWidth = 3;
        cx.stroke();
        cx.fillStyle = "#6fce7e";
        cx.font = "bold 30px monospace";
        cx.textAlign = "center";
        cx.textBaseline = "middle";
        cx.fillText("OK", r.Position.X + r.Size.X / 2, r.Position.Y + r.Size.Y / 2 + 1);
        Panel.Commit();
        // mesh.scale.set(VR_DETAIL_WIDTH, VR_DETAIL_WIDTH * h / VR_DETAIL_W)
        Planes.Set(Panel, Panel.Position, VR_DETAIL_WIDTH, VR_DETAIL_WIDTH * h / VR_DETAIL_W);
    }

    /** vrDetailOkAt: is this canvas pixel on the OK? */
    public bool OkAt(Vector2? px)
    {
        if (px is not Vector2 p) return false;
        var r = OkRect;
        return p.X >= r.Position.X && p.X < r.Position.X + r.Size.X && p.Y >= r.Position.Y && p.Y < r.Position.Y + r.Size.Y;
    }

    /** setVrDetailHover: the OK lit; repainted with the text it shows. */
    public void SetHover(bool hot, IEnumerable<string>? lines)
    {
        if (OkHot == hot) return;
        OkHot = hot;
        if (Root.Visible) Paint(lines);
    }

    // layoutVrDetail: in the dialogs' plane
    public static Vector3 PanelPosition => new(0, VR_MODAL_Y, VR_MODAL_Z);
    public void Layout() => Panel.Position = PanelPosition;
}
