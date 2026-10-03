using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using Lemmix.App.Audio;
using Lemmix.App.Board;
using Lemmix.App.Session;
using Lemmix.App.Ui.Pages;
using Lemmix.App.Ui.Windows;
using Lemmix.App.Xr;
using Lemmix.Input;
using Lemmix.Io;
using Lemmix.Library;
using Lemmix.Render;
using Lemmix.Store;
using Lemmix.Ui;
using static Lemmix.App.Xr.VrManager;

namespace Lemmix.App.Shell;

// web/3d/js/app.js in a headset session, as a Godot node: everything the parts need wired
// together - the settings store, the asset root, the controls table, the library, the sound, the
// headset's rig and its input (VrManager), the diorama's root and the room around it, the
// windows, the skills bar hanging in the bar's root, the beams - and the frame loop that drives a
// level (animateBody). The level flow is App.Level.cs, the ray and the hands App.Input.cs, the
// keyboard and the controls table App.Hotkeys.cs.
public sealed partial class App : Node3D, IVrHooks, IVrWindowsHost, IVrPagesHost
{
    public const string StoreFile = "user://lem3d-store.json";
    public const string SolutionsRoot = "res://Data/";

    public readonly AppOptions Options;
    public IStorage Store { get; private set; } = null!;
    LocalStore? _localStore;
    public Preferences Prefs { get; private set; } = null!;
    public ShellEffects Fx { get; private set; } = null!;
    public string UserDataDir { get; private set; } = "";
    public string AssetRoot { get; private set; } = "";
    public string ReplaysDir => Path.Combine(UserDataDir, "replays");
    public DiskFileSource Io { get; private set; } = null!;
    public HotkeyManager Hotkeys { get; private set; } = null!;
    public HotkeyDispatch Dispatch { get; private set; } = null!;
    public LevelTree Tree { get; private set; } = new();
    public LibraryState Library { get; private set; } = null!;
    public LevelProgress Progress { get; private set; } = null!;
    public Solutions Solutions { get; } = new();
    public ShellLibrary Catalog { get; private set; } = null!;
    public GameAudio Audio { get; private set; } = null!;
    public WorldEnvironment World { get; private set; } = null!;
    public XROrigin3D? Origin { get; private set; }
    public ControllerModels? Controllers { get; private set; }
    public Foveation Foveation { get; private set; } = null!;

    // ---- the view's height (native, the VR window): the viewpoint raised or lowered against the
    // floor, which stays the headset's own - seated, the view comes down so the virtual floor stays
    // where the real one is (the headset reports a standing player's height in a chair)
    public const string ViewHeightKey = "lemmix-frame-view-height";
    public const float SeatedDrop = 0.45f;            // standing eyes (~1.65 m) to seated ones (~1.20 m)
    public float ViewHeight { get; private set; }

