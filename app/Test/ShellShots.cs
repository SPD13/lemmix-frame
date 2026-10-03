using System;
using Godot;
using Lemmix.App.Shell;
using Lemmix.App.Xr;
using Lemmix.Store;
using ShellApp = Lemmix.App.Shell.App;

namespace Lemmix.App.Test;

// The "vr-scene" and "vr-catalog" shots: the whole headset view, one eye's worth, from a fixed
// head pose (1.6 m up, looking ahead and a little down) - the shell as a session runs it: the
// board placed in front, the room around it, the skills bar below with its row of controls, the
// status strip over the level, the right hand's beam on the board with NeoLemmix's cursor where it
// lands; or, with no level chosen, the lobby the app starts on ("vr-lobby", the beam on its PLAY
// sign) or the catalog its PLAY opens ("vr-catalog"). SHOT_LEVEL picks the level;
// SHOT_FOV the vertical field of view (75), to look closer; SHOT_ENV the room's mode (default full); SHOT_LOOK="yaw,pitch" (degrees, left and up positive) turns the head once the board is placed,
// to look round the room.
public static class ShellShots
{
    static void Dump(Node n)
    {
        if (n is VisualInstance3D v && v.IsVisibleInTree())
            GD.Print($"[dump] {v.GetPath()} {v.GetType().Name} at {v.GlobalPosition} aabb {v.GetAabb().Size}");
        foreach (var c in n.GetChildren()) Dump(c);
    }

    public static Viewport Make(Node root, string mode)
    {
        var vp = new SubViewport
        {
            Size = new Vector2I(1280, 960), OwnWorld3D = true, Msaa3D = Viewport.Msaa.Msaa4X,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
        };
        root.AddChild(vp);
        var input = new ScriptedXrInput();
        var head = new Transform3D(new Basis(Vector3.Right, Mathf.DegToRad(-14)), new Vector3(0, 1.6f, 0));
        input.HeadValue = head;
        // the right hand low and to the side, its beam on the board's middle; the left at rest
        var right = input.HandsValue[1];
        var from = new Vector3(0.16f, 1.28f, -0.25f);
        var to = new Vector3(0.02f, 1.42f, -0.9f);
        right.Aim = new Transform3D(Basis.LookingAt((to - from).Normalized(), Vector3.Up), from);
        right.Grip = right.Aim.Translated(new Vector3(0, -0.02f, 0.05f));
        var left = input.HandsValue[0];
        left.Aim = new Transform3D(Basis.LookingAt(new Vector3(0.1f, -0.3f, -1).Normalized(), Vector3.Up), new Vector3(-0.22f, 1.2f, -0.3f));
        left.Grip = left.Aim;
        string level = System.Environment.GetEnvironmentVariable("SHOT_LEVEL") ?? BoardShot.Builders;
        string envMode = System.Environment.GetEnvironmentVariable("SHOT_ENV") ?? "full"; // none | fog | full
        bool catalog = mode == "catalog", lobby = mode == "lobby" || mode == "lobby-vr";
        var args = catalog || lobby ? new[] { "--environment=" + envMode } : new[] { "--level=" + level, "--environment=" + envMode };
        var app = new ShellApp(new AppOptions
        {
            Args = ShellArgs.Parse(args), Input = input, Store = new LocalStore(),
            Head = new Camera3D { Name = "head", Fov = float.Parse(System.Environment.GetEnvironmentVariable("SHOT_FOV") ?? "75", System.Globalization.CultureInfo.InvariantCulture) }, EnvironmentInBackground = false,
            UserDataDir = OS.GetUserDataDir(), AssetRoot = TerrainShot.Assets,
        });
        if (catalog) app.Ready += () => app.Library.Navigate("Lemmings_Redux/Gentle");
        vp.AddChild(app);
        // once the session has started (the windows placed): the catalog opened, or the beam on PLAY
        if (catalog || lobby)
        {
            int frames = 0;
            void Open()
            {
                if (++frames < 2) return;
                if (catalog) app.Windows.SetCatalog(true);
                else if (mode == "lobby-vr") app.Windows.SetVrOptions(true);
                else
                {
                    app.Lobby.CentreScrollerText();
                    var sign = app.Lobby.Signs[0].GlobalPosition;
                    right.Aim = new Transform3D(Basis.LookingAt((sign - from).Normalized(), Vector3.Up), from);
                    right.Grip = right.Aim.Translated(new Vector3(0, -0.02f, 0.05f));
                }
                root.GetTree().ProcessFrame -= Open;
            }
            root.GetTree().ProcessFrame += Open;
        }
        if (System.Environment.GetEnvironmentVariable("SHOT_LOOK") is { } look && look.Split(',') is { Length: 2 } yp)
        {
            float yaw = Mathf.DegToRad(float.Parse(yp[0], System.Globalization.CultureInfo.InvariantCulture));
            float pitch = Mathf.DegToRad(float.Parse(yp[1], System.Globalization.CultureInfo.InvariantCulture));
            int frames = 0;
            void Turn()
            {
                if (++frames < 3) return;
                input.HeadValue = new Transform3D(new Basis(Vector3.Up, yaw) * new Basis(Vector3.Right, pitch), head.Origin);
                root.GetTree().ProcessFrame -= Turn;
            }
            root.GetTree().ProcessFrame += Turn;
        }
        if (System.Environment.GetEnvironmentVariable("SHOT_DEBUG") == "1")
            app.GetTree().CreateTimer(0.3).Timeout += () => Dump(app);
        return vp;
    }
}
