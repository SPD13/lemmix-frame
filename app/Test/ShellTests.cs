using System;
using System.IO;
using System.Linq;
using Godot;
using Lemmix.App.Board;
using Lemmix.App.Session;
using Lemmix.App.Shell;
using ShellApp = Lemmix.App.Shell.App;
using Lemmix.App.Ui;
using Lemmix.App.Ui.Pages;
using Lemmix.App.Ui.Windows;
using Lemmix.App.Xr;
using Lemmix.Engine;
using Lemmix.Store;

namespace Lemmix.App.Test;

// The shell end to end, headless, with scripted controllers: the app as a headset session runs
// it (Shell/App), driven frame by frame on a manual clock, the hands' rays aimed at what is on
// screen. Needs the assets (WEB_ASSETS); skipped without them.
public static class ShellTests
{
    const string Builders = BoardShot.Builders;
    // the stored solution that wins soonest (251 frames)
    const string Clones = "LemmingsPlus_All_20201114/Lemmings_Plus_Omega/Breezy/Attack_Of_The_Clones.nxlv";
    const string ClonesReplay = "res://Data/solutions/LemmingsPlus_All_20201114/levels/Lemmings_Plus_Omega/Breezy/Attack_Of_The_Clones.nxrp";

    internal sealed class Rig : IDisposable
    {
        public double Now = 1000;
        public readonly ScriptedXrInput Input = new();
        public readonly ShellApp App;
        public readonly string UserData;

        public Rig(string? assets = null, params string[] args)
        {
            UserData = Path.Combine(Path.GetTempPath(), "lemmix-shell-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(UserData);
            App = new ShellApp(new AppOptions
            {
                Args = ShellArgs.Parse(args.Append("--environment=none")),
                Input = Input, Store = new LocalStore(), Clock = () => Now, Manual = true,
                EnvironmentInBackground = false, UserDataDir = UserData, AssetRoot = assets ?? TerrainShot.Assets,
            });
            ((SceneTree)Godot.Engine.GetMainLoop()).Root.AddChild(App);
            // the hands down by the sides, pointing ahead
            At(Right, new Vector3(0.2f, 1.2f, 0), new Vector3(0.2f, 1.2f, -1));
            At(Left, new Vector3(-0.2f, 1.2f, 0), new Vector3(-0.2f, 1.2f, -1));
            Frame();
        }

        public HandState Right => Input.HandsValue[1];
        public HandState Left => Input.HandsValue[0];
        public GameSession Session => App.Session ?? throw new Exception("no level loaded");

        public void Frame(double ms = 16) { Now += ms; App.Frame(Now); }

        public static void At(HandState h, Vector3 from, Vector3 to)
        {
            h.Aim = new Transform3D(Basis.LookingAt((to - from).Normalized(), Vector3.Up), from);
            h.Grip = h.Aim;
        }

        // the right hand's beam on a point, then a trigger pull and release
        public void Click(Vector3 target)
        {
            At(Right, new Vector3(0.1f, 1.45f, 0), target);
            Frame();
            Right.Trigger = true; Frame();
            Right.Trigger = false; Frame();
        }

        // (a frame first: what was just opened is laid out)
        public void Click(Node3D node) { Frame(); Click(node.GlobalPosition); Frame(); }

        public void Dispose()
        {
            App.GetParent()?.RemoveChild(App);
            App.QueueFree();
            try { Directory.Delete(UserData, true); } catch (Exception) { }
        }
    }

    static bool HaveAssets()
    {
        bool ok = File.Exists(Path.Combine(TerrainShot.Assets, "levels", "index.json"));
        if (!ok) GD.Print("[test] (no assets: skipped)");
        return ok;
    }

    // a point on a window's canvas, in the world
    static Vector3 OnPanel(Panel3D p, float x, float y) =>
        p.GlobalTransform * new Vector3((x / p.Canvas.Width - 0.5f) * p.WidthMetres, (0.5f - y / p.Canvas.Height) * p.HeightMetres, 0);