    /**
     * The view `metres` above (below, negative) where the headset puts it: the XR origin moves, and
     * the board, the windows and a floating bar move with it, so they keep their place before the
     * eyes while the floor and the room around stay put.
     */
    public void SetViewHeight(float metres, bool save = true)
    {
        float v = MathF.Round(Math.Clamp(metres, Lemmix.App.Ui.Windows.FloorControl.Min, Lemmix.App.Ui.Windows.FloorControl.Max) * 100) / 100;
        float delta = v - ViewHeight;
        ViewHeight = v;
        if (save) Store.SetItem(ViewHeightKey, v.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (Origin != null) Origin.Position = new Vector3(Origin.Position.X, v, Origin.Position.Z);
        if (delta == 0) return;
        var up = new Vector3(0, delta, 0);
        DioramaRoot.Position += up;
        Windows.WindowRoot.Position += up;
        Pages.Root.Position += up;
        if (!Windows.Bar.Locked) Windows.Toolbar.GuiRoot.Position += up;
        if (Session != null) Env.PlaceForXR(DioramaRoot.Transform, HeadPose?.Origin);
    }

    Lemmix.App.Ui.Windows.FloorControl FloorControl() => new()
    {
        Get = () => ViewHeight,
        Set = v => SetViewHeight(v),
        Seated = () => SetViewHeight(-SeatedDrop),
        Standing = () => SetViewHeight(0),
        Reset = () => SetViewHeight(0),
    };

    // the VR window's rows (native): foveated rendering on or off, and its strength
    List<SettingRow> VrRows() => new()
    {
        new("foveated rendering", () => Foveation.On, Foveation.Toggle),
        new("strength", () => Foveation.On, Foveation.CycleLevel, () => Foveation.LevelName.ToUpperInvariant()),
    };
    public IXrInput Input { get; private set; } = null!;
    public Node3D Head { get; private set; } = null!;
    public VrManager Vr { get; private set; } = null!;
    public Node3D DioramaRoot { get; private set; } = null!;
    public EnvironmentView Env { get; private set; } = null!;
    public VrWindows Windows { get; private set; } = null!;
    public PointerView Pointers { get; private set; } = null!;
    public VrPages Pages { get; private set; } = null!;
    public CursorImages? Cursor { get; private set; }
    public Func<double> Now { get; private set; } = null!;

    // the level on the board (session.*): its game and board, the skills bar
    public GameSession? Session { get; private set; }
    public SkillBar? Bar { get; private set; }
    public SkillBarView? BarView { get; private set; }

    // state.*
    public string? LevelId { get; private set; }
    public double Speed { get; private set; } = 1;
    public bool Locked { get; private set; }          // library.locked: no level chosen yet
    public bool FirstRun { get; private set; }        // nothing installed: the setup page first
    bool _pendingSolution;                            // state.solution
    string? _pendingNxrp;                             // state.nxrp
    double _yawCorrection;                            // vrYawCorrection

    bool _built, _wasPresenting, _scriptedHead;
    double _last = double.NaN;

    public App() : this(new AppOptions()) { }
    public App(AppOptions options) { Options = options; Name = "App"; }

    public override void _Ready()
    {
        if (_built) return;
        _built = true;
        // play is frame-paced: the collector's full collections run in the background rather than
        // blocking a frame (concurrent GC is on; a level's load collects in full, CollectAfterLoad)
        // (a level's no-GC region may be under way - an earlier shell in this process: it ends itself)
        if (System.Runtime.GCSettings.LatencyMode != System.Runtime.GCLatencyMode.NoGCRegion)
            System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;
        Now = Options.Clock ?? (() => Time.GetTicksUsec() / 1000.0);
        var args = Options.Args;

        // the store, the switches, where the files are
        UserDataDir = Options.UserDataDir ?? OS.GetUserDataDir();
        Store = Options.Store ?? (_localStore = new LocalStore(ProjectSettings.GlobalizePath(StoreFile)));
        Prefs = new Preferences(Store, args.Params);
        Fx = new ShellEffects(this, Prefs);
        AssetRoot = Options.ResolveAssetRoot(UserDataDir);
        try { Directory.CreateDirectory(AssetRoot); } catch (IOException) { }
        Io = new DiskFileSource(AssetRoot);
        Hotkeys = new HotkeyManager(Store);
        Dispatch = new HotkeyDispatch(Hotkeys);

        // the library: the tree, the place in it, the clears, the solutions
        LoadTree();
        Library = new LibraryState(Store, Tree);
        Progress = new LevelProgress(Store, Tree);
        Solutions.Load(Godot.FileAccess.FileExists(SolutionsRoot + "solutions/index.json")
            ? Godot.FileAccess.GetFileAsString(SolutionsRoot + "solutions/index.json") : null);
        FirstRun = !Installed();

        // sound
        Audio = new GameAudio { Name = "audio" };
        AddChild(Audio);
        Audio.Configure(Io, Prefs.Sound, (float)Prefs.Volume);
        Audio.SpatialActive = () => Vr != null && Vr.Presenting;

        // the scene's background and tone (the room's fog colour past its last ring)
        World = new WorldEnvironment
        {
            Name = "world",
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = BasicShader.Rgb(EnvironmentView.ENV_SCENE_COLOR),
                TonemapMode = Godot.Environment.ToneMapper.Linear,
            },
        };
        AddChild(World);

        BuildRig();

        // the diorama's root (the board goes under it, VR placement scales it) and the room, its sibling
        DioramaRoot = new Node3D { Name = "dioramaRoot" };
        AddChild(DioramaRoot);
        EnvironmentView.SceneryEnabled = Options.Args.Scenery;
        Env = new EnvironmentView { Name = "environment", SceneEnvironment = World.Environment, BuildInBackground = Options.EnvironmentInBackground };
        AddChild(Env);
        Env.SetMode(Fx.Environment);
        Vr = new VrManager(Input, this, DioramaRoot);

        // the windows: the bar's root rides the head until a board is placed; the windows' root is
        // the scene's; the status strip stands over the board in its pixels
        Foveation = new Foveation(Store);
        Windows = new VrWindows(this, VrSettings.Rows(Fx), Head, this, Store.GetItem("lem3d-bar"), VrRows(), FloorControl());
        Head.AddChild(Windows.Toolbar.GuiRoot);
        AddChild(Windows.WindowRoot);
        AddChild(Windows.Tooltip.Panel);
        DioramaRoot.AddChild(Windows.Status.Root);
        Catalog = new ShellLibrary(this);
        Windows.Library = Catalog;
        Windows.Now = () => Now();
        Windows.Bar.Frame = BarFrame;
        Windows.Bar.Store = json => Store.SetItem("lem3d-bar", json);
        Windows.Catalog.WantsThumb = Catalog.WantThumb;
        Windows.Tooltip.SkillTip = SkillTip;
        Windows.Toolbar.PaintSound(Audio.Volume, Audio.Enabled);

        // the beams, NeoLemmix's cursor at their landing, the floor grid
        try { Cursor = CursorImages.Load(Io); } catch (Exception e) { GD.PushWarning("[app] cursor: " + e.Message); }
        Pointers = new PointerView(Cursor);
        if (Controllers != null) Pointers.HandModelShown = Controllers.Shown;
        AddChild(Pointers);

        BuildPages();
        ConnectOpenXr();
        StartUploadServerIfOn();
        Foveation.Apply();
        SetViewHeight(float.TryParse(Store.GetItem(ViewHeightKey), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float view) ? view : 0, save: false);

        // the level asked for (?level=), else the library, locked, until one is chosen
        Speed = args.Speed;
        _pendingSolution = args.Solution;
        _pendingNxrp = args.Nxrp;
        GD.Print($"[app] assets {AssetRoot} ({(FirstRun ? "nothing installed" : Tree.ById.Count + " levels")}), store {(_localStore != null ? StoreFile : "memory")}");
        if (args.Level != null && !FirstRun)
        {
            LevelId = args.Level;
            LoadLevel();
        }
        else
        {
            Locked = true;
            Windows.Status.Set(name: "choose a level", meta: "", note: "", kind: "");
        }
        SetProcess(!Options.Manual);
        SetProcessInput(true);
    }

