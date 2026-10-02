namespace Lemmix.Input;

// web/3d/js/app.js, how the table is read while playing (the keydown/keyup listeners ~5690-5716,
// onVrButton/onVrButtonHeld/onStick ~5091-5127, heldAction/checkShifts/keyUp/releaseHeldKeys
// ~5326-5367, runHotkey's gate ~5536-5549 and its per-function arithmetic). What each function
// then does to the game is the app's (the `switch (b.action)`); this is the part that decides
// whether, and with which binding, it runs - the same for a Bluetooth keyboard on the headset.
public sealed class HotkeyDispatch
{
    public const double NukeDoubleMs = 250;      // the second press of the nuke key must come within this
    public const double FastForwardSpeed = 4, SlowMotionSpeed = 0.25; // NeoLemmix's
    public const double YawStep = Math.PI / 12;  // yaw_left / yaw_right

    readonly HotkeyManager _hotkeys;
    readonly List<string> _held = new(); // heldCodes, a Set in insertion order
    double _lastNukeKeyAt;

    public HotkeyDispatch(HotkeyManager hotkeys) { _hotkeys = hotkeys; }

    public IReadOnlyList<string> HeldCodes => _held;

    void Hold(string code) { if (!_held.Contains(code)) _held.Add(code); }

    // the filters the held keys put on the game (checkShifts): the directional select, walkers only, athlete info
    public readonly record struct Shifts(int Dx, bool SelectWalkerOnly, bool ShowAthleteInfo);

    public bool HeldAction(string id)
    {
        foreach (var c in _held) { var b = _hotkeys.Get(c); if (b != null && b.Action == id) return true; }
        return false;
    }

    // CheckShifts: both directions held cancel out, as in NeoLemmix
    public Shifts CurrentShifts()
    {
        int dx = 0;
        if (HeldAction("dir_select_left")) dx--;
        if (HeldAction("dir_select_right")) dx++;
        return new Shifts(dx, HeldAction("force_walker"), HeldAction("athlete_info"));
    }

    // what a key going down does
    public sealed record KeyDownResult(
        HotkeyBinding? Binding, // the key's function, null when it has none
        bool Run,               // runHotkey(binding)
        bool CheckShifts,       // a held function: the filters re-read
        bool Taken,             // preventDefault: the key was the table's
        bool ViewKey,           // left to the fixed view keys (arrows pan): no function, or a held one
        bool Threw = false);    // the table held a function the page does not know: app.js fails on it

    // window keydown, after the dialogs and the library had their say: `code` is
    // KeyboardEvent.code, `repeat` the keyboard's auto-repeat
    public KeyDownResult KeyDown(string? code, bool repeat)
    {
        string c = Hotkeys.NormalizeCode(code);
        var b = _hotkeys.Get(c);
        if (b == null) return new KeyDownResult(null, false, false, false, true);
        var a = Hotkeys.ActionOf(b.Action);
        Hold(c);
        if (a == null) return new KeyDownResult(b, false, false, false, false, Threw: true); // a.held of undefined
        if (a.Held) return new KeyDownResult(b, false, true, true, true);
        return new KeyDownResult(b, !repeat || a.Repeat, false, true, false);
    }

    // what a key let go does (keyUp): a held release-rate key stops, a held clear-physics key
    // turns the mode off; the filters are re-read in any case
    public sealed record KeyUpResult(HotkeyBinding? Binding, bool StopReleaseRate, bool ClearPhysicsOff);

    public KeyUpResult KeyUp(string? code, bool session = true)
    {
        string c = Hotkeys.NormalizeCode(code);
        _held.Remove(c);
        var b = _hotkeys.Get(c);
        bool stop = false, off = false;
        if (b != null && session)
        {
            if (b.Action == "rr_down" || b.Action == "rr_up") stop = true;
            else if (b.Action == "clear_physics" && Store.Js.Truthy(b.Mod)) off = true;
        }
        return new KeyUpResult(b, stop, off);
    }

