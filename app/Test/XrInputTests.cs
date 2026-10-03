using Godot;
using Lemmix.App.Xr;

namespace Lemmix.App.Test;

// The Frame controller's aim and grip from its palm pose (SteamVR leaves the first two untracked
// on the Frame): with the controller at the identity, the palm stands where the driver's file puts
// openxr_handmodel, and the derived aim and grip land on its openxr_aim and openxr_grip.
public static class XrInputTests
{
    [AppTest]
    public static void FrameAimAndGripFollowFromThePalm()
    {
        for (int hand = 0; hand < 2; hand++)
        {
            float m = hand == 0 ? -1 : 1;
            var palm = new Transform3D(Basis.FromEuler(new Vector3(Mathf.DegToRad(-39.4f), 0, 0)), new Vector3(0.01125f * m, -0.00182941f, 0.1019482f));
            var aim = palm * OpenXrInput.FrameOffsets.AimFromPalm(hand);
            var grip = palm * OpenXrInput.FrameOffsets.GripFromPalm(hand);
            Check.Near(new Vector3(-0.012694f * m, -0.02522f, 0.020687f), aim.Origin, "aim origin, hand " + hand);
            Check.Near(new Vector3(0.003117f * m, -0.004277f, 0.099501f), grip.Origin, "grip origin, hand " + hand);
            // the aim ray: tilted 40 degrees down from the controller's forward
            var ray = -aim.Basis.Z.Normalized();
            Check.Near(new Vector3(0, -Mathf.Sin(Mathf.DegToRad(40)), -Mathf.Cos(Mathf.DegToRad(40))), ray, "aim direction, hand " + hand);
        }
    }
}