    // NeoLemmix's graphics (gfx/panel above all) and a levels index: what a level needs to load
    bool Installed() =>
        Tree.Root != null && Directory.Exists(Path.Combine(AssetRoot, "neolemmix", "gfx"));

    void LoadTree()
    {
        string? index = Io.Text("levels/index.json");
        if (index == null) return;
        try { Tree.Load(index, true); }
        catch (Exception e) { GD.PushError("[app] levels/index.json: " + e.Message); }
    }

    /** After an install (the setup page): the indexes read again, the catalog rebuilt. */
    public void ReloadLibrary()
    {
        Io = new DiskFileSource(AssetRoot); // its directory listings are cached
        Tree = new LevelTree();
        LoadTree();
        Library = new LibraryState(Store, Tree);
        Progress = new LevelProgress(Store, Tree);
        Catalog.Rebuild();
        RebuildLibraryPages();
        Audio.Configure(Io, Audio.Enabled, Audio.Volume);
        try { Cursor = CursorImages.Load(Io); } catch (Exception) { }
        FirstRun = !Installed();
        if (Windows.Catalog.Root.Visible) Windows.Catalog.Load(Catalog, true);
    }

    // ------------------------------------------------------------ the rig
    // OpenXR: the origin, the head, an aim and a grip controller per hand (the default action map's
    // poses); a test's or a shot's: its scripted input and a plain camera for the head
    void BuildRig()
    {
        if (Options.Input == null)
        {
            Origin = new XROrigin3D { Name = "XROrigin3D", Current = true };
            AddChild(Origin);
            var cam = new XRCamera3D { Name = "XRCamera3D", Near = 0.05f, Far = 300, Current = true };
            Origin.AddChild(cam);
            XRController3D Hand(string name, string tracker, string pose)
            {
                var c = new XRController3D { Name = name, Tracker = tracker, Pose = pose };
                Origin!.AddChild(c);
                return c;
            }
            var la = Hand("LeftAim", "left_hand", "aim_pose");
            var ra = Hand("RightAim", "right_hand", "aim_pose");
            var lg = Hand("LeftGrip", "left_hand", "grip_pose");
            var rg = Hand("RightGrip", "right_hand", "grip_pose");
            var lp = Hand("LeftPalm", "left_hand", "palm_pose");
            var rp = Hand("RightPalm", "right_hand", "palm_pose");
            Input = new OpenXrInput(la, ra, lg, rg, cam, lp, rp);
            Head = cam;
            AddChild(new DeviceCapture(cam)); // a picture over ssh (tools/frame-capture.sh)
            // no runtime (a desktop run): the head is that camera, standing where a player would
            if (!(XRServer.PrimaryInterface?.IsInitialized() ?? false))
            {
                var s = new ScriptedXrInput { PresentingValue = true };
                foreach (var h in s.HandsValue) h.Connected = false;
                Input = s;
                _scriptedHead = true;
            }
            else
            {
                // the runtime's own models of the controllers, with the app's sticker
                Controllers = new ControllerModels(Input);
                Origin.AddChild(Controllers);
            }
        }
        else
        {
            Input = Options.Input;
            var head = Options.Head ?? new Camera3D { Name = "head", Fov = 90, Near = 0.05f, Far = 300 };
            if (head.GetParent() == null) AddChild(head);
            head.Near = 0.05f;
            head.Far = 300;
            head.MakeCurrent();
            Head = head;
            _scriptedHead = true;
        }
    }

