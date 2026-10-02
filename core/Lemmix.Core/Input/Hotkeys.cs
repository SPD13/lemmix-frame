using Lemmix.Store;

namespace Lemmix.Input;

// web/3d/js/hotkeys.js, everything but the dialog: NeoLemmix's configurable keys
// (LemmixHotkeys.pas) - a table from key to function, some functions carrying a detail (which
// skill, how many frames, hold or toggle, which special skip); Shift, Ctrl and Alt are keys like
// any other. The headset's controllers sit in the same table under their own key names. Keys
// are KeyboardEvent.code names (NormalizeCode folds the left/right twins), so a file exported by
// the web loads here unchanged. The piece editor's rows (piece_editor, cycle_class) stay in the
// tables, as web files carry them, though the native app gives them nothing to do.

// a function of the table (ACTIONS): Mod is the detail it needs ("skill", "frames", "hold",
// "special"); Held does its work while the key is down; Repeat lets auto-repeat fire it again;
// Tag "lemmix" = nothing on a DOS level, "view" = the 3D page's own, "vr" = the headset's;
// Vr "button" or "stick" for the headset's own functions
public sealed record HotkeyAction(string Id, string Label, string? Mod = null, bool Held = false, bool Repeat = false, string? Tag = null, string? Vr = null);

public sealed record HotkeyKey(string Code, string Name);

public sealed record VrKey(string Code, string Name, string Kind);

// a preset entry: Mod null when the layout gives none (the function's default then)
public sealed record PresetEntry(string Code, string Action, object? Mod = null);

// a binding {action, mod}: Mod is whatever JS value the table holds (a file may put anything there)
public sealed class HotkeyBinding
{
    public string Action { get; }
    public object? Mod { get; }

    public HotkeyBinding(string action, object? mod)
    {
        Action = action;
        Mod = Js.Norm(mod);
    }

    public JsObject ToJs() => new(("action", Action), ("mod", Mod));
}

public static class Hotkeys
{
    public const string StorageKey = "lem3d-hotkeys";
    public const int Version = 1;
    public const string ExportFormat = "lemmings-3d-controls"; // the exported file names itself
    public const string ExportFile = "lemmings-3d-controls.json";

    // NeoLemmix's skills in its own order (TSkillPanelButton); the DOS engine has eight of them
    public static readonly string[] Skills = { "walker", "jumper", "shimmier", "slider", "climber", "swimmer", "floater", "glider",
        "disarmer", "bomber", "stoner", "blocker", "platformer", "builder", "stacker", "laserer", "basher",
        "fencer", "miner", "digger", "cloner" };
    public static readonly IReadOnlyList<string> DosSkills = new[] { "climber", "floater", "bomber", "blocker", "builder", "basher", "miner", "digger" };
    public static readonly string[] SpecialSkips = { "Previous Assignment", "Next Shrugger" };

