using System;
using System.Collections.Generic;
using Godot;
using Lemmix.App.Ui;
using Lemmix.App.Ui.Pages;
using Lemmix.App.Ui.Windows;
using Lemmix.App.Xr;
using Lemmix.Engine;
using static Lemmix.App.Xr.VrManager;

namespace Lemmix.App.Shell;

// The desktop pages in the headset (app/Ui/Pages) as the shell hosts them: built over the live
// store, controls table, asset root and user data folders; one up at a time (VrPages), owning the
// ray, the sticks, the keys and the buttons after the question (VrModal); where each is opened
// from - the catalog's row (search, setup, solutions), the settings' corner (controls, key hints,
// the QA checklist), the Load/Save Replay functions; nothing installed, the setup page first.
public sealed partial class App
{
    public VrSetupPage SetupPage { get; private set; } = null!;
    public VrSolutionsPage SolutionsPage { get; private set; } = null!;
    public VrControlsDialog ControlsPage { get; private set; } = null!;
    public VrKeyHints HintsPage { get; private set; } = null!;
    public VrReplayFiles ReplaysPage { get; private set; } = null!;
    public VrQaChecklist QaPage { get; private set; } = null!;
    public CatalogSearch Search { get; private set; } = null!;
    public PageFiles Files { get; private set; } = null!;
    public IPageConfirm Confirm { get; private set; } = null!;

    public static string AppVersion => ProjectSettings.GetSetting("application/config/version", "dev").AsString();

    // the entries the windows did not have: three in a row under the catalog, and three in the
    // settings' corner, left of its close
    public static readonly string[] CatalogEntries = { "catsearch", "catsolutions", "catsetup" };
    public static readonly string[] SettingsEntries = { "setqa", "sethints", "setcontrols" };
    readonly List<IconButton> _catalogExtras = new(), _settingsExtras = new();
    // a window a page was opened over: closed for it (the page stands behind the windows' plane), back after
    bool _catalogBehindPage, _settingsBehindPage;

    void BuildPages()
    {
        Pages = new VrPages(this);
        AddChild(Pages.Root);
        Files = new PageFiles(UserDataDir);
        Confirm = new WindowsPageConfirm(Windows);
        SetupPage = Pages.Add(new VrSetupPage(new SetupBackend(AssetRoot, Files, Store, Hotkeys, AppVersion, SetupPlay), Confirm));
        ControlsPage = Pages.Add(new VrControlsDialog(Hotkeys, Files));
        var controlsClosed = ControlsPage.Closed;
        ControlsPage.Closed = () => { controlsClosed?.Invoke(); RefreshKeyHints(); };
        HintsPage = Pages.Add(new VrKeyHints(Hotkeys));
        ReplaysPage = Pages.Add(new VrReplayFiles(new ReplayBackend(this), Confirm));
        QaPage = Pages.Add(new VrQaChecklist { AppVersion = AppVersion });
        RebuildLibraryPages();
        // the hints name the keys as they are bound now
        Hotkeys.OnChange = RefreshKeyHints;

        foreach (var name in CatalogEntries)
        {
            var b = new IconButton(name, EntryIcons.ByName(name), IconButton.GUI_ORDER_MODAL_BTN);
            Windows.Catalog.Root.AddChild(b);
            Windows.IconButtons.Add(b);
            _catalogExtras.Add(b);
        }
        foreach (var name in SettingsEntries)
        {
            var b = new IconButton(name, EntryIcons.ByName(name), IconButton.GUI_ORDER_MODAL_BTN);
            Windows.Settings.Root.AddChild(b);
            Windows.IconButtons.Add(b);
            _settingsExtras.Add(b);
        }
        var texts = Windows.Tooltip.Texts;
        Windows.Tooltip.Texts = n => EntryTip(n) ?? texts(n);
    }

    static string? EntryTip(string name) => name switch
    {
        "catsearch" => "search the levels",
        "catsolutions" => "the stored solutions",
        "catsetup" => "setup: install NeoLemmix, styles and levels",
        "setcontrols" => "configure controls",
        "sethints" => "the keys",
        "setqa" => "QA checklist",
        _ => null,
    };

    // the solutions list and the search read the tree: built again with it
    void RebuildLibraryPages()
    {
        if (SolutionsPage != null)
        {
            if (Pages.Current == SolutionsPage) Pages.Show(null);
            Pages.All.Remove(SolutionsPage);
            SolutionsPage.Root.GetParent()?.RemoveChild(SolutionsPage.Root);
            SolutionsPage.Root.QueueFree();
        }
        string? index = Godot.FileAccess.FileExists(SolutionsRoot + "solutions/index.json")
            ? Godot.FileAccess.GetFileAsString(SolutionsRoot + "solutions/index.json") : null;
        SolutionsPage = Pages.Add(new VrSolutionsPage(new SolutionsBackend(Tree, index, PlayFromPage, () => Pages.Show(null))));
        Search = new CatalogSearch(Windows.Catalog, Catalog, CatalogSearch.Over(Tree));
    }