    // the runtime's own events: the system's menu over the game holds it; the runtime's recentre
    void ConnectOpenXr()
    {
        if (Options.Input != null || XRServer.FindInterface("OpenXR") is not OpenXRInterface xr) return;
        xr.SessionVisible += () => { GD.Print("[xr] session visible"); ReleaseHeldKeys(); HoldSim("xr-focus"); };
        xr.SessionFocussed += () => { GD.Print("[xr] session focused"); ReleaseSim("xr-focus"); };
        xr.SessionBegun += () => GD.Print("[xr] session begun");
        xr.SessionStopping += () => GD.Print("[xr] session stopping");
        xr.PoseRecentered += () => { if (Presenting) Vr.RecenterNow(); };
    }

    // ------------------------------------------------------------ the frame loop
    public override void _Process(double delta) => Frame(Now());

    /**
     * animateBody: the level's clock and scene, the bar, the badges and buttons that follow the
     * game, the windows' layout and labels, then the headset's input (grabs, sticks, the beam and
     * the hover), the beams drawn, the level's end moving on.
     */
    public void Frame(double now)
    {
        using var _ = Perf.Time(Perf.S.Shell);
        double dt = double.IsNaN(_last) ? 0 : Math.Max(0, now - _last);
        _last = now;
        DrainUploadEvents();
        if (_reload) { _reload = false; if (LevelId != null) LoadLevel(); }
        CollectAfterLoad();
        if (_scriptedHead && Input.Head is Transform3D hp && Head.IsInsideTree()) Head.GlobalTransform = hp;
        bool presenting = Vr.Presenting;
        if (presenting != _wasPresenting)
        {
            _wasPresenting = presenting;
            if (presenting) OnSessionStart(); else OnSessionEnd();
        }

        var s = Session;
        if (s != null)
        {
            s.Step(now);
            // (a jump's refresh under way: the bar goes on showing the frame the board shows)
            if (Bar != null && !s.RestorePending)
            {
                using var bar = Perf.Time(Perf.S.Bar);
                Bar.SetViewRect(VisibleLevelRect());
                Bar.Update();
                BarView!.Sync();
            }
            Windows.Status.Badge.Set(s.Game.Replaying, presenting);
            SyncSolutionButton(false);
            LayoutGuiPanel();
            // the pause icon tracks the clock however it was stopped
            if (presenting) Windows.Toolbar.Pause.SetState(on: !s.Running);
        }
        if (presenting)
        {
            Windows.Modal.Layout();
            Windows.Catalog.Layout();
            Windows.Settings.Layout();
            Windows.LevelText.Layout();
            Windows.Update();
            Pages.Update();
            PagesFrame();
            // a page up parks a bar riding the head below the board, as a window does
            Windows.Bar.SyncForWindows(Windows.AnyWindowUp || Pages.AnyUp, HasSession);
        }
        else if (Windows.AnyWindowUp) CloseWindows();
        if (presenting)
        {
            Vr.Update(dt / 1000);
            // a window opened before the first pose was placed on a guess: on the real one now
            if (!Windows.WindowsPlaced && Vr.LastHeadPose is Transform3D head) Windows.PlaceWindows(FrontOf(head));
        }
        var cur = Session;
        Pointers.Update(Vr, Input, cur?.Hovered != null, cur?.Game.Sim.EffectiveSelectDx ?? 0, DioramaRoot.Scale.X);
        Pointers.SetFloor(presenting, Env.Active && Env.Shown);
        if (Catalog.PollThumbs() && Windows.Catalog.Root.Visible) Windows.Catalog.Paint();
        CheckLevelEnd(now);
    }