    public static readonly HotkeyAction[] Actions =
    {
        new("skill", "Select Skill", Mod: "skill"),
        new("previous_skill", "Previous Skill"),
        new("next_skill", "Next Skill"),
        new("rr_down", "Decrease Release Rate"), // held: keeps changing until released
        new("rr_up", "Increase Release Rate"),
        new("rr_min", "Minimum Release Rate"),
        new("rr_max", "Maximum Release Rate"),
        new("pause", "Pause"),
        new("nuke", "Nuke (press twice)"),
        new("fastforward", "Fast Forward"),
        new("slow_motion", "Slow Motion"),
        new("skip", "Time Skip", Mod: "frames", Repeat: true),
        new("special_skip", "Skip to", Mod: "special", Tag: "lemmix"),
        new("restart", "Restart"),
        new("save_state", "Save State", Tag: "lemmix"),
        new("load_state", "Load State", Tag: "lemmix"),
        new("dir_select_left", "Directional Select Left", Held: true, Tag: "lemmix"),
        new("dir_select_right", "Directional Select Right", Held: true, Tag: "lemmix"),
        new("force_walker", "Select Walker", Held: true, Tag: "lemmix"),
        new("athlete_info", "Show Athlete Info", Held: true, Tag: "lemmix"),
        new("clear_physics", "Clear Physics Mode", Mod: "hold", Tag: "lemmix"),
        new("toggle_shadows", "Toggle Skill Shadows", Tag: "lemmix"),
        new("replay_insert", "Replay Insert Mode", Tag: "lemmix"),
        new("cancel_replay", "Cancel Replay", Tag: "lemmix"),
        new("load_replay", "Load Replay", Tag: "lemmix"),
        new("watch_solution", "Watch Solution", Tag: "lemmix"),
        new("save_replay", "Save Replay"),
        new("toggle_music", "Toggle Music"),
        new("toggle_sound", "Toggle Sound"),
        new("zoom_in", "Zoom In", Repeat: true),
        new("zoom_out", "Zoom Out", Repeat: true),
        new("quit", "Quit to the World Library"),
        new("cheat", "Cheat (99 of each skill)"),
        new("reset_view", "Reset the View", Tag: "view"),
        new("recenter_vr", "Recentre (VR)", Tag: "view"),
        new("speed_up", "Speed Up", Tag: "view", Repeat: true),
        new("speed_down", "Speed Down", Tag: "view", Repeat: true),
        new("previous_level", "Previous Level", Tag: "view"),
        new("next_level", "Next Level", Tag: "view"),
        new("piece_editor", "Piece Editor", Tag: "view"),
        new("cycle_class", "Cycle Piece Class (editor)", Tag: "view"),
        new("yaw_left", "VR Yaw Correction −", Tag: "view"),
        new("yaw_right", "VR Yaw Correction +", Tag: "view"),
        // the headset's own: a button held dollies, a stick moves the board
        new("vr_dolly_in", "Dolly In (held)", Tag: "vr", Vr: "button"),
        new("vr_dolly_out", "Dolly Out (held)", Tag: "vr", Vr: "button"),
        new("vr_pan", "Pan the Board", Tag: "vr", Vr: "stick"),
        new("vr_tilt", "Tilt the Board", Tag: "vr", Vr: "stick"),
        new("vr_zoom", "Dolly (forward in, back out)", Tag: "vr", Vr: "stick"),
    };

    // what a headset cannot do (the page's DOM): not offered on its buttons
    public static readonly IReadOnlyList<string> DesktopOnly = new[] { "piece_editor", "cycle_class" };

    public static readonly IReadOnlyDictionary<string, HotkeyAction> ActionById = Actions.ToDictionary(a => a.Id, StringComparer.Ordinal);

    public static HotkeyAction? ActionOf(string? id) => id != null && ActionById.TryGetValue(id, out var a) ? a : null;

    // The keys, with NeoLemmix's hardcoded names (GetKeyNames), in its order.
    public static readonly HotkeyKey[] Keys = BuildKeys();

    static HotkeyKey[] BuildKeys()
    {
        var k = new List<HotkeyKey>();
        void Key(string code, string name) => k.Add(new HotkeyKey(code, name));
        Key("MouseRight", "Right-Click");
        Key("MouseMiddle", "Middle-Click");
        Key("Backspace", "Backspace");
        Key("Tab", "Tab");
        Key("Enter", "Enter");
        Key("Shift", "Shift");
        Key("Control", "Ctrl");
        Key("Alt", "Alt");
        Key("Pause", "Pause");
        Key("CapsLock", "Caps Lock");
        Key("Meta", "Cmd / Win");
        Key("Escape", "Esc");
        Key("Space", "Space");
        Key("PageUp", "Page Up");
        Key("PageDown", "Page Down");
        Key("End", "End");
        Key("Home", "Home");
        Key("ArrowLeft", "Left Arrow");
        Key("ArrowUp", "Up Arrow");
        Key("ArrowRight", "Right Arrow");
        Key("ArrowDown", "Down Arrow");
        Key("Insert", "Insert");
        Key("Delete", "Delete");
        for (int i = 0; i <= 9; i++) Key("Digit" + i, i.ToString());
        for (int i = 0; i < 26; i++) Key("Key" + (char)(65 + i), ((char)(65 + i)).ToString());
        for (int i = 0; i <= 9; i++) Key("Numpad" + i, "NumPad " + i);
        Key("NumpadMultiply", "NumPad *");
        Key("NumpadAdd", "NumPad +");
        Key("NumpadSubtract", "NumPad -");
        Key("NumpadDecimal", "NumPad .");
        Key("NumpadDivide", "NumPad /");
        Key("NumpadEnter", "NumPad Enter");
        for (int i = 1; i <= 12; i++) Key("F" + i, "F" + i);
        Key("NumLock", "NumLock");
        Key("ScrollLock", "Scroll Lock");
        Key("Semicolon", ";");
        Key("Equal", "+");
        Key("Comma", ",");
        Key("Minus", "-");
        Key("Period", ".");
        Key("Slash", "/");
        Key("Backquote", "~");
        Key("BracketLeft", "[");
        Key("Backslash", "\\");
        Key("BracketRight", "]");
        Key("Quote", "'");
        Key("IntlBackslash", "< >");
        return k.ToArray();
    }