    static Vector3 OnTile(VrCatalog c, int index)
    {
        var cell = c.Cells[index];
        return OnPanel(c.Panel, cell.X + cell.W / 2, VrCatalog.VR_CAT_VIEW_Y + cell.Y - c.Scroll + cell.H / 2);
    }

    static GameSession LoadedRig(Rig rig)
    {
        rig.Frame();
        return rig.Session;
    }

    // ------------------------------------------------------------ the catalog
    [AppTest]
    public static void StartsOnTheLockedCatalog()
    {
        if (!HaveAssets()) return;
        using var rig = new Rig();
        var w = rig.App.Windows;
        Check.True(w.Catalog.Root.Visible, "the catalog is up");
        Check.True(rig.App.Locked, "locked until a level is chosen");
        Check.True(!w.Catalog.Close.Visible, "a locked catalog has no close");
        Check.True(rig.App.Session == null, "no level on the board");
        Check.Equal("choose a level", w.Status.Status.Name, "the strip");
        // Escape does not close a locked catalog
        rig.App.KeyDown("Escape");
        Check.True(w.Catalog.Root.Visible, "still up after Escape");
    }

    [AppTest]
    public static void ATilePickedWithTheBeamLoadsTheLevel()
    {
        if (!HaveAssets()) return;
        using var rig = new Rig();
        var app = rig.App;
        var cat = app.Windows.Catalog;
        // down the tree with the beam: the pack's row, then its rank's
        int pack = cat.Items.FindIndex(it => it.Kind == "dir" && it.Path == "Lemmings_Redux");
        Check.True(pack >= 0, "the Redux pack is a row");
        rig.Click(OnTile(cat, pack));
        int rank = cat.Items.FindIndex(it => it.Kind == "dir" && it.Path == "Lemmings_Redux/Gentle");
        Check.True(rank >= 0, "its ranks are rows (" + cat.Heading + ")");
        rig.Click(OnTile(cat, rank));
        int tile = cat.Items.FindIndex(it => it.LevelId == Builders);
        Check.True(tile >= 0, "the level is a tile");
        // the beam on the tile lights it
        Rig.At(rig.Right, new Vector3(0.1f, 1.45f, 0), OnTile(cat, tile));
        rig.Frame();
        Check.Equal(tile, cat.Hover, "the tile under the beam is lit");
        Check.True(rig.App.Vr.LastHit(1) != null, "the beam lands on the catalog");
        rig.Click(OnTile(cat, tile));
        Check.True(app.Session != null, "the level is loaded");
        Check.Equal(Builders, app.LevelId, "the level picked");
        Check.True(!cat.Root.Visible, "the catalog went");
        Check.True(!app.Locked, "the lock is lifted");
        Check.Equal(Builders, app.Library.Recent.List().Items.FirstOrDefault() as string, "on the recent list");
        Check.True(app.Session!.Running, "the level runs");
        // placed 0.9 m ahead at 2.5 mm a pixel, the bar below it
        Check.True(Math.Abs(app.DioramaRoot.Scale.X - VrManager.VR_PIXEL_SCALE) < 1e-6, "board at 2.5 mm a pixel");
        Check.True(!app.Windows.Bar.Locked, "the bar went below the board");
        Check.True(app.Windows.Status.Panel.Visible, "the status strip stands over the board");
    }

