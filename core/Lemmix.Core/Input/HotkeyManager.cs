using Lemmix.Store;

namespace Lemmix.Input;

// the outcome of HotkeyManager.ImportJSON: {loaded, skipped, filled}
public sealed record HotkeyImport(int Loaded, int Skipped, List<string> Filled)
{
    public JsObject ToJs() => new(("loaded", Loaded), ("skipped", Skipped), ("filled", new JsArray(Filled)));
}

// a controls text that is not a controls file (ImportJSON's throw)
public sealed class HotkeyImportException : Exception
{
    public HotkeyImportException(string message) : base(message) { }
}

// hotkeys.js HotkeyManager: the table (code -> {action, mod}, in insertion order like the JS Map)
// and its keeping in the store under lem3d-hotkeys, method for method.
public sealed class HotkeyManager
{
    readonly IStorage _store;
    readonly List<string> _order = new();
    readonly Dictionary<string, HotkeyBinding> _table = new(StringComparer.Ordinal);

    public Action? OnChange;

    public HotkeyManager(IStorage store)
    {
        _store = store;
        Load();
    }

    // the table's entries, in its order
    public IEnumerable<KeyValuePair<string, HotkeyBinding>> Table => _order.Select(c => new KeyValuePair<string, HotkeyBinding>(c, _table[c])).ToList();

    void TableSet(string code, HotkeyBinding b)
    {
        if (!_table.ContainsKey(code)) _order.Add(code);
        _table[code] = b;
    }

    void TableDelete(string code)
    {
        if (_table.Remove(code)) _order.Remove(code);
    }

    void TableClear()
    {
        _table.Clear();
        _order.Clear();
    }

    static object? ModOr(string action, object? mod) => Js.IsNullish(mod) ? Hotkeys.DefaultMod(action) : mod;

    public void Load()
    {
        string? raw = null;
        try { raw = _store.GetItem(Hotkeys.StorageKey); } catch (Exception) { /* no storage */ }
        if (!string.IsNullOrEmpty(raw))
        {
            try
            {
                var data = JsJson.Parse(raw);
                if (Js.Truthy(data) && Js.Truthy(Js.Get(data, "keys")) && Js.TypeOf(Js.Get(data, "keys")) == "object")
                {
                    TableClear();
                    var keys = Js.Get(data, "keys");
                    foreach (var code in Js.OwnKeys(keys))
                    {
                        var b = Js.Get(keys, code);
                        if (Js.Truthy(b) && Js.Get(b, "action") is string action && Hotkeys.ActionById.ContainsKey(action))
                            TableSet(code, new HotkeyBinding(action, ModOr(action, Js.Get(b, "mod"))));
                    }
                    // a table saved before the controllers were in it: theirs as they were
                    if (!Js.Truthy(Js.Get(data, "vr"))) ApplyVrPreset(false);
                    FillDefaults(false); // a half the table does not cover at all
                    return;
                }
            }
            catch (JsSyntaxError) { /* unreadable: start over */ }
        }
        // nothing kept yet: the layouts as they ship, written out at once so the exported file
        // and the configuration files see them
        ApplyPreset(Hotkeys.DefaultPreset, false);
        ApplyVrPreset(false);
        Save();
    }

    // Is anything bound on this half of the table: the controllers, or the keyboard.
    public bool HasHalf(bool vr)
    {
        foreach (var code in _order) if (Hotkeys.IsVrCode(code) == vr) return true;
        return false;
    }

    // A half with nothing bound on it at all filled with what ships (the traditional layout, the
    // controllers of VR_PRESET). Returns the names of the halves it filled.
    public List<string> FillDefaults(bool save = true)
    {
        var filled = new List<string>();
        if (!HasHalf(false)) { ApplyPreset(Hotkeys.DefaultPreset, false); filled.Add("keyboard"); }
        if (!HasHalf(true)) { ApplyVrPreset(false); filled.Add("VR"); }
        if (filled.Count > 0 && save) Save();
        return filled;
    }

    JsObject KeysObject()
    {
        var keys = new JsObject();
        foreach (var code in _order) keys.Set(code, _table[code].ToJs());
        return keys;
    }

    public void Save()
    {
        var data = new JsObject(("version", Hotkeys.Version), ("vr", true), ("keys", KeysObject()));
        try { _store.SetItem(Hotkeys.StorageKey, JsJson.Stringify(data)!); } catch (IOException) { /* no storage */ }
    }

    void Changed() => OnChange?.Invoke();

    // A keyboard layout: the keyboard's half of the table replaced, the controllers kept.
    public void ApplyPreset(string? name, bool save = true)
    {
        var preset = Hotkeys.PresetOf(name);
        if (preset == null) return;
        foreach (var code in _order.ToList()) if (!Hotkeys.IsVrCode(code)) TableDelete(code);
        foreach (var e in preset) TableSet(e.Code, new HotkeyBinding(e.Action, ModOr(e.Action, e.Mod)));
        if (save) Save();
        Changed();
    }

