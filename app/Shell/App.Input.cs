using Godot;
using Lemmix.App.Board;
using Lemmix.App.Ui;
using Lemmix.App.Ui.Windows;
using Lemmix.App.Xr;
using static Lemmix.App.Xr.VrManager;

namespace Lemmix.App.Shell;

// web/3d/js/app.js, the headset's hooks (VRManager's): raycastHit's order - the question, a page,
// the level's text, the settings, the catalog, (past the windows, a level's) the volume slider,
// the icon buttons, the skills bar, the board's pick plane; pickWithRaycaster's picks; actOnPick;
// applyHover; the sticks, buttons and drags.
//
// Picks: the windows' VrPick("bar", BarTool: ...); the skills bar VrPick("panel", Data: uv) and
// its minimap VrPick("panel", BarTool: "minimap", Minimap: true, Data: uv); the board
// VrPick("board", Data: Vector2I sim point).
public sealed partial class App
{
    enum HitKind { None, Window, Panel, Board }

    // one ray through the scene: what it lands on, where and how far
    HitKind Cast(Vector3 o, Vector3 d, out VrPick? pick, out Vector3 point, out float distance)
    {
        pick = null; point = default; distance = 0;
        bool modal = Windows.Modal.Root.Visible;
        // a page (or the keyboard) up owns the ray - after the question it may have asked
        if (!modal && Pages.AnyUp)
        {
            var (owned, pp) = Pages.Pick(o, d);
            if (pp != null)
            {
                pick = pp;
                distance = PageDistance(pp, o, d);
                point = o + d * distance;
                return HitKind.Window;
            }
            // the search's keyboard alone leaves the catalog under it to the ray
            if (owned && !(Pages.Current == null && Windows.Catalog.Root.Visible)) return HitKind.None;
        }
        // the entries the shell adds to the settings and the catalog
        var entry = EntryPick(o, d, out distance);
        if (entry != null)
        {
            pick = entry;
            point = o + d * distance;
            return HitKind.Window;
        }
        var (wOwned, wp) = Windows.Pick(o, d);
        if (wp != null)
        {
            pick = wp;
            distance = WindowDistance(wp, o, d);
            point = o + d * distance;
            return HitKind.Window;
        }
        // no window up and no level: the lobby's signs, and its screen stops the beam
        if (Session == null && !Windows.AnyWindowUp)
        {
            var (lp, ld, onLobby) = Lobby.Pick(o, d);
            if (onLobby)
            {
                pick = lp;
                distance = ld;
                point = o + d * ld;
                return HitKind.Window;
            }
        }
        if (wOwned || Session == null) return HitKind.None;
        // the skills bar
        if (BarView != null && Bar?.Mesh is { } m && BarView.HitUv(o, d) is Vector2 uv)
        {
            var local = new Vector3((float)(m.Px + (uv.X - 0.5) * m.Sx), (float)(m.Py + (uv.Y - 0.5) * m.Sy), (float)m.Pz);
            point = BarView.GlobalTransform * local;
            distance = point.DistanceTo(o);
            // the minimap answers to a hold as a scrubber does, the buttons to a press
            pick = Bar.IsMinimap((uv.X, uv.Y))
                ? new VrPick("panel", BarTool: "minimap", Minimap: true, Data: uv)
                : new VrPick("panel", Data: uv);
            return HitKind.Panel;
        }
        // the board's pick plane: the level's rectangle (40 px taller) on the lemmings' plane
        if (Session.Pick(o, d) is Vector2I sim)
        {
            var inv = BoardMaterials.WorldOf(Session.Board).AffineInverse();
            var lo = inv * o;
            var ld = inv.Basis * d;
            float t = (float)((BoardZ.LEMMING_Z - lo.Z) / ld.Z);
            point = o + d * t;
            distance = t * d.Length();
            pick = new VrPick("board", Data: sim);
            return HitKind.Board;
        }
        return HitKind.None;
    }

    // how far along the ray a window's pick lands (the beam's length, the dot's place)
    float WindowDistance(VrPick p, Vector3 o, Vector3 d)
    {
        Panel3D? panel = p.BarTool switch
        {
            "volume" => Windows.Toolbar.Volume,
            "detailok" or "detailpanel" => Windows.LevelText.Panel,
            "setpanel" => Windows.Settings.Panel,
            "vrsetpanel" or "vrfloor" => Windows.VrOptions.Panel,
            "worldpanel" => Windows.Catalog.Panel,
            _ => null,
        };
        if (panel == null)
            foreach (var b in Windows.IconButtons)
                if (b.BarTool == p.BarTool && b.IsVisibleInTree()) { panel = b; break; }
        return panel != null && panel.Hit(o, d, out float dist) != null ? dist : 1;
    }

