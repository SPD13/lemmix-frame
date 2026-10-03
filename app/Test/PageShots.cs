using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Godot;
using Lemmix.App.Ui;
using Lemmix.App.Ui.Pages;
using Lemmix.App.Ui.Windows;

namespace Lemmix.App.Test;

// The pages in the headset for review: `--shot page-<state> <file>` renders one page state (the
// canvas, as oracle/pageshots.js's build/webshots/pages/<state>.png is the web's), and
// `--shot pages <dir>/pages.png` every state at once into <dir>/page-<state>.png.
public partial class Shots
{
    // added to the table by Shots' static constructor (WindowShots.cs). Not a [ModuleInitializer]:
    // that runs as the assembly loads, before Godot's interop is up, and Shots is a Node - an
    // exported build crashed on it (the editor's Debug run happened to survive)
    internal static void RegisterPageShots()
    {
        foreach (var name in PageShots.States.Keys)
        {
            string n = name;
            All["page-" + n] = root => PageShots.One(root, n);
        }
        All["pages"] = PageShots.Batch;
    }
}

public partial class PageShots : Node
{
    // the page in that state: (the node to add, the panel to grab)
    public static readonly Dictionary<string, Func<(Node Owner, Panel3D Panel)>> States = new(StringComparer.Ordinal)
    {
        ["setup"] = () => Setup(_ => { }),
        ["setup-scrolled"] = () => Setup(p => { p.Paint(); p.ScrollTo(1e6f); }),
        ["setup-progress"] = () => Setup(p =>
        {
            p.Progress("unpacking NeoLemmix_V12.14.0.zip — 3.2 MB of 7.0 MB, 412 files", 0.45);
            p.Say("progress", "lemmings-3d-progress.json: 12 levels merged");
            p.Say("prefs", "notes.json: notes.json is not a JSON file", true);
            p.Paint();
            p.ScrollTo(PageFixtureY(p, 820));
        }),
        ["setup-empty"] = () =>
        {
            var b = new PageFixture.Setup { Levels = false, Store = (8000, 10_740_000_000) };
            return Page(new VrSetupPage(b, new PageFixture.Confirms()), _ => { });
        },
        ["setup-popup"] = () => Setup(p =>
        {
            ((PageFixture.Setup)p.Backend).Mem.Put("/data/import", "NeoLemmix_V12.14.0.zip", "", 7_043_210);
            ((PageFixture.Setup)p.Backend).Mem.Put("/data/import", "LemmingsPlus_All_20201114.zip", "", 21_870_000);
            p.Paint();
            p.Press(new PagePick("zip-engine"));
        }),
        ["setup-confirm"] = () =>
        {
            var m = new VrModal();
            WindowsPageConfirm.PaintModal(m, "Replace NeoLemmix?", "replace",
                "The 1489 files installed on 10/2/2026, 3:19:00 PM on the headset are removed first.");
            m.Root.Visible = true;
            return (m.Root, m.Panel);
        },
        ["solutions"] = () => Solutions(_ => { }),
        ["solutions-search"] = () => Solutions(p => p.SetQuery("jst nk")),
        ["solutions-solved"] = () => Solutions(p => { p.Show = "solved"; p.Render(); }),
        ["solutions-skills"] = () => Solutions(p => p.SortBy("skills")),
        ["solutions-popup"] = () => Solutions(p => { p.Paint(); p.Press(new PagePick("pack")); }),
        ["controls-keyboard"] = () => Controls(_ => { }),
        ["controls-skill"] = () => Controls(d => d.Select("Digit1")),
        ["controls-frames"] = () => Controls(d => d.Select("Space")),
        ["controls-hold"] = () => Controls(d => d.Select("KeyT")),
        ["controls-special"] = () => Controls(d => d.Select("BracketLeft")),
        ["controls-unassigned"] = () => Controls(d => { d.ShowAll = true; d.Refresh(); }),
        ["controls-find"] = () => Controls(d => d.SetFinding(true)),
        ["controls-imported"] = () => Controls(d => d.ImportText("{\"format\":\"lemmings-3d-controls\",\"version\":1,\"keys\":{\"KeyP\":{\"action\":\"pause\",\"mod\":0},\"Nope\":{\"action\":\"pause\"}}}", "mine.json")),
        ["controls-vr"] = () => Controls(d => { d.ShowTab("vr"); d.Select("VrPointA"); }),
        ["controls-func-popup"] = () => Controls(d => { d.Select("Digit1"); d.Paint(); d.Press(new PagePick("func")); }),
        ["hints"] = () =>
        {
            var b = new PageFixture.Setup();
            return Page(new VrKeyHints(b.Hotkeys), _ => { });
        },
        ["keyboard"] = () =>
        {
            var k = new VrKeyboard();
            k.Open("jst nk", "search levels", "search levels…");
            return Page(k, _ => { });
        },
        ["keyboard-numeric"] = () =>
        {
            var k = new VrKeyboard();
            k.Open("170", "controls: frames", "", true);
            return Page(k, _ => { });
        },
        ["replays"] = () =>
        {
            string dir = System.IO.Path.Combine(OS.GetUserDataDir(), "shot-replays");
            System.IO.Directory.CreateDirectory(dir);
            var b = new PageFixture.Replays { Folder = dir };
            foreach (var n in new[] { "Just_Nuke_Them_.nxrp", "Up_For_A_Walk_.nxrp", "Time_Crime.nxrp" })
            {
                string f = System.IO.Path.Combine(dir, n);
                System.IO.File.WriteAllText(f, b.SaveText());
                System.IO.File.SetLastWriteTimeUtc(f, DateTimeOffset.FromUnixTimeMilliseconds(PageFixture.ShotDate).UtcDateTime);
            }
            var page = new VrReplayFiles(b, new PageFixture.Confirms());
            return Page(page, p => { ((VrReplayFiles)p).Refresh(); ((VrReplayFiles)p).Save(); });
        },
        ["library-search"] = () =>
        {
            var lib = new PageFixture.Lib();
            var cat = new VrCatalog();
            var s = new CatalogSearch(cat, lib, lib.Search);
            s.SetQuery("jst nk");
            cat.Root.Visible = true;
            return (cat.Root, cat.Panel);
        },
    };

