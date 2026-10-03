using Godot;
using static Lemmix.App.Xr.VrManager;

namespace Lemmix.App.Ui.Windows;

// The native app's loading banner: "Loading…" with a spinner, in front of the player while a level
// is prepared and handed to the engine (App.PollLoad). Opened where the windows open (the default
// forward, at the board's eye level), over everything but the beam. Both pictures are painted once;
// the spinner only turns (a quad's rotation), so the banner costs nothing while the load runs.
public sealed class VrLoadingBanner
{
    public const int W = 512, H = 128;
    public const float Width = 0.30f;                  // metres
    public const float SpinTurnsPerSecond = 1.2f;
    public readonly Node3D Root = new() { Name = "vr-loading", Visible = false };
    public readonly Panel3D Panel, Spinner;
    float _angle;

    public VrLoadingBanner()
    {
        Panel = new Panel3D(W, H, Width) { Name = "vr-loading-panel" };
        Panel.NoDepthTest = true;
        Panel.RenderPriority = IconButton.GUI_ORDER_MODAL;
        Root.AddChild(Panel);
        var cx = Panel.Canvas;
        cx.clearRect(0, 0, W, H);
        cx.fillStyle = "rgba(10, 14, 22, 0.95)";
        cx.beginPath();
        cx.roundRect(2, 2, W - 4, H - 4, 18);
        cx.fill();
        cx.strokeStyle = "#ffd866";
        cx.lineWidth = 4;
        cx.stroke();
        cx.fillStyle = "#f0f3f8";
        cx.font = "bold 44px monospace";
        cx.textAlign = "left";
        cx.fillText("Loading…", 150, H / 2 + 15);
        Panel.Commit();

        // the spinner: three quarters of a ring, its bright end leading; turned, never repainted
        const int S = 128;
        float side = Width * 84f / W;
        Spinner = new Panel3D(S, S, side) { Name = "vr-loading-spinner" };
        Spinner.NoDepthTest = true;
        Spinner.RenderPriority = IconButton.GUI_ORDER_MODAL_BTN;
        Root.AddChild(Spinner);
        var sc = Spinner.Canvas;
        sc.clearRect(0, 0, S, S);
        sc.lineWidth = 14;
        sc.lineCap = "round";
        sc.strokeStyle = "rgba(127, 214, 232, 0.25)";
        sc.beginPath();
        sc.arc(S / 2, S / 2, S / 2 - 12, 0, Mathf.Tau);
        sc.stroke();
        sc.strokeStyle = "#7fd6e8";
        sc.beginPath();
        sc.arc(S / 2, S / 2, S / 2 - 12, -Mathf.Pi / 2, Mathf.Pi);
        sc.stroke();
        Spinner.Commit();
        // the spinner at the panel's left, a hair in front of it
        Spinner.Position = new Vector3(-Width / 2 + Width * 74f / W, 0, 0.002f);
    }

    /** Shown at a window's place (the windows' root transform) or hidden. */
    public void Show(bool show, Transform3D? at = null)
    {
        if (show && !Root.Visible && at is Transform3D t)
        {
            Root.GlobalTransform = t;
            Panel.Position = new Vector3(0, VR_MODAL_Y, VR_MODAL_Z);
            Spinner.Position = new Vector3(Spinner.Position.X, VR_MODAL_Y, VR_MODAL_Z + 0.002f);
        }
        Root.Visible = show;
    }

    /** Each frame while shown: the spinner turns (clockwise, as seen). */
    public void Spin(double seconds)
    {
        if (!Root.Visible) return;
        _angle = (_angle - (float)seconds * SpinTurnsPerSecond * Mathf.Tau) % Mathf.Tau;
        Spinner.Rotation = new Vector3(0, 0, _angle);
    }
}