    // ------------------------------------------------------------ playing with the beam
    [AppTest]
    public static void WindowsOpenAlongTheDefaultForwardNotTheGaze()
    {
        if (!HaveAssets()) return;
        using var rig = new Rig(null, "--level=" + Builders);
        LoadedRig(rig);
        var app = rig.App;
        // the head turned 90 degrees to the right and tilted down, standing off the origin
        var headPos = new Vector3(0.3f, 1.6f, 0.2f);
        rig.Input.HeadValue = new Transform3D(new Basis(Vector3.Up, -Mathf.Pi / 2) * new Basis(Vector3.Right, -0.4f), headPos);
        rig.Frame();
        Check.True(app.Windows.Act(new VrPick("bar", BarTool: "quit")), "a question opens");
        var root = app.Windows.WindowRoot.GlobalTransform;
        Check.Near(Vector3.Back, root.Basis.Z.Normalized(), "the window faces the default forward (-Z), upright, not the gaze", 1e-4f);
        // the window's centre: VR_MODAL_Z away along the default forward, looking down as far as the
        // board's centre lies below the eyes
        float d = -VrManager.VR_MODAL_Z, pitch = ShellApp.WindowPitch;
        var centre = root * new Vector3(0, 0, VrManager.VR_MODAL_Z);
        Check.Near(headPos + new Vector3(0, -d * MathF.Sin(pitch), -d * MathF.Cos(pitch)), centre, "ahead, at the board's eye level", 1e-4f);
        Check.True(centre.Y < headPos.Y - 0.1f, "below the eyes (" + (headPos.Y - centre.Y) + " m)");
        app.Windows.Act(new VrPick("bar", BarTool: "no"));
        // the board, placed again with the head still turned: along the same forward, parallel to the windows
        app.Recenter();
        rig.Frame();
        var board = app.DioramaRoot.GlobalTransform;
        Check.Near(Vector3.Back, board.Basis.Z.Normalized(), "the board faces the default forward too", 1e-4f);
        Check.Near(root.Basis.Z.Normalized(), board.Basis.Z.Normalized(), "board and windows parallel", 1e-4f);
        // the level's middle (App.FocusX), half the slab deep
        var focus = board * new Vector3(rig.Session.Level.Width / 2f, rig.Session.Level.Height / 2f, (float)Lemmix.App.Board.BoardZ.TERRAIN_DEPTH / 2);
        Check.True(Mathf.Abs(focus.X - headPos.X) < 1e-3f, "the board's focus straight ahead (" + focus + ")");
        Check.True(focus.Z < headPos.Z - 0.5f, "in front, not to the side (" + focus + ")");
        app.Windows.Act(new VrPick("bar", BarTool: "no"));
    }

    [AppTest]
    public static void TheTriggerOnALemmingAssignsTheSelectedSkill()
    {
        if (!HaveAssets()) return;
        using var rig = new Rig(null, "--level=" + Builders);
        var s = LoadedRig(rig);
        var game = s.Game;
        Lemming? lem = null;
        for (int i = 0; i < 400 && lem == null; i++)
        {
            game.GameTimer.Tick();
            lem = game.Sim.Lemmings.FirstOrDefault(l => !l.Removed && l.Action == BA.WALKING);
        }
        Check.True(lem != null, "a lemming walks");
        game.Skills.SetSelectedSkill(game.Sim.ActiveSkills.IndexOf("BUILDER"));
        s.TogglePause();
        rig.Frame();
        int before = game.Sim.Recorded.Count;
        var world = BoardMaterials.WorldOf(s.Board) * new Vector3(lem!.X, lem.Y - 4, (float)BoardZ.LEMMING_Z);
        Rig.At(rig.Right, new Vector3(0.1f, 1.45f, 0), world);
        rig.Frame();
        Check.True(rig.App.Vr.LastHit(1) is { OnBoard: true }, "the beam lands on the board");
        Check.True(s.Hovered == lem, "the lemming under the beam is ringed");
        rig.Right.Trigger = true; rig.Frame();
        rig.Right.Trigger = false; rig.Frame();
        Check.True(game.Sim.Recorded.Count > before, $"the assignment is in the replay ({before} -> {game.Sim.Recorded.Count})");
        Check.Equal(BA.BUILDING, lem.Action, "the lemming builds");
    }