    // sessionstart: the bar on the head until the board is placed, then below it; the windows
    // placed on the first pose; no level chosen yet, the catalog (nothing installed: the setup)
    void OnSessionStart()
    {
        Windows.Bar.AutoPlace = true;
        LayoutGuiPanel();
        Windows.WindowsPlaced = false;
        if (FirstRun) OpenSetup();
        else if (LevelId == null) Windows.SetCatalog(true);
    }

    // sessionend: the in-scene windows are the headset's
    void OnSessionEnd()
    {
        CloseWindows();
        Windows.Tooltip.NoteHover(null, Now());
        Env.PlaceDesktop();
    }

    void CloseWindows()
    {
        Windows.SetModal(false);
        Windows.SetCatalog(false);
        Windows.SetSettings(false);
        Windows.SetVrOptions(false);
        Windows.SetDetail(false);
        Pages.CloseKeyboard();
        Pages.Show(null);
    }

    // ------------------------------------------------------------ the bar's place
    float FocusX => Session != null ? Session.Level.Width / 2f : 0; // levelFocusX
    float PanelWidthScale() => Bar?.Mesh != null ? Bar.CanvasWidth / 320f : 1;

    /** layoutGuiPanel, the headset's branch: the bar 0.6 m (x the panel's width) on its root, the
     *  row of controls over it, the sound column off its end, the status strip over the board. */
    public void LayoutGuiPanel()
    {
        if (Session == null || Bar == null) return;
        if (Presenting)
        {
            Bar.SetRelief(Fx.SkillBar);
            Bar.SetReliefDepth(GUI_VR_RELIEF_DEPTH);
            float guiW = VR_GUI_WIDTH * PanelWidthScale();
            Bar.Place(guiW, VR_GUI_Y, VR_GUI_Z);
            // the panel has no mesh until it has painted once
            if (Bar.Mesh == null) return;
            Windows.Layout(guiW, (float)Bar.Mesh.Sy, FocusX, Session.Level.Height);
        }
        else
        {
            Windows.Layout(0, 0, 0, 0);
            Bar.SetRelief(false);
            Bar.SetReliefDepth(1);
        }
    }

    // barDefaultPlacement's inputs
    VrWindowPlacement.BarFrame? BarFrame()
    {
        if (Session == null || Bar == null) return null;
        float guiW = VR_GUI_WIDTH * PanelWidthScale();
        float barH = VrWindowPlacement.BarFrame.BarHeight(guiW, Bar.CanvasWidth, Bar.CanvasHeight);
        return new VrWindowPlacement.BarFrame(DioramaRoot.Transform, DioramaRoot.Rotation.Y, FocusX, guiW, barH);
    }

    // the skills bar's hover label, hung over the raised button
    Node3D? _hoverTile;
    (string Text, double Since, Node3D Tile)? SkillTip()
    {
        if (Bar == null || _hoverTile == null || Bar.HoverTip() is not { } t) return null;
        return (t.Text, t.Since, _hoverTile);
    }

    // ------------------------------------------------------------ placing the board
    Transform3D HeadNow() => Vr.LastHeadPose ?? Input.Head ?? Head.GlobalTransform;

