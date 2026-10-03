using Godot;

namespace Lemmix.App.Shell;

// A Godot key event as the web's KeyboardEvent.code names it (the physical key, whatever the
// layout: "KeyA", "Digit5", "F1", "ShiftLeft", "Space", "Numpad3", "Backquote"...), the names the
// controls table is keyed by. HotkeyDispatch normalises the left/right modifiers.
public static class KeyCodes
{
    public static string? CodeOf(InputEventKey e)
    {
        var k = e.PhysicalKeycode != Key.None ? e.PhysicalKeycode : e.Keycode;
        bool right = e.Location == KeyLocation.Right;
        return CodeOf(k, right);
    }

    // cached strings: no allocation per key press
    static readonly string[] Letters = Make("Key", 'A', 26, true);
    static readonly string[] Digits = Make("Digit", '0', 10, true);
    static readonly string[] Pad = Make("Numpad", '0', 10, true);
    static readonly string[] Fs = MakeF();

    static string[] Make(string prefix, char first, int n, bool chars)
    {
        var a = new string[n];
        for (int i = 0; i < n; i++) a[i] = prefix + (char)(first + i);
        return a;
    }

    static string[] MakeF()
    {
        var a = new string[24];
        for (int i = 0; i < 24; i++) a[i] = "F" + (i + 1);
        return a;
    }

    public static string? CodeOf(Key k, bool right = false)
    {
        if (k >= Key.A && k <= Key.Z) return Letters[k - Key.A];
        if (k >= Key.Key0 && k <= Key.Key9) return Digits[k - Key.Key0];
        if (k >= Key.Kp0 && k <= Key.Kp9) return Pad[k - Key.Kp0];
        if (k >= Key.F1 && k <= Key.F24) return Fs[k - Key.F1];
        return k switch
        {
            Key.Shift => right ? "ShiftRight" : "ShiftLeft",
            Key.Ctrl => right ? "ControlRight" : "ControlLeft",
            Key.Alt => right ? "AltRight" : "AltLeft",
            Key.Meta => right ? "MetaRight" : "MetaLeft",
            Key.Space => "Space",
            Key.Enter => "Enter",
            Key.KpEnter => "NumpadEnter",
            Key.Escape => "Escape",
            Key.Backspace => "Backspace",
            Key.Tab => "Tab",
            Key.Left => "ArrowLeft",
            Key.Right => "ArrowRight",
            Key.Up => "ArrowUp",
            Key.Down => "ArrowDown",
            Key.Home => "Home",
            Key.End => "End",
            Key.Pageup => "PageUp",
            Key.Pagedown => "PageDown",
            Key.Insert => "Insert",
            Key.Delete => "Delete",
            Key.Minus => "Minus",
            Key.Equal => "Equal",
            Key.Bracketleft => "BracketLeft",
            Key.Bracketright => "BracketRight",
            Key.Backslash => "Backslash",
            Key.Semicolon => "Semicolon",
            Key.Apostrophe => "Quote",
            Key.Quoteleft => "Backquote",
            Key.Comma => "Comma",
            Key.Period => "Period",
            Key.Slash => "Slash",
            Key.KpMultiply => "NumpadMultiply",
            Key.KpDivide => "NumpadDivide",
            Key.KpSubtract => "NumpadSubtract",
            Key.KpAdd => "NumpadAdd",
            Key.KpPeriod => "NumpadDecimal",
            Key.Capslock => "CapsLock",
            Key.Numlock => "NumLock",
            Key.Scrolllock => "ScrollLock",
            Key.Pause => "Pause",
            Key.Print => "PrintScreen",
            Key.Menu => "ContextMenu",
            _ => null,
        };
    }
}
