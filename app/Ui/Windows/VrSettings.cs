using System;
using System.Collections.Generic;
using Godot;
using static Lemmix.App.Xr.VrManager;

namespace Lemmix.App.Ui.Windows;

// One row of the settings window: a switch (Get answers on/off, Text what its pill says instead of
// ON/OFF) or an action (no Get).
public sealed record SettingRow(string Label, Func<bool>? Get, Action Act, Func<string>? Text = null);

// The VR window's height offset (native): the viewpoint raised or lowered against the headset's
// own, in metres (the floor stays the play space's; standing or seated is SteamVR's recentre) -
// with its slider's range, as far up as down (up: a higher view), and its reset to 0.
public sealed class FloorControl
{
    public const float Min = -0.6f, Max = 0.6f;
    public required Func<float> Get;
    public required Action<float> Set;
    public required Action Reset;

    public static string Label(float metres)
    {
        int cm = (int)MathF.Round(metres * 100);
        return cm == 0 ? "0 cm" : (cm > 0 ? "+" : "\u2212") + Math.Abs(cm) + " cm";
    }
}

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
// 640-wide canvas, with a close in the corner; the game holds while it is up. The native app has
// a second one (the VR window: foveated rendering), with its own title, close and height.
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

    public readonly Node3D Root;
    public readonly Panel3D Panel;
    public readonly IconButton Close;
    public List<SettingRow> RowList;
    public int Hover = -1;
    public readonly string Title;
    public readonly int H;                          // canvas height: VR_SET_H for the web's window
    public bool FitPills;                           // a pill as wide as its text needs (the native VR window; the web's are 86)
    public FloorControl? Floor;                     // the native VR window's height offset, under the rows
    public string? HoverPart;                       // the floor section's part under the beam: slider, seated, standing, reset

    // the floor section's geometry, in canvas pixels (under the rows)
    public const float FloorHead = 52, SliderLen = 260, FloorTail = 30;
    public const float SliderX = 92, SliderHalfW = 40, ButtonX = 200, ButtonW = 400, ButtonH = 64, ButtonGap = 34;
    public static readonly string[] FloorButtons = { "reset" };
    static readonly string[] FloorButtonLabels = { "Reset" };
    // a button's top: the buttons stacked, centred on the slider
    public float ButtonTop(int i) => SliderTop + (SliderLen - (FloorButtons.Length * ButtonH + (FloorButtons.Length - 1) * ButtonGap)) / 2 + i * (ButtonH + ButtonGap);
    public float FloorTop => VR_SET_TOP + RowList.Count * VR_SET_ROW + 4;
    public float SliderTop => FloorTop + FloorHead + 16;
    public static int FloorSectionHeight => (int)(FloorHead + 16 + SliderLen + FloorTail);

    public VrSettings(List<SettingRow> rows, string title = "3D EFFECTS", string name = "set", int? rowsShown = null, bool floorSection = false)
    {
        RowList = rows;
        Title = title;
        H = rowsShown is int n ? (int)(VR_SET_TOP + n * VR_SET_ROW + 8) : VR_SET_H;
        if (floorSection) H += FloorSectionHeight;
        Root = new Node3D { Name = name == "set" ? "vr-settings" : "vr-" + name + "-settings", Visible = false };
        Panel = new Panel3D(VR_SET_W, H, 1f) { Name = "vr-" + name + "panel" };
        Panel.NoDepthTest = true;
        Panel.RenderPriority = IconButton.GUI_ORDER_MODAL;
        Root.AddChild(Panel);
        Close = new IconButton(name + "close", BarIcons.Cross, IconButton.GUI_ORDER_MODAL_BTN);
        Root.AddChild(Close);
        Paint();
    }

    public void Paint()
    {
        var cx = Panel.Canvas;
        cx.clearRect(0, 0, VR_SET_W, H);
        cx.fillStyle = "rgba(10, 14, 22, 0.96)";
        cx.beginPath();
        cx.roundRect(2, 2, VR_SET_W - 4, H - 4, 16);
        cx.fill();
        cx.strokeStyle = "#ffd866";
        cx.lineWidth = 4;
        cx.stroke();
        cx.textAlign = "left";
        cx.fillStyle = "#f0f3f8";
        cx.font = "bold 34px monospace";
        cx.fillText(Title, 28, 60);

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
            string pill = row.Text != null ? row.Text() : (isOn ? "ON" : "OFF");
            float pw = 86;
            if (FitPills) { cx.font = "bold 22px monospace"; pw = Math.Max(86, cx.measureText(pill).width + 28); }
            float px = VR_SET_W - 48 - pw - 12;
            cx.fillStyle = isOn ? "#1d5030" : "#3a2530";
            cx.beginPath();
            cx.roundRect(px, y + 12, pw, 32, 16);
            cx.fill();
            cx.fillStyle = isOn ? "#6fce7e" : "#e07a6a";
            cx.font = "bold 22px monospace";
            cx.textAlign = "center";
            cx.fillText(pill, px + pw / 2, y + 36);
            cx.textAlign = "left";
        }
        if (Floor != null) PaintFloor(cx, Floor);
        Panel.Commit();
    }

    // the floor section: its label and value, the vertical slider (up: the floor higher), the buttons
    void PaintFloor(Canvas2D cx, FloorControl f)
    {
        float top = FloorTop, sTop = SliderTop, sBot = sTop + SliderLen;
        cx.textAlign = "left";
        cx.fillStyle = "#f0f3f8";
        cx.font = "26px monospace";
        cx.fillText("height offset", 44, top + 34);
        cx.fillStyle = "#7fd6e8";
        cx.font = "bold 22px monospace";
        cx.textAlign = "right";
        cx.fillText(FloorControl.Label(f.Get()), VR_SET_W - 44, top + 34);
        cx.textAlign = "left";
        // the track, the zero mark, the fill from zero to the value, the knob
        bool hot = HoverPart == "slider";
        cx.fillStyle = hot ? "#2b3548" : "#19202c";
        cx.beginPath();
        cx.roundRect(SliderX - SliderHalfW, sTop - 14, SliderHalfW * 2, SliderLen + 28, 12);
        cx.fill();
        if (hot) { cx.strokeStyle = "#ffffff"; cx.lineWidth = 3; cx.stroke(); }
        cx.fillStyle = "#2a3446";
        cx.beginPath();
        cx.roundRect(SliderX - 6, sTop, 12, SliderLen, 6);
        cx.fill();
        float zeroY = YOf(0), valY = YOf(f.Get());
        cx.fillStyle = "#6fce7e";
        cx.fillRect(SliderX - 6, Math.Min(zeroY, valY), 12, Math.Abs(valY - zeroY));
        cx.strokeStyle = "#8fa1bb";
        cx.lineWidth = 2;
        cx.beginPath();
        cx.moveTo(SliderX - 22, zeroY); cx.lineTo(SliderX + 22, zeroY);
        cx.stroke();
        cx.fillStyle = "#f0f3f8";
        cx.beginPath();
        cx.arc(SliderX, valY, 15, 0, Mathf.Tau);
        cx.fill();
        // the buttons
        for (int i = 0; i < FloorButtons.Length; i++)
        {
            float by = ButtonTop(i);
            bool bh = HoverPart == FloorButtons[i];
            cx.fillStyle = bh ? "#2b3548" : "#19202c";
            cx.beginPath();
            cx.roundRect(ButtonX, by, ButtonW, ButtonH, 10);
            cx.fill();
            cx.strokeStyle = bh ? "#ffffff" : "#3a4558";
            cx.lineWidth = bh ? 3 : 2;
            cx.stroke();
            cx.fillStyle = "#f0f3f8";
            cx.font = "bold 26px monospace";
            cx.textAlign = "center";
            cx.fillText(FloorButtonLabels[i], ButtonX + ButtonW / 2, by + ButtonH / 2 + 9);
            cx.textAlign = "left";
        }
    }

    // the slider: the view's height to a canvas y (the top is the highest view) and back
    public float YOf(float metres) => SliderTop + (FloorControl.Max - Math.Clamp(metres, FloorControl.Min, FloorControl.Max)) / (FloorControl.Max - FloorControl.Min) * SliderLen;
    public float ValueAt(float y) => FloorControl.Max - Math.Clamp((y - SliderTop) / SliderLen, 0, 1) * (FloorControl.Max - FloorControl.Min);

    /** The height section's part at a canvas pixel: "slider" (with its value), "reset", or null. */
    public (string? Part, float Value) FloorPartAt(Vector2? px)
    {
        if (Floor == null || px is not Vector2 p) return (null, 0);
        float sTop = SliderTop;
        if (p.X >= SliderX - SliderHalfW && p.X <= SliderX + SliderHalfW && p.Y >= sTop - 14 && p.Y <= sTop + SliderLen + 14)
            return ("slider", ValueAt(p.Y));
        for (int i = 0; i < FloorButtons.Length; i++)
        {
            float by = ButtonTop(i);
            if (p.X >= ButtonX && p.X <= ButtonX + ButtonW && p.Y >= by && p.Y <= by + ButtonH) return (FloorButtons[i], 0);
        }
        return (null, 0);
    }

    public void SetHoverPart(string? part)
    {
        if (HoverPart == part) return;
        HoverPart = part;
        Paint();
    }

    /** A press on the floor section: the slider's value, or a button. */
    public void PressFloor(string part, float value)
    {
        if (Floor == null) return;
        switch (part)
        {
            case "slider": Floor.Set(value); break;
            case "reset": Floor.Reset(); break;
        }
        Paint();
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

    // layoutVrSettings (canvasH: a window's own height; the web's by default)
    public static Placement PanelPlacement(int canvasH = VR_SET_H)
    {
        float w = VR_SETTINGS_WIDTH, h = w * canvasH / VR_SET_W;
        return new Placement(new Vector3(0, VR_MODAL_Y, VR_MODAL_Z), w, h);
    }

    public static Placement ClosePlacement(bool hot, int canvasH = VR_SET_H)
    {
        var (_, w, h) = PanelPlacement(canvasH);
        float size = VR_BAR_TOOL_SIZE;
        float s = size * (hot ? VR_BAR_TOOL_HOVER : 1);
        return new Placement(new Vector3(w / 2 - size * 0.6f, VR_MODAL_Y + h / 2 - size * 0.6f, VR_MODAL_Z + (hot ? size * 0.25f : 0.001f)), s, s);
    }

    public void Layout()
    {
        var p = PanelPlacement(H);
        Planes.Set(Panel, p.Pos, p.ScaleX, p.ScaleY);
        var c = ClosePlacement(Close.State.Hovered, H);
        Close.Size = c.ScaleX;
        Close.Position = c.Pos;
    }
}
