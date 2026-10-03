using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Lemmix.App.Ui;
using Lemmix.App.Ui.Pages;
using Lemmix.App.Ui.Windows;
using Lemmix.App.Xr;
using Lemmix.Input;
using Lemmix.Setup;
using Lemmix.Store;

namespace Lemmix.App.Test;

// The desktop pages in the headset (app/Ui/Pages): the ray on their parts, and the flows of
// setup.js, solutions.js, hotkeys.js's dialog, refreshKeyHints and library.js's search, with fake
// backends (PageFixture) - the web's texts word for word.
public static class PagesTests
{
    // ---- helpers
    static T InTree<T>(T node) where T : Node { ((SceneTree)Godot.Engine.GetMainLoop()).Root.AddChild(node); return node; }

    static (Vector3, Vector3) AtPixel(Panel3D p, float x, float y)
    {
        var local = new Vector3((x / p.Canvas.Width - 0.5f) * p.WidthMetres, (0.5f - y / p.Canvas.Height) * p.HeightMetres, 0);
        var world = p.GlobalTransform * local;
        return (world + p.GlobalBasis.Z.Normalized() * 0.5f, -p.GlobalBasis.Z.Normalized());
    }

    sealed class Rig : IDisposable
    {
        public readonly PageFixture.PagesHost Host = new();
        public readonly VrPages Pages;
        public Rig() { Pages = new VrPages(Host); InTree(Pages.Root); Pages.PlaceWindows(Transform3D.Identity); }
        public void Dispose() => Pages.Root.Free();

        /** The ray on a region of the page up (by its id), then the trigger. */
        public VrPick Press(string id, VrPage? page = null)
        {
            var p = Aim(id, page);
            Pages.ApplyHover(p);
            Check.True(Pages.Act(p), "the pages took the press on " + id);
            return p;
        }

        public VrPick Aim(string id, VrPage? page = null)
        {
            page ??= Pages.Current!;
            page.Paint();
            if (page is VrControlsDialog dlg && id.StartsWith("key:", StringComparison.Ordinal)) { dlg.RevealCode(id[4..]); page.Paint(); }
            // an option of a long popup list: scrolled to, as the stick would
            if (page.Popup is PagePopup pop && id.StartsWith("popup:", StringComparison.Ordinal))
            {
                int i = int.Parse(id[6..]);
                pop.Scroll = Math.Clamp(i * pop.RowH - pop.Box.Size.Y / 2, 0, pop.MaxScroll);
                page.Paint();
            }
            // a region below the fold: scrolled to, as the stick would
            for (float s = 0; page.RegionRect(id) == null && s <= page.ContentHeight; s += page.View.Size.Y / 2) { page.ScrollTo(s); page.Paint(); }
            var r = page.RegionRect(id) ?? throw new Exception("no region " + id + " on " + page.Name + " (" + string.Join(", ", page.Regions.Select(x => x.Id).Take(40)) + ")");
            var (o, d) = AtPixel(page.Panel, r.GetCenter().X, r.GetCenter().Y);
            var (owned, pick) = Pages.Pick(o, d);
            Check.True(owned, "a page owns the ray");
            Check.True(pick != null, "the ray hits " + id);
            var data = (PagePick)pick!.Data!;
            Check.Equal(id, data.Id, "the pick on " + id);
            return pick;
        }
    }

