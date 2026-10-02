using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Lemmix.App.Xr;

namespace Lemmix.App.Ui.Windows;

// What the windows ask of the game (web/3d/js/app.js: the session, the sim's clock, the audio,
// the level moves). Integration answers it.
public interface IVrWindowsHost
{
    bool Presenting { get; }               // renderer.xr.isPresenting
    bool HasSession { get; }               // a level is loaded
    Transform3D? HeadPose { get; }         // the latest head pose (vr.lastHeadPose)
    void HoldSim(string who);
    void ReleaseSim(string who);
    bool GameRunning { get; }              // the game's clock runs (the pause tip)
    bool AudioEnabled { get; }
    float Volume { get; }
    void SetVolume(float volume);
    void ToggleMute();                     // audio.setEnabled(!enabled), the desktop icon, the music
    void TogglePause();
    void MoveLevel(int delta);             // 0: restart
    bool CanWatchSolution { get; }         // session.game.sim && Solutions.has(levelId)
    void WatchSolution();
    void EnterLevel(string levelId);       // library.enter
}

// The data a window pick carries beyond its bar tool (web: the pick object's tile, scrollBar,
// scrollAt, row, volume).
public sealed record WindowPickData(int Tile = -1, float ScrollAt = 0, int Row = -1, float Volume = 0);

// The headset's windows together, as app.js wires them: the bar's buttons, the question, the
// catalog, the settings, the level's text, the status strip, the tooltip - who owns the ray while
// a window is up, the hover and the press of each part, the clock held while one is up, the bar
// parked out of their way. Picks are VrPick("bar", BarTool: name, ...) with WindowPickData.
public sealed class VrWindows
{
    public readonly IVrWindowsHost Host;
    public readonly VrToolbar Toolbar = new();
    public readonly VrModal Modal = new();
    public readonly VrCatalog Catalog = new();
    public readonly VrSettings Settings;
    public readonly VrLevelText LevelText = new();
    public readonly VrStatusStrip Status = new();
    public readonly VrTooltip Tooltip;
    public readonly VrBar Bar;
    // the in-scene windows hang off this root, fixed in the world where they opened
    public readonly Node3D WindowRoot = new() { Name = "vr-windowroot" };
    public bool WindowsPlaced;
    public float WindowYaw;
    public ICatalogLibrary? Library;
    public List<string>? LevelTextLines;  // the level's text, flowed (null: none)
    public Func<double> Now = () => Time.GetTicksMsec();
    Action? _confirm;

    // every icon button, in the web's creation order (setBarToolHover walks them all)
    public readonly List<IconButton> IconButtons;
    // the bar's widgets the ray can hit, in the web's order (vrWidgets)
    public IEnumerable<IconButton> Widgets => Toolbar.Buttons.Concat(new[] { Toolbar.Mute, Status.Detail });

    public VrWindows(IVrWindowsHost host, List<SettingRow> settingRows, Node3D head, Node3D scene, string? barPrefs = null)
    {
        Host = host;
        Settings = new VrSettings(settingRows);
        Tooltip = new VrTooltip(TipText);
        Bar = new VrBar(Toolbar.GuiRoot, head, scene, barPrefs) { LockChanged = on => Toolbar.Lock.SetState(on: on) };
        WindowRoot.AddChild(Modal.Root);
        WindowRoot.AddChild(Catalog.Root);
        WindowRoot.AddChild(Settings.Root);
        WindowRoot.AddChild(LevelText.Root);
        var t = Toolbar;
        IconButtons = new List<IconButton> { t.Lock, t.Move, t.Park, t.Pause, t.Restart, t.Solution, t.Prev, t.Next, t.Worlds, t.Mute, t.Settings,
            Status.Detail, Modal.Yes, Modal.No, Catalog.Close, Catalog.Recent, Catalog.Fav, Settings.Close };
        // laid out now: until then they would be metre-wide planes on the camera
        Modal.Layout();
        Catalog.Layout();
        Settings.Layout();
        LevelText.Layout();
    }

