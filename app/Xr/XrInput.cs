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
public sealed class OpenXrInput : IXrInput
{
    readonly XRController3D[] _aim;
    readonly XRController3D[] _grip;
    readonly XRCamera3D _camera;
    readonly HandState[] _hands = { new() { Handedness = "left" }, new() { Handedness = "right" } };

    public OpenXrInput(XRController3D leftAim, XRController3D rightAim, XRController3D leftGrip, XRController3D rightGrip, XRCamera3D camera)
    {
        _aim = new[] { leftAim, rightAim };
        _grip = new[] { leftGrip, rightGrip };
        _camera = camera;
    }

    public bool Presenting => XRServer.PrimaryInterface?.IsInitialized() == true;
    public IReadOnlyList<HandState> Hands => _hands;
    public Transform3D? Head => Presenting ? _camera.GlobalTransform : null;

    public void Poll()
    {
        for (int i = 0; i < 2; i++)
        {
            var c = _aim[i];
            var h = _hands[i];
            h.Connected = c.GetHasTrackingData();
            h.Aim = c.GlobalTransform;
            h.Grip = _grip[i].GlobalTransform;
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