    [AppTest]
    public static void TheRestartButtonAsksThenRestarts()
    {
        if (!HaveAssets()) return;
        using var rig = new Rig(null, "--level=" + Builders);
        var first = LoadedRig(rig);
        var w = rig.App.Windows;
        Check.True(w.Toolbar.Restart.IsVisibleInTree(), "the restart button is on the bar");
        rig.Click(w.Toolbar.Restart);
        Check.True(w.Modal.Root.Visible, "it asks first");
        Check.True(!first.Running, "the question holds the clock");
        Check.Equal(first, rig.App.Session, "nothing restarted yet");
        rig.Click(w.Modal.Yes);
        Check.True(!w.Modal.Root.Visible, "the question went");
        Check.True(rig.App.Session != null && rig.App.Session != first, "a new game of the level");
        Check.Equal(Builders, rig.App.LevelId, "the same level");
        Check.True(rig.App.Session!.Running, "and it runs");
    }

    [AppTest]
    public static void PauseAndTheWindowsHoldTheClock()
    {
        if (!HaveAssets()) return;
        using var rig = new Rig(null, "--level=" + Builders);
        var s = LoadedRig(rig);
        var w = rig.App.Windows;
        rig.Click(w.Toolbar.Pause);
        Check.True(!s.Running, "the pause button pauses");
        Check.True(w.Toolbar.Pause.State.On, "and shows it");
        rig.Click(w.Toolbar.Pause);
        Check.True(s.Running, "and resumes");
        rig.Click(w.Toolbar.Settings);
        Check.True(w.Settings.Root.Visible, "the settings window");
        Check.True(!s.Running, "holds the clock");
        rig.Click(w.Settings.Close);
        Check.True(s.Running, "its close lets it go");
    }

    // ------------------------------------------------------------ the controls table
    [AppTest]
    public static void AKeyAndAControllerButtonRunTheirFunctions()
    {
        if (!HaveAssets()) return;
        using var rig = new Rig(null, "--level=" + Builders);
        var s = LoadedRig(rig);
        var app = rig.App;
        app.Hotkeys.Set("Digit5", "skill", "builder");
        app.HandleKey(new InputEventKey { PhysicalKeycode = Key.Key5, Pressed = true });
        app.HandleKey(new InputEventKey { PhysicalKeycode = Key.Key5, Pressed = false });
        rig.Frame();
        int sel = s.Game.Skills.GetSelectedSkill();
        Check.Equal("BUILDER", sel >= 0 ? s.Game.Sim.ActiveSkills[sel] : "(none)", "Digit5 selects the builder");
        // P pauses (the traditional layout), Shift is held as a filter
        app.HandleKey(new InputEventKey { PhysicalKeycode = Key.P, Pressed = true });
        Check.True(!s.Running, "KeyP pauses");
        // the pointing hand's A recentres: the board back in front of the head
        var placed = app.DioramaRoot.Transform;
        app.DioramaRoot.Position += new Vector3(0.5f, 0.2f, 0);
        rig.Right.Lower = true; rig.Frame();
        rig.Right.Lower = false; rig.Frame();
        Check.Near(placed.Origin, app.DioramaRoot.Position, "VrPointA recentred the board", 1e-3f);
        // a stick pans (the pointing hand's, by default)
        rig.Right.Stick = new Vector2(1, 0); rig.Frame(100);
        rig.Right.Stick = Vector2.Zero;
        Check.True(app.DioramaRoot.Position.DistanceTo(placed.Origin) > 0.01f, "the pointing stick pans the board");
    }

    [AppTest]
    public static void TheGripsMoveAndScaleTheBoard()
    {
        if (!HaveAssets()) return;
        using var rig = new Rig(null, "--level=" + Builders);
        LoadedRig(rig);
        var root = rig.App.DioramaRoot;
        var p0 = root.Position;
        rig.Right.Squeeze = true; rig.Frame();
        Rig.At(rig.Right, new Vector3(0.3f, 1.25f, 0), new Vector3(0.3f, 1.25f, -1));
        rig.Frame();
        Check.Near(p0 + new Vector3(0.1f, 0.05f, 0), root.Position, "one grip drags the board with the hand", 1e-3f);
        float s0 = root.Scale.X;
        rig.Left.Squeeze = true; rig.Frame();
        Rig.At(rig.Left, new Vector3(-0.5f, 1.2f, 0), new Vector3(-0.5f, 1.2f, -1));
        rig.Frame();
        Check.True(root.Scale.X > s0 * 1.2f, $"both grips apart scale it up ({s0} -> {root.Scale.X})");
        rig.Right.Squeeze = rig.Left.Squeeze = false; rig.Frame();
    }