    // ---- vrTipTexts
    string? TipText(string name) => name switch
    {
        "lock" => Bar.Locked ? "let the bar go: it stays where it hangs" : "lock the bar to your head",
        "move" => "hold the trigger here and move your hand to carry the bar",
        "park" => "put the bar back below the board",
        "settings" => "3D effects: the terrain's switches, the environment",
        "pause" => Host.HasSession && !Host.GameRunning ? "resume" : "pause",
        "restart" => "restart the level (asks first)",
        "prev" => "previous level (asks first)",
        "next" => "next level (asks first)",
        "worlds" => "world library: choose a level",
        "mute" => Host.AudioEnabled ? "sound off" : "sound on",
        "catclose" => "close the library",
        "catrecent" => Catalog.Filter == "recent" ? "back to the directories" : "the last levels played",
        "catfav" => Catalog.Filter == "favorites" ? "back to the directories" : "your favorite levels",
        "setclose" => "close the settings",
        "detail" => "the level's text",
        _ => null,
    };

    /** anyVrWindowUp */
    public bool AnyWindowUp => Modal.Root.Visible || Catalog.Root.Visible || Settings.Root.Visible || LevelText.Root.Visible;

    void SyncBar() => Bar.SyncForWindows(AnyWindowUp, Host.HasSession);

    /** placeVrWindows: the windows' frame from the head (or the pose given). */
    public void PlaceWindows(Transform3D? headPose = null)
    {
        var head = headPose ?? Host.HeadPose ?? Transform3D.Identity;
        var (pos, quat, yaw) = VrWindowPlacement.PlaceWindows(head.Origin, head.Basis.GetRotationQuaternion(), WindowYaw);
        WindowYaw = yaw;
        WindowRoot.Transform = new Transform3D(new Basis(quat), pos);
        WindowsPlaced = true;
    }

    // ---- the question
    public bool ModalNotice;
    public void SetModal(bool open, bool notice = false)
    {
        if (open && Host.Presenting && !AnyWindowUp) PlaceWindows();
        Modal.Root.Visible = open && Host.Presenting;
        ModalNotice = Modal.Notice = Modal.Root.Visible && notice;
        foreach (var b in new[] { Modal.Yes, Modal.No })
        {
            b.Visible = Modal.Root.Visible && !(ModalNotice && b == Modal.No);
            b.SetState(hovered: false);
        }
        if (Modal.Root.Visible) Host.HoldSim("vr-modal");
        else { _confirm = null; Host.ReleaseSim("vr-modal"); }
        SyncBar();
        if (Modal.Root.Visible) Modal.Layout(); // its buttons where this dialog wants them, now
    }

    /** askVrConfirm: the in-scene twin of askConfirm. */
    public void AskConfirm(string title, Action action)
    {
        Modal.Ask(title);
        SetModal(true);
        _confirm = action; // after SetModal, which clears it on close
    }

    /** askVrNotice: a notice in the same frame, with one button to take it away. */
    public void AskNotice(string title, string body)
    {
        Modal.Ask(title, body);
        SetModal(true, true);
    }

    // ---- the level's text
    /** setLevelText: the level's lines (null: none), flowed into paragraphs. */
    public void SetLevelText(IEnumerable<string>? lines)
    {
        var list = lines?.ToList();
        bool has = list != null && list.Count > 0;
        LevelTextLines = has ? VrLevelText.Flow(list!) : null;
        if (!has) SetDetail(false);
    }

    public void SetDetail(bool open)
    {
        bool show = open && Host.Presenting && LevelTextLines is { Count: > 0 };
        if (show == LevelText.Root.Visible) return;
        if (show && !AnyWindowUp) PlaceWindows();
        LevelText.Root.Visible = show;
        if (show) { LevelText.OkHot = false; LevelText.Paint(LevelTextLines); Host.HoldSim("vr-detail"); }
        else Host.ReleaseSim("vr-detail");
        SyncBar();
    }

    // ---- the settings
    public void SetSettings(bool open)
    {
        bool show = open && Host.Presenting;
        if (show == Settings.Root.Visible) return;
        if (show && !AnyWindowUp) PlaceWindows();
        Settings.Root.Visible = show;
        Settings.Close.Visible = show;
        Settings.Close.SetState(hovered: false);
        Settings.SetHover(-1);
        if (show) { Host.HoldSim("vr-settings"); Settings.Paint(); }
        else Host.ReleaseSim("vr-settings");
        SyncBar();
    }

