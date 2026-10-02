using Lemmix.Input;

namespace Lemmix.Store;

// a file of the player's configuration (config-store.js FILES)
public sealed record ConfigFile(string Kind, string Name, string Format, IReadOnlyList<string> Keys);

// a file offered for download: its name and its text, as the web writes it
public sealed record ConfigDownload(string Name, string Text);

// the line an upload leaves (setup.js say(): green, or red for a complaint)
public sealed record ConfigMessage(string Text, bool Bad)
{
    public string ClassName => "msg" + (Text.Length > 0 ? (Bad ? " err" : " ok") : "");
}

// a file of the wrong kind (config-store.js apply's throw)
public sealed class ConfigFileException : Exception
{
    public ConfigFileException(string message) : base(message) { }
}

// The player's three files - controls, preferences, progress - the same files the web's setup
// page downloads and uploads and its launcher keeps in config/:
//  - web/3d/js/config-store.js build/apply: a file's object from the store, and back;
//  - web/3d/js/setup.js exportControls/exportPrefs/exportProgress (the downloaded texts,
//    JSON.stringify(…, null, 2)) and importControls/importPrefs/importProgress (the messages,
//    and the progress MERGE: best time the smaller, clears and saved the larger, talismans the
//    union in order). The native app has no server: landed() is always "".
public static class ConfigFiles
{
    // localStorage keys that are preferences
    public static readonly IReadOnlyList<string> PrefsKeys = new[]
    {
        "lem3d-emboss", "lem3d-smooth", "lem3d-smooth-terrain", "lem3d-color-blend", "lem3d-environment",
        "lem3d-doors", "lem3d-skillbar", "lem3d-flatskills", "lem3d-flat", "lem3d-shadows",
        "lem3d-music", "lem3d-sound", "lem3d-volume", "lem3d-bar", "lem3d-lib-order", "lem3d-lib-path",
        "lem3d-gal-open", "lem3d-favorites",
    };
    public const string HotkeysKey = "lem3d-hotkeys";     // HotkeyManager
    public const string ClearedKey = "lem3d-cleared";     // LevelProgress
    public const string TalismansKey = "lem3d-talismans"; // app.js, the NeoLemmix talismans earned

    public static readonly ConfigFile Controls = new("controls", "lemmings-3d-controls.json", "lemmings-3d-controls", new[] { HotkeysKey });
    public static readonly ConfigFile Prefs = new("prefs", "lemmings-3d-preferences.json", "lemmings-3d-preferences", PrefsKeys);
    public static readonly ConfigFile Progress = new("progress", "lemmings-3d-progress.json", "lemmings-3d-progress", new[] { ClearedKey, TalismansKey });
    public static readonly IReadOnlyList<ConfigFile> Files = new[] { Controls, Prefs, Progress };

    public static ConfigFile? FileOf(string kind) => Files.FirstOrDefault(f => f.Kind == kind);

    // which file a store key belongs to (KIND_OF_KEY), null for none
    public static string? KindOfKey(string key) => Files.FirstOrDefault(f => f.Keys.Contains(key))?.Kind;

    static string? GetItem(IStorage s, string k)
    {
        try { return s.GetItem(k); } catch (IOException) { return null; }
    }

    // JSON.parse(getItem(k)) || {}, {} when it does not parse
    static object? GetJSON(IStorage s, string k)
    {
        try { return Js.Or(JsJson.Parse(GetItem(s, k)), new JsObject()); }
        catch (JsSyntaxError) { return new JsObject(); }
    }

    // ---------------------------------------------------------------- config-store.js

