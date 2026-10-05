using System.Collections.Generic;
using Godot;

namespace Lemmix.App.Xr;

// What VrManager reads of the controllers each frame (WebXR's inputSources in the web version):
// a pose per hand, its trigger and grip, the face buttons, stick click and the stick. Two
// sources: OpenXR through Godot's XRController3D (on the Frame), and a scripted one the tests
// on the Mac drive with recorded poses.
public sealed class HandState
{
    public string? Handedness;          // "left" | "right" | null when the runtime does not say
    public bool Connected;
    public Transform3D Aim = Transform3D.Identity;   // target ray: -Z is where it points
    public Transform3D Grip = Transform3D.Identity;
    public bool Trigger, Squeeze;
    public bool Lower, Upper, StickClick;            // A/X, B/Y, the stick pressed (xr-standard 4, 5, 3)
    public Vector2 Stick;                            // y negative away from the player, as WebXR's
}

public interface IXrInput
{
    bool Presenting { get; }
    // the two controllers, in a fixed order (index 0, 1 like renderer.xr.getController(i))
    IReadOnlyList<HandState> Hands { get; }
    // the head's pose in the play space, or null before the runtime gives one
    Transform3D? Head { get; }
    void Poll();
}

// OpenXR through Godot: the hands are XRController3D nodes under the XROrigin3D, with the
// default action map's names (trigger_click, grip_click, ax_button, by_button, primary,
// primary_click). Godot gives the pose of the node's `pose` (aim for the ray); the grip pose
// comes from a second controller node set to the grip pose.
//
// The palm pose stands in for an aim or grip pose the runtime does not deliver. SteamVR 2.17 on
// the Steam Frame (Oct 2026) leaves the Frame controller's aim and grip action poses (its /pose/tip
// and /pose/grip) untracked for this app while the palm (/pose/openxr_handmodel) tracks; the
// driver's render model file gives all three components' offsets in the controller's frame, so
// aim = palm * handmodel^-1 * aim exactly (FrameOffsets). Another controller without aim: its palm.
public sealed class OpenXrInput : IXrInput
{
    readonly XRController3D[] _aim;
    readonly XRController3D[] _grip;
    readonly XRController3D[]? _palm;
    readonly XRCamera3D _camera;
    readonly HandState[] _hands = { new() { Handedness = "left" }, new() { Handedness = "right" } };

    public OpenXrInput(XRController3D leftAim, XRController3D rightAim, XRController3D leftGrip, XRController3D rightGrip, XRCamera3D camera,
        XRController3D? leftPalm = null, XRController3D? rightPalm = null)
    {
        _aim = new[] { leftAim, rightAim };
        _grip = new[] { leftGrip, rightGrip };
        _palm = leftPalm != null && rightPalm != null ? new[] { leftPalm, rightPalm } : null;
        _camera = camera;
    }

    public const string FrameProfile = "/interaction_profiles/valve/frame_controller_valve";

    // The Frame controller's poses from its palm pose: SteamVR's frame_controller render model file
    // (drivers/frame_controller/resources/rendermodels/frame_controller_<hand>/*.json, components'
    // component_local: origin in metres, rotate_xyz in degrees; the left hand's x mirrored).
    public static class FrameOffsets
    {
        static Transform3D Component(float x, float y, float z, float rotXDeg) =>
            new(Basis.FromEuler(new Vector3(Mathf.DegToRad(rotXDeg), 0, 0)), new Vector3(x, y, z));

        static Transform3D Hand(int hand) => Component(hand == 0 ? -0.01125f : 0.01125f, -0.00182941f, 0.1019482f, -39.4f);
        static Transform3D Aim(int hand) => Component(hand == 0 ? 0.012694f : -0.012694f, -0.02522f, 0.020687f, -40f);
        static Transform3D Grip(int hand) => Component(hand == 0 ? -0.003117f : 0.003117f, -0.004277f, 0.099501f, 2.8091f);

        public static Transform3D AimFromPalm(int hand) => Hand(hand).AffineInverse() * Aim(hand);
        public static Transform3D GripFromPalm(int hand) => Hand(hand).AffineInverse() * Grip(hand);
        // the grip in the runtime's render model (its glTF shares the render model file's frame:
        // the meshes' extents in the device report put this grip mid-handle, behind the head)
        public static Transform3D GripInModel(int hand) => Grip(hand);
    }

