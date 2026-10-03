using System;
using System.Collections.Generic;
using Godot;
using static Lemmix.App.Xr.VrManager;

namespace Lemmix.App.Ui.Windows;

// One row of the settings window: a switch (Get answers on/off, Text what its pill says instead of
// ON/OFF) or an action (no Get).
public sealed record SettingRow(string Label, Func<bool>? Get, Action Act, Func<string>? Text = null);

// The render switches the rows act on (web: state.* and the toggle* functions). Integration
// answers it from the settings store and the renderer.
public interface IVrEffects
{
    bool Emboss { get; }
    bool Doors { get; }
    bool Smooth { get; }
    bool SmoothTerrain { get; }
    string ColorBlend { get; }      // "off", "soft", "smooth"
    bool SkillBar { get; }
    bool FlatSkills { get; }
    string Environment { get; }     // "none", "fog", "full"
    void ToggleEmboss();
    void ToggleDoors();
    void ToggleSmooth();
    void ToggleSmoothTerrain();
    void ToggleColorBlend();
    void ToggleSkillBar();
    void ToggleFlatSkills();
    void ToggleEnvironment();
    void Recenter();
}

// web/3d/js/app.js "settings (VR)": the render switches a monitor has as buttons, as rows on a
// 640-wide canvas, with a close in the corner; the game holds while it is up.
public sealed class VrSettings
{
    public const int VR_SET_W = 640;                // canvas pixels
    public const float VR_SET_TOP = 96;             // first row
    public const float VR_SET_ROW = 68;
    public const int VR_SET_ROWS = 9;               // a row each
    public const int VR_SET_H = (int)(VR_SET_TOP + VR_SET_ROWS * VR_SET_ROW + 8);

    // COLOR_BLEND_LEVELS: the label the colour blend's pill shows
    public static string ColorBlendLabel(string name) => name switch { "off" => "off", "smooth" => "smooth", _ => "soft" };

    /** vrSettingRows: the 8 effect switches and the recentre. */
    public static List<SettingRow> Rows(IVrEffects fx) => new()
    {
        new("3D terrain", () => fx.Emboss, fx.ToggleEmboss),
        new("3D doors", () => fx.Doors, fx.ToggleDoors),
        new("smooth relief", () => fx.Smooth, fx.ToggleSmooth),
        new("edge smoothing", () => fx.SmoothTerrain, fx.ToggleSmoothTerrain),
        new("colour blend", () => fx.ColorBlend != "off", fx.ToggleColorBlend, () => ColorBlendLabel(fx.ColorBlend).ToUpperInvariant()),
        new("3D skills bar", () => fx.SkillBar, fx.ToggleSkillBar),
        new("flat skills", () => fx.FlatSkills, fx.ToggleFlatSkills),
        new("environment", () => fx.Environment != "none", fx.ToggleEnvironment, () => fx.Environment.ToUpperInvariant()),
        new("recentre the board", null, fx.Recenter),
    };

    public readonly Node3D Root = new() { Name = "vr-settings", Visible = false };
    public readonly Panel3D Panel;
    public readonly IconButton Close;
    public List<SettingRow> RowList;
    public int Hover = -1;

    public VrSettings(List<SettingRow> rows)
    {
        RowList = rows;
        Panel = new Panel3D(VR_SET_W, VR_SET_H, 1f) { Name = "vr-setpanel" };
        Panel.NoDepthTest = true;
        Panel.RenderPriority = IconButton.GUI_ORDER_MODAL;
        Root.AddChild(Panel);
        Close = new IconButton("setclose", BarIcons.Cross, IconButton.GUI_ORDER_MODAL_BTN);
        Root.AddChild(Close);
        Paint();
    }