    // ---- the catalog
    public void SetCatalog(bool open)
    {
        bool show = open && Host.Presenting;
        if (show == Catalog.Root.Visible) return;
        if (show && !AnyWindowUp) PlaceWindows();
        Catalog.Root.Visible = show;
        // locked (no level chosen yet), the catalog has no close: nothing behind it to go back to
        Catalog.Close.Visible = show && !(Library?.Locked ?? false);
        Catalog.Recent.Visible = Catalog.Fav.Visible = show;
        foreach (var b in Catalog.Tools) b.SetState(hovered: false);
        Catalog.SetHover(-1);
        if (show) { Host.HoldSim("vr-catalog"); if (Library != null) Catalog.Load(Library, true); }
        else Host.ReleaseSim("vr-catalog");
        SyncBar();
    }

    // ---- the ray: raycastHit + pickWithRaycaster for the windows and the bar's widgets
    static VrPick P(string tool, WindowPickData? data = null, bool scrollBar = false) => new("bar", BarTool: tool, ScrollBar: scrollBar, Data: data);

    /**
     * What the ray is on among the windows and the bar's own widgets. Owned is true when a window
     * is up: then the pick (or null) is final and nothing behind may be hit; otherwise a null pick
     * means the ray goes on to the skills bar and the board.
     */
    public (bool Owned, VrPick? Pick) Pick(Vector3 origin, Vector3 dir)
    {
        // a question on screen owns the ray: its answers are the only things that can be hit
        if (Modal.Root.Visible)
        {
            foreach (var b in new[] { Modal.Yes, Modal.No })
            {
                if (!b.Visible) continue; // a notice has no "no"
                if (b.Hit(origin, dir, out _) != null) return (true, P(b.BarTool));
            }
            return (true, null);
        }
        // the level's text owns it the same way: its window alone
        if (LevelText.Root.Visible)
        {
            var px = LevelText.Panel.Hit(origin, dir, out _);
            return (true, px == null ? null : P(LevelText.OkAt(px) ? "detailok" : "detailpanel"));
        }
        if (Settings.Root.Visible)
        {
            if (Settings.Close.Hit(origin, dir, out _) != null) return (true, P("setclose"));
            var px = Settings.Panel.Hit(origin, dir, out _);
            return (true, px == null ? null : P("setpanel", new WindowPickData(Row: Settings.RowAt(px))));
        }
        // the catalog: the grid and its buttons only (the hidden close of a locked catalog skipped)
        if (Catalog.Root.Visible)
        {
            foreach (var b in Catalog.Tools)
            {
                if (!b.Visible) continue;
                if (b.Hit(origin, dir, out _) != null) return (true, P(b.BarTool));
            }
            var px = Catalog.Panel.Hit(origin, dir, out _);
            if (px == null) return (true, null);
            var cp = Catalog.PickAt(px);
            return (true, P("worldpanel", new WindowPickData(Tile: cp.Tile, ScrollAt: cp.ScrollAt), cp.ScrollBar));
        }
        // past the windows, everything else belongs to a level
        if (!Host.HasSession) return (true, null);
        if (Toolbar.Volume.Visible)
        {
            var px = Toolbar.Volume.Hit(origin, dir, out _);
            // up the track is the value: the plane's own V, 0 at the bottom
            if (px is Vector2 v) return (false, P("volume", new WindowPickData(Volume: 1 - v.Y / Toolbar.Volume.Canvas.Height)));
        }
        // the icon buttons sit over the bar and the board and take the ray first
        foreach (var b in Widgets)
        {
            if (!b.Visible) continue;
            if (b.Hit(origin, dir, out _) != null) return (false, P(b.BarTool));
        }
        return (false, null);
    }

