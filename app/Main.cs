using System.Linq;
using Godot;

namespace Lemmix.App;

// The root of the app. Starts OpenXR when a runtime answers (the Frame, SteamVR on a PC);
// without one (the Mac, CI, a container) it stays on the desktop camera, which only the
// tests use: the shipped app is VR only.
public partial class Main : Node3D
{
    public bool XrActive { get; private set; }

    public override void _Ready()
    {
        // Godot starts OpenXR itself (project setting xr/openxr/enabled); tests on the Mac pass
        // --xr-mode off, since the no-runtime path there can stall the start
        var xr = XRServer.FindInterface("OpenXR");
        if (xr != null && xr.IsInitialized())
        {
            GetViewport().UseXR = true;
            DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
            XrActive = true;
        }
        GetNode<Camera3D>("DesktopCamera").Current = !XrActive;
        GD.Print($"[lemmix] started, xr={(XrActive ? "openxr" : "off")}, renderer={RenderingServer.GetCurrentRenderingMethod()}");

        var args = OS.GetCmdlineUserArgs();
        if (args.Contains("--probe")) AddChild(new Test.Probe());
        if (args.Contains("--test")) AddChild(new Test.TestRunner());
        if (args.Contains("--shot")) AddChild(new Test.Shots());
        if (args.Contains("--smoke"))
            GetTree().CreateTimer(0.5).Timeout += () => { GD.Print("[lemmix] smoke ok"); GetTree().Quit(0); };
    }
}