    // The controllers as they started out, the keyboard kept.
    public void ApplyVrPreset(bool save = true)
    {
        foreach (var code in _order.ToList()) if (Hotkeys.IsVrCode(code)) TableDelete(code);
        foreach (var e in Hotkeys.VrPreset) TableSet(e.Code, new HotkeyBinding(e.Action, ModOr(e.Action, e.Mod)));
        if (save) Save();
        Changed();
    }

    // The whole table - the keyboard's keys and the controllers - as the JSON text the web exports.
    public string ExportJSON()
    {
        var data = new JsObject(("format", Hotkeys.ExportFormat), ("version", Hotkeys.Version), ("keys", KeysObject()));
        return JsJson.Stringify(data, 2)!;
    }

    // A table from exportJSON's text, replacing this one. Entries that name an unknown key or
    // function, or put a function on an input that cannot take it, are skipped; a half the file
    // leaves empty keeps what ships. Throws HotkeyImportException on a text that is not a
    // controls file at all.
    public HotkeyImport ImportJSON(string text)
    {
        object? data;
        try { data = JsJson.Parse(text); } catch (JsSyntaxError) { throw new HotkeyImportException("not a JSON file"); }
        if (!Js.Truthy(data) || Js.TypeOf(data) != "object" || !Js.Truthy(Js.Get(data, "keys")) || Js.TypeOf(Js.Get(data, "keys")) != "object" || Js.Get(data, "keys") is JsArray)
            throw new HotkeyImportException("not a controls file (no \"keys\" table)");
        var format = Js.Get(data, "format");
        if (Js.Truthy(format) && !Js.StrictEquals(format, Hotkeys.ExportFormat))
            throw new HotkeyImportException("not a controls file (format " + Js.ToStr(format) + ")");
        var keys = Js.Get(data, "keys");
        var next = new List<(string, HotkeyBinding)>(); // Object.keys gives each code once
        int skipped = 0;
        foreach (var code in Js.OwnKeys(keys))
        {
            var b = Js.Get(keys, code);
            bool known = Hotkeys.KeyByCode.ContainsKey(code) || Hotkeys.VrKeyByCode.ContainsKey(code);
            var action = Js.Truthy(b) && Js.TypeOf(b) == "object" ? Js.Get(b, "action") as string : null;
            if (!known || action == null || !Hotkeys.ActionById.ContainsKey(action) || !Hotkeys.AllowedOn(code, action)) { skipped++; continue; }
            next.Add((code, new HotkeyBinding(action, ModOr(action, Js.Get(b, "mod")))));
        }
        TableClear();
        foreach (var (code, b) in next) TableSet(code, b);
        int loaded = _order.Count;
        var filled = FillDefaults(false); // a file that left a half empty
        Save();
        Changed();
        return new HotkeyImport(loaded, skipped, filled);
    }

    // Give `code` a function (a falsy action clears it).
    public void Set(string code, string? action, object? mod = null)
    {
        if (string.IsNullOrEmpty(action)) TableDelete(code);
        else TableSet(code, new HotkeyBinding(action, ModOr(action, mod)));
        Save();
        Changed();
    }

    public HotkeyBinding? Get(string code) => _table.TryGetValue(code, out var b) ? b : null;

    // The function of a key event's code, or null.
    public HotkeyBinding? ForEvent(string? code) => Get(Hotkeys.NormalizeCode(code));

    // Codes bound to a function (and, when given, to that detail: JsUndefined.Value = not given).
    public List<string> CodesFor(string action, object? mod)
    {
        var outList = new List<string>();
        foreach (var code in _order)
        {
            var b = _table[code];
            if (b.Action != action) continue;
            if (!Js.IsUndefined(mod) && !Js.StrictEquals(b.Mod, mod)) continue;
            outList.Add(code);
        }
        return outList;
    }

    public List<string> CodesFor(string action) => CodesFor(action, Js.Undefined);

    // The name of the first key bound to a function, "" when none: a keyboard key over a mouse
    // button or a controller, when both do it.
    public string KeyNameFor(string action, object? mod)
    {
        var codes = CodesFor(action, mod);
        static int Rank(string c) => c.StartsWith("Mouse", StringComparison.Ordinal) ? 1 : Hotkeys.IsVrCode(c) ? 2 : 0;
        codes = codes.OrderBy(Rank).ToList(); // stable, as Array.prototype.sort
        return codes.Count > 0 ? Hotkeys.KeyName(codes[0]) : "";
    }

    public string KeyNameFor(string action) => KeyNameFor(action, Js.Undefined);
}
