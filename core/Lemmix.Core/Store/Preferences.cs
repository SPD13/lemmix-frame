using System.Text;

namespace Lemmix.Store;

// The preferences (ConfigFiles.PrefsKeys) as typed values over the store, read and written the way
// the web's modules do it, so a preferences file moves between the two unchanged:
//  - web/3d/js/app.js setting() (~136-145) and the state it builds at start (~146-185): a
//    switch is "on"/"off" in the store, anything else is its default; a URL parameter wins
//    ("", 1, on, true, yes = on) - the native app passes its own (command line) or none;
//    the colour blend (~157-166) and the environment (~167-176) map their older names;
//  - web/3d/js/audio.js GameAudio (~58-65, setVolume ~136, setEnabled ~344): the sound is
//    on unless "off", the volume parseFloat'ed and clamped to 0..1 (1 when not a number);
//  - library.js: lem3d-lib-order ("world" or "level"), lem3d-lib-path ("" by default),
//    lem3d-favorites (Favorites.cs);
//  - app.js ~2263-2284 lem3d-bar (the bar's place: {locked, pos, quat}, JSON);
//  - galleries.js lem3d-gal-open (kept as it is: the galleries are the web's);
//  - vfs.js ~493 lem3d-setup-seen (not a preference: the setup page was shown).
// Unported: a stored colour blend or environment named like an Object.prototype member
// ("constructor", "toString", ...) gives the web a function or an object rather than a name.
public sealed class Preferences
{
    readonly IStorage _store;
    readonly IReadOnlyDictionary<string, string> _params;

    public Preferences(IStorage store, IReadOnlyDictionary<string, string>? urlParams = null)
    {
        _store = store;
        _params = urlParams ?? new Dictionary<string, string>();
    }

