using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Godot;
using static Lemmix.App.Xr.VrManager;

namespace Lemmix.App.Ui.Windows;

// web/3d/js/app.js: where the windows open (placeVrWindows) and where the bar hangs (setBarLocked,
// resetBar, barDefaultPlacement, saveBarPrefs, placeBarBelowDiorama, placeBarAtStart,
// syncBarForWindows), as pure maths over transforms, then VrBar, which applies it to the nodes.
public static class VrWindowPlacement
{
    /**
     * placeVrWindows: the windows' root goes where a head would have to be for a window laid out
     * VR_MODAL_Z ahead to sit centred on the gaze - turned by the gaze's yaw alone, so the window
     * stands upright however the head was tilted. A gaze too vertical to have a heading keeps the
     * last yaw. Returns the root's world transform and the yaw kept.
     */
    public static (Vector3 Pos, Quaternion Quat, float Yaw) PlaceWindows(Vector3 headPos, Quaternion headQuat, float lastYaw)
    {
        var gaze = headQuat * new Vector3(0, 0, -1);
        // where the window's plane meets the gaze
        var centre = headPos + gaze * -VR_MODAL_Z;
        var flat = new Vector3(gaze.X, 0, gaze.Z);
        float yaw = lastYaw;
        if (flat.LengthSquared() > 1e-4f) yaw = MathF.Atan2(-flat.X, -flat.Z);
        var quat = new Quaternion(Vector3.Up, yaw);
        var pos = centre - quat * new Vector3(0, 0, VR_MODAL_Z);
        return (pos, quat, yaw);
    }

    // the frontmost plane the board reaches, in game pixels: max(TERRAIN_DEPTH, OBJECT_DECAL_Z,
    // DEPTH_BANDS[RELIEF].front + RELIEF_TOP) = max(16, 16.25, 22 + 24)
    public const float BOARD_FRONT_PX = 46;

    /** What the bar's default placement is worked out from (null in the web: no level). */
    public readonly record struct BarFrame(Transform3D Diorama, float DioramaYaw, float FocusX, float GuiW, float BarH)
    {
        // the bar's height from the skills panel's canvas (40 / 320 when it has none yet)
        public static float BarHeight(float guiW, int canvasW, int canvasH) => guiW * (canvasW != 0 ? (float)canvasH / canvasW : 40f / 320);
    }

    /**
     * barDefaultPlacement: the bar just below the board, centred on the level's focus, facing the
     * way the board faces, a little nearer the player than the board's frontmost face. guiRoot is
     * laid out as if it were a head (the panel VR_GUI_Y below and VR_GUI_Z ahead), so this is
     * where such a head would be.
     */
    public static (Vector3 Pos, Quaternion Quat) BarDefault(BarFrame f)
    {
        // the level's bottom edge, under the focus, on the board's front plane
        var edge = f.Diorama * new Vector3(f.FocusX, 0, BOARD_FRONT_PX);
        // the row of controls stands over the bar
        float above = VR_BAR_TOOL_SIZE * 1.55f;
        var quat = new Quaternion(Vector3.Up, f.DioramaYaw);
        var centre = edge;
        centre.Y -= 0.02f + above + f.BarH / 2;
        // and a little nearer the player still, whatever the board is zoomed to
        centre += quat * new Vector3(0, 0, VR_BAR_FRONT);
        var pos = centre - quat * new Vector3(0, VR_GUI_Y, VR_GUI_Z);
        return (pos, quat);
    }

    /** The bar's place as the player left it (lem3d-bar): on the head, or an offset in the room. */
    public sealed record BarPrefs(bool Locked, Vector3 Pos, Quaternion Quat)
    {
        public string ToJson() => new JsonObject
        {
            ["locked"] = Locked,
            ["pos"] = new JsonArray(Pos.X, Pos.Y, Pos.Z),
            ["quat"] = new JsonArray(Quat.X, Quat.Y, Quat.Z, Quat.W),
        }.ToJsonString();

        public static BarPrefs? FromJson(string? json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                var o = JsonNode.Parse(json)!.AsObject();
                var p = o["pos"]!.AsArray();
                var q = o["quat"]!.AsArray();
                return new BarPrefs(o["locked"]?.GetValue<bool>() == true,
                    new Vector3(F(p[0]), F(p[1]), F(p[2])), new Quaternion(F(q[0]), F(q[1]), F(q[2]), F(q[3])));
            }
            catch (Exception) { return null; }
            static float F(JsonNode? n) => (float)n!.GetValue<double>();
        }
    }

    /** saveBarPrefs: on the head the head-relative hang; in the room the offset from the default,
     *  in the default's own frame. */
    public static BarPrefs SavePrefs(bool locked, Vector3 guiPos, Quaternion guiQuat, (Vector3 Pos, Quaternion Quat)? d)
    {
        if (locked) return new BarPrefs(true, guiPos, guiQuat);
        var inv = d!.Value.Quat.Inverse();
        return new BarPrefs(false, inv * (guiPos - d.Value.Pos), inv * guiQuat);
    }

    /** placeBarBelowDiorama: the default spot, moved by the offset the player left it at unless plain. */
    public static (Vector3 Pos, Quaternion Quat) BelowDiorama((Vector3 Pos, Quaternion Quat) d, BarPrefs? prefs, bool plain)
    {
        var pos = d.Pos;
        var quat = d.Quat;
        var p = !plain && prefs != null && !prefs.Locked ? prefs : null;
        if (p != null)
        {
            pos += d.Quat * p.Pos;
            quat *= p.Quat;
        }
        return (pos, quat);
    }
}