    public static readonly IReadOnlyDictionary<string, HotkeyKey> KeyByCode = Keys.ToDictionary(k => k.Code, StringComparer.Ordinal);

    // The headset's inputs, by the hand's role rather than its side: the beam moves to whichever
    // hand pulls its trigger, and the functions follow it. A "stick" takes the stick functions,
    // a "button" any other.
    public static readonly VrKey[] VrKeys =
    {
        new("VrPointA", "Pointing hand: A / X", "button"),
        new("VrPointB", "Pointing hand: B / Y", "button"),
        new("VrPointStickClick", "Pointing hand: stick click", "button"),
        new("VrPointStick", "Pointing hand: thumbstick", "stick"),
        new("VrFreeA", "Free hand: A / X", "button"),
        new("VrFreeB", "Free hand: B / Y", "button"),
        new("VrFreeStickClick", "Free hand: stick click", "button"),
        new("VrFreeStick", "Free hand: thumbstick", "stick"),
    };

    public static readonly IReadOnlyDictionary<string, VrKey> VrKeyByCode = VrKeys.ToDictionary(k => k.Code, StringComparer.Ordinal);

    public static bool IsVrCode(string code) => VrKeyByCode.ContainsKey(code);

    // the controllers as they work today: the pointing hand recentres and pans, the free hand
    // dollies and tilts
    public static readonly PresetEntry[] VrPreset =
    {
        new("VrPointA", "recenter_vr"), new("VrPointStick", "vr_pan"),
        new("VrFreeB", "vr_dolly_in"), new("VrFreeA", "vr_dolly_out"), new("VrFreeStick", "vr_tilt"),
    };

    // Can this function go on this input? A stick takes stick functions, a button the rest.
    public static bool AllowedOn(string code, string? action)
    {
        var a = ActionOf(action);
        if (a == null) return false;
        if (!VrKeyByCode.TryGetValue(code, out var vk)) return a.Vr == null; // a keyboard key: nothing that is the headset's own
        if (vk.Kind == "stick") return a.Vr == "stick";
        return a.Vr != "stick" && !DesktopOnly.Contains(action!);
    }

    // A KeyboardEvent's code as the table names it: one Shift, Ctrl, Alt, Cmd each.
    public static string NormalizeCode(string? code)
    {
        if (string.IsNullOrEmpty(code)) return "";
        if (code == "ShiftLeft" || code == "ShiftRight") return "Shift";
        if (code == "ControlLeft" || code == "ControlRight") return "Control";
        if (code == "AltLeft" || code == "AltRight") return "Alt";
        if (code == "MetaLeft" || code == "MetaRight" || code == "OSLeft" || code == "OSRight") return "Meta";
        return code;
    }

    static PresetEntry P(string code, string action, object? mod = null) => new(code, action, Js.Norm(mod));

