using Godot;
using Lemmix.App.Ui.Pages;
using Lemmix.Engine;
using Lemmix.Input;
using static Lemmix.App.Xr.VrManager;

namespace Lemmix.App.Shell;

// web/3d/js/app.js's controls table at play (~5326-5716): a key or a controller button looked up
// and its function run (runHotkey), the held ones (direction filters, walkers only, athlete info)
// read from the set of inputs down (checkShifts), the release-rate and "hold" clear-physics keys
// undone on their release, every held key let go when the focus goes; a Bluetooth keyboard's
// events as KeyboardEvent.code names; the arrows' view keys (pan, shift: orbit). What decides
// whether a function runs is the core's (HotkeyDispatch); what it does to the game is here.
public sealed partial class App
{
    public override void _Input(InputEvent e)
    {
        if (e is InputEventKey k && HandleKey(k)) GetViewport()?.SetInputAsHandled();
    }

    /** A key event: true when it was taken. */
    public bool HandleKey(InputEventKey e)
    {
        var code = KeyCodes.CodeOf(e);
        if (code == null) return false;
        if (!e.Pressed) { KeyUp(code); return true; }
        string? text = e.Unicode > 31 ? char.ConvertFromUtf32((int)e.Unicode) : null;
        return KeyDown(code, e.Echo, e.ShiftPressed, text);
    }

    /** window keydown: `code` is KeyboardEvent.code, `repeat` the keyboard's auto-repeat. */
    public bool KeyDown(string code, bool repeat = false, bool shift = false, string? text = null)
    {
        // a question on screen owns the keyboard: Enter answers yes, Escape no (the desktop
        // confirm's keys), and a stray space bar does not pause the game behind it
        if (Windows.Modal.Root.Visible)
        {
            if (code == "Escape") OnSelectPick(new Xr.VrPick("bar", BarTool: Windows.ModalNotice ? "yes" : "no"));
            else if (code == "Enter" || code == "NumpadEnter") OnSelectPick(new Xr.VrPick("bar", BarTool: "yes"));
            return true;
        }
        // a page or the keyboard up types; the controls dialog and the setup take every key
        if (Pages.AnyUp)
        {
            if (Pages.OnKey(code, text)) return true;
            if (!(Pages.Current is VrKeyHints)) return true;
        }
        // no level yet: the rest waits for a level; Escape closes the catalog (back to the lobby without one)
        if (Windows.Catalog.Root.Visible && (Locked || code == "Escape"))
        {
            if (code == "Escape") Windows.SetCatalog(false);
            return true;
        }
        var r = Dispatch.KeyDown(code, repeat);
        if (r.CheckShifts) CheckShifts();
        if (r.Run && r.Binding != null) RunHotkey(r.Binding);
        // a held function leaves the fixed view keys their work
        if (!r.ViewKey) return r.Taken;
        return ViewKey(code, shift) || r.Taken;
    }

    public void KeyUp(string code) => ApplyKeyUp(Dispatch.KeyUp(code, Session != null));

    // keyUp's effects: a held release-rate key stops, a held clear-physics key lets go; the filters re-read
    void ApplyKeyUp(HotkeyDispatch.KeyUpResult r)
    {
        if (Session != null)
        {
            if (r.StopReleaseRate) HoldReleaseRate(0);
            if (r.ClearPhysicsOff) Session.Game.SetClearPhysics(false);
        }
        CheckShifts();
    }

    /** releaseHeldKeys: focus lost, or a dialog taking the keyboard. */
    public void ReleaseHeldKeys()
    {
        if (Dispatch == null) return;
        foreach (var r in Dispatch.ReleaseHeldKeys(Session != null))
        {
            if (Session == null) continue;
            if (r.StopReleaseRate) HoldReleaseRate(0);
            if (r.ClearPhysicsOff) Session.Game.SetClearPhysics(false);
        }
        CheckShifts();
    }

    /** CheckShifts: the held keys' filters into the game. */
    void CheckShifts()
    {
        if (Session == null) return;
        var game = Session.Game;
        var s = Dispatch.CurrentShifts();
        game.Sim.HotkeyDx = s.Dx;
        game.Sim.SelectWalkerOnly = s.SelectWalkerOnly;
        if (game.ShowAthleteInfo != s.ShowAthleteInfo)
        {
            game.ShowAthleteInfo = s.ShowAthleteInfo;
            game.Gui?.Render(true);
        }
    }

    /** holdReleaseRate: the Lemmix panel's own repeat. */
    void HoldReleaseRate(int dir)
    {
        if (Session == null) return;
        var game = Session.Game;
        if (dir != 0) game.QueueCommand(dir > 0 ? new CommandReleaseRateIncrease(1) : new CommandReleaseRateDecrease(1));
        if (game.Gui is Lemmix.Ui.GamePanel p) p.SetRrHeld(dir);
    }