    // a body y of the setup page, as a scroll that shows it (the progress state's bar)
    static float PageFixtureY(VrPage p, float css) => css * p.S - 200;

    static (Node, Panel3D) Page(VrPage page, Action<VrPage> state)
    {
        page.Opened();
        page.Paint();
        state(page);
        page.Paint();
        page.Root.Visible = true;
        return (page.Root, page.Panel);
    }

    static (Node, Panel3D) Setup(Action<VrSetupPage> state) =>
        Page(new VrSetupPage(PageFixture.ShotSetup(), new PageFixture.Confirms()), p => state((VrSetupPage)p));

    static (Node, Panel3D) Solutions(Action<VrSolutionsPage> state) =>
        Page(new VrSolutionsPage(new PageFixture.Sols()), p => state((VrSolutionsPage)p));

    static (Node, Panel3D) Controls(Action<VrControlsDialog> state)
    {
        var b = new PageFixture.Setup();
        b.Hotkeys.ApplyPreset("traditional");
        b.Hotkeys.ApplyVrPreset();
        return Page(new VrControlsDialog(b.Hotkeys, b.Mem), p => state((VrControlsDialog)p));
    }

    public static Viewport One(Node root, string name)
    {
        var (owner, panel) = States[name]();
        root.AddChild(owner);
        panel.Commit();
        return panel.Target;
    }

    readonly List<(string Name, Panel3D Panel)> _all = new();
    string _dir = "";
    int _frames;

    public static Viewport Batch(Node root)
    {
        var args = OS.GetCmdlineUserArgs().SkipWhile(a => a != "--shot").Skip(1).ToList();
        var batch = new PageShots { _dir = System.IO.Path.GetDirectoryName(args.ElementAtOrDefault(1) ?? "user://x.png") ?? "." };
        Viewport? first = null;
        foreach (var name in States.Keys)
        {
            var (owner, panel) = States[name]();
            root.AddChild(owner);
            panel.Commit();
            batch._all.Add((name, panel));
            first ??= panel.Target;
        }
        root.AddChild(batch);
        return first!;
    }

    public override void _Process(double delta)
    {
        if (++_frames != 4) return;
        foreach (var (name, panel) in _all)
            panel.Target.GetTexture().GetImage().SavePng(System.IO.Path.Combine(_dir, "page-" + name + ".png"));
        GD.Print($"[lemmix] page shots saved: {_all.Count} -> {_dir}");
    }
}
