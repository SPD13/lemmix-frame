using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Lemmix.App.Ui;
using Lemmix.App.Ui.Windows;
using Lemmix.App.Xr;
using static Lemmix.App.Xr.VrManager;

namespace Lemmix.App.Test;

// The headset's windows (app/Ui/Windows) against the web's: every paint call for call (the
// fixture's traces), the layout in metres, the hit-testing, and the flows of app.js.
public static class WindowsTests
{
    // ---- paint: the same calls as the web's, state by state
    [AppTest]
    public static void EveryWindowPaintsTheWebsCalls()
    {
        var failures = new List<string>();
        var accepted = new List<string>();
        int compared = 0;
        foreach (var name in WindowFixture.Names)
        {
            var web = WindowFixture.WebTrace(name);
            var made = WindowFixture.Make(name);
            try
            {
                if (web.Count == 0) continue; // painted once at creation (the REPLAY plate): picture only
                compared++;
                var ok = new List<string>();
                var trace = made.Trace;
                // a native departure: a question too wide for its frame is set smaller (VrModal.FitFont) -
                // a second font call straight after the web's own
                if (name.StartsWith("modal-", StringComparison.Ordinal))
                {
                    var kept = new List<string>();
                    foreach (var t in trace)
                    {
                        if (t.StartsWith("font=", StringComparison.Ordinal) && kept.Count > 0 && kept[^1].StartsWith("font=", StringComparison.Ordinal))
                        { ok.Add("native: " + t + " (fitted to the frame)"); continue; }
                        kept.Add(t);
                    }
                    trace = kept;
                }
                var diffs = WindowFixture.Compare(web, trace, fontSlack: name.StartsWith("tip-"), accepted: ok);
                if (ok.Count > 0) accepted.Add(name + ": " + string.Join(" ; ", ok));
                if (diffs.Count > 0) failures.Add(name + " (" + diffs.Count + " differ) " + string.Join(" ; ", diffs.Take(3)));
            }
            finally { made.Owner.Free(); }
        }
        Check.True(compared > 70, "states compared: " + compared);
        GD.Print($"[windows] {compared} states painted call for call; font-metric differences accepted in {accepted.Count}:");
        foreach (var a in accepted) GD.Print("  " + a);
        if (failures.Count > 0) throw new Exception(failures.Count + " states differ: " + string.Join("\n  ", failures));
    }

    // ---- helpers
    static System.Text.Json.Nodes.JsonObject L => WindowFixture.Layout;
    static Vector3 V3(System.Text.Json.Nodes.JsonNode n) { var a = n.AsArray(); return new Vector3(a[0]!.GetValue<float>(), a[1]!.GetValue<float>(), a[2]!.GetValue<float>()); }
    static Quaternion Q(System.Text.Json.Nodes.JsonNode n) { var a = n.AsArray(); return new Quaternion(a[0]!.GetValue<float>(), a[1]!.GetValue<float>(), a[2]!.GetValue<float>(), a[3]!.GetValue<float>()); }
    static (Vector3 Pos, Vector3 Scale, Quaternion Quat) Pose(string button)
    {
        var b = L["buttons"]![button]!;
        return (V3(b["pos"]!), V3(b["scale"]!), Q(b["quat"]!));
    }
    static void Near(float e, float a, string what, float eps = 2e-5f) { if (Math.Abs(e - a) > eps) throw new Exception($"{what}: expected {e}, got {a}"); }
    static void NearQ(Quaternion e, Quaternion a, string what, float eps = 2e-5f)
    {
        if (Math.Abs(e.Dot(a)) < 1 - eps) throw new Exception($"{what}: expected {e}, got {a}");
    }
    static T InTree<T>(T node) where T : Node { ((SceneTree)Godot.Engine.GetMainLoop()).Root.AddChild(node); return node; }
    // the web's guiRoot in the fixture: the Lemmix panel, 416 x 40 canvas pixels
    static float GuiW => VR_GUI_WIDTH * L["gui"]!["canvasW"]!.GetValue<int>() / 320f;
    static float BarH => VrWindowPlacement.BarFrame.BarHeight(GuiW, L["gui"]!["canvasW"]!.GetValue<int>(), L["gui"]!["canvasH"]!.GetValue<int>());

    // a ray straight at a node's centre, from in front of it
    static (Vector3, Vector3) At(Node3D n) => (n.GlobalPosition + new Vector3(0, 0, 0.5f), new Vector3(0, 0, -1));
    static (Vector3, Vector3) AtPixel(Panel3D p, float x, float y)
    {
        var local = new Vector3((x / p.Canvas.Width - 0.5f) * p.WidthMetres, (0.5f - y / p.Canvas.Height) * p.HeightMetres, 0);
        var world = p.GlobalTransform * local;
        return (world + p.GlobalBasis.Z.Normalized() * 0.5f, -p.GlobalBasis.Z.Normalized());
    }