    // The three layouts of LemmixHotkeys.pas, key codes for its virtual key codes. Functions
    // NeoLemmix has and the page does not leave their keys free; the page's own view keys sit on
    // keys the layout does not use.
    public static readonly IReadOnlyList<KeyValuePair<string, PresetEntry[]>> Presets = new KeyValuePair<string, PresetEntry[]>[]
    {
        new("traditional", new[]
        {
            P("MouseMiddle", "pause"), P("Backspace", "load_state"), P("Enter", "save_state"),
            P("Control", "force_walker"), P("Escape", "quit"),
            P("ArrowLeft", "dir_select_left"), P("ArrowRight", "dir_select_right"),
            P("KeyC", "cancel_replay"), P("KeyF", "fastforward"), P("KeyH", "toggle_shadows"),
            P("KeyL", "load_replay"), P("KeyM", "toggle_music"), P("KeyP", "pause"), P("KeyR", "restart"),
            P("KeyS", "toggle_sound"), P("KeyU", "save_replay"), P("KeyW", "replay_insert"),
            P("KeyX", "next_skill"), P("KeyZ", "previous_skill"),
            P("F1", "rr_down"), P("F2", "rr_up"), P("F11", "pause"),
            P("KeyT", "clear_physics", 1), P("Alt", "athlete_info"),
            P("Space", "skip", 170), P("KeyB", "skip", -1), P("KeyN", "skip", 1), P("NumpadSubtract", "skip", -17),
            P("Comma", "skip", -85), P("Minus", "skip", -17), P("Period", "skip", 85),
            P("BracketLeft", "special_skip", 0), P("BracketRight", "special_skip", 1),
            P("Tab", "skill", "slider"), P("Digit1", "skill", "walker"), P("Digit2", "skill", "shimmier"),
            P("Digit3", "skill", "swimmer"), P("Digit4", "skill", "glider"), P("Digit5", "skill", "disarmer"),
            P("Digit6", "skill", "stoner"), P("Digit7", "skill", "platformer"), P("Digit8", "skill", "stacker"),
            P("Digit9", "skill", "fencer"), P("Digit0", "skill", "cloner"), P("KeyQ", "skill", "laserer"),
            P("F3", "skill", "climber"), P("F4", "skill", "floater"), P("F5", "skill", "bomber"),
            P("F6", "skill", "blocker"), P("F7", "skill", "builder"), P("F8", "skill", "basher"),
            P("F9", "skill", "miner"), P("F10", "skill", "digger"), P("Equal", "skill", "jumper"),
            // the page's own
            P("Home", "reset_view"), P("KeyV", "recenter_vr"), P("PageUp", "zoom_in"), P("PageDown", "zoom_out"),
            P("KeyJ", "piece_editor"), P("KeyK", "cycle_class"),
        }),
        new("functional", new[]
        {
            P("Backspace", "toggle_shadows"), P("KeyS", "dir_select_left"), P("KeyF", "dir_select_right"),
            P("ArrowLeft", "dir_select_left"), P("ArrowRight", "dir_select_right"), P("Space", "pause"),
            P("F1", "restart"), P("F2", "load_state"), P("F3", "save_state"),
            P("Digit4", "fastforward"), P("Digit5", "fastforward"), P("MouseMiddle", "pause"), P("Escape", "quit"),
            P("F6", "save_replay"), P("F7", "load_replay"), P("KeyM", "toggle_music"), P("KeyN", "toggle_sound"),
            P("F4", "rr_down"), P("F5", "rr_up"), P("KeyO", "replay_insert"),
            P("Slash", "clear_physics", 1),
            P("Digit1", "skip", -17), P("Digit2", "skip", -1), P("Digit3", "skip", 1), P("Digit6", "skip", 170),
            P("Digit7", "special_skip", 0), P("Digit8", "special_skip", 1),
            P("Shift", "previous_skill"), P("KeyB", "next_skill"),
            P("KeyD", "skill", "walker"), P("KeyR", "skill", "jumper"), P("Alt", "skill", "shimmier"),
            P("KeyH", "skill", "slider"), P("KeyZ", "skill", "climber"), P("KeyQ", "skill", "floater"),
            P("Tab", "skill", "glider"), P("KeyV", "skill", "bomber"), P("KeyX", "skill", "blocker"),
            P("KeyT", "skill", "platformer"), P("KeyA", "skill", "builder"), P("KeyY", "skill", "laserer"),
            P("KeyE", "skill", "basher"), P("KeyC", "skill", "fencer"), P("KeyG", "skill", "miner"),
            P("KeyW", "skill", "digger"),
            // the page's own
            P("Home", "reset_view"), P("PageUp", "zoom_in"), P("PageDown", "zoom_out"),
            P("KeyK", "piece_editor"), P("KeyL", "cycle_class"),
        }),
        new("minimal", new[]
        {
            P("MouseMiddle", "pause"), P("Escape", "quit"),
            P("Home", "reset_view"), P("PageUp", "zoom_in"), P("PageDown", "zoom_out"),
        }),
    };