    public void Paint()
    {
        var cx = Panel.Canvas;
        cx.clearRect(0, 0, VR_SET_W, VR_SET_H);
        cx.fillStyle = "rgba(10, 14, 22, 0.96)";
        cx.beginPath();
        cx.roundRect(2, 2, VR_SET_W - 4, VR_SET_H - 4, 16);
        cx.fill();
        cx.strokeStyle = "#ffd866";
        cx.lineWidth = 4;
        cx.stroke();
        cx.textAlign = "left";
        cx.fillStyle = "#f0f3f8";
        cx.font = "bold 34px monospace";
        cx.fillText("3D EFFECTS", 28, 60);

        for (int i = 0; i < RowList.Count; i++)
        {
            var row = RowList[i];
            float y = VR_SET_TOP + i * VR_SET_ROW;
            bool hot = i == Hover;
            bool? on = row.Get != null ? row.Get() : null;
            cx.fillStyle = hot ? "#2b3548" : "#19202c";
            cx.beginPath();
            cx.roundRect(24, y, VR_SET_W - 48, VR_SET_ROW - 12, 10);
            cx.fill();
            if (hot)
            {
                cx.strokeStyle = "#ffffff";
                cx.lineWidth = 3;
                cx.stroke();
            }
            cx.fillStyle = "#f0f3f8";
            cx.font = "26px monospace";
            cx.fillText(row.Label, 44, y + 38);
            if (on is not bool isOn) continue;          // an action, not a switch
            float pw = 86, px = VR_SET_W - 48 - pw - 12;
            cx.fillStyle = isOn ? "#1d5030" : "#3a2530";
            cx.beginPath();
            cx.roundRect(px, y + 12, pw, 32, 16);
            cx.fill();
            cx.fillStyle = isOn ? "#6fce7e" : "#e07a6a";
            cx.font = "bold 22px monospace";
            cx.textAlign = "center";
            cx.fillText(row.Text != null ? row.Text() : (isOn ? "ON" : "OFF"), px + pw / 2, y + 36);
            cx.textAlign = "left";
        }
        Panel.Commit();
    }

    /** vrSettingsRowAt: which row the beam is on, from the panel's canvas pixel. */
    public int RowAt(Vector2? px)
    {
        if (px is not Vector2 p) return -1;
        float x = p.X, y = p.Y;
        if (x < 24 || x > VR_SET_W - 24) return -1;
        int i = (int)Math.Floor((y - VR_SET_TOP) / VR_SET_ROW);
        if (i < 0 || i >= RowList.Count) return -1;
        return JsMod(y - VR_SET_TOP, VR_SET_ROW) <= VR_SET_ROW - 12 ? i : -1;
    }

    // JavaScript's %: the sign of the dividend
    static float JsMod(float a, float b) => a % b;

    public void SetHover(int index)
    {
        if (Hover == index) return;
        Hover = index;
        Paint();
    }

    /** A press on a row: what it does, and the panel again. */
    public void Press(int row)
    {
        if (row < 0 || row >= RowList.Count) return;
        RowList[row].Act();
        Paint();
    }

    public readonly record struct Placement(Vector3 Pos, float ScaleX, float ScaleY);

    // layoutVrSettings
    public static Placement PanelPlacement()
    {
        float w = VR_SETTINGS_WIDTH, h = w * VR_SET_H / VR_SET_W;
        return new Placement(new Vector3(0, VR_MODAL_Y, VR_MODAL_Z), w, h);
    }

    public static Placement ClosePlacement(bool hot)
    {
        var (_, w, h) = PanelPlacement();
        float size = VR_BAR_TOOL_SIZE;
        float s = size * (hot ? VR_BAR_TOOL_HOVER : 1);
        return new Placement(new Vector3(w / 2 - size * 0.6f, VR_MODAL_Y + h / 2 - size * 0.6f, VR_MODAL_Z + (hot ? size * 0.25f : 0.001f)), s, s);
    }

    public void Layout()
    {
        var p = PanelPlacement();
        Planes.Set(Panel, p.Pos, p.ScaleX, p.ScaleY);
        var c = ClosePlacement(Close.State.Hovered);
        Close.Size = c.ScaleX;
        Close.Position = c.Pos;
    }
}
