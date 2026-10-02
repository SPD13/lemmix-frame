using Lemmix.Util;

namespace Lemmix.Parse;

// web/lemmix/js/parser.js - the NeoLemmix text format, as read by LemNeoParser.pas: one keyword
// per line, the value being everything after the first space; `$NAME` opens a section that
// `$END` closes (sections nest); `#` starts a comment; keys are case-insensitive; a key with
// no value is a flag. Level files (.nxlv), pack metadata (.nxmi), gadget and terrain metadata
// (.nxmo, .nxmt), themes (.nxtm) and sprite schemes (scheme.nxmi) all use it.
public sealed class NxEntry
{
    public NxEntry(string key, string value) { Key = key; Value = value; }
    public string Key { get; }
    public string Value { get; }
}

public sealed class NxSection
{
    public NxSection(string? name) { Name = name; }

    public string? Name { get; }                       // upper-case, null for the file itself
    public List<NxEntry> Entries { get; } = new();     // in file order
    public List<NxSection> Sections { get; } = new();  // child sections in file order

    // The value of a key - the last one, as NeoLemmix reads it - or null.
    public string? Get(string key)
    {
        key = JsString.Upper(key);
        for (int i = Entries.Count - 1; i >= 0; i--)
            if (Entries[i].Key == key) return Entries[i].Value;
        return null;
    }

    // Every value of a repeated key (LEVEL, TRACK, LINE ...).
    public List<string> GetAll(string key)
    {
        key = JsString.Upper(key);
        var all = new List<string>();
        foreach (var e in Entries) if (e.Key == key) all.Add(e.Value);
        return all;
    }

    // Whether the key appears at all - how flags are read.
    public bool Has(string key)
    {
        key = JsString.Upper(key);
        foreach (var e in Entries) if (e.Key == key) return true;
        return false;
    }

    // An integer value (decimal, or hex written as x1F), else the default.
    public int? Int(string key, int? dflt = null)
    {
        string? v = Get(key);
        if (v == null || v == "") return dflt;
        long? n = Number(v);
        return n == null ? dflt : checked((int)n.Value);
    }

    public int Int(string key, int dflt) => Int(key, (int?)dflt)!.Value;

    // An integer that was meant to be signed 16-bit (a coordinate).
    public int? Int16(string key, int? dflt = null)
    {
        int? n = Int(key, dflt);
        return n != null && n >= 32768 && n <= 65535 ? n - 65536 : n;
    }

    public int Int16(string key, int dflt) => Int16(key, (int?)dflt)!.Value;

    // All child sections of a name.
    public List<NxSection> SectionsNamed(string name)
    {
        name = JsString.Upper(name);
        var all = new List<NxSection>();
        foreach (var s in Sections) if (s.Name == name) all.Add(s);
        return all;
    }

    // The first child section of a name, or null.
    public NxSection? Section(string name)
    {
        name = JsString.Upper(name);
        foreach (var s in Sections) if (s.Name == name) return s;
        return null;
    }

    // NxSection.number: x1F (hex) or -?[0-9]+, else null (JS NaN).
    public static long? Number(string v)
    {
        v = JsString.Trim(v);
        if (v.Length > 1 && (v[0] == 'x' || v[0] == 'X') && v.Skip(1).All(JsString.IsHexDigit))
            return Convert.ToInt64(v.Substring(1), 16);
        int start = v.Length > 0 && v[0] == '-' ? 1 : 0;
        if (v.Length > start && v.Skip(start).All(JsString.IsAsciiDigit))
            return long.Parse(v, System.Globalization.CultureInfo.InvariantCulture);
        return null;
    }
}

public static class NxParser
{
    // Parse a whole file into its root section.
    public static NxSection Parse(string text)
    {
        var file = new NxSection(null);
        var stack = new List<NxSection> { file };
        foreach (string raw in text.Split('\n'))
        {
            string line = JsString.Trim(raw.EndsWith('\r') ? raw[..^1] : raw);
            if (line.Length == 0 || line[0] == '#') continue;
            if (line[0] == '$')
            {
                string name = JsString.Upper(JsString.Trim(line[1..]));
                if (name == "END")
                {
                    if (stack.Count > 1) stack.RemoveAt(stack.Count - 1);
                    continue;
                }
                var section = new NxSection(name);
                stack[^1].Sections.Add(section);
                stack.Add(section);
                continue;
            }
            int sp = line.IndexOf(' ');
            string key = JsString.Upper(sp < 0 ? line : line[..sp]);
            string value = sp < 0 ? "" : JsString.Trim(line[(sp + 1)..]);
            stack[^1].Entries.Add(new NxEntry(key, value));
        }
        return file;
    }

    // A NeoLemmix colour (xRRGGBB or RRGGBB) as 0xRRGGBB, or null.
    public static int? Color(string? v)
    {
        if (v == null) return null;
        v = JsString.Trim(v);
        if (v.Length > 0 && (v[0] == 'x' || v[0] == 'X')) v = v[1..];
        if (v.Length != 6 || !v.All(JsString.IsHexDigit)) return null;
        return Convert.ToInt32(v, 16);
    }
}