    /**
     * placeDioramaForXR: the board 0.9 m ahead of the head's current pose, its level face turned
     * back toward the player, at 2.5 mm a pixel, its focus just below eye level; the bar goes with
     * it unless it rides the head; the room takes this placement.
     */
    public bool PlaceDiorama(Transform3D? headPose)
    {
        if (Session == null) return false;
        var head = headPose ?? HeadNow();
        var headPos = head.Origin;
        // along the play space's default forward, as the windows are (DefaultForward): the board
        // and the windows face the same way and stand in front of the player whatever way the head
        // was turned when the level loaded (a native departure: the web places it on the gaze)
        var fwd = DefaultForward();
        float yaw = MathF.Atan2(-fwd.X, -fwd.Z);
        var basis = new Basis(Vector3.Up, yaw) * Basis.FromScale(Vector3.One * VR_PIXEL_SCALE);
        var focusLocal = new Vector3(FocusX, Session.Level.Height / 2f, (float)BoardZ.TERRAIN_DEPTH / 2);
        var target = headPos + fwd * BoardAhead;
        target.Y = Math.Max(0.7f, headPos.Y - BoardBelowEye); // just below eye level
        DioramaRoot.Transform = new Transform3D(basis, target - basis * focusLocal);
        Windows.Bar.OnDioramaPlaced();
        Env.PlaceForXR(DioramaRoot.Transform, headPos);
        return true;
    }

    public void OnRecenter(Transform3D? headPose)
    {
        Windows.PlaceWindows(FrontOf(headPose));
        if (Pages.AnyUp) Pages.PlaceWindows(FrontOf(headPose));
    }

    /** The settings' "recentre the board", reset_view, recenter_vr. */
    public void Recenter() { if (Presenting) Vr.RecenterNow(); }

    // ------------------------------------------------------------ moving the board
    Vector3 DioramaFocusWorld() =>
        DioramaRoot.Transform * new Vector3(FocusX, (Session?.Level.Height ?? 0) / 2f, (float)BoardZ.TERRAIN_DEPTH / 2);

    void RotateDioramaAroundPivot(Quaternion q, Vector3 pivot)
    {
        var t = DioramaRoot.Transform;
        DioramaRoot.Transform = new Transform3D(new Basis(q) * t.Basis, q * (t.Origin - pivot) + pivot);
    }

    /** panDioramaBy: the board slides across the view (the camera is the head: the other way). */
    void PanDioramaBy(float dx, float dy, double seconds)
    {
        var hb = HeadNow().Basis;
        float step = (float)(VR_STICK_PAN * seconds);
        DioramaRoot.Position -= hb.X.Normalized() * (dx * step) + hb.Y.Normalized() * (dy * step);
    }

    /** tiltDioramaBy: yaw about world up, pitch about the head's horizontal, about the focus. */
    void TiltDioramaBy(float dx, float dy, double seconds)
    {
        if (Session == null) return;
        float step = (float)(VR_STICK_TILT * seconds);
        TurnDiorama(dx * step, dy * step);
    }

    void TurnDiorama(float yaw, float pitch)
    {
        var pivot = DioramaFocusWorld();
        RotateDioramaAroundPivot(new Quaternion(Vector3.Up, yaw), pivot);
        var right = HeadNow().Basis.X;
        right.Y = 0;
        if (right.LengthSquared() > 1e-4f) RotateDioramaAroundPivot(new Quaternion(right.Normalized(), pitch), pivot);
    }

    /** scaleDioramaAbout: whatever is under the pivot stays under it. */
    void ScaleDioramaAbout(float next, Vector3 pivot)
    {
        float k = next / DioramaRoot.Scale.X;
        var t = DioramaRoot.Transform;
        DioramaRoot.Transform = new Transform3D(t.Basis.Orthonormalized() * Basis.FromScale(Vector3.One * next), (t.Origin - pivot) * k + pivot);
    }

    /**
     * dollyVr: the board slides along the line of sight, so the point being looked at comes closer
     * or recedes and stays dead centre. Not while a window is up, which owns the hands.
     */
    void DollyVr(int dir, double seconds)
    {
        if (Session == null || Windows.AnyWindowUp || Vr.LastHeadPose is not Transform3D head) return;
        var gaze = head.Basis * new Vector3(0, 0, -1);
        var normal = (DioramaRoot.Basis.Orthonormalized() * new Vector3(0, 0, 1)).Normalized();
        var focus = DioramaFocusWorld();
        var plane = new Plane(normal, focus);
        var point = plane.IntersectsRay(head.Origin, gaze) ?? focus;
        var line = point - head.Origin;
        float dist = line.Length();
        if (dist < 1e-4f) return;
        float next = Math.Clamp(dist / MathF.Pow(VR_ZOOM_RATE, (float)(dir * seconds)), VR_ZOOM_NEAR, VR_ZOOM_FAR);
        DioramaRoot.Position += line / dist * (next - dist);
    }