    public const string DefaultPreset = "traditional";

    public static PresetEntry[]? PresetOf(string? name)
    {
        foreach (var p in Presets) if (p.Key == name) return p.Value;
        return null;
    }

    // The detail a function carries when none was given.
    public static object DefaultMod(string? action)
    {
        var a = ActionOf(action);
        if (a == null || a.Mod == null) return 0.0;
        if (a.Mod == "skill") return Skills[0];
        if (a.Mod == "frames") return 1.0;
        return 0.0;
    }

    // A binding read as text, as the list shows it (FEditHotkeys.RefreshList).
    public static string Describe(HotkeyBinding? binding)
    {
        if (binding == null) return "";
        var a = ActionOf(binding.Action);
        if (a == null) return binding.Action;
        switch (a.Mod)
        {
            case "skill":
                {
                    string s = Js.ToStr(Js.Or(binding.Mod, ""));
                    return "Select Skill: " + (s.Length > 0 ? Js.ToUpper(s[0].ToString()) + s[1..] : "???");
                }
            case "frames":
                {
                    int n = Js.ToInt32(binding.Mod);
                    if (n < -1) return "Time Skip: Back " + Js.NumberToString(-(double)n) + " Frames";
                    if (n == -1) return "Time Skip: Back 1 Frame";
                    if (n > 1) return "Time Skip: Forward " + n + " Frames";
                    return "Time Skip: Forward 1 Frame";
                }
            case "hold": return a.Label + (Js.Truthy(binding.Mod) ? " (hold)" : " (toggle)");
            case "special":
                {
                    int i = Js.ToInt32(binding.Mod);
                    return "Skip to " + (i >= 0 && i < SpecialSkips.Length ? SpecialSkips[i] : "???");
                }
        }
        return a.Label;
    }

    // "lemmix" when the binding does nothing on a DOS level, "view" / "vr" for the page's or the
    // headset's own, else "".
    public static string TagOf(HotkeyBinding? binding)
    {
        if (binding == null) return "";
        var a = ActionOf(binding.Action);
        if (a == null) return "";
        if (a.Tag != null) return a.Tag;
        if (a.Id == "skill" && !(binding.Mod is string s && DosSkills.Contains(s))) return "lemmix";
        if (a.Id == "skip" && Js.ToInt32(binding.Mod) < 0) return "lemmix";
        return "";
    }

    // A key's name: NeoLemmix's (the web can also ask the browser for the keyboard's own labels;
    // the native app has no such source).
    public static string KeyName(string code)
    {
        if (KeyByCode.TryGetValue(code, out var k)) return k.Name;
        if (VrKeyByCode.TryGetValue(code, out var v)) return v.Name;
        return code;
    }

    // HotkeyDialog.importText's line under the head after an import
    public static string ImportStatus(HotkeyImport r, string? name) =>
        (string.IsNullOrEmpty(name) ? "" : name + ": ") + r.Loaded + " binding" + (r.Loaded == 1 ? "" : "s") + " loaded" +
        (r.Skipped != 0 ? ", " + r.Skipped + " skipped (unknown key or function)" : "") +
        (r.Filled.Count > 0 ? ", " + string.Join(" and ", r.Filled) + " left at the default" : "");
}