    // a level chosen on a page (the solutions list's play / watch, the search's Enter)
    void PlayFromPage(string levelId, bool solution)
    {
        Pages.CloseKeyboard();
        Pages.Show(null);
        _catalogBehindPage = _settingsBehindPage = false;
        Windows.SetCatalog(false);
        Windows.SetSettings(false);
        EnterLevel(levelId, solution);
    }

    // ------------------------------------------------------------ opening them
    /** A page up: the window it was opened from steps aside until it goes. */
    void ShowPage(VrPage page)
    {
        if (!Presenting) return;
        if (Windows.Catalog.Root.Visible) { _catalogBehindPage = true; Windows.SetCatalog(false); }
        if (Windows.Settings.Root.Visible) { _settingsBehindPage = true; Windows.SetSettings(false); }
        Pages.Show(page);
    }

    public void OpenSetup() => ShowPage(SetupPage);
    public void OpenSolutions() => ShowPage(SolutionsPage);
    public void OpenControls() { ReleaseHeldKeys(); ControlsPage.Open(); ShowPage(ControlsPage); }
    public void OpenKeyHints() => ShowPage(HintsPage);
    public void OpenReplayFiles() => ShowPage(ReplaysPage);
    public void OpenQaChecklist() => ShowPage(QaPage);

    /** library.js's search field: the keyboard types the query, the catalog lists the matches. */
    public void OpenLibrarySearch()
    {
        if (!Windows.Catalog.Root.Visible) Windows.SetCatalog(true);
        Pages.OpenLibrarySearch(Search, id => PlayFromPage(id, false), () => { if (!Locked) Windows.SetCatalog(false); });
    }

    public void RefreshKeyHints() { if (HintsPage.Root.Visible) HintsPage.Paint(); }

    // the setup page's PLAY (and its close): the indexes read again; something to play, the catalog
    bool _setupPlayed;
    void SetupPlay()
    {
        Pages.Show(null);
        _setupPlayed = true;
        AfterSetup();
    }

    void AfterSetup()
    {
        ReloadLibrary();
        if (FirstRun) { Pages.Show(SetupPage); return; } // nothing to play yet: setup is all there is
        _catalogBehindPage = false;
        if (LevelId == null) Windows.SetCatalog(true);
    }

    // per frame: the window a page stood in front of comes back when the pages are gone; the extra
    // entries laid out in their windows
    VrPage? _lastPage;
    void PagesFrame()
    {
        // the setup page gone (its close, Escape): what it installed read in
        if (_lastPage == SetupPage && Pages.Current != SetupPage && !_setupPlayed) AfterSetup();
        _setupPlayed = false;
        _lastPage = Pages.Current;
        if (!Pages.AnyUp)
        {
            if (_catalogBehindPage) { _catalogBehindPage = false; Windows.SetCatalog(true); }
            if (_settingsBehindPage) { _settingsBehindPage = false; Windows.SetSettings(true); }
        }
        bool cat = Windows.Catalog.Root.Visible;
        for (int i = 0; i < _catalogExtras.Count; i++)
        {
            var b = _catalogExtras[i];
            b.Visible = cat;
            if (!cat) continue;
            // in a row under the panel's left end (its heading's right end has the note)
            var (_, w, h) = VrCatalog.PanelPlacement();
            float size = VR_BAR_TOOL_SIZE, hot = b.State.Hovered ? 1 : 0;
            b.Position = new Vector3(-w / 2 + size * 0.6f + i * size * 1.15f, VR_CATALOG_Y - h / 2 - size * 0.7f, VR_MODAL_Z + (hot > 0 ? size * 0.25f : 0.001f));
            b.Size = size * (hot > 0 ? VR_BAR_TOOL_HOVER : 1);
        }
        bool set = Windows.Settings.Root.Visible;
        for (int i = 0; i < _settingsExtras.Count; i++)
        {
            var b = _settingsExtras[i];
            b.Visible = set;
            if (!set) continue;
            var c = VrSettings.ClosePlacement(b.State.Hovered);
            b.Position = c.Pos - new Vector3((i + 1) * VR_BAR_TOOL_SIZE * 1.15f, 0, 0);
            b.Size = c.ScaleX;
        }
    }

    // the ray on the extra entries (the windows they stand in being up)
    VrPick? EntryPick(Vector3 origin, Vector3 dir, out float distance)
    {
        distance = 0;
        if (Windows.Modal.Root.Visible || Windows.LevelText.Root.Visible) return null;
        var list = Windows.Settings.Root.Visible ? _settingsExtras : Windows.Catalog.Root.Visible ? _catalogExtras : null;
        if (list == null) return null;
        foreach (var b in list)
            if (b.Visible && b.Hit(origin, dir, out distance) != null) return new VrPick("bar", BarTool: b.BarTool);
        return null;
    }

    bool ActOnEntry(string? tool)
    {
        switch (tool)
        {
            case "catsearch": OpenLibrarySearch(); return true;
            case "catsolutions": OpenSolutions(); return true;
            case "catsetup": OpenSetup(); return true;
            case "setcontrols": OpenControls(); return true;
            case "sethints": OpenKeyHints(); return true;
            case "setqa": OpenQaChecklist(); return true;
        }
        return false;
    }