// The bar's node and its states, as the web keeps them: locked to the head (a child of the head's
// node, at a head-relative hang) or the room's (a child of the scene); parked below the board
// while a window is up and it was on the head; carried by the move handle.
public sealed class VrBar
{
    public readonly Node3D GuiRoot;
    public Node3D Head, Scene;
    public bool Locked = true;
    public VrWindowPlacement.BarPrefs? Prefs;
    Transform3D? _parked;                 // the head-relative transform to give back
    Vector3? _dragFrom;
    public bool AutoPlace;                // the first placement of a session
    // the board's frame for the default spot (null: no level)
    public Func<VrWindowPlacement.BarFrame?> Frame = () => null;
    // the lock button's state: on while the bar floats free
    public Action<bool>? LockChanged;
    // storage (lem3d-bar)
    public Action<string>? Store;

    public VrBar(Node3D guiRoot, Node3D head, Node3D scene, string? stored = null)
    {
        GuiRoot = guiRoot;
        Head = head;
        Scene = scene;
        Prefs = VrWindowPlacement.BarPrefs.FromJson(stored);
    }

    public bool Parked => _parked != null;

    static void SetLocal(Node3D n, Vector3 pos, Quaternion quat, Vector3 scale) => n.Transform = new Transform3D(new Basis(quat) * Basis.FromScale(scale), pos);

    /** setBarLocked: hand the bar to the head or to the room, keeping where it is in the world. */
    public void SetLocked(bool locked)
    {
        if (Locked == locked) return;
        Locked = locked;
        var parent = locked ? Head : Scene;
        var world = GuiRoot.IsInsideTree() ? GuiRoot.GlobalTransform : GuiRoot.Transform;
        if (GuiRoot.GetParent() is Node old) old.RemoveChild(GuiRoot);
        parent.AddChild(GuiRoot);
        var parentWorld = parent.IsInsideTree() ? parent.GlobalTransform : parent.Transform;
        GuiRoot.Transform = parentWorld.AffineInverse() * world;
        LockChanged?.Invoke(!locked);
    }

    /** resetBar: back to riding the head, square in front, with no drag offset. */
    public void Reset()
    {
        _parked = null;
        Locked = true;
        if (GuiRoot.GetParent() != Head) { if (GuiRoot.GetParent() is Node old) old.RemoveChild(GuiRoot); Head.AddChild(GuiRoot); }
        LockChanged?.Invoke(false);
        GuiRoot.Transform = Transform3D.Identity;
        _dragFrom = null;
    }

    public (Vector3 Pos, Quaternion Quat)? Default() => Frame() is { } f ? VrWindowPlacement.BarDefault(f) : null;

    /** saveBarPrefs: when the player moves the bar - never while a window has it parked. */
    public void SavePrefs(bool session)
    {
        if (!session || _parked != null) return;
        var d = Default();
        if (!Locked && d == null) return;
        Prefs = VrWindowPlacement.SavePrefs(Locked, GuiRoot.Position, GuiRoot.Quaternion, d);
        Store?.Invoke(Prefs.ToJson());
    }

    /** placeBarBelowDiorama */
    public void PlaceBelowDiorama(bool plain = false)
    {
        var d = Default();
        if (d == null) return;
        SetLocked(false);
        var (pos, quat) = VrWindowPlacement.BelowDiorama(d.Value, Prefs, plain);
        SetLocal(GuiRoot, pos, quat, Vector3.One);
        _dragFrom = null;
    }

    /** placeBarAtStart: a session's first placement - on the head as it hung, or in the room. */
    public void PlaceAtStart()
    {
        if (Prefs is { Locked: true } p)
        {
            SetLocked(true);
            SetLocal(GuiRoot, p.Pos, p.Quat, Vector3.One);
            _dragFrom = null;
        }
        else PlaceBelowDiorama();
    }

    /** The board was placed (placeDioramaForXR): the bar goes with it unless it rides the head. */
    public void OnDioramaPlaced()
    {
        if (AutoPlace) PlaceAtStart();
        else if (!Locked) PlaceBelowDiorama();
        AutoPlace = false;
    }

    /** syncBarForWindows: a bar on the head is parked below the board while a window is up, and
     *  handed back to the head as it was when the last one goes. */
    public void SyncForWindows(bool up, bool session)
    {
        if (up && _parked == null && Locked && session)
        {
            _parked = GuiRoot.Transform;
            PlaceBelowDiorama();
        }
        else if (!up && _parked is Transform3D back)
        {
            _parked = null;
            SetLocked(true);
            GuiRoot.Transform = back;
            _dragFrom = null;
        }
    }

    /** The park button: back to the default, for good. */
    public void Park(bool session)
    {
        _parked = null;
        PlaceBelowDiorama(true);
        SavePrefs(session);
    }

    /** The padlock. */
    public void ToggleLock(bool session)
    {
        SetLocked(!Locked);
        SavePrefs(session);
    }

    // the move handle: the hand's world delta, turned into the space the bar lives in
    public void DragStart() => _dragFrom = GuiRoot.Position;
    public void Drag(Vector3 worldDelta)
    {
        if (_dragFrom is not Vector3 from || GuiRoot.GetParent() is not Node3D parent) return;
        var q = parent.IsInsideTree() ? parent.GlobalBasis.GetRotationQuaternion() : parent.Quaternion;
        GuiRoot.Position = from + q.Inverse() * worldDelta;
    }
    public void DragEnd(bool session) => SavePrefs(session); // where it was let go is where it stays
}