    // ---- hover (applyHover's share)
    /** setBarToolHover: the beam is on one of the buttons (or has left them all). */
    public void SetBarToolHover(string? name)
    {
        foreach (var b in IconButtons) b.SetState(hovered: b.BarTool == name);
        Tooltip.NoteHover(name, Now());
        Toolbar.NoteHover(name, Now());
    }

    public void ApplyHover(VrPick? p)
    {
        var d = p?.Data as WindowPickData;
        SetBarToolHover(p?.BarTool);
        Catalog.SetHover(p?.BarTool == "worldpanel" ? (p.ScrollBar ? -2 : d?.Tile ?? -1) : -1);
        Settings.SetHover(p?.BarTool == "setpanel" ? d?.Row ?? -1 : -1);
        LevelText.SetHover(p?.BarTool == "detailok", LevelTextLines);
    }

    // ---- a press (actOnPick's share): true when it was one of these
    public bool Act(VrPick p)
    {
        var d = p.Data as WindowPickData;
        switch (p.BarTool)
        {
            case "lock": Bar.ToggleLock(Host.HasSession); return true;
            case "park": Bar.Park(Host.HasSession); return true;
            case "move": return true; // nothing on a tap; it is the drag that moves the bar
            case "volume":
                Host.SetVolume(d?.Volume ?? Host.Volume);
                Toolbar.PaintSound(Host.Volume, Host.AudioEnabled);
                return true;
            case "mute":
                Host.ToggleMute();
                Toolbar.PaintSound(Host.Volume, Host.AudioEnabled);
                return true;
            case "pause": Host.TogglePause(); return true;
            case "restart": AskConfirm("Restart level?", () => Host.MoveLevel(0)); return true;
            case "solution":
                if (Host.HasSession && Host.CanWatchSolution) AskConfirm("Watch the solution?", Host.WatchSolution);
                else AskNotice("No solution", "This level has no stored solution.");
                return true;
            case "prev": AskConfirm("Go back a level?", () => Host.MoveLevel(-1)); return true;
            case "next": AskConfirm("Skip to the next level?", () => Host.MoveLevel(1)); return true;
            case "settings": SetSettings(true); return true;
            case "setclose": SetSettings(false); return true;
            case "setpanel": Settings.Press(d?.Row ?? -1); return true;
            case "detail": SetDetail(true); return true;
            case "detailok": SetDetail(false); return true;
            case "detailpanel": return true;
            case "worlds":
                if (Host.HasSession) AskConfirm("Open the world catalog?", () => SetCatalog(true));
                else SetCatalog(true);
                return true;
            case "catclose": if (!(Library?.Locked ?? false)) SetCatalog(false); return true;
            case "catrecent": if (Library != null) Catalog.SetFilter("recent", Library); return true;
            case "catfav": if (Library != null) Catalog.SetFilter("favorites", Library); return true;
            case "worldpanel":
            {
                var enter = Catalog.Press(new VrCatalog.Pick(d?.Tile ?? -1, p.ScrollBar, d?.ScrollAt ?? 0), p.Scrubbing, Library);
                if (enter != null) { SetCatalog(false); Host.EnterLevel(enter); }
                return true;
            }
            case "yes":
            {
                var act = _confirm;
                SetModal(false);
                act?.Invoke();
                return true;
            }
            case "no": SetModal(false); return true;
        }
        return false;
    }

    /** A thumbstick: while the catalog is up either one scrolls it, and nothing else. */
    public bool OnStick(float y, double seconds)
    {
        if (!Catalog.Root.Visible) return false;
        Catalog.OnStick(y, seconds);
        return true;
    }

    /** The headset branch of layoutGuiPanel for these parts: the bar's row and sound column, the
     *  strip over the board (focusX, levelHeight: board pixels). */
    public void Layout(float guiW, float barH, float focusX, float levelHeight)
    {
        if (Host.Presenting)
        {
            Status.Apply(focusX, levelHeight, LevelTextLines is { Count: > 0 });
            Toolbar.Layout(guiW, barH, Now());
        }
        else
        {
            Toolbar.Hide();
            Status.Hide();
        }
    }

    /** Per frame in a session: the tooltip. */
    public void Update() => Tooltip.Update(IconButtons, Now());
}