    // The file's object built from the store, or null when it holds nothing of that kind.
    public static JsObject? Build(IStorage s, string kind)
    {
        var f = FileOf(kind);
        if (kind == "controls")
        {
            var stored = GetJSON(s, HotkeysKey);
            var keys = Js.Get(stored, "keys");
            if (!Js.Truthy(keys) || Js.TypeOf(keys) != "object") return null;
            return new JsObject(("format", f!.Format), ("version", Js.Get(stored, "version")), ("keys", keys));
        }
        if (kind == "prefs")
        {
            var values = new JsObject();
            int n = 0;
            foreach (var k in PrefsKeys)
            {
                var v = GetItem(s, k);
                if (v != null) { values.Set(k, v); n++; }
            }
            return n != 0 ? new JsObject(("format", f!.Format), ("version", 1), ("values", values)) : null;
        }
        if (kind == "progress")
        {
            var cleared = GetJSON(s, ClearedKey);
            var talismans = GetJSON(s, TalismansKey);
            if (Js.OwnKeys(cleared).Count == 0 && Js.OwnKeys(talismans).Count == 0) return null;
            return new JsObject(("format", f!.Format), ("version", 1), ("cleared", cleared), ("talismans", talismans));
        }
        return null;
    }

    // A file's object written into the store, replacing what was there. Throws
    // ConfigFileException on a file of the wrong kind. Preferences the file does not name are
    // left as they are.
    public static void Apply(IStorage s, string kind, object? data)
    {
        var f = FileOf(kind) ?? throw new ArgumentException("no such file: " + kind);
        if (!Js.Truthy(data) || Js.TypeOf(data) != "object") throw new ConfigFileException("not a " + kind + " file");
        var format = Js.Get(data, "format");
        if (Js.Truthy(format) && !Js.StrictEquals(format, f.Format)) throw new ConfigFileException("not a " + kind + " file");
        if (kind == "controls")
        {
            var keys = Js.Get(data, "keys");
            if (!Js.Truthy(keys) || Js.TypeOf(keys) != "object") throw new ConfigFileException("not a controls file");
            // `vr` says the file carried the controllers; without it HotkeyManager puts the
            // default controller setup back rather than leaving them dead
            bool vr = Js.OwnKeys(keys).Any(code => code.StartsWith("Vr", StringComparison.Ordinal));
            s.SetItem(HotkeysKey, JsJson.Stringify(new JsObject(("version", Js.Get(data, "version")), ("vr", vr), ("keys", keys)))!);
        }
        else if (kind == "prefs")
        {
            var values = Js.Get(data, "values");
            if (!Js.Truthy(values) || Js.TypeOf(values) != "object") throw new ConfigFileException("not a preferences file");
            foreach (var k in PrefsKeys) if (Js.Get(values, k) is string v) s.SetItem(k, v);
        }
        else if (kind == "progress")
        {
            var cleared = Js.Get(data, "cleared");
            if (!Js.Truthy(cleared) || Js.TypeOf(cleared) != "object") throw new ConfigFileException("not a progress file");
            s.SetItem(ClearedKey, JsJson.Stringify(cleared)!);
            var talismans = Js.Get(data, "talismans");
            s.SetItem(TalismansKey, JsJson.Stringify(Js.Truthy(talismans) && Js.TypeOf(talismans) == "object" ? talismans : new JsObject())!);
        }
    }

    // ---------------------------------------------------------------- setup.js

    static object? ParseJSON(string text, string name)
    {
        try { return JsJson.Parse(text); } catch (JsSyntaxError) { throw new ConfigFileException(name + " is not a JSON file"); }
    }

    public static ConfigDownload ExportControls(HotkeyManager controls) => new(Controls.Name, controls.ExportJSON());

    public static ConfigDownload ExportPrefs(IStorage s) =>
        new(Prefs.Name, JsJson.Stringify(Build(s, "prefs") ?? new JsObject(("format", Prefs.Format), ("version", 1), ("values", new JsObject())), 2)!);

    public static ConfigDownload ExportProgress(IStorage s) =>
        new(Progress.Name, JsJson.Stringify(Build(s, "progress") ?? new JsObject(("format", Progress.Format), ("version", 1), ("cleared", new JsObject()), ("talismans", new JsObject())), 2)!);

    // An uploaded controls file into the table (its validation is HotkeyManager.ImportJSON's).
    public static ConfigMessage ImportControls(HotkeyManager controls, string text, string name)
    {
        try
        {
            var r = controls.ImportJSON(text);
            return new ConfigMessage(name + ": " + r.Loaded + " bindings loaded" + (r.Skipped != 0 ? ", " + r.Skipped + " skipped" : "") +
                (r.Filled.Count > 0 ? ", " + string.Join(" and ", r.Filled) + " left at the default" : ""), false);
        }
        catch (Exception e) when (e is HotkeyImportException or JsTypeError) { return new ConfigMessage(name + ": " + e.Message, true); }
    }