    static void WaitFor(VrPage page, Func<bool> cond, string what)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!cond())
        {
            if (DateTime.UtcNow > until) throw new Exception("timed out: " + what);
            page.Update();
            System.Threading.Thread.Sleep(5);
        }
    }

    static readonly List<ZipEntryName> EngineZip = new() { new("NeoLemmix.exe", 10), new("gfx/panel/empty_slot.png", 10), new("levels/NeoLemmix_Introduction_Pack/a.nxlv", 10) };
    static readonly List<ZipEntryName> StylesZip = new() { new("styles/styles.ini", 10), new("styles/orig_dirt/a.png", 10) };
    static readonly List<ZipEntryName> PackZip = new() { new("Pack/levels.nxmi", 10), new("Pack/a.nxlv", 10) };

    static (PageFixture.Setup B, PageFixture.Confirms C, VrSetupPage P) SetupPage(Rig rig, PageFixture.Setup? b = null)
    {
        b ??= PageFixture.ShotSetup();
        var c = new PageFixture.Confirms();
        var p = rig.Pages.Add(new VrSetupPage(b, c));
        rig.Pages.Show(p);
        return (b, c, p);
    }

    // ---- the ray: who owns it, the clock held
    [AppTest]
    public static void PagesOwnTheRayAndHoldTheClock()
    {
        using var rig = new Rig();
        var (_, _, setup) = SetupPage(rig);
        Check.True(setup.Root.Visible && setup.Close.Visible, "the page is up with its close");
        Check.True(rig.Host.Held.Contains("page-setup"), "the clock is held while it is up");
        // a ray past the page is still the page's (nothing behind may be hit)
        var (owned, pick) = rig.Pages.Pick(new Vector3(5, 5, 0), new Vector3(0, 0, -1));
        Check.True(owned && pick == null, "a miss is owned while a page is up");
        // the close
        var cp = rig.Pages.Pick(setup.Close.GlobalPosition + new Vector3(0, 0, 0.5f), new Vector3(0, 0, -1));
        Check.Equal("pageclose", cp.Pick?.BarTool, "the close button");
        rig.Pages.ApplyHover(cp.Pick);
        Check.True(setup.Close.State.Hovered, "the close lights under the beam");
        rig.Pages.Act(cp.Pick!);
        Check.True(!setup.Root.Visible && rig.Pages.Current == null, "closed");
        Check.True(!rig.Host.Held.Contains("page-setup"), "the clock is released");
        Check.True(!rig.Pages.Pick(new Vector3(0, 0, 0), new Vector3(0, 0, -1)).Owned, "no page: the ray goes on");
        // not presenting: nothing opens
        rig.Host.Presenting = false;
        rig.Pages.Show(setup);
        Check.True(rig.Pages.Current == null, "the desktop shows no page");
    }

    [AppTest]
    public static void ScrollbarAndStickScrollThePage()
    {
        using var rig = new Rig();
        var (_, _, p) = SetupPage(rig);
        p.Paint();
        Check.True(p.ContentHeight > p.View.Size.Y, "the setup page is taller than its window");
        rig.Pages.OnStick(1, 0.5);       // down on the stick: up the page (as the catalog)
        Check.Equal(0f, p.Scroll, "at the top already");
        rig.Pages.OnStick(-1, 0.5);
        Check.True(p.Scroll > 0, "the stick scrolls down");
        var bar = p.Bar();
        var (o, d) = AtPixel(p.Panel, bar.X + bar.W / 2, bar.Y + bar.H - 2);
        var pick = rig.Pages.Pick(o, d).Pick!;
        Check.True(pick.ScrollBar, "the scrollbar is picked");
        rig.Pages.Act(pick);
        Check.True(Math.Abs(p.Scroll - bar.Max) < 1, "a press at its foot goes to the end");
    }

    // ---- setup: installs
    [AppTest]
    public static void SetupInstallAsksToReplaceThenUnpacksOffTheFrame()
    {
        using var rig = new Rig();
        var (b, c, p) = SetupPage(rig);
        string zip = "/data/import/NeoLemmix_V12.14.0.zip";
        b.Mem.Put("/data/import", "NeoLemmix_V12.14.0.zip", "", 7_043_210);
        b.Zips[zip] = EngineZip;
        b.PlanOf = path => new InstallPlan("engine", "NeoLemmix", new UnitInfo { Id = "engine", Files = 1489 }, new() { "NeoLemmix_Introduction_Pack" }, new() { "NeoLemmix_Introduction_Pack" });
        // a zip in the import folder (the page has no zip button: a download lands there, as does a
        // zip a computer uploads)
        p.InstallZip(zip, "engine");
        Check.Equal("Replace NeoLemmix?", c.Last.Title, "replacing asks first");
        Check.Equal("replace", c.Last.Verb, "its verb");
        Check.Equal("The 1489 files installed on " + PageText.Date(PageFixture.ShotDate) + " on the headset are removed first.", c.Last.Body, "its body");
        c.Last.Yes();
        Check.Equal("Replace the level directory NeoLemmix_Introduction_Pack?", c.Last.Title, "the pack the zip carries clashes");
        Check.Equal("NeoLemmix_Introduction_Pack already installed: removed first.", c.Last.Body, "its body");
        b.InstallGate = new System.Threading.ManualResetEventSlim(false);
        c.Last.Yes();
        Check.True(p.Busy, "busy while it unpacks");
        WaitFor(p, () => p.ProgressLabel != null && p.ProgressLabel.StartsWith("unpacking NeoLemmix_V12.14.0.zip — ", StringComparison.Ordinal), "the progress label");
        Check.True(p.ProgressFrac is > 0 and < 1, "the bar fills: " + p.ProgressFrac);
        Check.True(p.ProgressLabel!.EndsWith(" of 7.0 MB, 2 files", StringComparison.Ordinal), "the web's label: " + p.ProgressLabel);
        // the busy buttons do nothing meanwhile
        p.Paint();
        Check.True(p.Regions.Any(r => r.Id == "get-styles" && !r.Enabled), "the install buttons are off while busy");
        b.InstallGate.Set();
        p.WaitIdle();
        Check.Equal("NeoLemmix installed on the headset: 1489 files, 7.0 MB", p.Messages["nx"].Text, "the done message");
        Check.True(!p.Messages["nx"].Bad && !p.Busy && p.ProgressLabel == null, "the bar is gone");
        Check.Equal("install NeoLemmix_V12.14.0.zip as engine", b.Log[0], "installed from the zip");
    }

    [AppTest]
    public static void SetupInstallComplaints()
    {
        using var rig = new Rig();
        var (b, c, p) = SetupPage(rig);
        p.InstallZip("/data/import/broken.zip", "engine");
        Check.Equal("broken.zip: not a zip file (no central directory)", p.Messages["nx"].Text, "not a zip");
        Check.True(p.Messages["nx"].Bad, "in red");
        b.Zips["/data/import/notes.zip"] = new() { new("readme.txt", 3) };
        p.InstallZip("/data/import/notes.zip", "levels");
        Check.Equal("notes.zip does not look like NeoLemmix, its styles package or a level pack", p.Messages["levels"].Text, "what it is not");
        // a level pack picked on the NeoLemmix row: asks, then installs it as that
        b.Zips["/data/import/Pack.zip"] = PackZip;
        b.PlanOf = _ => new InstallPlan("levels", "a level pack", null, new(), new() { "Pack" });
        p.InstallZip("/data/import/Pack.zip", "engine");
        Check.Equal("Pack.zip is a level pack", c.Last.Title, "the kind question");
        Check.Equal("install it as that", c.Last.Verb, "its verb");
        Check.Equal("It was picked on NeoLemmix.", c.Last.Body, "its body");
        c.Last.Yes();
        p.WaitIdle();
        Check.Equal("Pack installed on the headset: 31 files, 2.0 MB", p.Messages["levels"].Text, "the pack's message");
        // a failure on the way
        b.Zips["/data/import/Styles.zip"] = StylesZip;
        b.Units.Remove("styles");
        b.PlanOf = _ => new InstallPlan("styles", "the styles package", null, new(), new());
        b.InstallFails = new System.IO.IOException("disk full");
        p.Refresh();
        p.InstallZip("/data/import/Styles.zip", "styles");
        p.WaitIdle();
        Check.Equal("install failed: disk full", p.Messages["nx"].Text, "the failure");
        Check.True(p.Messages["nx"].Bad && !p.Busy, "red, and the buttons back");
    }

    [AppTest]
    public static void SetupDownloadsThenInstalls()
    {
        using var rig = new Rig();
        var (b, c, p) = SetupPage(rig);
        b.DownloadTo = "/data/import/styles.zip";
        b.Zips[b.DownloadTo] = StylesZip;
        b.Mem.Put("/data/import", "styles.zip", "", 91_000_000);
        b.PlanOf = _ => new InstallPlan("styles", "the styles package", new UnitInfo { Id = "styles", Files = 9817 }, new(), new());
        Check.Equal("Get Style Packages", VrSetupPage.GetLabel["styles"], "the button's label");
        p.Paint();
        Check.True(!p.Regions.Any(r => r.Id.StartsWith("zip-", StringComparison.Ordinal)), "no zip buttons");
        rig.Press("get-styles");
        p.WaitIdle();
        Check.Equal("download styles", b.Log[0], "downloaded from neolemmix.com");
        Check.Equal("Replace the styles package?", c.Last.Title, "then installed, asking to replace");
        b.DownloadFails = new System.Net.Http.HttpRequestException("503");
        rig.Press("get-engine");
        p.WaitIdle();
        Check.Equal("download failed: 503", p.Messages["nx"].Text, "a failed download");
    }

    [AppTest]
    public static void SetupDeletesAPack()
    {
        using var rig = new Rig();
        var (b, c, p) = SetupPage(rig);
        rig.Press("del:Lemmings_Redux");
        Check.Equal("Delete Lemmings_Redux?", c.Last.Title, "asks first");
        Check.Equal("delete", c.Last.Verb, "its verb");
        Check.Equal("Its levels leave this headset's storage; your progress on them stays.", c.Last.Body, "its body");
        Check.Equal(3, p.LevelDirs.Count, "nothing gone before the yes");
        c.Last.Yes();
        p.WaitIdle();
        Check.Equal("Lemmings_Redux deleted from the headset", p.Messages["levels"].Text, "the message");
        Check.Equal(2, p.LevelDirs.Count, "the list without it");
    }

    [AppTest]
    public static void SetupConfigFiles()
    {
        using var rig = new Rig();
        var (b, _, p) = SetupPage(rig);
        b.Prefs.SetItem(ConfigFiles.ClearedKey, "{\"a\":{\"best\":50,\"clears\":1}}");
        p.ScrollTo(1e6f);
        rig.Press("dl-progress");
        Check.Equal("saved as /data/export/lemmings-3d-progress.json", p.Messages["progress"].Text, "exported");
        Check.True(b.Mem.Texts["/data/export/lemmings-3d-progress.json"].Contains("\"best\": 50"), "the web's text");
        // progress import merges: the best of both
        b.Mem.Put("/data/import", "progress.json", "{\"format\":\"lemmings-3d-progress\",\"version\":1,\"cleared\":{\"a\":{\"best\":40,\"clears\":3,\"saved\":7},\"b\":{\"best\":10,\"clears\":1}},\"talismans\":{}}");
        rig.Press("ul-progress");
        Check.True(p.Popup != null, "the import folder's files");
        int i = p.Popup!.Options.FindIndex(o => o.Value == "/data/import/progress.json");
        rig.Press("popup:" + i);
        Check.Equal("progress.json: 2 levels merged", p.Messages["progress"].Text, "the merge message");
        var cleared = JsJson.Parse(b.Prefs.GetItem(ConfigFiles.ClearedKey)!);
        Check.Equal(40.0, Js.Get(Js.Get(cleared, "a"), "best"), "the better time");
        Check.Equal(3.0, Js.Get(Js.Get(cleared, "a"), "clears"), "the more clears");
        // a file that is not JSON
        b.Mem.Put("/data/import", "notes.json", "hello");
        p.ImportFile("prefs", "/data/import/notes.json");
        Check.Equal("notes.json: notes.json is not a JSON file", p.Messages["prefs"].Text, "the complaint");
        Check.True(p.Messages["prefs"].Bad, "in red");
        // controls into the live table
        b.Mem.Put("/data/import", "mine.json", "{\"format\":\"lemmings-3d-controls\",\"version\":1,\"keys\":{\"KeyP\":{\"action\":\"pause\",\"mod\":0}}}");
        p.ImportFile("controls", "/data/import/mine.json");
        Check.Equal("mine.json: 1 bindings loaded, VR left at the default", p.Messages["controls"].Text, "the controls message");
        Check.Equal("pause", b.Hotkeys.Get("KeyP")?.Action, "the table took it");
    }

    [AppTest]
    public static void SetupPlayAndWhatIsMissing()
    {
        using var rig = new Rig();
        var empty = new PageFixture.Setup { Levels = false };
        var (b, _, p) = SetupPage(rig, empty);
        Check.Equal("to play, install NeoLemmix, the styles package and a level pack below", p.PlayWhy(), "nothing installed");
        p.Paint();
        Check.True(p.RegionRect("play") == null, "no PLAY yet");
        Check.Equal("not installed", VrSetupPage.UnitState(p.Engine), "the row's state");
        b.Units["engine"] = new SetupUnit(1489, 7_000_000, PageFixture.ShotDate, "V12.14.0");
        b.Units["styles"] = new SetupUnit(10, 1000, null, "");
        p.Refresh();
        Check.Equal("to play, install a level pack below", p.PlayWhy(), "one thing missing");
        Check.Equal("installed (V12.14.0): 1489 files, 7.0 MB, " + PageText.Date(PageFixture.ShotDate), VrSetupPage.UnitState(p.Engine), "installed");
        b.Levels = true;
        p.Refresh();
        Check.Equal("", p.PlayWhy(), "all there");
        rig.Press("play");
        Check.Equal("play", b.Log[^1], "PLAY goes to the library");
        Check.Equal("storage used by this app: 4 KB of 10.74 GB available", p.StorageText(), "the storage line");
    }

    [AppTest]
    public static void ConfirmsPaintOnTheWindowsModal()
    {
        var root = InTree(new Node3D());
        try
        {
            var w = new VrWindows(new WinHost(), new List<SettingRow>(), new Node3D(), root);
            root.AddChild(w.WindowRoot);
            bool yes = false;
            new WindowsPageConfirm(w).Ask("Delete Lemmings_Redux?", "delete", "Its levels leave this headset's storage; your progress on them stays.", () => yes = true);
            Check.True(w.Modal.Root.Visible, "the question is up");
            Check.Equal("Delete Lemmings_Redux?", w.Modal.Title, "its title");
            Check.True(w.Modal.Body.StartsWith("Its levels", StringComparison.Ordinal), "its body");
            w.Act(new VrPick("bar", BarTool: "yes"));
            Check.True(yes && !w.Modal.Root.Visible, "yes runs it");
        }
        finally { root.Free(); }
    }

    sealed class WinHost : IVrWindowsHost
    {
        public bool Presenting => true; public bool HasSession => true; public Transform3D? HeadPose => Transform3D.Identity;
        public void HoldSim(string who) { } public void ReleaseSim(string who) { }
        public bool GameRunning => true; public bool AudioEnabled => true; public float Volume => 1;
        public void SetVolume(float v) { } public void ToggleMute() { } public void TogglePause() { } public void MoveLevel(int d) { }
        public bool CanWatchSolution => false; public void WatchSolution() { } public void EnterLevel(string id) { }
    }

    // ---- solutions
    [AppTest]
    public static void SolutionsListsFiltersAndPlays()
    {
        using var rig = new Rig();
        var b = new PageFixture.Sols();
        var p = rig.Pages.Add(new VrSolutionsPage(b));
        rig.Pages.Show(p);
        // the head: the web's summary, word for word
        string web = PageFixture.F["solutions"]!["summary"]!.GetValue<string>();
        Check.True(web.StartsWith("85 of 1076 levels have a solution (7.9%)", StringComparison.Ordinal), "the web's summary: " + web);
        Check.True(p.SummaryText().StartsWith("85 of 1076 levels have a solution (7.9%)", StringComparison.Ordinal), "the summary: " + p.SummaryText());
        Check.True(p.SummaryText().Contains("3 level folders, 12 packs"), "folders and packs: " + p.SummaryText());
        Check.Equal(1076, p.Shown.Count, "every level shown");
        Check.Equal("1076 shown", PageFixture.F["solutions"]!["note"]!.GetValue<string>(), "as the web");
        // the search on the keyboard
        rig.Press("search");
        Check.True(rig.Pages.Keyboard.Root.Visible && p.FocusField == "search", "the keyboard opens on the field");
        foreach (var k in new[] { "j", "s", "t", "space", "n", "k" }) rig.Press("k:" + k, rig.Pages.Keyboard);
        Check.Equal("jst nk", p.Query, "typed");
        Check.Equal(32, p.Shown.Count, "the web's 32 matches");
        Check.Equal("LemmingsPlus_All_20201114/Lemmings_Plus_I/Wimpy/Just_When_You_Think_You_Know!.nxlv".Split('/')[0], p.Shown[0].L.Id.Split('/')[0], "best first, as the web: " + p.Shown[0].L.Id);
        Check.Equal("Just Digging Into NeoLemmix", p.Shown[1].L.Title, "the second, as the web");
        rig.Pages.Keyboard.Escape();
        Check.Equal("", p.Query, "Escape clears");
        Check.Equal(1076, p.Shown.Count, "all again");
        rig.Pages.CloseKeyboard();
        // show: solved, not found
        rig.Press("show");
        rig.Press("popup:1");
        Check.Equal("solved", p.Show, "show solved");
        Check.Equal(85, p.Shown.Count, "85 solved");
        p.Show = "all";
        // pack filter
        rig.Press("pack");
        int at = p.Popup!.Options.FindIndex(o => o.Value == "folder:Lemmings Redux");
        rig.Press("popup:" + at);
        Check.Equal(160, p.Shown.Count, "Lemmings Redux's 160");
        p.Pack = "";
        p.Render();
        // sort by skills: the fewest first, unsolved last
        rig.Press("sort:skills");
        Check.True(p.Shown[0].D.Solved && p.Shown[0].D.Rec!.SkillsUsed == 0, "the fewest skills first");
        Check.True(!p.Shown[^1].D.Solved, "unsolved at the end");
        rig.Press("sort:skills");
        Check.Equal(-1, p.SortDir, "a second press turns it round");
        // play
        p.SortKey = "level"; p.SortDir = 1; p.Render(); p.Scroll = 0;
        var first = p.Shown[0].L.Id;
        rig.Press("sol:" + first);
        rig.Press("play:" + first);
        Check.Equal("solution " + first, b.Log[0], "▶ play solution");
        Check.Equal("play " + first, b.Log[1], "play level");
        rig.Press("back");
        Check.Equal("back", b.Log[2], "back to the game");
        Check.Equal("1:08", VrSolutionsPage.MmSs(1166), "m:ss of frames");
        Check.Equal("1m 21s", VrSolutionsPage.Human(81.4), "human");
    }

    // ---- controls
    static (HotkeyManager M, PageFixture.MemFiles F, VrControlsDialog D) Controls(Rig rig)
    {
        var b = new PageFixture.Setup();
        b.Hotkeys.ApplyPreset("traditional");
        b.Hotkeys.ApplyVrPreset();
        var d = rig.Pages.Add(new VrControlsDialog(b.Hotkeys, b.Mem));
        rig.Pages.Show(d);
        return (b.Hotkeys, b.Mem, d);
    }

    [AppTest]
    public static void ControlsSelectAndEdit()
    {
        using var rig = new Rig();
        var (m, _, d) = Controls(rig);
        Check.Equal("click a key in the list", d.EditingText(), "nothing selected");
        rig.Press("key:Digit1");
        Check.Equal("Digit1", d.Selected, "a row selects its key");
        Check.Equal("editing: 1", d.EditingText(), "the editor");
        Check.Equal("skill", d.DetailKind(), "a skill key shows the skill");
        rig.Press("skill");
        rig.Press("popup:" + d.Popup!.Options.FindIndex(o => o.Value == "builder"));
        Check.Equal("Select Skill: Builder", Hotkeys.Describe(m.Get("Digit1")), "the skill changed");
        // the function: Time Skip starts at its default detail
        rig.Press("func");
        Check.True(d.Popup!.Options.All(o => o.Value == "" || Hotkeys.AllowedOn("Digit1", o.Value)), "only what the key can take");
        rig.Press("popup:" + d.Popup!.Options.FindIndex(o => o.Value == "skip"));
        Check.Equal("Time Skip: Forward 1 Frame", Hotkeys.Describe(m.Get("Digit1")), "a function just chosen: its default");
        rig.Press("frames");
        Check.True(rig.Pages.Keyboard.Root.Visible && rig.Pages.Keyboard.Numeric, "the frames on the number keyboard");
        rig.Pages.Keyboard.Escape();   // clears
        foreach (var k in new[] { "-", "1", "7" }) rig.Press("k:" + k, rig.Pages.Keyboard);
        Check.Equal("Time Skip: Back 17 Frames", Hotkeys.Describe(m.Get("Digit1")), "typed frames apply as typed");
        Check.Equal("lemmix", Hotkeys.TagOf(m.Get("Digit1")), "back is NeoLemmix's");
        rig.Press("k:↵", rig.Pages.Keyboard);
        Check.True(!rig.Pages.Keyboard.Root.Visible, "Enter closes the keyboard");
        // hold
        rig.Press("key:KeyT");
        Check.Equal("Clear Physics Mode (hold)", Hotkeys.Describe(m.Get("KeyT")), "the traditional T holds");
        rig.Press("hold");
        Check.Equal("Clear Physics Mode (toggle)", Hotkeys.Describe(m.Get("KeyT")), "the checkbox");
        // special
        rig.Press("key:BracketLeft");
        rig.Press("special");
        rig.Press("popup:1");
        Check.Equal("Skip to Next Shrugger", Hotkeys.Describe(m.Get("BracketLeft")), "the special skip");
        // clear a key
        rig.Press("func");
        rig.Press("popup:0");
        Check.True(m.Get("BracketLeft") == null && !d.Codes.Contains("BracketLeft"), "(none) clears it, and it leaves the list");
    }

    [AppTest]
    public static void ControlsListsFindsPresetsAndFiles()
    {
        using var rig = new Rig();
        var (m, f, d) = Controls(rig);
        Check.True(!d.Codes.Contains("Shift"), "an unbound key is not listed");
        rig.Press("unassigned");
        Check.True(d.ShowAll && d.Codes.Count == Hotkeys.Keys.Length, "show unassigned keys: all " + Hotkeys.Keys.Length);
        rig.Press("unassigned");
        // find key: a Bluetooth keyboard's key
        rig.Press("find");
        Check.True(d.Finding, "waiting for a key");
        rig.Pages.OnKey("ShiftLeft", null);
        Check.True(!d.Finding && d.Selected == "Shift" && d.ShowAll, "the key pressed, listed though unbound");
        // find key: a controller button
        rig.Press("find");
        Check.True(rig.Pages.OnVrButton("VrFreeStickClick"), "the dialog takes the button");
        Check.Equal("vr", d.Tab, "the VR tab");
        Check.Equal("VrFreeStickClick", d.Selected, "that input selected");
        Check.Equal(8, d.Codes.Count, "the headset's inputs, always listed");
        // the VR tab's reset
        m.Set("VrPointA", "pause");
        rig.Press("vr-reset");
        Check.Equal("recenter_vr", m.Get("VrPointA")?.Action, "back to the default controls");
        // presets
        rig.Press("tab:keyboard");
        d.ShowAll = false;
        rig.Press("preset:minimal");
        Check.Equal(5, d.Codes.Count, "the minimal layout's 5 keys");
        Check.Equal("recenter_vr", m.Get("VrPointA")?.Action, "the controllers kept");
        rig.Press("preset:traditional");
        // export, import
        rig.Press("export");
        Check.Equal(m.ExportJSON(), f.Texts["/data/export/lemmings-3d-controls.json"], "the exported file is the table's");
        Check.Equal("exported as /data/export/lemmings-3d-controls.json", d.StatusText, "the status line");
        f.Put("/data/import", "mine.json", "{\"format\":\"lemmings-3d-controls\",\"version\":1,\"keys\":{\"KeyP\":{\"action\":\"pause\",\"mod\":0},\"Nope\":{\"action\":\"pause\"}}}");
        rig.Press("import");
        rig.Press("popup:0");
        Check.Equal("mine.json: 1 binding loaded, 1 skipped (unknown key or function), VR left at the default", d.StatusText, "the web's import line");
        Check.True(!d.StatusBad && d.Codes.SequenceEqual(new[] { "KeyP" }), "the table replaced");
        f.Put("/data/import", "mine.json", "[1,2]");
        d.ImportText("[1,2]", "mine.json");
        Check.True(d.StatusBad && d.StatusText.StartsWith("mine.json: not a controls file", StringComparison.Ordinal), "a complaint: " + d.StatusText);
        // close saves and closes; Escape too
        bool closed = false;
        var was = d.Closed;
        d.Closed = () => { closed = true; was?.Invoke(); };
        rig.Press("done");
        Check.True(closed && rig.Pages.Current == null, "close");
    }

    // ---- key hints
    [AppTest]
    public static void KeyHintsFollowTheTable()
    {
        var store = new LocalStore();
        var m = new HotkeyManager(store);
        string web = PageFixture.F["texts"]!["hints"]!.GetValue<string>();
        var lines = VrKeyHints.Lines(m);
        Check.Equal(web.Replace("\r", ""), string.Join("\n", lines), "the drawer's text, as the web's");
        m.Set("KeyP", null);
        Check.True(!VrKeyHints.Lines(m)[2].Contains("P pause") && VrKeyHints.Lines(m)[2].StartsWith("Middle-Click pause", StringComparison.Ordinal) == false, "an unbound key leaves the line");
        Check.True(VrKeyHints.Lines(m)[2].StartsWith("F11 pause", StringComparison.Ordinal) || VrKeyHints.Lines(m)[2].Contains("pause"), "the next key for pause: " + VrKeyHints.Lines(m)[2]);
        var titles = VrKeyHints.ButtonTitles(new HotkeyManager(new LocalStore()));
        Check.Equal("pause / resume (P)", titles["pause"], "a button's title");
        Check.Equal("world library (Esc)", titles["library"], "another");
        Check.Equal("previous level", titles["prev"], "no key: no brackets");
    }

    // ---- the keyboard and the library's search
    [AppTest]
    public static void KeyboardTypesAndTheLibrarySearches()
    {
        using var rig = new Rig();
        var lib = new PageFixture.Lib();
        var cat = new VrCatalog();
        InTree(cat.Root);
        try
        {
            var search = new CatalogSearch(cat, lib, lib.Search);
            string? entered = null;
            bool closedLib = false;
            rig.Pages.OpenLibrarySearch(search, id => entered = id, () => closedLib = true);
            Check.True(rig.Pages.Keyboard.Root.Visible && rig.Pages.AnyUp, "the keyboard is up");
            Check.True(rig.Pages.Pick(new Vector3(9, 9, 0), new Vector3(0, 0, -1)).Owned, "it owns the ray");
            foreach (var k in new[] { "j", "s", "x" }) rig.Press("k:" + k, rig.Pages.Keyboard);
            rig.Press("k:⌫", rig.Pages.Keyboard);
            foreach (var k in new[] { "t", "space", "n", "k" }) rig.Press("k:" + k, rig.Pages.Keyboard);
            Check.Equal("jst nk", search.Query, "typed, a letter taken back");
            Check.True(cat.Heading.StartsWith("matching “jst nk” · ", StringComparison.Ordinal), "the catalog lists the matches: " + cat.Heading);
            Check.True(cat.Items.Count > 0 && cat.Items.All(it => it.Kind == "level"), "level tiles");
            // a Bluetooth keyboard types too
            rig.Pages.OnKey("KeyA", "a");
            Check.Equal("jst nka", search.Query, "the physical keyboard");
            rig.Pages.OnKey("Backspace", null);
            // Enter: the first match plays
            string first = cat.Items[0].LevelId!;
            rig.Press("k:↵", rig.Pages.Keyboard);
            Check.Equal(first, entered, "Enter plays the first match");
            Check.True(!rig.Pages.Keyboard.Root.Visible, "and the keyboard goes");
            // Escape: clears, then closes the library
            rig.Pages.OpenLibrarySearch(search, id => entered = id, () => closedLib = true);
            rig.Pages.OnKey("Escape", null);
            Check.Equal("", search.Query, "Escape clears the query");
            Check.True(cat.Heading == "levels" && !closedLib, "the directory back");
            rig.Pages.OnKey("Escape", null);
            Check.True(closedLib && !rig.Pages.Keyboard.Root.Visible, "a second Escape closes the library");
            // no match
            search.SetQuery("zzzzqqq");
            Check.Equal("no level matches", cat.Note, "nothing found");
            Check.True(search.FirstMatch() == null, "nothing to play");
        }
        finally { cat.Root.Free(); }
    }

    // ---- replay files
    [AppTest]
    public static void ReplayFilesSaveAndLoad()
    {
        using var rig = new Rig();
        string dir = System.IO.Path.Combine(OS.GetUserDataDir(), "test-replays-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var b = new PageFixture.Replays { Folder = dir };
            var c = new PageFixture.Confirms();
            var p = rig.Pages.Add(new VrReplayFiles(b, c));
            rig.Pages.Show(p);
            Check.Equal(0, p.Files.Count, "an empty folder");
            rig.Press("save");
            Check.Equal("saved as Just_Nuke_Them_.nxrp", p.Message, "the web's file name");
            rig.Press("save");
            Check.Equal("saved as Just_Nuke_Them_ (1).nxrp", p.Message, "a name taken gets a number");
            Check.Equal(2, p.Files.Count, "both listed");
            rig.Press("load:" + System.IO.Path.Combine(dir, "Just_Nuke_Them_.nxrp"));
            Check.Equal("Just_Nuke_Them_.nxrp x7A3F00D2C9E1B044", b.Loaded[0], "loaded");
            Check.True(rig.Pages.Current == null, "the window goes once it plays");
            // another level's replay asks first
            b.LevelReplayId = "x0000000000000001";
            rig.Pages.Show(p);
            rig.Press("load:" + System.IO.Path.Combine(dir, "Just_Nuke_Them_.nxrp"));
            Check.Equal("Replay from another level?", c.Last.Title, "asks");
            Check.Equal("load it anyway", c.Last.Verb, "its verb");
            Check.Equal(1, b.Loaded.Count, "not before the yes");
            c.Last.Yes();
            Check.Equal(2, b.Loaded.Count, "loaded on yes");
            b.CanSave = false;
            rig.Pages.Show(p);
            p.Paint();
            Check.True(p.Regions.Any(r => r.Id == "save" && !r.Enabled), "no level: no save");
            Check.Equal("Just_Nuke_Them_.nxrp", VrReplayFiles.FileName(" Just Nuke Them! ", _ => false), "the name rule");
            Check.Equal("level.nxrp", VrReplayFiles.FileName("", _ => false), "no name");
        }
        finally { if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, true); }
    }
}