    // ------------------------------------------------------------ IVrHooks
    /** pickWithRaycaster */
    public VrPick? Pick(Vector3 origin, Vector3 direction)
    {
        Cast(origin, direction, out var pick, out _, out _);
        return pick;
    }

    /** raycastHit: where the beam ends */
    public VrHit? RaycastHit(Vector3 origin, Vector3 direction)
    {
        var kind = Cast(origin, direction, out _, out var point, out float distance);
        return kind == HitKind.None ? null : new VrHit(point, distance, kind == HitKind.Board);
    }

    /** actOnPick: a confirmed activation (the trigger let go, or a slider held). */
    public void OnSelectPick(VrPick p)
    {
        if (Pages.Act(p)) return;
        if (ActOnEntry(p.BarTool)) return;
        if (ActOnLobby(p.BarTool)) return;
        if (Windows.Act(p)) return;
        if (Session == null) return;
        if (p.BarTool == "minimap" && Bar != null && p.Data is Vector2 mu)
        {
            // the press centres the view; held and moved, it goes on centring
            if (Bar.MinimapDrag) Bar.OnMouseMove((mu.X, mu.Y));
            else Bar.OnMouseDown((mu.X, mu.Y), 0);
        }
        else if (p.Kind == "panel" && Bar != null && p.Data is Vector2 uv)
        {
            Bar.OnMouseDown((uv.X, uv.Y), 0);
            Bar.OnMouseUp((uv.X, uv.Y));
        }
        else if (p.Kind == "board" && p.Data is Vector2I sim)
        {
            // actOnSimPick: the selected skill to the lemming at the point
            Session.AssignAt(sim.X, sim.Y);
        }
    }

    /** applyHover: the buttons' and windows' hover, the bar's raised button, the board's ring. */
    public void OnHoverPick(VrPick? p)
    {
        Pages.ApplyHover(p);
        Windows.ApplyHover(p);
        Lobby.SetHover(p?.BarTool);
        if (Session == null) return;
        Bar?.SetHover(p != null && p.Kind == "panel" && p.Data is Vector2 uv ? (uv.X, uv.Y) : null);
        Session.SetPointer(p != null && p.Kind == "board" && p.Data is Vector2I sim ? sim : null);
    }

    /** The lobby's signs: the world catalog (nothing installed: the setup), the VR window, the end. */
    bool ActOnLobby(string? tool)
    {
        switch (tool)
        {
            case "lobbyplay": if (FirstRun) OpenSetup(); else Windows.SetCatalog(true); return true;
            case "lobbyvr": Windows.SetVrOptions(true); return true;
            case "lobbyquit": QuitGame(); return true;
        }
        return false;
    }

    // a hold on the minimap ends with the trigger, or when the beam leaves it
    public void OnScrubEnd(string? scrub) { if (scrub == "minimap" && Bar != null) Bar.OnMouseUp(null); }
    public void OnScrubOff(string? scrub) { if (scrub == "minimap" && Bar != null) Bar.OnMouseMove(null); }

    public void OnBarDragStart() => Windows.Bar.DragStart();
    public void OnBarDrag(Vector3 worldDelta) => Windows.Bar.Drag(worldDelta);
    public void OnBarDragEnd() => Windows.Bar.DragEnd(HasSession);

    /**
     * A thumbstick: a page up scrolls; the catalog up, either stick scrolls its list and neither
     * moves the board; otherwise what the controls table says (pan, tilt or dolly).
     */
    public void OnStick(string code, float x, float y, double seconds)
    {
        if (Pages.OnStick(y, seconds)) return;
        if (Windows.OnStick(y, seconds)) return;
        if (Session == null) return;
        switch (Dispatch.Stick(code))
        {
            case "vr_tilt": TiltDioramaBy(x, y, seconds); break;
            case "vr_pan": PanDioramaBy(x, y, seconds); break;
            case "vr_zoom": if (y != 0) DollyVr(System.Math.Sign(y), System.Math.Abs(y) * seconds); break;
        }
    }

    /** The face buttons and stick clicks: a function of the controls table on the press (a window
     *  up owns the hands, bar a recentre), the held filters through the inputs down. */
    public void OnVrButton(string code, bool down)
    {
        if (down)
        {
            // the controls' "find key" takes the button it is waiting for
            if (Pages.Current != null && Pages.OnVrButton(code)) return;
            var r = Dispatch.VrButtonDown(code, Windows.AnyWindowUp || Pages.AnyUp);
            if (r.CheckShifts) CheckShifts();
            if (r.Run && r.Binding != null) RunHotkey(r.Binding);
        }
        else ApplyKeyUp(Dispatch.VrButtonUp(code));
    }

    /** A button held: the dollies, for as long as it stays down. */
    public void OnVrButtonHeld(string code, double seconds)
    {
        int dir = Dispatch.VrButtonHeld(code);
        if (dir != 0) DollyVr(dir, seconds);
    }
}