    // ------------------------------------------------------------ the end of a level
    static void PlayToTheEnd(Rig rig)
    {
        for (int i = 0; i < 4000 && rig.App.EndAdvanceAt == null; i++) rig.Frame(40);
        Check.True(rig.App.EndAdvanceAt != null, "the level ended");
    }

    [AppTest]
    public static void AWatchedSolutionRecordsNothingAndMovesOn()
    {
        if (!HaveAssets()) return;
        using var rig = new Rig(null, "--level=" + Clones, "--solution", "--speed=10");
        var s = LoadedRig(rig);
        Check.True(s.Game.WatchingSolution, "the stored solution is the replay");
        Check.True(rig.App.Windows.Status.Badge.Panel.Visible, "REPLAY over the strip");
        PlayToTheEnd(rig);
        Check.Equal("SOLUTION COMPLETE", rig.App.Windows.Status.Status.Note, "the strip");
        Check.True(rig.App.Progress.Best(Clones) == null, "a watched win records no clear");
        rig.Frame(ShellApp.END_ADVANCE_MS + 10);
        Check.Equal(rig.App.Tree.Next(Clones, 1), rig.App.LevelId, "3 s later the next level");
        Check.True(rig.App.Session != null && rig.App.Session != s, "loaded");
    }

    [AppTest]
    public static void APlayedWinRecordsProgressAndAStepBackCancelsTheMove()
    {
        if (!HaveAssets()) return;
        using var rig = new Rig(null, "--level=" + Clones, "--nxrp=" + ClonesReplay, "--speed=10");
        var s = LoadedRig(rig);
        Check.True(!s.Game.WatchingSolution, "a replay file is not a watched solution");
        PlayToTheEnd(rig);
        Check.True(rig.App.Progress.Best(Clones) != null, "the clear is recorded");
        Check.True(rig.App.Windows.Status.Status.Note.StartsWith("COMPLETE", StringComparison.Ordinal), "the strip: " + rig.App.Windows.Status.Status.Note);
        Check.Equal("won", rig.App.Windows.Status.Status.Kind, "in green");
        // a step back makes the level playable again: no move
        rig.App.KeyDown("KeyB");
        Check.True(rig.App.EndAdvanceAt == null, "the step back cancels the move on");
        rig.Frame(ShellApp.END_ADVANCE_MS + 10);
        Check.Equal(Clones, rig.App.LevelId, "still the same level");
        Check.Equal(s, rig.App.Session, "the same game");
    }