    /**
     * runHotkey: a function of the table, on its key or button going down. Without a level, only
     * what does not need one. Returns whether the binding was taken.
     */
    public bool RunHotkey(HotkeyBinding b)
    {
        var gate = HotkeyDispatch.RunGate(b, Session != null, lemmixLevel: true);
        if (gate == HotkeyDispatch.Gate.NotTaken) return false;
        if (gate == HotkeyDispatch.Gate.Swallowed) return true;
        if (Session == null)
        {
            switch (b.Action)
            {
                case "quit": if (Presenting) Windows.SetCatalog(true); return true;
                case "previous_level": MoveLevel(-1); return true;
                case "next_level": MoveLevel(1); return true;
            }
            return false;
        }
        var s = Session;
        var game = s.Game;
        var timer = game.GameTimer;
        switch (b.Action)
        {
            case "skill": game.SelectSkillByName(HotkeyDispatch.SkillOf(b)); break;
            case "previous_skill": game.StepSkill(-1); break;
            case "next_skill": game.StepSkill(1); break;
            case "rr_down": HoldReleaseRate(-1); break;
            case "rr_up": HoldReleaseRate(1); break;
            case "rr_min": game.SetReleaseRateExtreme(-1); break;
            case "rr_max": game.SetReleaseRateExtreme(1); break;
            case "pause": s.TogglePause(); break;
            case "nuke":
                // a double press, so a stray key does not end the level
                if (Dispatch.NukePress(Now()))
                {
                    game.QueueCommand(new CommandNuke());
                    game.NukePrepared = false;
                }
                break;
            case "fastforward": s.ToggleSpeed(HotkeyDispatch.FastForwardSpeed); break;
            case "slow_motion": s.ToggleSpeed(HotkeyDispatch.SlowMotionSpeed); break;
            case "skip":
            {
                int n = HotkeyDispatch.FramesOf(b);
                if (n < 0) game.BackFrames(-n); else game.ForwardFrames(n);
                break;
            }
            case "special_skip": if (HotkeyDispatch.SpecialIsLastAction(b)) game.SkipToLastAction(); else game.SkipToNextShrugger(); break;
            case "restart": game.RestartReplay(); break;
            case "save_state": game.SaveStateMark(); break;
            case "load_state": game.LoadStateMark(); break;
            case "clear_physics": if (HotkeyDispatch.ClearPhysicsHold(b)) game.SetClearPhysics(true); else game.ToggleClearPhysics(); break;
            case "toggle_shadows": Fx.ToggleShadows(); break;
            case "replay_insert": game.ToggleReplayInsert(); break;
            case "cancel_replay": game.CancelReplay(); break;
            case "load_replay": game.RequestLoadReplay(); break;
            case "watch_solution": WatchSolution(); break;
            case "save_replay": SaveReplayFile(); break;
            case "toggle_music": Fx.ToggleMusic(); break;
            case "toggle_sound": ToggleMute(); Windows.Toolbar.PaintSound(Audio.Volume, Audio.Enabled); break;
            case "zoom_in": ZoomView(true); break;
            case "zoom_out": ZoomView(false); break;
            case "quit": if (Presenting) Windows.SetCatalog(true); break;
            case "cheat": game.Skills.Cheat(); game.Gui?.Render(true); break;
            case "reset_view": Recenter(); break;          // the desktop's framing has no headset meaning
            case "recenter_vr": Recenter(); break;
            case "speed_up": timer.SpeedFactor = HotkeyDispatch.SpeedUp(timer.SpeedFactor); break;
            case "speed_down": timer.SpeedFactor = HotkeyDispatch.SpeedDown(timer.SpeedFactor); break;
            case "previous_level": MoveLevel(-1); break;
            case "next_level": MoveLevel(1); break;
            case "piece_editor": break;                     // the tagging workbench: the web's only
            case "cycle_class": break;
            case "yaw_left":
            case "yaw_right":
                _yawCorrection = HotkeyDispatch.Yaw(b.Action, _yawCorrection);
                GD.Print("[vr] yaw correction: " + Mathf.RoundToInt(Mathf.RadToDeg((float)_yawCorrection)) + "°");
                Recenter();
                break;
            default: return false;
        }
        return true;
    }

    /** viewKey: the arrows pan the board (0.12 m a press), shift+arrows turn it (10 degrees). */
    bool ViewKey(string code, bool shift)
    {
        int dx = code == "ArrowRight" ? 1 : code == "ArrowLeft" ? -1 : 0;
        int dy = code == "ArrowUp" ? 1 : code == "ArrowDown" ? -1 : 0;
        if (dx == 0 && dy == 0) return false;
        if (Session == null) return false;
        if (!Presenting) return true;
        if (shift)
        {
            float step = Mathf.Pi / 18;
            TurnDiorama(dx * step, dy * step);
            return true;
        }
        var hb = HeadNow().Basis;
        const float stepM = 0.12f;
        DioramaRoot.Position -= hb.X.Normalized() * (dx * stepM) + hb.Y.Normalized() * (dy * stepM);
        return true;
    }
}