    /** zoomView in a headset: the board scaled about its focus by 1.15 a step. */
    void ZoomView(bool zoomIn)
    {
        if (Session == null || !Presenting) return;
        float cur = DioramaRoot.Scale.X;
        float next = Math.Clamp(cur * (zoomIn ? 1.15f : 1 / 1.15f), VR_PIXEL_SCALE * 0.15f, VR_PIXEL_SCALE * 8);
        ScaleDioramaAbout(next, DioramaFocusWorld());
    }

    // ------------------------------------------------------------ the view's rectangle (the minimap)
    // the head's frustum: a headset's field of view (both eyes' union, about 100 degrees), or the
    // camera's own off a headset
    void ViewTangents(out float tx, out float ty)
    {
        if (Origin != null && !_scriptedHead) { tx = ty = MathF.Tan(Mathf.DegToRad(50)); return; }
        float fov = Head is Camera3D c ? c.Fov : 90;
        ty = MathF.Tan(Mathf.DegToRad(fov / 2));
        var size = GetViewport()?.GetVisibleRect().Size ?? new Vector2(1, 1);
        tx = ty * (size.Y > 0 ? size.X / size.Y : 1);
    }

    // levelPointAt: where the ray through the view's (nx, ny) lands on the lemmings' plane, in level px
    (float X, float Y, bool Hit) LevelPointAt(Transform3D eye, Transform3D inv, float nx, float ny, float tx, float ty)
    {
        var dir = eye.Basis * new Vector3(nx * tx, ny * ty, -1);
        var o = inv * eye.Origin;
        var d = inv.Basis * dir;
        float z = (float)BoardZ.LEMMING_Z;
        float t = Math.Abs(d.Z) > 1e-9f ? (z - o.Z) / d.Z : -1;
        bool hit = t > 0;
        if (!hit) t = 1e6f; // the far point along the ray, projected on the plane
        return (o.X + d.X * t, o.Y + d.Y * t, hit);
    }

    /** visibleLevelRect: the level-space box the view covers, clamped; null when no corner reaches. */
    LevelRect? VisibleLevelRect()
    {
        if (Session == null) return null;
        float w = Session.Level.Width, h = Session.Level.Height;
        var eye = HeadNow();
        var inv = BoardMaterials.WorldOf(Session.Board).AffineInverse();
        ViewTangents(out float tx, out float ty);
        float x0 = float.MaxValue, x1 = float.MinValue, y0 = float.MaxValue, y1 = float.MinValue;
        bool any = false;
        for (int i = 0; i < 4; i++)
        {
            float nx = i == 0 || i == 3 ? -1 : 1, ny = i < 2 ? -1 : 1;
            var p = LevelPointAt(eye, inv, nx, ny, tx, ty);
            any |= p.Hit;
            x0 = Math.Min(x0, p.X); x1 = Math.Max(x1, p.X);
            y0 = Math.Min(y0, p.Y); y1 = Math.Max(y1, p.Y);
        }
        if (!any) return null;
        return new LevelRect(Math.Clamp(x0, 0, w), Math.Clamp(y0, 0, h), Math.Clamp(x1, 0, w), Math.Clamp(y1, 0, h));
    }

    /** centerViewOn, the headset's branch: the board moved so the level point is where the view's centre is. */
    void CenterViewOn(double simX, double simY)
    {
        if (Session == null) return;
        float width = Session.Level.Width, height = Session.Level.Height;
        var r = VisibleLevelRect();
        float rw = r is { } a ? (float)(a.X1 - a.X0) : 0, rh = r is { } b ? (float)(b.Y1 - b.Y0) : 0;
        float cx = rw >= width ? width / 2 : Math.Clamp((float)simX, rw / 2, width - rw / 2);
        float cy = rh >= height ? height / 2 : Math.Clamp((float)simY, rh / 2, height - rh / 2);
        var eye = HeadNow();
        var world = BoardMaterials.WorldOf(Session.Board);
        ViewTangents(out float tx, out float ty);
        var c = LevelPointAt(eye, world.AffineInverse(), 0, 0, tx, ty);
        (float X, float Y) cur;
        if (c.Hit) cur = (c.X, c.Y);
        else if (r is { } rr) cur = ((float)(rr.X0 + rr.X1) / 2, (float)(rr.Y0 + rr.Y1) / 2);
        else return;
        float z = (float)BoardZ.LEMMING_Z;
        var delta = world * new Vector3(cx, cy, z) - world * new Vector3(cur.X, cur.Y, z);
        if (Presenting) DioramaRoot.Position -= delta;
    }