    public static ConfigMessage ImportPrefs(IStorage s, string text, string name)
    {
        try
        {
            var data = ParseJSON(text, name);
            var values = Js.StrictEquals(Js.Get(data, "format"), Prefs.Format) ? Js.Get(data, "values") : null;
            if (!Js.StrictEquals(Js.Get(data, "format"), Prefs.Format) || !Js.Truthy(values) || Js.TypeOf(values) != "object")
                throw new ConfigFileException("not a preferences file");
            int n = 0;
            foreach (var k in PrefsKeys)
            {
                if (Js.Get(values, k) is string v) { s.SetItem(k, v); n++; }
            }
            return new ConfigMessage(name + ": " + n + " preferences loaded (in force when the game page reloads)", false);
        }
        catch (Exception e) when (e is ConfigFileException or JsTypeError) { return new ConfigMessage(name + ": " + e.Message, true); }
    }

    // JSON.parse(localStorage.getItem(k)) || {}, {} when it does not parse (setup.js readJSONKey)
    static object? ReadJSONKey(IStorage s, string k) => GetJSON(s, k);

    // Merge: the best of both for every level, so a file never loses a clear.
    public static ConfigMessage ImportProgress(IStorage s, string text, string name)
    {
        try
        {
            var data = ParseJSON(text, name);
            if (!Js.StrictEquals(Js.Get(data, "format"), Progress.Format) || !Js.Truthy(Js.Get(data, "cleared")) || Js.TypeOf(Js.Get(data, "cleared")) != "object")
                throw new ConfigFileException("not a progress file");
            var cleared = ReadJSONKey(s, ClearedKey);
            int n = 0;
            var theirs = Js.Get(data, "cleared");
            var entries = Js.OwnKeys(theirs).Select(id => (id, rec: Js.Get(theirs, id))).ToList();
            foreach (var (id, rec) in entries)
            {
                if (!Js.Truthy(rec) || Js.TypeOf(rec) != "object") continue;
                var mine = Js.Or(Js.Get(cleared, id), new JsObject(("best", null), ("clears", 0)));
                object? recBest = Js.Get(rec, "best");
                object? best = recBest is double ? recBest : null;
                object? mineBest = Js.Get(mine, "best");
                var merged = new JsObject();
                merged.Set("best", mineBest == null ? best : best == null ? mineBest : Js.MathMin(Js.ToNumber(mineBest), Js.ToNumber(best)));
                merged.Set("clears", Js.MathMax(Js.ToNumber(Js.Or(Js.Get(mine, "clears"), 0.0)), Js.ToNumber(Js.Or(Js.Get(rec, "clears"), 0.0))));
                double saved = Js.MathMax(Js.ToNumber(Js.Or(Js.Get(mine, "saved"), 0.0)), Js.ToNumber(Js.Or(Js.Get(rec, "saved"), 0.0)));
                if (Js.Truthy(saved)) merged.Set("saved", saved);
                Js.Set(cleared, id, merged);
                n++;
            }
            s.SetItem(ClearedKey, JsJson.Stringify(cleared)!);
            var talismans = ReadJSONKey(s, TalismansKey);
            var tsrc = Js.Or(Js.Get(data, "talismans"), new JsObject());
            var tentries = Js.OwnKeys(tsrc).Select(id => (id, list: Js.Get(tsrc, id))).ToList();
            foreach (var (id, list) in tentries)
            {
                if (list is not JsArray arr) continue;
                var items = Js.Spread(Js.Or(Js.Get(talismans, id), new JsArray()), "(talismans[id] || [])");
                items.AddRange(arr.Items);
                Js.Set(talismans, id, Js.UniqueArray(items));
            }
            s.SetItem(TalismansKey, JsJson.Stringify(talismans)!);
            return new ConfigMessage(name + ": " + n + " levels merged", false);
        }
        catch (Exception e) when (e is ConfigFileException or JsTypeError) { return new ConfigMessage(name + ": " + e.Message, true); }
    }
}
