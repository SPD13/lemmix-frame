using System.Collections.Generic;
using Godot;
using Lemmix.App.Xr;

namespace Lemmix.App.Test;

// web/3d/js/vr.js behaviour, driven by scripted controller poses.
public static class VrManagerTests
{
    sealed class Hooks : IVrHooks
    {
        public VrPick? Under = new("board");
        public readonly List<string> Log = new();
        public VrPick? Pick(Vector3 o, Vector3 d) => Under;
        public VrHit? RaycastHit(Vector3 o, Vector3 d) => Under == null ? null : new VrHit(o + d, 1, true);
        public void OnSelectPick(VrPick pick) => Log.Add("select:" + pick.Kind + (pick.Scrubbing ? ":scrub" : ""));
        public void OnHoverPick(VrPick? pick) { }
        public void OnVrButton(string code, bool down) => Log.Add((down ? "down:" : "up:") + code);
        public void OnStick(string code, float x, float y, double dt) => Log.Add($"stick:{code}:{x:0.00},{y:0.00}");
        public bool PlaceDiorama(Transform3D? head) { Log.Add("place"); return true; }
    }

    static (ScriptedXrInput, Hooks, VrManager, Node3D) Make()
    {
        var input = new ScriptedXrInput();
        var hooks = new Hooks();
        var root = new Node3D();
        var vr = new VrManager(input, hooks, root);
        vr.Update(0); // session start: placement
        return (input, hooks, vr, root);
    }

    static void At(HandState h, Vector3 p) { h.Aim = new Transform3D(Basis.Identity, p); h.Grip = h.Aim; }

    [AppTest]
    public static void SessionStartPlacesTheDiorama()
    {
        var (_, hooks, _, root) = Make();
        Check.True(hooks.Log.Contains("place"), "placed on the first frame with a head pose");
        root.Free();
    }

    [AppTest]
    public static void ATriggerPullWithoutMovingIsAClick()
    {
        var (input, hooks, vr, root) = Make();
        var right = input.HandsValue[1];
        At(right, new Vector3(0, 1, 0));
        right.Trigger = true; vr.Update(0.016);
        At(right, new Vector3(0.01f, 1, 0)); vr.Update(0.016);   // 1 cm: still a click
        right.Trigger = false; vr.Update(0.016);
        Check.True(hooks.Log.Contains("select:board"), "the release is the click");
        Check.Near(Vector3.Zero, root.Position, "the board did not move");
        root.Free();
    }

    [AppTest]
    public static void TriggerHeldAndMovedDragsTheBoardInThreeAxes()
    {
        var (input, hooks, vr, root) = Make();
        var right = input.HandsValue[1];
        At(right, new Vector3(0, 1, 0));
        right.Trigger = true; vr.Update(0.016);
        At(right, new Vector3(0.1f, 1.05f, 0.2f)); vr.Update(0.016);
        right.Trigger = false; vr.Update(0.016);
        Check.Near(new Vector3(0.1f, 0.05f, 0.2f), root.Position, "the board follows the hand");
        Check.True(!hooks.Log.Contains("select:board"), "a drag is not a click");
        root.Free();
    }

    [AppTest]
    public static void TheOtherHandsTriggerTakesTheBeamAndDoesNothingElse()
    {
        var (input, hooks, vr, root) = Make();
        var left = input.HandsValue[0];
        left.Trigger = true; vr.Update(0.016);
        left.Trigger = false; vr.Update(0.016);
        Check.Equal("left", vr.PointerHand, "the beam moved to the left hand");
        Check.True(!hooks.Log.Contains("select:board"), "the takeover pull does not click");
        left.Trigger = true; vr.Update(0.016); left.Trigger = false; vr.Update(0.016);
        Check.True(hooks.Log.Contains("select:board"), "the next pull clicks");
        root.Free();
    }

    [AppTest]
    public static void TwoGripsScaleAboutTheHandsClamped()
    {
        var (input, _, vr, root) = Make();
        HandState l = input.HandsValue[0], r = input.HandsValue[1];
        At(l, new Vector3(-0.1f, 1, 0)); At(r, new Vector3(0.1f, 1, 0));
        l.Squeeze = true; r.Squeeze = true; vr.Update(0.016);
        At(l, new Vector3(-0.2f, 1, 0)); At(r, new Vector3(0.2f, 1, 0)); vr.Update(0.016);
        Check.Near(Vector3.One * 2, root.Scale, "hands twice as far apart: twice the size");
        At(l, new Vector3(-10f, 1, 0)); At(r, new Vector3(10f, 1, 0)); vr.Update(0.016);
        Check.Near(Vector3.One * 8, root.Scale, "clamped at 8x");
        root.Free();
    }

    [AppTest]
    public static void OneGripDragsTheBoard()
    {
        var (input, _, vr, root) = Make();
        var l = input.HandsValue[0];
        At(l, new Vector3(0, 1, 0));
        l.Squeeze = true; vr.Update(0.016);
        At(l, new Vector3(0, 1.3f, -0.2f)); vr.Update(0.016);
        Check.Near(new Vector3(0, 0.3f, -0.2f), root.Position, "the board follows the gripping hand");
        root.Free();
    }

    [AppTest]
    public static void ButtonsAndSticksAreNamedByTheHandsRole()
    {
        var (input, hooks, vr, root) = Make();
        HandState l = input.HandsValue[0], r = input.HandsValue[1];
        r.Lower = true; l.Upper = true; vr.Update(0.016);
        r.Lower = false; l.Upper = false; vr.Update(0.016);
        Check.True(hooks.Log.Contains("down:VrPointA") && hooks.Log.Contains("up:VrPointA"), "right A is the pointing hand's A");
        Check.True(hooks.Log.Contains("down:VrFreeB"), "left B is the free hand's B");
        r.Stick = new Vector2(0.1f, -0.5f); vr.Update(0.016);   // x within the dead zone; y away
        Check.True(hooks.Log.Contains("stick:VrPointStick:0.00,0.50"), "dead zone and the flip to away-positive");
        root.Free();
    }
}