    // URLSearchParams(query): the first value of each name, + and %xx decoded
    public static IReadOnlyDictionary<string, string> ParseParams(string? query)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(query)) return d;
        if (query[0] == '?') query = query[1..];
        foreach (var part in query.Split('&'))
        {
            if (part.Length == 0) continue;
            int eq = part.IndexOf('=');
            string k = Decode(eq < 0 ? part : part[..eq]);
            string v = eq < 0 ? "" : Decode(part[(eq + 1)..]);
            d.TryAdd(k, v);
        }
        return d;
    }

    static string Decode(string s)
    {
        var bytes = new List<byte>();
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '+') { bytes.Add((byte)' '); continue; }
            if (c == '%' && i + 2 < s.Length && Util.JsString.IsHexDigit(s[i + 1]) && Util.JsString.IsHexDigit(s[i + 2]))
            {
                bytes.Add(Convert.ToByte(s.Substring(i + 1, 2), 16));
                i += 2;
                continue;
            }
            bytes.AddRange(Encoding.UTF8.GetBytes(c.ToString()));
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    string? Get(string key)
    {
        try { return _store.GetItem(key); } catch (IOException) { return null; }
    }

    void Put(string key, string value)
    {
        try { _store.SetItem(key, value); } catch (IOException) { /* no storage */ }
    }

    // app.js setting(name, key, dflt): the URL first, then what was last toggled here
    public bool Setting(string name, string key, bool dflt)
    {
        if (_params.TryGetValue(name, out var p))
        {
            string v = Js.ToLower(p);
            return v == "" || v == "1" || v == "on" || v == "true" || v == "yes";
        }
        string? stored = Get(key);
        if (stored == "on") return true;
        if (stored == "off") return false;
        return dflt;
    }

    void Switch(string key, bool on) => Put(key, on ? "on" : "off");

    // the render switches (state.emboss … state.music), all on but the 2D view
    public bool Emboss { get => Setting("emboss", "lem3d-emboss", true); set => Switch("lem3d-emboss", value); }
    public bool Smooth { get => Setting("smooth", "lem3d-smooth", true); set => Switch("lem3d-smooth", value); }
    public bool SmoothTerrain { get => Setting("smoothterrain", "lem3d-smooth-terrain", true); set => Switch("lem3d-smooth-terrain", value); }
    public bool Doors { get => Setting("doors", "lem3d-doors", true); set => Switch("lem3d-doors", value); }
    public bool SkillBar { get => Setting("skillbar", "lem3d-skillbar", true); set => Switch("lem3d-skillbar", value); }
    public bool FlatSkills { get => Setting("flatskills", "lem3d-flatskills", true); set => Switch("lem3d-flatskills", value); }
    public bool Flat { get => Setting("flat", "lem3d-flat", false); set => Switch("lem3d-flat", value); }
    public bool Shadows { get => Setting("shadows", "lem3d-shadows", true); set => Switch("lem3d-shadows", value); }
    public bool Music { get => Setting("music", "lem3d-music", true); set => Switch("lem3d-music", value); }

    // the colour blend: "off", "soft" (the default) or "smooth"; "0" and "1" are older names
    public static readonly IReadOnlyList<string> ColorBlendLevels = new[] { "off", "soft", "smooth" };

    public string ColorBlend
    {
        get
        {
            string? raw = _params.TryGetValue("colorblend", out var p) ? p : Get("lem3d-color-blend");
            string s = raw ?? "null";
            return s switch { "off" => "off", "soft" => "soft", "smooth" => "smooth", "0" => "off", "1" => "soft", _ => "soft" };
        }
        set => Put("lem3d-color-blend", value);
    }

    // the room around the board: "none", "fog" (native: the haze alone) or "full" (older saved
    // states named more)
    public string Environment
    {
        get
        {
            string? raw = _params.TryGetValue("environment", out var p) ? p : Get("lem3d-environment");
            string s = Js.ToLower(raw ?? "null");
            return s switch
            {
                "none" or "off" or "0" or "false" => "none",
                "fog" => "fog",
                "full" or "ambient" or "1" or "2" or "true" or "on" => "full",
                _ => "full",
            };
        }
        set => Put("lem3d-environment", value);
    }

    // audio.js: the sound effects' switch
    public bool Sound { get => Get("lem3d-sound") != "off"; set => Put("lem3d-sound", value ? "on" : "off"); }

    // audio.js: the volume, 0..1
    public double Volume
    {
        get
        {
            string? vol = Get("lem3d-volume");
            double v = vol == null ? 1 : Js.MathMin(1, Js.MathMax(0, Js.ParseFloat(vol)));
            return double.IsFinite(v) ? v : 1; // NaN (not a number) -> 1
        }
    }

    // setVolume: clamped, stored as String(volume); returns what it keeps
    public double SetVolume(double v)
    {
        double volume = Js.MathMin(1, Js.MathMax(0, v));
        Put("lem3d-volume", Js.NumberToString(volume));
        return volume;
    }

    // library.js: a classic pack by level number or by world (the native app has no classic
    // packs, but the file carries it)
    public string LibOrder { get => Get("lem3d-lib-order") == "world" ? "world" : "level"; set => Put("lem3d-lib-order", value); }

    // library.js: the directory the library was left on
    public string LibPath { get => Get("lem3d-lib-path") is { Length: > 0 } p ? p : ""; set => Put("lem3d-lib-path", value); }

    // app.js: the bar's place as the player left it: JSON.parse of the stored text, null when
    // there is none or it does not parse
    public object? Bar
    {
        get
        {
            string? raw = Get("lem3d-bar");
            if (string.IsNullOrEmpty(raw)) return null;
            try { return JsJson.Parse(raw); } catch (JsSyntaxError) { return null; }
        }
    }

    // saveBarPrefs: { locked, pos: [x, y, z], quat: [x, y, z, w] }
    public void SaveBar(bool locked, double[] pos, double[] quat) =>
        Put("lem3d-bar", JsJson.Stringify(new JsObject(("locked", locked), ("pos", new JsArray(pos.Select(x => (object?)x))), ("quat", new JsArray(quat.Select(x => (object?)x)))))!);

    // galleries.js: the open directories, as the web left them
    public string? GalleriesOpen => Get("lem3d-gal-open");

    // vfs.js: the setup page was shown (any non-empty value)
    public bool SetupSeen => !string.IsNullOrEmpty(Get("lem3d-setup-seen"));
    public void MarkSetupSeen() => Put("lem3d-setup-seen", "1");

    // the start-up state's switches as app.js builds them (the oracle's order)
    public JsObject ReadState() => new(
        ("emboss", Emboss), ("smooth", Smooth), ("smoothTerrain", SmoothTerrain), ("doors", Doors), ("skillBar", SkillBar),
        ("flatSkills", FlatSkills), ("flat", Flat), ("shadows", Shadows), ("music", Music),
        ("colorBlend", ColorBlend), ("environment", Environment));
}