    sealed class Host : IVrWindowsHost
    {
        public bool Presenting { get; set; } = true;
        public bool HasSession { get; set; } = true;
        public Transform3D? HeadPose { get; set; } = Transform3D.Identity;
        public readonly HashSet<string> Held = new();
        public readonly List<string> Log = new();
        public void HoldSim(string who) => Held.Add(who);
        public void ReleaseSim(string who) => Held.Remove(who);
        public bool GameRunning { get; set; } = true;
        public bool AudioEnabled { get; set; } = true;
        public float Volume { get; set; } = 1;
        public void SetVolume(float v) { Volume = v; Log.Add("volume " + v.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)); }
        public void ToggleMute() { AudioEnabled = !AudioEnabled; Log.Add("mute"); }
        public void TogglePause() { GameRunning = !GameRunning; Log.Add("pause"); }
        public void MoveLevel(int d) => Log.Add("move " + d);
        public bool CanWatchSolution { get; set; }
        public void WatchSolution() => Log.Add("solution");
        public void EnterLevel(string id) => Log.Add("enter " + id);
        public void QuitGame() => Log.Add("quit");
        public void ExitToLobby() => Log.Add("lobby");
    }

    sealed class Fx : IVrEffects
    {
        public bool Emboss { get; set; } = true;
        public bool Doors => true; public bool Smooth => false; public bool SmoothTerrain => true;
        public string ColorBlend { get; set; } = "soft";
        public bool SkillBar => true; public bool FlatSkills => false;
        public string Environment => "none";
        public readonly List<string> Log = new();
        public void ToggleEmboss() { Emboss = !Emboss; Log.Add("emboss"); }
        public void ToggleDoors() => Log.Add("doors"); public void ToggleSmooth() => Log.Add("smooth");
        public void ToggleSmoothTerrain() => Log.Add("smoothTerrain");
        public void ToggleColorBlend() { ColorBlend = ColorBlend == "off" ? "soft" : ColorBlend == "soft" ? "smooth" : "off"; Log.Add("colorBlend"); }
        public void ToggleSkillBar() => Log.Add("skillBar"); public void ToggleFlatSkills() => Log.Add("flatSkills");
        public void ToggleEnvironment() => Log.Add("environment"); public void Recenter() => Log.Add("recenter");
    }

    // a small tree: levels > Pack A (lemmix) > Easy (6 levels), Hard (3, all cleared); Classic > Fun (2)
    sealed class Lib : ICatalogLibrary
    {
        public CatalogNode Root { get; } = new() { Name = "levels", Path = "" };
        CatalogNode _cur;
        public bool Locked { get; set; }
        public string? CurrentLevelId { get; set; }
        public List<string> RecentIds = new(), FavIds = new();
        public readonly List<string> Log = new();
        readonly Dictionary<string, CatalogNode> _byPath = new(), _byLevel = new();
        public Lib()
        {
            CatalogNode Dir(CatalogNode parent, string name, string engine, int levels)
            {
                var n = new CatalogNode { Name = name, Path = (parent.Path == "" ? "" : parent.Path + "/") + name, Engine = engine, Parent = parent };
                for (int i = 1; i <= levels; i++) { string id = n.Path + "/l" + i; n.Levels.Add(id); _byLevel[id] = n; }
                parent.Children.Add(n);
                _byPath[n.Path] = n;
                return n;
            }
            var a = Dir(Root, "Pack A", "lemmix", 0);
            Dir(a, "Easy", "lemmix", 6); Dir(a, "Hard", "lemmix", 3);
            var c = Dir(Root, "Classic", "classic", 0);
            Dir(c, "Fun", "classic", 2);
            foreach (var n in _byPath.Values) n.Count = Count(n);
            Root.Count = Count(Root);
            _byPath[""] = Root;
            _cur = Root;
            static int Count(CatalogNode n) { int k = n.Levels.Count; foreach (var ch in n.Children) k += Count(ch); return k; }
        }
        public CatalogNode? CurrentNode() => _cur;
        public void Navigate(string path) { _cur = _byPath[path]; Log.Add("nav " + path); }
        public void Up() { if (_cur.Parent != null) _cur = _cur.Parent; Log.Add("up"); }
        public CatalogNode? NodeOf(string id) => _byLevel.GetValueOrDefault(id);
        public bool CanLoad(string? engine) => engine == "lemmix";
        public string LevelName(string id) => "Level " + id[(id.LastIndexOf('l') + 1)..];
        public string WorldOf(string id) => "orig_dirt";
        public double? Best(string id) => id.Contains("Hard") ? 61 : null;
        public int ClearedUnder(CatalogNode n) => n.Path.Contains("Hard") ? 3 : n.Path == "Pack A" ? 3 : 0;
        public bool HasSolution(string id) => id.EndsWith("l1");
        public bool IsFavorite(string id) => FavIds.Contains(id);
        public IReadOnlyList<string> Recent() => RecentIds;
        public IReadOnlyList<string> Favorites() => FavIds;
        public void EnsureNames(CatalogNode n) => Log.Add("scan " + n.Path);
    }

    static VrWindows Make(Host host, out Node3D scene, Fx? fx = null)
    {
        scene = InTree(new Node3D { Name = "test-scene" });
        var head = new Node3D { Name = "head" };
        scene.AddChild(head);
        var w = new VrWindows(host, VrSettings.Rows(fx ?? new Fx()), head, scene);
        head.AddChild(w.Toolbar.GuiRoot);
        scene.AddChild(w.WindowRoot);
        scene.AddChild(w.Status.Root);
        scene.AddChild(w.Tooltip.Panel);
        w.PlaceWindows(Transform3D.Identity);
        w.Layout(GuiW, BarH, 260, 160);
        return w;
    }

    // ---- layout: the web's numbers (the fixture's meshes, laid out in a headset)
    [AppTest]
    public static void ToolbarLayoutIsTheWebs()
    {
        Near(VR_GUI_WIDTH, Pose("status").Scale.X * VR_PIXEL_SCALE, "the strip is as wide as the DOS bar at the board's default scale", 1e-4f);
        Near(L["gui"]!["mesh"]!["scale"]![0]!.GetValue<float>(), GuiW, "the Lemmix bar's width");
        Near(L["gui"]!["mesh"]!["scale"]![1]!.GetValue<float>(), BarH, "bar height");
        var bar = new VrToolbar();
        try
        {
            string[] left = { "lock", "move", "park", "settings" }, right = { "worlds", "prev", "restart", "solution", "next" };
            for (int i = 0; i < left.Length; i++)
            {
                var p = VrToolbar.ButtonPlace(VrToolbar.RowX(i, 4, 5, GuiW, true, false), BarH, false);
                Check.Near(Pose(left[i]).Pos, p.Pos, left[i], 2e-5f);
                Near(Pose(left[i]).Scale.X, p.Scale.X, left[i] + " size");
            }
            for (int i = 0; i < right.Length; i++)
                Check.Near(Pose(right[i]).Pos, VrToolbar.ButtonPlace(VrToolbar.RowX(i, 4, 5, GuiW, false, false), BarH, false).Pos, right[i], 2e-5f);
            Check.Near(Pose("pause").Pos, VrToolbar.ButtonPlace(0, BarH, false).Pos, "pause", 2e-5f);
            Check.Near(Pose("mute").Pos, VrToolbar.MutePlace(GuiW, BarH, false).Pos, "mute", 2e-5f);
            var vol = VrToolbar.VolumePlace(GuiW, BarH, false);
            Check.Near(Pose("volume").Pos, vol.Pos, "volume", 2e-5f);
            Near(Pose("volume").Scale.X, vol.Scale.X, "volume width"); Near(Pose("volume").Scale.Y, vol.Scale.Y, "volume height");
            // hovered: grown and stepped forward; the slider over a hovered mute
            var ph = L["pauseHovered"]!;
            var hp = VrToolbar.ButtonPlace(0, BarH, true);
            Check.Near(V3(ph["pos"]!), hp.Pos, "hovered pause", 2e-5f);
            Near(V3(ph["scale"]!).X, hp.Scale.X, "hovered pause size");
            Check.Near(V3(L["muteHovered"]!["pos"]!), VrToolbar.MutePlace(GuiW, BarH, true).Pos, "hovered mute", 2e-5f);
            Check.Near(V3(L["volumeLingering"]!["pos"]!), VrToolbar.VolumePlace(GuiW, BarH, true).Pos, "slider over a hovered mute", 2e-5f);
            Check.True(L["volumeLingering"]!["visible"]!.GetValue<bool>() && !L["buttons"]!["volume"]!["visible"]!.GetValue<bool>(), "the slider shows only while summoned");
            // the nodes as laid out: the planes are the web's size
            bar.Pause.SetState(hovered: true);
            bar.Layout(GuiW, BarH, 0);
            Check.Near(hp.Pos, bar.Pause.Position, "node: hovered pause", 1e-6f);
            var size = Planes.SizeOf(bar.Volume);
            Near(vol.Scale.X, size.X, "node: slider width", 1e-6f); Near(vol.Scale.Y, size.Y, "node: slider height", 1e-6f);
            Check.True(!bar.Volume.Visible && bar.Lock.Visible && bar.Mute.Visible, "laid out: buttons shown, slider not summoned");
            bar.Hide();
            Check.True(!bar.Lock.Visible && !bar.Mute.Visible, "the desktop hides them");
        }
        finally { bar.GuiRoot.Free(); }
    }

    [AppTest]
    public static void WindowLayoutIsTheWebs()
    {
        var mp = VrModal.PanelPlacement();
        Check.Near(Pose("modalpanel").Pos, mp.Pos, "modal panel", 2e-5f);
        Near(Pose("modalpanel").Scale.X, mp.ScaleX, "modal w"); Near(Pose("modalpanel").Scale.Y, mp.ScaleY, "modal h");
        Check.Near(Pose("yes").Pos, VrModal.ButtonPlacement(-1, false).Pos, "yes", 2e-5f);
        Check.Near(Pose("no").Pos, VrModal.ButtonPlacement(1, false).Pos, "no", 2e-5f);
        Near(Pose("yes").Scale.X, VrModal.ButtonPlacement(-1, false).ScaleX, "answer size");
        var cp = VrCatalog.PanelPlacement();
        Check.Near(Pose("worldpanel").Pos, cp.Pos, "catalog", 2e-5f);
        Near(Pose("worldpanel").Scale.Y, cp.ScaleY, "catalog h");
        string[] tools = { "catclose", "catrecent", "catfav" };
        for (int i = 0; i < 3; i++) Check.Near(Pose(tools[i]).Pos, VrCatalog.ToolPlacement(i, false).Pos, tools[i], 2e-5f);
        var sp = VrSettings.PanelPlacement();
        Check.Near(Pose("setpanel").Pos, sp.Pos, "settings", 2e-5f);
        Near(Pose("setpanel").Scale.Y, sp.ScaleY, "settings h");
        Check.Near(Pose("setclose").Pos, VrSettings.ClosePlacement(false).Pos, "settings close", 2e-5f);
        Check.Near(Pose("detailpanel").Pos, VrLevelText.PanelPosition, "level text", 2e-5f);
        // the level text's height follows its lines: the web's window held the opening text
        var w = new VrLevelText();
        try
        {
            w.Paint(WindowFixture.Shots["leveltext-pretext"]!["lines"]!.AsArray().Select(x => x!.GetValue<string>()));
            Near(Pose("detailpanel").Scale.Y, Planes.SizeOf(w.Panel).Y, "level text h", 1e-4f);
            Near(Pose("detailpanel").Scale.X, Planes.SizeOf(w.Panel).X, "level text w", 1e-6f);
        }
        finally { w.Root.Free(); }
    }

    [AppTest]
    public static void StatusStripLayoutIsTheWebs()
    {
        float width = L["level"]!["width"]!.GetValue<float>(), height = L["level"]!["height"]!.GetValue<float>();
        var l = VrStatusStrip.Compute(width / 2, height, false);
        Check.Near(Pose("status").Pos, l.StripPos, "strip", 2e-3f);
        Near(Pose("status").Scale.X, l.StripScale.X, "strip w", 1e-3f); Near(Pose("status").Scale.Y, l.StripScale.Y, "strip h", 1e-3f);
        Check.Near(Pose("detail").Pos, l.DetailPos, "detail button", 2e-3f);
        Near(Pose("detail").Scale.X, l.DetailScale, "detail size", 1e-3f);
        Check.Near(Pose("replaybadge").Pos, l.BadgePos, "REPLAY plate", 2e-3f);
        Near(Pose("replaybadge").Scale.X, l.BadgeScale.X, "plate w", 1e-3f);
    }

    [AppTest]
    public static void TooltipSitsOverItsButtonAsTheWebs()
    {
        var tip = L["tip"]!;
        var a = tip["anchorWorld"]!.AsArray().Select(x => x!.GetValue<float>()).ToArray();
        var p = tip["parentWorld"]!.AsArray().Select(x => x!.GetValue<float>()).ToArray();
        // three's matrices are column-major
        Transform3D M(float[] e) => new(new Basis(new Vector3(e[0], e[1], e[2]), new Vector3(e[4], e[5], e[6]), new Vector3(e[8], e[9], e[10])), new Vector3(e[12], e[13], e[14]));
        var parentQ = M(p).Basis.GetRotationQuaternion();
        var scale = V3(tip["mesh"]!["scale"]!);
        var xf = VrTooltip.PlaceOver(M(a), parentQ, new Vector2(scale.X, scale.Y));
        Check.Near(V3(tip["mesh"]!["pos"]!), xf.Origin, "tooltip over pause", 1e-4f);
        NearQ(Q(tip["mesh"]!["quat"]!), xf.Basis.GetRotationQuaternion(), "tooltip in the bar's plane", 1e-5f);
        Check.Equal("pause", tip["text"]!.GetValue<string>(), "the web's tip over a running game");
    }

    [AppTest]
    public static void BarDefaultPlacementIsTheWebs()
    {
        var d = L["diorama"]!;
        float yaw = d["rotY"]!.GetValue<float>(), s = d["scale"]!.GetValue<float>();
        var diorama = new Transform3D(new Basis(Vector3.Up, yaw) * Basis.FromScale(Vector3.One * s), V3(d["pos"]!));
        var frame = new VrWindowPlacement.BarFrame(diorama, yaw, L["level"]!["width"]!.GetValue<float>() / 2, GuiW, BarH);
        var (pos, quat) = VrWindowPlacement.BarDefault(frame);
        Check.Near(V3(L["barDefault"]!["pos"]!), pos, "bar default position", 2e-5f);
        NearQ(Q(L["barDefault"]!["quat"]!), quat, "bar default facing");
    }

    [AppTest]
    public static void WindowsOpenCentredOnTheGazeUpright()
    {
        // a head turned 0.7 rad and looking 0.4 rad down: the window centre on the gaze, 0.75 m out
        var head = new Quaternion(Vector3.Up, 0.7f) * new Quaternion(Vector3.Right, -0.4f);
        var at = new Vector3(0.2f, 1.6f, -0.1f);
        var (pos, quat, yaw) = VrWindowPlacement.PlaceWindows(at, head, 0);
        Near(0.7f, yaw, "the gaze's yaw", 1e-5f);
        var centre = pos + quat * new Vector3(0, 0, VR_MODAL_Z);
        Check.Near(at + head * new Vector3(0, 0, -1) * 0.75f, centre, "centred on the gaze", 1e-5f);
        Check.Near(Vector3.Up, quat * Vector3.Up, "upright", 1e-6f);
        // straight down: no heading, the last yaw kept
        var (_, q2, yaw2) = VrWindowPlacement.PlaceWindows(at, new Quaternion(Vector3.Right, -Mathf.Pi / 2), 0.3f);
        Near(0.3f, yaw2, "a vertical gaze keeps the yaw");
        NearQ(new Quaternion(Vector3.Up, 0.3f), q2, "turned by it");
    }

    // ---- the catalog: layout, scrolling, picking
    [AppTest]
    public static void CatalogListLaysOutAsTheWebs()
    {
        foreach (var (name, node) in WindowFixture.F["catalog"]!.AsObject())
        {
            var c = node!.AsObject();
            var cat = new VrCatalog();
            try
            {
                cat.SetList(WindowFixture.CatalogItems(c), c["heading"]!.GetValue<string>(), c["note"]!.GetValue<string>());
                var cells = c["cells"]!.AsArray();
                Check.Equal(cells.Count, cat.Cells.Count, name + " cells");
                for (int i = 0; i < cells.Count; i++)
                {
                    var e = cells[i]!.AsArray().Select(x => x!.GetValue<float>()).ToArray();
                    var m = cat.Cells[i];
                    Check.True(e[0] == m.I && Math.Abs(e[1] - m.X) < 1e-3 && Math.Abs(e[2] - m.Y) < 1e-3 && Math.Abs(e[3] - m.W) < 1e-3 && Math.Abs(e[4] - m.H) < 1e-3,
                        $"{name} cell {i}: web [{string.Join(",", e)}], port [{m.I},{m.X},{m.Y},{m.W},{m.H}]");
                }
            }
            finally { cat.Root.Free(); }
        }
    }

    [AppTest]
    public static void CatalogScrollsAndPagesAsTheWeb()
    {
        var c = WindowFixture.F["catalog"]!["catalog-levels"]!.AsObject();
        var cat = new VrCatalog();
        try
        {
            cat.SetList(WindowFixture.CatalogItems(c), c["heading"]!.GetValue<string>(), "");
            // 48 band + 60 back row + 4 rows of 126 = 612 tall, 522 shown: 90 to travel
            Near(612, cat.ListHeight, "list height");
            var bar = cat.Bar();
            Near(90, bar.Max, "travel");
            Near(VrCatalog.VR_CAT_VIEW_H * VrCatalog.VR_CAT_VIEW_H / 612f, bar.Thumb, "thumb as tall as the share shown");
            Near(1024 - 26 - 16, bar.X, "bar x");
            // the thumb centres on the press
            Near(0, cat.ScrollFor(bar.Y + bar.Thumb / 2), "press at the thumb's top centre");
            Near(90, cat.ScrollFor(bar.Y + bar.H - bar.Thumb / 2), "press at its bottom centre");
            Check.True(!cat.ScrollTo(-50), "clamped at the top: nothing moves");
            Check.True(cat.ScrollTo(1000) && cat.Scroll == 90, "clamped at the end");
            Check.True(!cat.ScrollTo(95), "past the end: nothing moves");
            // the stick (y as vr.js reports it, up positive): 900 px/s at full deflection
            cat.ScrollTo(0);
            cat.OnStick(-1, 0.05);
            Near(45, cat.Scroll, "stick down 50 ms: 45 px down");
            cat.OnStick(1, 0.02);
            Near(27, cat.Scroll, "stick up 20 ms: 18 px back");
            // revealing the level being played: a third of the way down
            int here = cat.Items.FindIndex(it => it.Current);
            cat.ScrollTo(0);
            cat.RevealItem(here);
            Near(Math.Min(90, cat.Cells[here].Y - VrCatalog.VR_CAT_VIEW_H / 3), cat.Scroll, "revealed");
            // a short list has no bar to press
            var root = WindowFixture.F["catalog"]!["catalog-root"]!.AsObject();
            cat.SetList(WindowFixture.CatalogItems(root), "levels", "");
            Check.Equal(0f, cat.Bar().Max, "nothing to scroll");
            Check.True(!cat.PickAt(new Vector2(bar.X + 4, 300)).ScrollBar, "no bar to press");
        }
        finally { cat.Root.Free(); }
    }

    [AppTest]
    public static void CatalogPicksTilesAndTheBar()
    {
        var c = WindowFixture.F["catalog"]!["catalog-levels"]!.AsObject();
        var cat = new VrCatalog();
        try
        {
            cat.SetList(WindowFixture.CatalogItems(c), c["heading"]!.GetValue<string>(), "");
            cat.ScrollTo(40);
            foreach (var cell in cat.Cells)
            {
                float py = cell.Y + cell.H / 2 + VrCatalog.VR_CAT_VIEW_Y - cat.Scroll;
                if (py < VrCatalog.VR_CAT_VIEW_Y || py > VrCatalog.VR_CAT_VIEW_Y + VrCatalog.VR_CAT_VIEW_H) continue;
                Check.Equal(cell.I, cat.TileAt(new Vector2(cell.X + cell.W / 2, py)), "tile under its centre");
                Check.Equal(cell.I, cat.PickAt(new Vector2(cell.X + 1, py)).Tile, "tile at its left edge");
            }
            var t = cat.Cells[1];
            Check.Equal(-1, cat.TileAt(new Vector2(t.X - 3, t.Y + 20 + VrCatalog.VR_CAT_VIEW_Y - cat.Scroll)), "the gap between tiles");
            Check.Equal(-1, cat.TileAt(new Vector2(500, 60)), "the heading above the list");
            Check.Equal(-1, cat.TileAt(null), "off the panel");
            var bar = cat.Bar();
            var onBar = cat.PickAt(new Vector2(bar.X - 11, bar.Y + 10));
            Check.True(onBar.ScrollBar && onBar.Tile == -1, "the bar, with its slack");
            Near(cat.ScrollFor(bar.Y + 10), onBar.ScrollAt, "the scroll a press there gives");
            Check.True(!cat.PickAt(new Vector2(bar.X - 13, bar.Y + 10)).ScrollBar, "past the slack");
            Check.True(!cat.PickAt(new Vector2(bar.X + 4, bar.Y - 1)).ScrollBar, "above the bar");
            // hover: a tile, the bar (-2); repainted only on a change
            int ops = cat.Panel.Canvas.OpCount;
            cat.SetHover(-1);
            Check.Equal(ops, cat.Panel.Canvas.OpCount, "no change, no paint");
            cat.SetHover(-2);
            Check.Equal(-2, cat.Hover, "the bar lit");
        }
        finally { cat.Root.Free(); }
    }

    // ---- settings and level text: hit-testing
    [AppTest]
    public static void SettingsRowsHitAsTheWeb()
    {
        var s = new VrSettings(VrSettings.Rows(new Fx()));
        try
        {
            Check.Equal(716, VrSettings.VR_SET_H, "canvas height");
            Check.Equal(9, s.RowList.Count, "eight switches and the recentre");
            Check.Equal(0, s.RowAt(new Vector2(100, 96)), "the first row's top");
            Check.Equal(0, s.RowAt(new Vector2(100, 96 + 56)), "its bottom");
            Check.Equal(-1, s.RowAt(new Vector2(100, 96 + 57)), "the gap under it");
            Check.Equal(1, s.RowAt(new Vector2(100, 96 + 68)), "the second row");
            Check.Equal(8, s.RowAt(new Vector2(615, 96 + 8 * 68 + 20)), "the recentre, at the right edge");
            Check.Equal(-1, s.RowAt(new Vector2(23, 120)), "left of the rows");
            Check.Equal(-1, s.RowAt(new Vector2(617, 120)), "right of them");
            Check.Equal(-1, s.RowAt(new Vector2(100, 60)), "the title");
            Check.Equal(-1, s.RowAt(new Vector2(100, 96 + 9 * 68 + 4)), "below the last");
            Check.Equal(-1, s.RowAt(null), "off the panel");
            Check.Equal("SOFT", s.RowList[4].Text!(), "the colour blend says its level");
            Check.Equal("NONE", s.RowList[7].Text!(), "the environment its mode");
            Check.True(!s.RowList[7].Get!(), "environment none is off");
            Check.True(s.RowList[8].Get == null, "the recentre is an action");
        }
        finally { s.Root.Free(); }
    }

    [AppTest]
    public static void LevelTextFlowsWrapsAndHasAnOk()
    {
        var flowed = VrLevelText.Flow(new[] { "  Lemmings normally can't swim,", "so water is a death trap", "", "", " Second  paragraph. ", "" });
        Check.Equal(2, flowed.Count, "two paragraphs");
        Check.Equal("Lemmings normally can't swim, so water is a death trap", flowed[0], "lines run together");
        Check.Equal("Second  paragraph.", flowed[1], "trimmed, inner spaces kept");
        var w = new VrLevelText();
        try
        {
            var lines = WindowFixture.Shots["leveltext-pretext"]!["lines"]!.AsArray().Select(x => x!.GetValue<string>()).ToList();
            w.Paint(lines);
            Check.Equal(WindowFixture.Shots["leveltext-pretext"]!["h"]!.GetValue<int>(), w.CanvasHeight, "as tall as the web's window");
            var r = w.OkRect;
            Check.True(r.Position.X == 300 && r.Size.X == 168 && r.Size.Y == 58, "the OK centred, 168 x 58: " + r);
            Check.True(w.OkAt(new Vector2(300, r.Position.Y)) && w.OkAt(new Vector2(467.9f, r.Position.Y + 57.9f)), "inside the OK");
            Check.True(!w.OkAt(new Vector2(468, r.Position.Y + 10)) && !w.OkAt(new Vector2(299.9f, r.Position.Y + 10)) && !w.OkAt(null), "outside it");
            var many = Enumerable.Range(0, 30).Select(i => "line " + i).ToList();
            w.Paint(many);
            Check.Equal(16, w.Lines.Count, "no more than 16 lines");
            Check.Equal("line 15 …", w.Lines[15], "the last one says there is more");
            Check.Equal((int)(40 + 16 * 34 + 24 + 58 + 30), w.CanvasHeight, "the canvas grows with the lines");
        }
        finally { w.Root.Free(); }
    }

    // ---- the flows: the question, the notice, the hover, the tooltip, the sound column
    [AppTest]
    public static void LongQuestionsFitTheirFrame()
    {
        var m = new VrModal();
        try
        {
            foreach (var q in new[] { "Open the world catalog?", "Skip to the next level?", "Go back a level?", "Restart level?", "Quit Lemmix?" })
            {
                m.Ask(q);
                var cx = m.Panel.Canvas;
                VrModal.FitFont(cx, q, "bold ", 38);
                Check.True(cx.measureText(q).width <= VrModal.TextWidth, q + " inside the frame (" + cx.font + ")");
            }
            // one that fits keeps the web's size
            VrModal.FitFont(m.Panel.Canvas, "Restart level?", "bold ", 38);
            Check.Equal("bold 38px monospace", m.Panel.Canvas.font, "a short question at the web's size");
        }
        finally { m.Root.Free(); }
    }

    [AppTest]
    public static void TheVrButtonOpensFoveationsSwitchAndStrength()
    {
        var host = new Host();
        bool on = true; int level = 2;
        string[] names = { "off", "low", "medium", "high" };
        var rows = new List<SettingRow>
        {
            new("foveated rendering", () => on, () => on = !on),
            new("strength", () => on, () => { level = level % 3 + 1; on = true; }, () => names[level].ToUpperInvariant()),
        };
        var w = new VrWindows(host, VrSettings.Rows(new Fx()), new Node3D(), new Node3D(), null, rows);
        try
        {
            Check.True(w.Toolbar.LeftTools.Contains(w.Toolbar.VrButton) && w.IconButtons.Contains(w.Toolbar.VrButton), "a VR button in the row, with hover and tip");
            Check.True(w.Act(new VrPick("bar", BarTool: "vr")), "the VR button is a bar tool");
            Check.True(w.VrOptions.Root.Visible && w.VrOptions.Close.Visible && host.Held.Contains("vr-vroptions"), "the VR window up, the game held");
            Check.Equal("VR", w.VrOptions.Title, "its title");
            Check.True(w.VrOptions.H < VrSettings.VR_SET_H, "sized for its two rows");
            Check.True(w.AnyWindowUp, "counts as a window");
            w.Act(new VrPick("bar", BarTool: "vrsetpanel", Data: new WindowPickData(Row: 1)));
            Check.True(on && level == 3, "the strength row steps to high");
            w.Act(new VrPick("bar", BarTool: "vrsetpanel", Data: new WindowPickData(Row: 0)));
            Check.True(!on, "the switch turns it off");
            w.Act(new VrPick("bar", BarTool: "vrsetpanel", Data: new WindowPickData(Row: 1)));
            Check.True(on && level == 1, "a strength press turns it back on, wrapping to low");
            w.Act(new VrPick("bar", BarTool: "vrsetclose"));
            Check.True(!w.VrOptions.Root.Visible && !host.Held.Contains("vr-vroptions"), "closed, the game given back");
        }
        finally { w.WindowRoot.Free(); w.Toolbar.GuiRoot.Free(); }
    }

    [AppTest]
    public static void TheVrWindowsHeightOffsetSliderAndReset()
    {
        var host = new Host();
        float offset = 0;
        var control = new FloorControl { Get = () => offset, Set = v => offset = v, Reset = () => offset = 0 };
        var w = new VrWindows(host, VrSettings.Rows(new Fx()), new Node3D(), new Node3D(), null, new List<SettingRow>(), control);
        try
        {
            var v = w.VrOptions;
            Check.True(FloorControl.Max == -FloorControl.Min, "as far up as down");
            Check.True(Mathf.Abs(v.ValueAt(v.SliderTop) - FloorControl.Max) < 1e-4f && Mathf.Abs(v.ValueAt(v.SliderTop + VrSettings.SliderLen) - FloorControl.Min) < 1e-4f, "the slider's ends");
            Check.True(Mathf.Abs(v.YOf(0) - (v.SliderTop + VrSettings.SliderLen / 2)) < 1e-3f, "0 in the middle");
            var (part, value) = v.FloorPartAt(new Vector2(VrSettings.SliderX, v.YOf(0.4f)));
            Check.Equal("slider", part, "the slider under the beam");
            w.Act(new VrPick("bar", BarTool: "vrfloor", ScrollBar: true, Data: new WindowPickData(Volume: value)));
            Check.True(Mathf.Abs(offset - 0.4f) < 1e-3f, "dragged up to +40 cm: " + offset);
            Check.Equal("+40 cm", FloorControl.Label(offset), "its label");
            Check.True(VrSettings.FloorButtons.Length == 1 && VrSettings.FloorButtons[0] == "reset", "no seated or standing button: a reset");
            var (rp, _) = v.FloorPartAt(new Vector2(VrSettings.ButtonX + 20, v.ButtonTop(0) + 10));
            Check.Equal("reset", rp, "the reset under the beam");
            w.Act(new VrPick("bar", BarTool: "vrsetpanel", Data: new WindowPickData(Part: rp)));
            Check.True(offset == 0 && FloorControl.Label(0) == "0 cm", "reset to 0");
            w.ApplyHover(new VrPick("bar", BarTool: "vrfloor", ScrollBar: true, Data: new WindowPickData(Volume: 0)));
            Check.Equal("slider", v.HoverPart, "the slider lit");
            w.ApplyHover(null);
            Check.True(v.HoverPart == null, "nothing lit");
        }
        finally { w.WindowRoot.Free(); w.Toolbar.GuiRoot.Free(); }
    }

    [AppTest]
    public static void QuitAsksThenEndsTheGame()
    {
        var host = new Host();
        var w = Make(host, out var scene);
        try
        {
            Check.True(w.Toolbar.LeftTools[0] == w.Toolbar.Quit, "quit at the left end of the row");
            // like the row's other buttons: the beam lands on it, lights it and shows its tip, with
            // a level or without one
            Check.True(w.IconButtons.Contains(w.Toolbar.Quit), "among the icon buttons (hover, tooltip, beam length)");
            w.Layout(0.6f, 0.06f, 0, 160);
            foreach (bool session in new[] { true, false })
            {
                host.HasSession = session;
                var (o, d) = At(w.Toolbar.Quit);
                var (_, pick) = w.Pick(o, d);
                Check.Equal("quit", pick?.BarTool, "the beam on quit (level: " + session + ")");
                w.ApplyHover(pick);
                Check.True(w.Toolbar.Quit.State.Hovered, "quit lit under the beam");
                w.ApplyHover(null);
            }
            // with a level: back to the lobby, asking first
            host.HasSession = true;
            Check.True(w.Act(new VrPick("bar", BarTool: "quit")), "quit is a bar tool");
            Check.Equal("Back to the lobby?", w.Modal.Title, "asks first");
            Check.True(w.Modal.Yes.Visible && w.Modal.No.Visible, "with yes and no");
            w.Act(new VrPick("bar", BarTool: "no"));
            Check.True(!w.Modal.Root.Visible && host.Log.Count == 0, "no: nothing happens");
            w.Act(new VrPick("bar", BarTool: "quit"));
            w.Act(new VrPick("bar", BarTool: "yes"));
            Check.Equal("lobby", host.Log.LastOrDefault(), "yes: back to the lobby");
            // without one: the app ends
            host.HasSession = false;
            w.Act(new VrPick("bar", BarTool: "quit"));
            Check.Equal("Quit Lemmix?", w.Modal.Title, "asks first");
            w.Act(new VrPick("bar", BarTool: "yes"));
            Check.Equal("quit", host.Log.LastOrDefault(), "yes: the game ends");
        }
        finally { scene.Free(); }
    }

    [AppTest]
    public static void RestartAsksAndYesActs()
    {
        var host = new Host();
        var w = Make(host, out var scene);
        try
        {
            Check.True(w.Act(new VrPick("bar", BarTool: "restart")), "restart is a bar tool");
            Check.True(w.Modal.Root.Visible && w.Modal.Yes.Visible && w.Modal.No.Visible && !w.ModalNotice, "a question with two answers");
            Check.Equal("Restart level?", w.Modal.Title, "asks");
            Check.True(host.Held.Contains("vr-modal"), "holds the clock");
            Check.Near(VrModal.ButtonPlacement(-1, false).Pos, w.Modal.Yes.Position, "yes on the left", 1e-6f);
            // while it is up its answers are the only things the ray can hit
            var (o, d) = At(w.Toolbar.Pause);
            var (owned, pick) = w.Pick(o, d);
            Check.True(owned && pick == null, "the bar is out of reach");
            (o, d) = At(w.Modal.Yes);
            (owned, pick) = w.Pick(o, d);
            Check.Equal("yes", pick?.BarTool, "the yes under the ray");
            (o, d) = At(w.Modal.No);
            Check.Equal("no", w.Pick(o, d).Pick?.BarTool, "the no");
            w.Act(pick!);
            Check.True(!w.Modal.Root.Visible && !host.Held.Contains("vr-modal"), "closed, clock given back");
            Check.Equal("move 0", host.Log.LastOrDefault(), "the level restarted");
            // no: nothing happens
            w.Act(new VrPick("bar", BarTool: "next"));
            Check.Equal("Skip to the next level?", w.Modal.Title, "next asks");
            w.Act(new VrPick("bar", BarTool: "no"));
            Check.True(!w.Modal.Root.Visible && host.Log.Count == 1, "no: closed, nothing done");
            w.Act(new VrPick("bar", BarTool: "prev"));
            Check.Equal("Go back a level?", w.Modal.Title, "prev asks");
            w.Act(new VrPick("bar", BarTool: "yes"));
            Check.Equal("move -1", host.Log.Last(), "went back");
            // the catalog asks first when a level is being played
            w.Act(new VrPick("bar", BarTool: "worlds"));
            Check.Equal("Open the world catalog?", w.Modal.Title, "worlds asks");
            w.Act(new VrPick("bar", BarTool: "yes"));
            Check.True(w.Catalog.Root.Visible && host.Held.Contains("vr-catalog"), "the catalog opened");
        }
        finally { scene.Free(); }
    }

    [AppTest]
    public static void NoSolutionIsANoticeWithOneButton()
    {
        var host = new Host();
        var w = Make(host, out var scene);
        try
        {
            w.Act(new VrPick("bar", BarTool: "solution"));
            Check.True(w.ModalNotice && w.Modal.Yes.Visible && !w.Modal.No.Visible, "one button");
            Check.Equal("No solution", w.Modal.Title, "title");
            Check.Equal("This level has no stored solution.", w.Modal.Body, "body");
            Check.Near(VrModal.ButtonPlacement(0, false).Pos, w.Modal.Yes.Position, "the OK centred", 1e-6f);
            var (o, d) = At(w.Modal.No);
            Check.True(w.Pick(o, d).Pick == null, "the hidden no cannot be hit");
            w.Act(new VrPick("bar", BarTool: "yes"));
            Check.True(!w.Modal.Root.Visible && host.Log.Count == 0, "OK only closes it");
            host.CanWatchSolution = true;
            w.Act(new VrPick("bar", BarTool: "solution"));
            Check.True(!w.ModalNotice && w.Modal.No.Visible && w.Modal.Title == "Watch the solution?", "with one, a question");
            w.Act(new VrPick("bar", BarTool: "yes"));
            Check.Equal("solution", host.Log.Last(), "watched");
            // outside a headset nothing opens
            host.Presenting = false;
            w.Act(new VrPick("bar", BarTool: "restart"));
            Check.True(!w.Modal.Root.Visible && !host.Held.Contains("vr-modal"), "no window off the headset");
        }
        finally { scene.Free(); }
    }

    [AppTest]
    public static void HoverLightsOneButtonAndTheTipWaits()
    {
        var host = new Host();
        double now = 1000;
        var w = Make(host, out var scene);
        w.Now = () => now;
        try
        {
            var (o, d) = At(w.Toolbar.Pause);
            var (owned, pick) = w.Pick(o, d);
            Check.True(!owned && pick?.BarTool == "pause", "the ray on pause");
            w.ApplyHover(pick);
            Check.True(w.Toolbar.Pause.State.Hovered && w.IconButtons.Count(b => b.State.Hovered) == 1, "pause alone lit");
            w.Layout(GuiW, BarH, 260, 160);
            Near(VR_BAR_TOOL_SIZE * VR_BAR_TOOL_HOVER, w.Toolbar.Pause.Size, "it grows");
            now += 1499; w.Update();
            Check.True(!w.Tooltip.Panel.Visible, "no label before 1500 ms");
            now += 1; w.Update();
            Check.True(w.Tooltip.Panel.Visible && w.Tooltip.TipText == "pause", "the label after the rest");
            Check.True(w.Tooltip.Panel.GlobalPosition.Y > w.Toolbar.Pause.GlobalPosition.Y, "above its button");
            host.GameRunning = false; w.Update();
            Check.Equal("resume", w.Tooltip.TipText, "a stopped game: resume");
            // moving on restarts the wait; leaving hides it at once
            w.ApplyHover(new VrPick("bar", BarTool: "lock"));
            Check.True(!w.Tooltip.Panel.Visible, "gone the moment the beam moves");
            now += 1500; w.Update();
            Check.Equal("let the bar go: it stays where it hangs", w.Tooltip.TipText, "the padlock's label, the bar on the head");
            w.ApplyHover(null);
            w.Update();
            Check.True(!w.Tooltip.Panel.Visible && w.IconButtons.All(b => !b.State.Hovered), "nothing lit, no label");
            // a button without a label (the slider) never gets one
            w.ApplyHover(new VrPick("bar", BarTool: "volume"));
            now += 5000; w.Update();
            Check.True(!w.Tooltip.Panel.Visible, "no label for the slider");
        }
        finally { scene.Free(); }
    }

    [AppTest]
    public static void TheSliderLingersAndTakesTheBeamsHeight()
    {
        var host = new Host();
        double now = 10_000;
        var w = Make(host, out var scene);
        w.Now = () => now;
        try
        {
            w.Layout(GuiW, BarH, 260, 160);
            Check.True(!w.Toolbar.Volume.Visible, "not summoned");
            w.ApplyHover(new VrPick("bar", BarTool: "mute"));
            w.Layout(GuiW, BarH, 260, 160);
            Check.True(w.Toolbar.Volume.Visible, "summoned by the speaker");
            w.ApplyHover(null);
            now += 1999; w.Layout(GuiW, BarH, 260, 160);
            Check.True(w.Toolbar.Volume.Visible, "lingers for the beam to cross");
            now += 2; w.Layout(GuiW, BarH, 260, 160);
            Check.True(!w.Toolbar.Volume.Visible, "gone after 2000 ms");
            w.ApplyHover(new VrPick("bar", BarTool: "mute"));
            w.Layout(GuiW, BarH, 260, 160);
            var (o, d) = AtPixel(w.Toolbar.Volume, 32, 256 * 0.25f);
            var pick = w.Pick(o, d).Pick;
            Check.Equal("volume", pick?.BarTool, "the slider takes the ray first");
            Near(0.75f, ((WindowPickData)pick!.Data!).Volume, "the value is where the beam lands", 1e-3f);
            w.Act(pick);
            Check.True(Math.Abs(host.Volume - 0.75f) < 1e-3 && Math.Abs(w.Toolbar.VolumeLevel - 0.75f) < 1e-3, "set and painted");
            (o, d) = At(w.Toolbar.Mute);
            w.Act(w.Pick(o, d).Pick!);
            Check.True(!host.AudioEnabled && w.Toolbar.Mute.State.On, "muted: the switch shows it");
        }
        finally { scene.Free(); }
    }

    // ---- the catalog's flows, over a library
    [AppTest]
    public static void CatalogOpensOnTheLevelAndNavigates()
    {
        var host = new Host();
        var lib = new Lib { CurrentLevelId = "Pack A/Easy/l5", FavIds = { "Pack A/Easy/l2" }, RecentIds = { "Pack A/Easy/l5", "Classic/Fun/l1", "gone/l9" } };
        var w = Make(host, out var scene);
        w.Library = lib;
        try
        {
            w.SetCatalog(true);
            var c = w.Catalog;
            Check.True(c.Root.Visible && c.Close.Visible && host.Held.Contains("vr-catalog"), "open, closable, clock held");
            Check.Equal("levels › Pack A › Easy", c.Heading, "the directory of the level played");
            Check.Equal("back", c.Items[0].Kind, "a way back up first");
            Check.Equal(7, c.Items.Count, "and its six levels");
            var tile = c.Items[5];
            Check.True(tile.Current && tile.Label == "Easy 5" && tile.Name == "Level 5" && tile.Playable, "the level played, labelled by its rank: " + tile.Label);
            Check.True(c.Items[2].Favorite && c.Items[1].Solution, "favorite and solution marks");
            Check.Equal("", c.Note, "nothing to say");
            // the back row ascends; a directory row descends
            w.Act(new VrPick("bar", BarTool: "worldpanel", Data: new WindowPickData(Tile: 0)));
            Check.Equal("levels › Pack A", c.Heading, "up a level");
            Check.True(c.Items[1].Kind == "dir" && c.Items[2].Done == 3 && c.Items[2].Count == 3, "rows with their counts");
            w.Act(new VrPick("bar", BarTool: "worldpanel", Data: new WindowPickData(Tile: 2)));
            Check.Equal("levels › Pack A › Hard", c.Heading, "into Hard");
            Check.True(c.Items[1].Best == 61, "a cleared level's best");
            // a tile enters its level and closes the catalog
            w.Act(new VrPick("bar", BarTool: "worldpanel", Data: new WindowPickData(Tile: 3)));
            Check.Equal("enter Pack A/Hard/l3", host.Log.Last(), "entered");
            Check.True(!c.Root.Visible && !host.Held.Contains("vr-catalog"), "closed");
            // a classic directory is scanned for its names, and cannot be played without its engine
            w.SetCatalog(true);
            lib.Navigate("Classic/Fun");
            c.Load(lib, false);
            Check.True(lib.Log.Contains("scan Classic/Fun") && c.Note == "needs the Lemmix engine" && !c.Items[1].Playable, "classic: " + c.Note);
            w.Act(new VrPick("bar", BarTool: "worldpanel", Data: new WindowPickData(Tile: 1)));
            Check.True(c.Root.Visible, "an unplayable tile does nothing");
        }
        finally { scene.Free(); }
    }

    [AppTest]
    public static void CatalogListsRecentAndFavorites()
    {
        var host = new Host();
        var lib = new Lib { CurrentLevelId = "Pack A/Easy/l5", RecentIds = { "Pack A/Easy/l5", "Classic/Fun/l1", "gone/l9" } };
        var w = Make(host, out var scene);
        w.Library = lib;
        try
        {
            w.SetCatalog(true);
            var c = w.Catalog;
            w.Act(new VrPick("bar", BarTool: "catrecent"));
            Check.True(c.Recent.State.On && !c.Fav.State.On && c.Filter == "recent", "the clock lit");
            Check.Equal("recently played · 2 levels", c.Heading, "a level that is gone is left out");
            Check.Equal("Pack A › Easy 5", c.Items[1].Label, "labelled with the pack it lives in");
            Check.True(lib.Log.Contains("scan Classic/Fun"), "classic names scanned");
            w.ApplyHover(new VrPick("bar", BarTool: "catrecent"));
            w.Update();
            // the same button again: the directories
            w.Act(new VrPick("bar", BarTool: "catrecent"));
            Check.True(c.Filter == null && !c.Recent.State.On && c.Heading == "levels › Pack A › Easy", "back to the directory");
            w.Act(new VrPick("bar", BarTool: "catfav"));
            Check.True(c.Fav.State.On && c.Heading == "favorites · 0 levels", "favorites: " + c.Heading);
            Check.Equal("no favorite yet - star a level to keep it here", c.Note, "says why it is empty");
            // the back row leaves the list
            w.Act(new VrPick("bar", BarTool: "worldpanel", Data: new WindowPickData(Tile: 0)));
            Check.True(c.Filter == null && !c.Fav.State.On && c.Heading == "levels › Pack A › Easy", "‹ back to the directory");
            // the sticks scroll it while it is up
            Check.True(w.OnStick(1, 0.1), "a stick scrolls the catalog");
            w.SetCatalog(false);
            Check.True(!w.OnStick(1, 0.1), "and the board otherwise");
            // locked (no level yet): no close, and it says what to do
            lib.Locked = true;
            lib.CurrentLevelId = null;
            lib.Navigate("");
            w.SetCatalog(true);
            Check.True(!c.Close.Visible && c.Note == "choose a level to play", "locked: " + c.Note);
            w.Act(new VrPick("bar", BarTool: "catclose"));
            Check.True(c.Root.Visible, "the close does nothing while locked");
        }
        finally { scene.Free(); }
    }

    [AppTest]
    public static void CatalogOwnsTheRay()
    {
        var host = new Host();
        var lib = new Lib { CurrentLevelId = "Pack A/Easy/l1" };
        var w = Make(host, out var scene);
        w.Library = lib;
        try
        {
            w.SetCatalog(true);
            var c = w.Catalog;
            var (o, d) = At(c.Close);
            Check.Equal("catclose", w.Pick(o, d).Pick?.BarTool, "the close");
            (o, d) = At(c.Fav);
            Check.Equal("catfav", w.Pick(o, d).Pick?.BarTool, "the star");
            var cell = c.Cells[2];
            (o, d) = AtPixel(c.Panel, cell.X + cell.W / 2, cell.Y + cell.H / 2 + VrCatalog.VR_CAT_VIEW_Y - c.Scroll);
            var pick = w.Pick(o, d).Pick!;
            Check.True(pick.BarTool == "worldpanel" && ((WindowPickData)pick.Data!).Tile == 2, "a tile through the panel");
            w.ApplyHover(pick);
            Check.Equal(2, c.Hover, "lit");
            (o, d) = At(w.Toolbar.Pause);
            Check.True(w.Pick(o, d) is (true, null), "nothing behind it");
            w.Act(new VrPick("bar", BarTool: "catclose"));
            Check.True(!c.Root.Visible, "closed");
        }
        finally { scene.Free(); }
    }

    [AppTest]
    public static void SettingsActAndClose()
    {
        var host = new Host();
        var fx = new Fx();
        var w = Make(host, out var scene, fx);
        try
        {
            w.Act(new VrPick("bar", BarTool: "settings"));
            var s = w.Settings;
            Check.True(s.Root.Visible && s.Close.Visible && host.Held.Contains("vr-settings"), "open, clock held");
            var (o, d) = AtPixel(s.Panel, 300, 96 + 4 * 68 + 20);
            var pick = w.Pick(o, d).Pick!;
            Check.True(pick.BarTool == "setpanel" && ((WindowPickData)pick.Data!).Row == 4, "the colour blend's row");
            w.ApplyHover(pick);
            Check.Equal(4, s.Hover, "lit");
            w.Act(pick);
            Check.True(fx.ColorBlend == "smooth" && fx.Log.Last() == "colorBlend", "toggled");
            (o, d) = AtPixel(s.Panel, 300, 96 + 8 * 68 + 20);
            w.Act(w.Pick(o, d).Pick!);
            Check.Equal("recenter", fx.Log.Last(), "the recentre row");
            (o, d) = At(s.Close);
            Check.Equal("setclose", w.Pick(o, d).Pick?.BarTool, "the close");
            w.Act(new VrPick("bar", BarTool: "setclose"));
            Check.True(!s.Root.Visible && !host.Held.Contains("vr-settings") && s.Hover == -1, "closed");
        }
        finally { scene.Free(); }
    }

    [AppTest]
    public static void LevelTextOpensFromTheStripAndOkCloses()
    {
        var host = new Host();
        var w = Make(host, out var scene);
        try
        {
            w.Layout(GuiW, BarH, 260, 160);
            Check.True(!w.Status.Detail.Visible, "no text, no detail button");
            w.SetLevelText(new[] { "Lemmings normally can't swim,", "so water is a death trap." });
            w.Layout(GuiW, BarH, 260, 160);
            Check.True(w.Status.Detail.Visible, "a text: the button");
            var (o, d) = At(w.Status.Detail);
            var pick = w.Pick(o, d).Pick!;
            Check.Equal("detail", pick.BarTool, "the strip's button");
            w.Act(pick);
            Check.True(w.LevelText.Root.Visible && host.Held.Contains("vr-detail"), "open, clock held");
            var r = w.LevelText.OkRect;
            (o, d) = AtPixel(w.LevelText.Panel, r.Position.X + 10, r.Position.Y + 10);
            pick = w.Pick(o, d).Pick!;
            Check.Equal("detailok", pick.BarTool, "the OK");
            w.ApplyHover(pick);
            Check.True(w.LevelText.OkHot, "lit");
            (o, d) = AtPixel(w.LevelText.Panel, 50, 50);
            Check.Equal("detailpanel", w.Pick(o, d).Pick?.BarTool, "the rest of the window");
            w.Act(new VrPick("bar", BarTool: "detailok"));
            Check.True(!w.LevelText.Root.Visible && !host.Held.Contains("vr-detail"), "closed");
            w.SetLevelText(null);
            w.Act(new VrPick("bar", BarTool: "detail"));
            Check.True(!w.LevelText.Root.Visible, "nothing to show");
        }
        finally { scene.Free(); }
    }

    // ---- the bar: lock, park, carry, and parked out of a window's way
    [AppTest]
    public static void BarLocksParksAndRemembers()
    {
        var host = new Host();
        var w = Make(host, out var scene);
        string? stored = null;
        try
        {
            var head = (Node3D)w.Toolbar.GuiRoot.GetParent();
            head.Position = new Vector3(0.1f, 1.6f, 0.3f);
            head.Rotation = new Vector3(-0.2f, 0.4f, 0);
            var bar = w.Bar;
            bar.Store = j => stored = j;
            var diorama = new Transform3D(new Basis(Vector3.Up, 0.5f) * Basis.FromScale(Vector3.One * VR_PIXEL_SCALE), new Vector3(-0.7f, 1.25f, -0.3f));
            bar.Frame = () => new VrWindowPlacement.BarFrame(diorama, 0.5f, 260, GuiW, BarH);
            var d = bar.Default()!.Value;
            // the padlock: let go where it hangs (it does not jump), saved as an offset from the default
            var before = w.Toolbar.GuiRoot.GlobalTransform;
            w.Act(new VrPick("bar", BarTool: "lock"));
            Check.True(!bar.Locked && w.Toolbar.GuiRoot.GetParent() == scene && w.Toolbar.Lock.State.On, "in the room, the padlock open");
            Check.Near(before.Origin, w.Toolbar.GuiRoot.GlobalTransform.Origin, "it did not move", 1e-5f);
            var prefs = VrWindowPlacement.BarPrefs.FromJson(stored)!;
            Check.True(!prefs.Locked, "saved unlocked");
            Check.Near(w.Toolbar.GuiRoot.Position, d.Pos + d.Quat * prefs.Pos, "the offset is in the default's frame", 1e-5f);
            Check.True(stored!.StartsWith("{\"locked\":false,\"pos\":[", StringComparison.Ordinal) && stored.Contains("\"quat\":["), "the web's lem3d-bar shape: " + stored);
            // park: the default, for good
            w.Act(new VrPick("bar", BarTool: "park"));
            Check.Near(d.Pos, w.Toolbar.GuiRoot.Position, "parked at the default", 1e-6f);
            NearQ(d.Quat, w.Toolbar.GuiRoot.Quaternion, "facing the board's way");
            prefs = VrWindowPlacement.BarPrefs.FromJson(stored)!;
            Check.True(prefs.Pos.Length() < 1e-5f && !prefs.Locked, "a zero offset saved");
            // carried with the move handle, then let go: remembered relative to the default
            bar.DragStart();
            bar.Drag(new Vector3(0.1f, 0.05f, 0));
            bar.DragEnd(true);
            Check.Near(d.Pos + new Vector3(0.1f, 0.05f, 0), w.Toolbar.GuiRoot.Position, "carried by the hand's delta", 1e-6f);
            // the board placed again (a recentre, a level): the bar follows at its offset
            diorama = diorama.Translated(new Vector3(0, 0, -0.2f));
            bar.OnDioramaPlaced();
            Check.Near(bar.Default()!.Value.Pos + new Vector3(0.1f, 0.05f, 0), w.Toolbar.GuiRoot.Position, "the offset kept", 1e-5f);
            // locked again: rides the head from where it is; a window parks it below the board and hands it back
            w.Act(new VrPick("bar", BarTool: "lock"));
            Check.True(bar.Locked && w.Toolbar.GuiRoot.GetParent() == head && !w.Toolbar.Lock.State.On, "on the head");
            var hang = w.Toolbar.GuiRoot.Transform;
            w.Act(new VrPick("bar", BarTool: "settings"));
            Check.True(bar.Parked && w.Toolbar.GuiRoot.GetParent() == scene, "parked in the room while the window is up");
            w.Act(new VrPick("bar", BarTool: "setclose"));
            Check.True(!bar.Parked && bar.Locked && w.Toolbar.GuiRoot.GetParent() == head, "back on the head");
            Check.Near(hang.Origin, w.Toolbar.GuiRoot.Transform.Origin, "as it hung", 1e-5f);
            // a new session starts it where it was left: on the head
            var fresh = new VrBar(new Node3D(), new Node3D(), new Node3D(), stored) { Frame = bar.Frame };
            Check.True(fresh.Prefs!.Locked, "saved locked");
            fresh.Locked = false;
            fresh.Scene.AddChild(fresh.GuiRoot);
            fresh.PlaceAtStart();
            Check.True(fresh.Locked && fresh.GuiRoot.GetParent() == fresh.Head, "starts on the head");
            Check.Near(hang.Origin, fresh.GuiRoot.Position, "at the same hang", 1e-5f);
            fresh.Head.Free(); fresh.Scene.Free();
            // reset: square in front of the head
            bar.Reset();
            Check.True(bar.Locked && w.Toolbar.GuiRoot.Transform == Transform3D.Identity, "reset on the head");
        }
        finally { scene.Free(); }
    }

    [AppTest]
    public static void BarPrefsReadTheWebsJson()
    {
        var p = VrWindowPlacement.BarPrefs.FromJson("{\"locked\":true,\"pos\":[0.1,-0.02,0.003],\"quat\":[0,0.247404,0,0.968912]}")!;
        Check.True(p.Locked, "locked");
        Check.Near(new Vector3(0.1f, -0.02f, 0.003f), p.Pos, "pos");
        Check.True(Math.Abs(p.Quat.W - 0.968912f) < 1e-6, "quat w last, as three writes it");
        Check.True(VrWindowPlacement.BarPrefs.FromJson("not json") == null && VrWindowPlacement.BarPrefs.FromJson(null) == null, "garbage is no prefs");
        var back = VrWindowPlacement.BarPrefs.FromJson(p.ToJson())!;
        Check.Near(p.Pos, back.Pos, "round trip");
    }

    [AppTest]
    public static void StatusRepaintsOnlyOnAChange()
    {
        var s = new VrStatusStrip();
        try
        {
            s.Set(name: "Just dig!", meta: "Lemmings Redux · Gentle 1 · save 1/10", note: "", kind: "");
            var trace = new List<string>();
            s.Panel.Canvas.Trace = trace;
            s.Set(note: "");
            Check.Equal(0, trace.Count, "the same: no paint");
            s.Set(note: "FAILED", kind: "lost");
            Check.True(trace.Contains("strokeStyle=#e07a6a") && trace.Contains("fillText(\"FAILED\",998,84)"), "a loss in red, right-aligned");
            Check.Equal("Just dig!", s.Status.Name, "the name kept");
            s.Badge.Set(true, true);
            Check.True(s.Badge.Panel.Visible, "REPLAY in a headset");
            s.Badge.Set(true, false);
            Check.True(!s.Badge.Panel.Visible, "never on a monitor");
        }
        finally { s.Root.Free(); }
    }
}
