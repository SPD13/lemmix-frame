using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Lemmix.App.Ui.Windows;
using Lemmix.App.Xr;

namespace Lemmix.App.Ui.Pages;

// What the pages ask of the game: the headset, the head, the clock held while one is up.
public interface IVrPagesHost
{
    bool Presenting { get; }
    Transform3D? HeadPose { get; }
    void HoldSim(string who);
    void ReleaseSim(string who);
}

// The desktop pages in the headset together, as VrWindows has the windows: one page up at a time
// (the setup, the solutions, the controls dialog, the key hints, the replay files), the VR
// keyboard under it when a field is typed into, who owns the ray while they are up, hover and
// press, the sticks, the keys of a Bluetooth keyboard and the controllers' buttons (the controls'
// "find key"), the work in the background drained once a frame. Picks are VrPick("bar",
// BarTool: "page" | "pageclose" | "kbd" | "kbdclose", Data: PagePick).
public sealed class VrPages
{
    public readonly IVrPagesHost Host;
    public readonly Node3D Root = new() { Name = "vr-pages" };
    public readonly VrKeyboard Keyboard = new();
    public readonly List<VrPage> All = new();
    public VrPage? Current;
    public float WindowYaw;
    // the field the keyboard types into: a page's, or the library's search
    VrPage? _kbPage;
    string? _kbField;

    public VrPages(IVrPagesHost host)
    {
        Host = host;
        Root.AddChild(Keyboard.Root);
        Keyboard.Closed = CloseKeyboard;
        Keyboard.Layout();
    }

    /** A page joins: in the scene under Root, its close and its fields wired. */
    public T Add<T>(T page) where T : VrPage
    {
        All.Add(page);
        Root.AddChild(page.Root);
        page.Closed = () => { if (Current == page) Show(null); };
        page.EditText = OpenKeyboard;
        page.Layout();
        return page;
    }

    public T? Get<T>() where T : VrPage => All.OfType<T>().FirstOrDefault();

    public bool AnyUp => Current != null || Keyboard.Root.Visible;

    /** placeVrWindows for the pages' root: in front of the gaze, upright. */
    public void PlaceWindows(Transform3D? headPose = null)
    {
        var head = headPose ?? Host.HeadPose ?? Transform3D.Identity;
        var (pos, quat, yaw) = VrWindowPlacement.PlaceWindows(head.Origin, head.Basis.GetRotationQuaternion(), WindowYaw);
        WindowYaw = yaw;
        Root.Transform = new Transform3D(new Basis(quat), pos);
    }

    /** Show a page (null: none): the one up before goes, the clock is held while one is up. */
    public void Show(VrPage? page)
    {
        if (page != null && !Host.Presenting) page = null;
        if (Current == page) return;
        if (Current != null)
        {
            var was = Current;
            was.Root.Visible = false;
            was.Close.Visible = false;
            was.Popup = null;
            if (_kbPage == was) CloseKeyboard();
            Host.ReleaseSim("page-" + was.Name);
            Current = null;
        }
        if (page == null) return;
        if (!Keyboard.Root.Visible) PlaceWindows();
        Current = page;
        page.Hover = null;
        page.Close.SetState(hovered: false);
        page.Root.Visible = true;
        page.Close.Visible = true;
        page.Layout();
        page.Opened();
        Host.HoldSim("page-" + page.Name);
    }

    public void Toggle(VrPage page) => Show(Current == page ? null : page);

    // ---- the keyboard
    void OpenKeyboard(VrPage page, string field, string text, bool numeric)
    {
        _kbPage = page;
        _kbField = field;
        page.FocusField = field;
        Keyboard.Open(text, page.Name + ": " + field, numeric ? "" : "type to search", numeric);
        Keyboard.Changed = t => { page.TextChanged(field, t); page.Paint(); };
        Keyboard.Entered = _ => { page.TextEnter(field); CloseKeyboard(); };
        Keyboard.Escaped = () => { page.TextEscape(field); CloseKeyboard(); };
        ShowKeyboard();
    }

    /** The keyboard on any field: the library's search, for one (OpenLibrarySearch). */
    public void OpenKeyboard(string text, string label, string placeholder, Action<string> changed, Action<string> entered, Action escaped, bool numeric = false)
    {
        if (_kbPage != null) _kbPage.TextDone(_kbField!);
        _kbPage = null;
        _kbField = null;
        Keyboard.Open(text, label, placeholder, numeric);
        Keyboard.Changed = changed;
        Keyboard.Entered = t => { entered(t); };
        Keyboard.Escaped = escaped;
        ShowKeyboard();
    }