    // how far along the ray a pages' pick lands
    float PageDistance(VrPick p, Vector3 o, Vector3 d)
    {
        Panel3D? panel = p.BarTool switch
        {
            "kbd" => Pages.Keyboard.Panel,
            "kbdclose" => Pages.Keyboard.Close,
            "page" => Pages.Current?.Panel,
            "pageclose" => Pages.Current?.Close,
            _ => null,
        };
        return panel != null && panel.Hit(o, d, out float dist) != null ? dist : 1;
    }

    // ------------------------------------------------------------ replays
    // app.js saveReplayFile and the replay-file input, over the level on the board
    sealed class ReplayBackend : IReplayFilesBackend
    {
        readonly App _a;
        public ReplayBackend(App a) { _a = a; }
        public string Folder => _a.ReplaysDir;
        public bool CanSave => _a.Session != null;
        public string LevelName => _a.Session is { } s ? (s.Level.Name.Trim() is { Length: > 0 } n ? n : "(unnamed level)") : "";
        public string? LevelReplayId => _a.Session?.Level.Info.Id;
        public string SaveText() => Replay.Serialize(_a.Session!.Game.Sim, new ReplayExtra());
        public void Load(ParsedReplay replay, string name)
        {
            if (_a.Session == null) return;
            _a.Session.Game.LoadReplayFile(replay);
            _a.Windows.Status.Set(note: "REPLAY", kind: "");
        }
    }

    /** Save Replay: the attempt into <user data>/replays under the level's name; a note says where. */
    public string? SaveReplayFile()
    {
        if (Session == null) return null;
        var name = ReplaysPage.Save();
        Windows.Status.Set(note: name != null ? "saved " + name : "save failed", kind: name != null ? "" : "lost");
        return name;
    }
}

// The entries' icons, drawn as the bar's are (BarIcons.BarToolIcon: the rounded plate, the white
// outline under the beam, 5 px round strokes on a 64 x 64 canvas).
public static class EntryIcons
{
    const string Bg = "#1c2432", BgHot = "#33405a", Ink = "#cdd6e4";

    static void Plate(Canvas2D cx, IconState st, Action<Canvas2D> body) =>
        BarIcons.BarToolIcon(cx, st.Hovered, st.Hovered ? BgHot : Bg, Ink, body);

    // a magnifying glass
    public static void Search(Canvas2D cx, IconState st) => Plate(cx, st, c =>
    {
        c.beginPath(); c.arc(28, 28, 12, 0, Mathf.Tau); c.stroke();
        c.beginPath(); c.moveTo(37, 37); c.lineTo(49, 49); c.stroke();
    });

    // a box with an arrow into it: install
    public static void Setup(Canvas2D cx, IconState st) => Plate(cx, st, c =>
    {
        c.beginPath(); c.moveTo(16, 34); c.lineTo(16, 48); c.lineTo(48, 48); c.lineTo(48, 34); c.stroke();
        c.beginPath(); c.moveTo(32, 14); c.lineTo(32, 38); c.moveTo(23, 29); c.lineTo(32, 38); c.lineTo(41, 29); c.stroke();
    });

    // a list with ticks: the solutions
    public static void Solutions(Canvas2D cx, IconState st) => Plate(cx, st, c =>
    {
        c.beginPath();
        for (int i = 0; i < 3; i++)
        {
            float y = 20 + i * 12;
            c.moveTo(14, y); c.lineTo(18, y + 4); c.lineTo(24, y - 4);
            c.moveTo(30, y); c.lineTo(50, y);
        }
        c.stroke();
    });

    // a keyboard: the controls
    public static void Controls(Canvas2D cx, IconState st) => Plate(cx, st, c =>
    {
        c.lineWidth = 4;
        c.strokeRect(12, 20, 40, 26);
        c.beginPath();
        for (int i = 0; i < 4; i++) { c.moveTo(19 + i * 9, 28); c.lineTo(20 + i * 9, 28); }
        c.moveTo(22, 38); c.lineTo(42, 38);
        c.stroke();
    });

    // a question mark: which key does what
    public static void Hints(Canvas2D cx, IconState st) => Plate(cx, st, c =>
    {
        c.beginPath(); c.arc(32, 25, 9, Mathf.Pi * 1.1f, Mathf.Pi * 2.35f); c.lineTo(32, 38); c.stroke();
        c.beginPath(); c.moveTo(32, 47); c.lineTo(32, 48); c.stroke();
    });

    // a ticked box: the QA checklist
    public static void Qa(Canvas2D cx, IconState st) => Plate(cx, st, c =>
    {
        c.strokeRect(16, 16, 32, 32);
        c.beginPath(); c.moveTo(22, 32); c.lineTo(29, 39); c.lineTo(42, 24); c.stroke();
    });

    public static Action<Canvas2D, IconState> ByName(string name) => name switch
    {
        "catsearch" => Search,
        "catsolutions" => Solutions,
        "catsetup" => Setup,
        "setcontrols" => Controls,
        "sethints" => Hints,
        "setqa" => Qa,
        _ => throw new ArgumentException(name),
    };
}