    // ------------------------------------------------------------ the pages
    [AppTest]
    public static void NothingInstalledOpensTheSetupPage()
    {
        string empty = Path.Combine(Path.GetTempPath(), "lemmix-empty-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(empty);
        try
        {
            using var rig = new Rig(empty);
            Check.True(rig.App.FirstRun, "nothing installed");
            Check.True(rig.App.Pages.Current == rig.App.SetupPage, "the setup page is up");
            Check.True(!rig.App.Windows.Catalog.Root.Visible, "no catalog");
            // its close: still nothing to play, the setup again
            rig.Click(rig.App.SetupPage.Close);
            Check.True(rig.App.Pages.Current == rig.App.SetupPage, "setup is all there is");
        }
        finally { try { Directory.Delete(empty, true); } catch (Exception) { } }
    }

    [AppTest]
    public static void TheCatalogOpensSearchSetupAndSolutions()
    {
        if (!HaveAssets()) return;
        using var rig = new Rig();
        var app = rig.App;
        Node3D Entry(string name) => app.Windows.Catalog.Root.GetNode<Node3D>("vr-" + name);
        rig.Click(Entry("catsetup"));
        Check.True(app.Pages.Current == app.SetupPage, "the catalog's setup entry");
        Check.True(!app.Windows.Catalog.Root.Visible, "the catalog stepped aside");
        rig.Click(app.SetupPage.Close);
        Check.True(app.Pages.Current == null, "closed");
        rig.Frame();
        Check.True(app.Windows.Catalog.Root.Visible, "the catalog came back");
        rig.Click(Entry("catsolutions"));
        Check.True(app.Pages.Current == app.SolutionsPage, "the solutions list");
        rig.Click(app.SolutionsPage.Close);
        rig.Frame();
        rig.Click(Entry("catsearch"));
        Check.True(app.Pages.Keyboard.Root.Visible, "the search's keyboard");
        app.KeyDown("KeyB", text: "b");
        app.KeyDown("KeyU", text: "u");
        app.KeyDown("KeyI", text: "i");
        app.KeyDown("KeyL", text: "l");
        app.KeyDown("KeyD", text: "d");
        Check.Equal("build", app.Search.Query, "typed on a keyboard");
        Check.True(app.Search.Matches.Count > 0, "levels match");
        app.KeyDown("Enter");
        Check.True(app.Session != null, "Enter plays the first match");
        Check.True(!app.Pages.AnyUp && !app.Windows.Catalog.Root.Visible, "the keyboard and the catalog went");
    }

    [AppTest]
    public static void TheSettingsOpenControlsHintsAndTheChecklist()
    {
        if (!HaveAssets()) return;
        using var rig = new Rig(null, "--level=" + Builders);
        var s = LoadedRig(rig);
        var app = rig.App;
        Node3D Entry(string name) => app.Windows.Settings.Root.GetNode<Node3D>("vr-" + name);
        app.Windows.Bar.SetLocked(true); // the bar riding the head: a page parks it below the board
        foreach (var (name, page) in new (string, VrPage)[] { ("setcontrols", app.ControlsPage), ("sethints", app.HintsPage), ("setqa", app.QaPage) })
        {
            rig.Click(app.Windows.Toolbar.Settings);
            Check.True(app.Windows.Settings.Root.Visible, "the settings");
            rig.Click(Entry(name));
            Check.True(app.Pages.Current == page, name + " opens its page");
            Check.True(!s.Running, "a page holds the clock");
            Check.True(app.Windows.Bar.Parked, "the bar is parked while a page is up");
            app.Pages.Show(null);
            rig.Frame();
            Check.True(app.Windows.Settings.Root.Visible, "back to the settings");
            rig.Click(app.Windows.Settings.Close);
            Check.True(s.Running, "and the clock runs again");
        }
    }

    [AppTest]
    public static void ReplaysSaveAndLoadThroughTheirPage()
    {
        if (!HaveAssets()) return;
        using var rig = new Rig(null, "--level=" + Builders);
        LoadedRig(rig);
        var app = rig.App;
        app.KeyDown("KeyU"); // save_replay
        var files = Directory.Exists(app.ReplaysDir) ? Directory.GetFiles(app.ReplaysDir, "*.nxrp") : Array.Empty<string>();
        Check.Equal(1, files.Length, "a replay saved in <user data>/replays");
        Check.True(app.Windows.Status.Status.Note.StartsWith("saved ", StringComparison.Ordinal), "the strip says so");
        app.KeyDown("KeyL"); // load_replay
        Check.True(app.Pages.Current == app.ReplaysPage, "the replay files page");
        Check.Equal(1, app.ReplaysPage.Files.Count, "listing the file");
        app.ReplaysPage.Load(files[0]);
        Check.True(app.Session!.Game.Replaying || app.Session.Game.ReplayEngaged, "the replay plays");
        Check.True(app.Pages.Current == null, "the page went");
    }
}