    readonly Transform3D[] _aimFromPalm = { FrameOffsets.AimFromPalm(0), FrameOffsets.AimFromPalm(1) };
    readonly Transform3D[] _gripFromPalm = { FrameOffsets.GripFromPalm(0), FrameOffsets.GripFromPalm(1) };

    public bool Presenting => XRServer.PrimaryInterface?.IsInitialized() == true;
    public IReadOnlyList<HandState> Hands => _hands;
    public Transform3D? Head => Presenting ? _camera.GlobalTransform : null;

    // what the controllers report, in the log: on any change of a hand's tracking or profile, and
    // every few seconds for the first two minutes (device bring-up: input that never arrives)
    ulong _logStart, _logNext;
    string _logLast = "";

    void LogState()
    {
        ulong now = Time.GetTicksMsec();
        if (_logStart == 0) _logStart = now;
        var parts = new List<string>();
        for (int i = 0; i < 2; i++)
        {
            var c = _aim[i];
            var t = XRServer.GetTracker(c.Tracker) as XRControllerTracker;
            var pose = t?.GetPose(c.Pose);
            string poses = "";
            foreach (var name in new[] { "aim_pose", "grip_pose", "palm_pose", "default_pose" })
            {
                var pz = t?.GetPose(name);
                poses += " " + name + "=" + (pz == null ? "none" : pz.HasTrackingData + "/" + pz.TrackingConfidence + "@" + pz.Transform.Origin.ToString("0.00"));
            }
            parts.Add($"{c.Tracker}: profile={t?.Profile ?? "-"}{poses} tracking={c.GetHasTrackingData()} trigger={c.GetFloat("trigger"):0.00} stick={c.GetVector2("primary").ToString("0.0")}");
        }
        string state = string.Join(" | ", parts);
        string key = System.Text.RegularExpressions.Regex.Replace(state, @"(trigger|stick)=\S+|@\(\S+, \S+, \S+\)", "");
        bool early = now - _logStart < 120_000;
        if (key == _logLast && !(early && now >= _logNext)) return;
        _logLast = key;
        _logNext = now + 3000;
        GD.Print("[xr] " + state);
    }

    public void Poll()
    {
        LogState();
        for (int i = 0; i < 2; i++)
        {
            var c = _aim[i];
            var h = _hands[i];
            var palm = _palm?[i];
            bool aimOk = c.GetHasTrackingData(), gripOk = _grip[i].GetHasTrackingData();
            bool palmOk = palm != null && palm.GetHasTrackingData();
            bool frame = palmOk && (XRServer.GetTracker(c.Tracker) as XRControllerTracker)?.Profile == FrameProfile;
            h.Connected = aimOk || palmOk;
            h.Aim = aimOk || !palmOk ? c.GlobalTransform : palm!.GlobalTransform * (frame ? _aimFromPalm[i] : Transform3D.Identity);
            h.Grip = gripOk || !palmOk ? _grip[i].GlobalTransform : palm!.GlobalTransform * (frame ? _gripFromPalm[i] : Transform3D.Identity);
            h.Trigger = c.IsButtonPressed("trigger_click");
            h.Squeeze = c.IsButtonPressed("grip_click");
            h.Lower = c.IsButtonPressed("ax_button");
            h.Upper = c.IsButtonPressed("by_button");
            h.StickClick = c.IsButtonPressed("primary_click");
            var v = c.GetVector2("primary");
            h.Stick = new Vector2(v.X, -v.Y); // OpenXR y is up/away positive; WebXR's is negative away
        }
    }
}

// The tests' input: whatever the script set, as it set it.
public sealed class ScriptedXrInput : IXrInput
{
    public bool PresentingValue = true;
    public readonly HandState[] HandsValue = { new() { Handedness = "left", Connected = true }, new() { Handedness = "right", Connected = true } };
    public Transform3D? HeadValue = new Transform3D(Basis.Identity, new Vector3(0, 1.6f, 0));
    public bool Presenting => PresentingValue;
    public IReadOnlyList<HandState> Hands => HandsValue;
    public Transform3D? Head => HeadValue;
    public void Poll() { }
}