    void ShowKeyboard()
    {
        if (!Keyboard.Root.Visible && Current == null && Host.Presenting) PlaceWindows();
        Keyboard.Root.Visible = Host.Presenting;
        Keyboard.Close.Visible = Keyboard.Root.Visible;
        Keyboard.Hover = null;
        Keyboard.Layout();
        Keyboard.Paint();
        _kbPage?.Paint();
    }

    public void CloseKeyboard()
    {
        if (_kbPage != null) { _kbPage.TextDone(_kbField!); _kbPage.Paint(); }
        _kbPage = null;
        _kbField = null;
        Keyboard.Root.Visible = false;
        Keyboard.Close.Visible = false;
    }

    /**
     * library.js's search field in the headset: the keyboard types the query, the catalog lists
     * what matches as it is typed; Enter plays the first match, Escape clears the query - or, with
     * nothing typed, closes the library.
     */
    public void OpenLibrarySearch(CatalogSearch search, Action<string> enterLevel, Action closeLibrary)
    {
        OpenKeyboard(search.Query, "search levels", "search levels…", search.SetQuery, _ =>
        {
            var id = search.FirstMatch();
            if (id == null) return;
            CloseKeyboard();
            enterLevel(id);
        }, () => { CloseKeyboard(); if (!search.Escape()) closeLibrary(); });
    }

    // ---- the ray
    static VrPick P(string tool, PagePick data) => new("bar", BarTool: tool, ScrollBar: data.ScrollBar, Data: data);

    /** What the ray is on among the pages (Owned: a page or the keyboard is up, nothing behind may be hit). */
    public (bool Owned, VrPick? Pick) Pick(Vector3 origin, Vector3 dir)
    {
        if (!AnyUp) return (false, null);
        if (Keyboard.Root.Visible)
        {
            if (Keyboard.Close.Hit(origin, dir, out _) != null) return (true, P("kbdclose", new PagePick(null)));
            var kp = Keyboard.Panel.Hit(origin, dir, out _);
            if (kp != null) return (true, P("kbd", Keyboard.PickAt(kp)));
        }
        if (Current != null)
        {
            if (Current.Close.Hit(origin, dir, out _) != null) return (true, P("pageclose", new PagePick(null)));
            var px = Current.Panel.Hit(origin, dir, out _);
            if (px != null) return (true, P("page", Current.PickAt(px)));
        }
        return (true, null);
    }

    public void ApplyHover(VrPick? p)
    {
        var d = p?.Data as PagePick? ?? new PagePick(null);
        Current?.SetHover(p?.BarTool == "page" ? d : new PagePick(null));
        Current?.Close.SetState(hovered: p?.BarTool == "pageclose");
        if (Current != null) Current.Layout();
        if (Keyboard.Root.Visible)
        {
            Keyboard.SetHover(p?.BarTool == "kbd" ? d : new PagePick(null));
            Keyboard.Close.SetState(hovered: p?.BarTool == "kbdclose");
            Keyboard.Layout();
        }
    }

    /** A press (the trigger): true when it was the pages'. */
    public bool Act(VrPick p)
    {
        var d = p.Data as PagePick? ?? new PagePick(null);
        switch (p.BarTool)
        {
            case "page": Current?.Press(d, p.Scrubbing); return true;
            case "pageclose":
                if (Current is VrControlsDialog c) c.CloseDialog(); // saves the table, as its close does
                else Show(null);
                return true;
            case "kbd": Keyboard.Press(d, p.Scrubbing); return true;
            case "kbdclose": CloseKeyboard(); return true;
        }
        return false;
    }

    /** A thumbstick while a page is up: it scrolls (its popup list first). */
    public bool OnStick(float y, double seconds)
    {
        if (Current == null) return false;
        Current.OnStick(y, seconds);
        return true;
    }

    /** A key of a Bluetooth keyboard (KeyboardEvent.code, the character typed or null). */
    public bool OnKey(string code, string? text)
    {
        if (Keyboard.Root.Visible && !(Current is VrControlsDialog { Finding: true })) return Keyboard.OnKey(code, text);
        if (Current == null) return false;
        if (Current.OnKey(code, text)) { if (Current != null) Current.Paint(); return true; }
        if (code == "Escape") { Show(null); return true; }
        return false;
    }

    /** A controller button (the table's Vr* code): the controls' "find key" takes it. */
    public bool OnVrButton(string code) => Current?.OnVrButton(code) ?? false;

    /** Once a frame: the background work's news. */
    public void Update()
    {
        foreach (var page in All) if (page.Root.Visible || page.Working) page.Update();
        if (Keyboard.Root.Visible) Keyboard.Update();
    }
}
