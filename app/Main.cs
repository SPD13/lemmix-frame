using System.Linq;
using Godot;

namespace Lemmix.App;

// The root of the app. Starts OpenXR when a runtime answers (the Frame, SteamVR on a PC) and runs
// the shell (Shell/App: the game as the web's app.js wires it in a headset session). Without a
// runtime (the Mac, CI, a container) the shell stands a fixed head where a player would; the
// tests, shots and probes run instead of it, on the desktop camera.
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
        GD.Print($"[lemmix] started, xr={(XrActive ? "openxr" : "off")}, renderer={RenderingServer.GetCurrentRenderingMethod()}");

        var args = OS.GetCmdlineUserArgs();
        bool tool = args.Contains("--probe") || args.Contains("--test") || args.Contains("--shot") || args.Contains("--trailer");
        GetNode<Camera3D>("DesktopCamera").Current = tool && !XrActive;
        if (!tool)
        {
            var app = new Shell.App(Shell.AppOptions.FromCommandLine(args));
            AddChild(app);
            if (args.Contains("--benchmark")) AddChild(new Shell.Benchmark(app));
        }
        if (args.Contains("--probe")) AddChild(new Test.Probe());
        if (args.Contains("--test")) AddChild(new Test.TestRunner());
        if (args.Contains("--shot")) AddChild(new Test.Shots());
        if (args.Contains("--trailer")) AddChild(new Test.Trailer());
        if (args.Contains("--smoke"))
            GetTree().CreateTimer(0.5).Timeout += () => { GD.Print("[lemmix] smoke ok"); GetTree().Quit(0); };
    }
}