    // every held key let go at once (focus lost, a dialog taking the keyboard)
    public List<KeyUpResult> ReleaseHeldKeys(bool session = true)
    {
        var outList = new List<KeyUpResult>();
        foreach (var code in _held.ToList()) outList.Add(KeyUp(code, session));
        _held.Clear();
        return outList;
    }

    // a controller's face button or stick click going down (onVrButton): the function runs
    // unless it is held (the filters) or a dolly (onVrButtonHeld's); a window up owns the hands,
    // except for a recentre. Taken here means the input was held as down.
    public KeyDownResult VrButtonDown(string code, bool windowUp)
    {
        var b = _hotkeys.Get(code);
        if (b == null) return new KeyDownResult(null, false, false, false, false);
        var a = Hotkeys.ActionOf(b.Action);
        Hold(code);
        if (a != null && a.Held) return new KeyDownResult(b, false, true, true, false);
        if (b.Action != "vr_dolly_in" && b.Action != "vr_dolly_out")
            return new KeyDownResult(b, !windowUp || b.Action == "recenter_vr", false, true, false);
        return new KeyDownResult(b, false, false, true, false);
    }

    // and let go: keyUp
    public KeyUpResult VrButtonUp(string code) => KeyUp(code);

    // a button held down (onVrButtonHeld): +1 dollies in, -1 out, 0 nothing
    public int VrButtonHeld(string code)
    {
        var b = _hotkeys.Get(code);
        if (b == null) return 0;
        if (b.Action == "vr_dolly_in") return 1;
        if (b.Action == "vr_dolly_out") return -1;
        return 0;
    }

    // a thumbstick (onStick): the stick function bound to it ("vr_tilt", "vr_pan", "vr_zoom"),
    // null for none. vr_zoom moves only with y (dollyVr(sign(y), |y| * seconds)).
    public string? Stick(string code)
    {
        var b = _hotkeys.Get(code);
        if (b == null) return null;
        return b.Action is "vr_tilt" or "vr_pan" or "vr_zoom" ? b.Action : null;
    }

    // runHotkey's gate: whether a binding is run at all, and how. NotTaken: not a function that
    // runs on a press (unknown, or a held one), or none without a level for the ones that need
    // one. Swallowed: a NeoLemmix function on a DOS level, taken and doing nothing. Run: the
    // app's switch does it.
    public enum Gate { NotTaken, Swallowed, Run }

    public static Gate RunGate(HotkeyBinding b, bool session, bool lemmixLevel)
    {
        var a = Hotkeys.ActionOf(b.Action);
        if (a == null || a.Held) return Gate.NotTaken;
        if (!session) return b.Action is "quit" or "previous_level" or "next_level" ? Gate.Run : Gate.NotTaken;
        if (Hotkeys.TagOf(b) == "lemmix" && !lemmixLevel) return Gate.Swallowed;
        return Gate.Run;
    }

    // the details as runHotkey reads them
    public static string SkillOf(HotkeyBinding b) => Store.Js.ToStr(b.Mod);   // selectSkillNamed(b.mod)
    public static int FramesOf(HotkeyBinding b) => Store.Js.ToInt32(b.Mod);  // skipFrames(b.mod | 0)
    public static bool SpecialIsLastAction(HotkeyBinding b) => Store.Js.ToInt32(b.Mod) == 0; // else the next shrugger
    public static bool ClearPhysicsHold(HotkeyBinding b) => Store.Js.Truthy(b.Mod); // held: on while down; else a toggle

    // "nuke": a double press, so a stray key does not end the level. True when this press nukes.
    public bool NukePress(double nowMs)
    {
        if (nowMs - _lastNukeKeyAt < NukeDoubleMs)
        {
            _lastNukeKeyAt = 0;
            return true;
        }
        _lastNukeKeyAt = nowMs;
        return false;
    }

    public static double SpeedUp(double speedFactor) => Math.Min(10, speedFactor + 1);
    public static double SpeedDown(double speedFactor) => Math.Max(0.5, speedFactor - 0.5);
    public static double Yaw(string action, double correction) => correction + (action == "yaw_right" ? 1 : -1) * YawStep;
}
