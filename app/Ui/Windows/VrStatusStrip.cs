using Godot;
using static Lemmix.App.Xr.VrManager;

namespace Lemmix.App.Ui.Windows;

// What the strip says (web vrStatus): the level's name, its pack · rank · save, and the outcome
// (kind "won" / "lost" colours it, "" is a plain note such as "loading…").
public sealed record StatusModel(string Name = "", string Meta = "", string Note = "loading…", string Kind = "");

// web/3d/js/app.js "status strip (VR)": the level's name, pack and outcome on a 1024 x 132 canvas,
// standing over the board just above the level, in the board's own pixels, so it moves and scales
// with it; the detail button (the level's text) off its right end, the REPLAY plate above it.
// Root goes under the diorama's root (the web's dioramaRoot).
public sealed class VrStatusStrip
{
    public const int VR_STATUS_W = 1024, VR_STATUS_H = 132;
    public const float VR_STATUS_GAP = 14; // px between the level's top edge and the strip
    public const float TERRAIN_DEPTH = 16; // terrain.js: the main slab's front Z

    public readonly Node3D Root = new() { Name = "vr-statusroot" };
    public readonly Panel3D Panel;
    public readonly IconButton Detail;
    public readonly VrReplayBadge Badge = new();
    public StatusModel Status = new();

    public VrStatusStrip()
    {
        Panel = new Panel3D(VR_STATUS_W, VR_STATUS_H, 1f) { Name = "vr-status", Visible = false };
        Panel.NoDepthTest = true;
        Panel.RenderPriority = IconButton.GUI_ORDER_BAR_TOOL;
        Root.AddChild(Panel);
        Detail = new IconButton("detail", BarIcons.Detail);
        Root.AddChild(Detail);
        Root.AddChild(Badge.Panel);
        Paint();
    }

    public void Paint()
    {
        var cx = Panel.Canvas;
        var s = Status;
        cx.clearRect(0, 0, VR_STATUS_W, VR_STATUS_H);
        cx.fillStyle = "rgba(10, 14, 22, 0.86)";
        cx.beginPath();
        cx.roundRect(2, 2, VR_STATUS_W - 4, VR_STATUS_H - 4, 14);
        cx.fill();
        cx.strokeStyle = s.Kind == "won" ? "#6fce7e" : s.Kind == "lost" ? "#e07a6a" : "#2a3446";
        cx.lineWidth = 3;
        cx.stroke();
        cx.textAlign = "left";
        cx.fillStyle = "#f0f3f8";
        cx.font = "bold 40px monospace";
        cx.fillText(s.Name != "" ? s.Name : "…", 26, 56);
        cx.fillStyle = "#8fa1bb";
        cx.font = "26px monospace";
        cx.fillText(s.Meta, 26, 98);
        if (s.Note != "")
        {
            // the outcome, or that a level is on its way in
            cx.textAlign = "right";
            cx.fillStyle = s.Kind == "won" ? "#6fce7e" : s.Kind == "lost" ? "#e07a6a" : "#ffd866";
            cx.font = "bold 32px monospace";
            cx.fillText(s.Note, VR_STATUS_W - 26, 84);
        }
        Panel.Commit();
    }

    /** setVrStatus: a field left null keeps what it had; repainted only on a change. */
    public void Set(string? name = null, string? meta = null, string? note = null, string? kind = null)
    {
        var next = new StatusModel(name ?? Status.Name, meta ?? Status.Meta, note ?? Status.Note, kind ?? Status.Kind);
        if (next == Status) return;
        Status = next;
        Paint();
    }

    /** vrStatusPx: as wide as the bar is at the board's default scale, in board pixels. */
    public static Vector2 StripPx()
    {
        float w = VR_GUI_WIDTH / VR_PIXEL_SCALE;
        return new Vector2(w, w * VR_STATUS_H / VR_STATUS_W);
    }

    public readonly record struct Layout(Vector3 StripPos, Vector2 StripScale, Vector3 DetailPos, float DetailScale,
        Vector3 BadgePos, Vector2 BadgeScale);

    /** layoutVrStatus's numbers: the strip centred on the level's focus, over its top edge. */
    public static Layout Compute(float focusX, float levelHeight, bool detailHot)
    {
        var px = StripPx();
        float w = px.X, h = px.Y;
        float x = focusX;
        float y = levelHeight + VR_STATUS_GAP + h / 2;
        float size = VR_BAR_TOOL_SIZE / VR_PIXEL_SCALE;
        float ds = size * (detailHot ? VR_BAR_TOOL_HOVER : 1);
        var detail = new Vector3(x + w / 2 + size * 0.7f, y, TERRAIN_DEPTH + 3 + (detailHot ? size * 0.25f : 0));
        float rw = w * 0.3f, rh = rw * VrReplayBadge.VR_REPLAY_H / VrReplayBadge.VR_REPLAY_W;
        var badge = new Vector3(x, y + h / 2 + 5 + rh / 2, TERRAIN_DEPTH + 2);
        return new Layout(new Vector3(x, y, TERRAIN_DEPTH + 2), new Vector2(w, h), detail, ds, badge, new Vector2(rw, rh));
    }

    /** layoutVrStatus: the level's focus x (levelFocusX: its middle) and its height, in board px;
     *  hasText: the level has a text, so the detail button shows. */
    public void Apply(float focusX, float levelHeight, bool hasText)
    {
        var l = Compute(focusX, levelHeight, Detail.State.Hovered);
        Planes.Set(Panel, l.StripPos, l.StripScale.X, l.StripScale.Y);
        Panel.Visible = true;
        Detail.Size = l.DetailScale;
        Detail.Position = l.DetailPos;
        Detail.Visible = hasText;
        Planes.Set(Badge.Panel, l.BadgePos, l.BadgeScale.X, l.BadgeScale.Y);
    }

    // the desktop branch of layoutGuiPanel: none of it shows
    public void Hide()
    {
        Panel.Visible = false;
        Detail.Visible = false;
    }
}

// web/3d/js/app.js vrReplayLabelMesh: a red REPLAY plate over the status strip while an attempt is
// replaying, painted once.
public sealed class VrReplayBadge
{
    public const int VR_REPLAY_W = 256, VR_REPLAY_H = 72;
    public readonly Panel3D Panel;

    public VrReplayBadge()
    {
        Panel = new Panel3D(VR_REPLAY_W, VR_REPLAY_H, 1f) { Name = "vr-replaybadge", Visible = false };
        Panel.NoDepthTest = true;
        Panel.RenderPriority = IconButton.GUI_ORDER_BAR_TOOL;
        Paint();
    }

    void Paint()
    {
        var cx = Panel.Canvas;
        cx.fillStyle = "rgba(40, 8, 8, 0.85)";
        cx.beginPath();
        cx.roundRect(3, 3, VR_REPLAY_W - 6, VR_REPLAY_H - 6, 12);
        cx.fill();
        cx.strokeStyle = "#ff3b3b";
        cx.lineWidth = 5;
        cx.stroke();
        cx.fillStyle = "#ff3b3b";
        cx.font = "bold 40px monospace";
        cx.textAlign = "center";
        cx.textBaseline = "middle";
        cx.fillText("REPLAY", VR_REPLAY_W / 2, VR_REPLAY_H / 2 + 2);
        Panel.Commit();
    }

    /** setReplayBadge: shown while an attempt is replaying, in a headset only. */
    public void Set(bool on, bool presenting) => Panel.Visible = on && presenting;
}
