using System;
using Godot;

namespace Lemmix.App.Ui.Windows;

// web/3d/js/app.js makeIconButton: a small square with a drawn icon (64x64 canvas) on a 1x1 plane,
// scaled to its size in metres; drawn over everything (no depth test) in its GUI order. Hidden
// until laid out, as there. The node's name is the mesh's ("vr-lock"); BarTool is the bare name.
public partial class IconButton : Panel3D
{
    public readonly IconState State = new();
    public readonly string BarTool;
    readonly Action<Canvas2D, IconState> _draw;

    // three's renderOrder for the bar's buttons, the windows and their buttons, the tooltip
    public const int GUI_ORDER_BAR_TOOL = 55, GUI_ORDER_MODAL = 56, GUI_ORDER_MODAL_BTN = 57;

    public IconButton(string name, Action<Canvas2D, IconState> draw, int order = GUI_ORDER_BAR_TOOL) : base(64, 64, 1f)
    {
        Name = "vr-" + name;
        BarTool = name;
        _draw = draw;
        NoDepthTest = true;
        RenderPriority = order;
        Visible = false;
        Repaint();
    }

    // the web keeps the canvas between paints: each paint starts with its own clearRect
    public void Repaint()
    {
        _draw(Canvas, State);
        Commit();
    }

    /** setBarToolState: change the state, repainting only when something actually moved. */
    public bool SetState(bool? on = null, bool? hovered = null)
    {
        bool changed = false;
        if (on is bool o && State.On != o) { State.On = o; changed = true; }
        if (hovered is bool h && State.Hovered != h) { State.Hovered = h; changed = true; }
        if (changed) Repaint();
        return changed;
    }

    // scale.setScalar(size): the plane is 1 x 1
    public float Size
    {
        get => Scale.X;
        set => Scale = new Vector3(value, value, value);
    }
}
