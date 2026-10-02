using Godot;
using static Lemmix.App.Xr.VrManager;

namespace Lemmix.App.Ui.Windows;

// web/3d/js/app.js vrModal: the question (yes / no) or the notice (one OK) in front of the eyes - a
// 512 x 192 panel with the answers as two icon buttons under it. While it is up the answers are
// the only things the ray can hit (VrWindows.Pick).
public sealed class VrModal
{
    public const int W = 512, H = 192;
    public readonly Node3D Root = new() { Name = "vr-modal", Visible = false };
    public readonly Panel3D Panel;
    public readonly IconButton Yes, No;
    public bool Notice;                 // one button (OK) rather than a yes and a no
    public string Title = "", Body = "";

    public VrModal()
    {
        Panel = new Panel3D(W, H, 1f) { Name = "vr-modalpanel" };
        Panel.NoDepthTest = true;
        Panel.RenderPriority = IconButton.GUI_ORDER_MODAL;
        Root.AddChild(Panel);
        Yes = new IconButton("yes", BarIcons.Yes, IconButton.GUI_ORDER_MODAL_BTN);
        No = new IconButton("no", BarIcons.Cross, IconButton.GUI_ORDER_MODAL_BTN);
        Root.AddChild(Yes);
        Root.AddChild(No);
        Ask("Restart level?");
    }

    // userData.ask: paint the question
    public void Ask(string title, string? body = null)
    {
        Title = title;
        Body = body ?? "";
        var cx = Panel.Canvas;
        cx.clearRect(0, 0, 512, 192);
        cx.fillStyle = "rgba(10, 14, 22, 0.95)";
        cx.beginPath();
        cx.roundRect(2, 2, 508, 188, 16);
        cx.fill();
        cx.strokeStyle = "#ffd866";
        cx.lineWidth = 4;
        cx.stroke();
        cx.fillStyle = "#f0f3f8";
        cx.font = "bold 38px monospace";
        cx.textAlign = "center";
        cx.fillText(title, 256, 76);
        cx.fillStyle = "#8fa1bb";
        cx.font = "24px monospace";
        cx.fillText(string.IsNullOrEmpty(body) ? "progress on this level is lost" : body, 256, 114);
        Panel.Commit();
    }

    public readonly record struct Placement(Vector3 Pos, float ScaleX, float ScaleY);

    // layoutVrModal, in metres of the windows' frame
    public static Placement PanelPlacement()
    {
        float w = VR_MODAL_WIDTH, h = w * 192f / 512;
        return new Placement(new Vector3(0, VR_MODAL_Y, VR_MODAL_Z), w, h);
    }

    public static Placement ButtonPlacement(int side, bool hot)
    {
        var (_, w, h) = PanelPlacement();
        float size = VR_BAR_TOOL_SIZE * 1.3f;
        float s = size * (hot ? VR_BAR_TOOL_HOVER : 1);
        return new Placement(new Vector3(side * w * 0.22f, VR_MODAL_Y - h * 0.62f, VR_MODAL_Z + (hot ? size * 0.25f : 0.001f)), s, s);
    }

    public void Layout()
    {
        var p = PanelPlacement();
        Planes.Set(Panel, p.Pos, p.ScaleX, p.ScaleY);
        foreach (var (b, side) in new[] { (Yes, Notice ? 0 : -1), (No, 1) })
        {
            var bp = ButtonPlacement(side, b.State.Hovered);
            b.Size = bp.ScaleX;
            b.Position = bp.Pos;
        }
    }
}