    // ------------------------------------------------------------ IVrWindowsHost
    /** The toolbar's quit, answered yes: the settings saved, the upload server stopped, the app ended. */
    public void QuitGame()
    {
        GD.Print("[app] quit from the toolbar");
        StopUploadServer();
        _localStore?.Flush();
        GetTree().Quit(0);
    }

    public bool Presenting => Vr != null && Vr.Presenting;
    public bool HasSession => Session != null;
    public Transform3D? HeadPose => Vr.LastHeadPose ?? Input.Head;

    // the board's place at a session's start: this far ahead, this far below the eyes
    public const float BoardAhead = 0.9f, BoardBelowEye = 0.15f;
    // the windows open as far below the line of sight as the board's centre is (about 9.5 degrees)
    public static readonly float WindowPitch = MathF.Atan2(BoardBelowEye, BoardAhead);

    /**
     * Where the windows and pages open (the questions, the world library, the settings, the
     * setup...): at the head, facing the play space's default forward - its -Z, turned by the yaw
     * correction as the board is - rather than wherever the head looks, and looking down by
     * WindowPitch, so they stand at the board's eye level (upright still: the placement keeps the
     * yaw only). A native departure from the web, which opens them on the gaze (device session 1).
     */
    public Transform3D? WindowPose => FrontOf(HeadPose);

    Transform3D? FrontOf(Transform3D? head)
    {
        if (head is not Transform3D h) return null;
        var fwd = DefaultForward();
        return new Transform3D(new Basis(Vector3.Up, MathF.Atan2(-fwd.X, -fwd.Z)) * new Basis(Vector3.Right, -WindowPitch), h.Origin);
    }

    /** The play space's default forward on the floor: the origin's -Z, turned by the yaw correction. */
    Vector3 DefaultForward()
    {
        var fwd = (Origin?.GlobalBasis ?? Basis.Identity) * Vector3.Forward;
        fwd.Y = 0;
        fwd = fwd.LengthSquared() < 1e-6f ? Vector3.Forward : fwd.Normalized();
        return fwd.Rotated(Vector3.Up, (float)_yawCorrection);
    }
    public bool GameRunning => Session?.Running ?? false;
    public bool AudioEnabled => Audio.Enabled;
    public float Volume => Audio.Volume;

    public void SetVolume(float volume)
    {
        Audio.SetVolume(volume);
        Prefs.SetVolume(volume);
    }

    // the speaker: the sound on or off, kept; the music back with it
    public void ToggleMute()
    {
        Audio.SetEnabled(!Audio.Enabled);
        Prefs.Sound = Audio.Enabled;
        if (Audio.Enabled) Session?.PlayMusic();
    }

    public void TogglePause() => Session?.TogglePause();

    // ------------------------------------------------------------ holding the sim
    // holdSim / releaseSim: anything the player has to deal with holds the clock; the session
    // keeps whether it ran, the shell keeps who holds, so a level arriving behind a window starts held
    readonly HashSet<string> _holders = new(StringComparer.Ordinal);
    public IReadOnlyCollection<string> Holders => _holders;

    public void HoldSim(string who)
    {
        if (Session == null || !_holders.Add(who)) return;
        Session.Hold(who);
    }

    public void ReleaseSim(string who)
    {
        if (!_holders.Remove(who)) return;
        Session?.Release(who);
    }

    // ------------------------------------------------------------ lifetime
    public override void _Notification(int what)
    {
        if (what == NotificationApplicationFocusOut || what == NotificationWMWindowFocusOut) { if (_built) ReleaseHeldKeys(); }
        else if (what == NotificationWMCloseRequest || what == NotificationApplicationPaused) _localStore?.Flush();
    }

    public override void _ExitTree()
    {
        if (!_built) return;
        DisposeSession();
        StopUploadServer();
        _localStore?.Flush();
    }
}
