using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Lemmix.App.Ui;
using Lemmix.App.Ui.Windows;
using Lemmix.App.Xr;

namespace Lemmix.App.Test;

// The headset's windows for review: `--shot win-<state> <file>` renders one window state of the
// fixture (app/Test/Fixtures/windows.json, oracle/webshots.js) - the canvas, as the web's
// build/webshots/<state>.png is - and `--shot windows <dir>/windows-scene.png` renders every state
// at once into <dir>/<state>.png, with the windows in the room as the picture it was asked for.
public partial class Shots
{
    static Shots()
    {
        RegisterPageShots();
        foreach (var name in WindowFixture.Names.ToList())
            All["win-" + name] = root => One(root, name);
        All["windows"] = WindowShots.Batch;
        All["windows-scene"] = WindowShots.Scene;
    }

    static Viewport One(Node root, string name)
    {
        var made = WindowFixture.Make(name);
        root.AddChild(made.Owner);
        made.Panel.Commit();
        return made.Panel.Target;
    }
}

public partial class WindowShots : Node
{
    readonly List<(string Name, Panel3D Panel)> _all = new();
    string _dir = "";
    int _frames;

    // every state in one run, each saved from its panel's own viewport
    public static Viewport Batch(Node root)
    {
        var args = OS.GetCmdlineUserArgs().SkipWhile(a => a != "--shot").Skip(1).ToList();
        var batch = new WindowShots { _dir = System.IO.Path.GetDirectoryName(args.ElementAtOrDefault(1) ?? "user://x.png") ?? "." };
        foreach (var name in WindowFixture.Names)
        {
            var made = WindowFixture.Make(name);
            root.AddChild(made.Owner);
            made.Panel.Commit();
            batch._all.Add((name, made.Panel));
        }
        root.AddChild(batch);
        return Scene(root);
    }

    public override void _Process(double delta)
    {
        if (++_frames != 4) return;
        foreach (var (name, panel) in _all)
        {
            var img = panel.Target.GetTexture().GetImage();
            img.SavePng(System.IO.Path.Combine(_dir, name + ".png"));
        }
        GD.Print($"[lemmix] window shots saved: {_all.Count} -> {_dir}");
    }

    sealed class Host : IVrWindowsHost
    {
        public bool Presenting => true;
        public bool HasSession => true;
        public Transform3D? HeadPose => Transform3D.Identity;
        public void HoldSim(string who) { }
        public void ReleaseSim(string who) { }
        public bool GameRunning => true;
        public bool AudioEnabled => true;
        public float Volume => 0.7f;
        public void SetVolume(float v) { }
        public void ToggleMute() { }
        public void TogglePause() { }
        public void MoveLevel(int d) { }
        public bool CanWatchSolution => true;
        public void WatchSolution() { }
        public void EnterLevel(string id) { }
    }

    sealed class NoFx : IVrEffects
    {
        public bool Emboss => true; public bool Doors => true; public bool Smooth => true; public bool SmoothTerrain => false;
        public string ColorBlend => "soft"; public bool SkillBar => true; public bool FlatSkills => false; public string Environment => "full";
        public void ToggleEmboss() { } public void ToggleDoors() { } public void ToggleSmooth() { } public void ToggleSmoothTerrain() { }
        public void ToggleColorBlend() { } public void ToggleSkillBar() { } public void ToggleFlatSkills() { } public void ToggleEnvironment() { }
        public void Recenter() { }
    }

    // the windows in the room, from the head: the catalog open on the fixture's list, the bar's
    // row and sound column below it (the skills bar itself is another port's: its place is outlined)
    public static Viewport Scene(Node root)
    {
        var vp = new SubViewport { Size = new Vector2I(1280, 800), OwnWorld3D = true, RenderTargetUpdateMode = SubViewport.UpdateMode.Always, Msaa3D = Viewport.Msaa.Msaa4X };
        var env = new WorldEnvironment { Environment = new Godot.Environment { BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new Color("10141c") } };
        vp.AddChild(env);
        var scene = new Node3D { Name = "scene" };
        vp.AddChild(scene);
        var head = new Camera3D { Fov = 62, Near = 0.01f, Far = 20, Name = "head" };
        scene.AddChild(head);
        root.AddChild(vp);
        head.MakeCurrent();
        var w = new VrWindows(new Host(), VrSettings.Rows(new NoFx()), head, scene);
        head.AddChild(w.Toolbar.GuiRoot);
        scene.AddChild(w.WindowRoot);
        scene.AddChild(w.Tooltip.Panel);
        w.PlaceWindows(Transform3D.Identity);
        float guiW = VrManager.VR_GUI_WIDTH * 416 / 320f, barH = guiW * 40 / 416;
        // the skills bar's place
        var slab = new MeshInstance3D
        {
            Mesh = new QuadMesh { Size = new Vector2(guiW, barH) },
            Position = new Vector3(0, VrManager.VR_GUI_Y, VrManager.VR_GUI_Z),
            MaterialOverride = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = new Color("2a3446") },
        };
        w.Toolbar.GuiRoot.AddChild(slab);
        w.Catalog.Root.Visible = true;
        var c = WindowFixture.F["catalog"]!["catalog-levels"]!.AsObject();
        w.Catalog.SetList(WindowFixture.CatalogItems(c), c["heading"]!.GetValue<string>(), "");
        w.Catalog.RevealItem(w.Catalog.Items.FindIndex(it => it.Current));
        foreach (var b in w.Catalog.Tools) b.Visible = true;
        w.Catalog.Fav.SetState(on: true);
        double now = 0;
        w.Now = () => now;
        w.ApplyHover(new VrPick("bar", BarTool: "mute"));
        w.Catalog.SetHover(6);
        w.Toolbar.PaintSound(0.7f, true);
        w.Layout(guiW, barH, 260, 160);
        now = 2000;
        w.Update();
        return vp;
    }
}
