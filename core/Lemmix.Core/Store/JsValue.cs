using System.Globalization;
using System.Numerics;
using System.Text;
using Lemmix.Util;

namespace Lemmix.Store;

// The JavaScript values the player's files hold, and the JS operations the settings code applies
// to them (web/3d/js/config-store.js, setup.js, hotkeys.js, library.js, app.js). Those files are
// written by one version and read by the other, and are the player's to edit: whatever JSON they
// carry, the port must do what the web does with it - a "best" that is a string, a "keys" table
// that is an array, a store entry that is not JSON at all.
//
// A value is one of: null (JSON null), JsUndefined.Value, bool, double (every number), string,
// JsArray, JsObject. int and long are accepted where a value is made and read as doubles.

public sealed class JsUndefined
{
    public static readonly JsUndefined Value = new();
    JsUndefined() { }
    public override string ToString() => "undefined";
}

// A TypeError the JS code would throw (its V8 message, which setup.js shows the player).
public sealed class JsTypeError : Exception
{
    public JsTypeError(string message) : base(message) { }
}

// A plain object. Own keys enumerate as JS orders them: array-index keys ("0".."4294967294")
// ascending first, then the others in insertion order; replacing a value keeps its place.
public sealed class JsObject
{
    readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);
    readonly List<string> _order = new();

    public JsObject() { }

    public JsObject(params (string Key, object? Value)[] props)
    {
        foreach (var (k, v) in props) Set(k, v);
    }

    public int Count => _values.Count;
    public bool Has(string key) => _values.ContainsKey(key);
    public object? Get(string key) => _values.TryGetValue(key, out var v) ? v : JsUndefined.Value;

    public void Set(string key, object? value)
    {
        if (!_values.ContainsKey(key)) _order.Add(key);
        _values[key] = Js.Norm(value);
    }

    public bool Remove(string key)
    {
        if (!_values.Remove(key)) return false;
        _order.Remove(key);
        return true;
    }

    public object? this[string key] { get => Get(key); set => Set(key, value); }

    public List<string> Keys
    {
        get
        {
            var idx = new List<(uint, string)>();
            var rest = new List<string>();
            foreach (var k in _order)
            {
                if (Js.IsArrayIndex(k, out uint i)) idx.Add((i, k));
                else rest.Add(k);
            }
            var keys = idx.OrderBy(p => p.Item1).Select(p => p.Item2).ToList();
            keys.AddRange(rest);
            return keys;
        }
    }
}

// An array. Properties that are not indices (a = []; a["x"] = 1) live beside the items, as JS
// keeps them, and are left out of JSON.
public sealed class JsArray
{
    public readonly List<object?> Items;
    public JsObject? Props;

    public JsArray() { Items = new List<object?>(); }
    public JsArray(IEnumerable<object?> items) { Items = items.Select(Js.Norm).ToList(); }

    public int Count => Items.Count;
    public object? this[int i] => Items[i];
    public void Add(object? v) => Items.Add(Js.Norm(v));
}

public static class Js
{
    public static readonly object Undefined = JsUndefined.Value;

    // ints and longs as the doubles JS has
    public static object? Norm(object? v) => v switch
    {
        int i => (double)i,
        long l => (double)l,
        float f => (double)f,
        _ => v,
    };

    public static bool IsUndefined(object? v) => v is JsUndefined;
    public static bool IsNullish(object? v) => v == null || v is JsUndefined;

    // ToPropertyKey of an array index: a canonical integer in [0, 2^32 - 2]
    public static bool IsArrayIndex(string k, out uint index)
    {
        index = 0;
        if (k.Length == 0 || k.Length > 10) return false;
        if (k.Length > 1 && k[0] == '0') return false;
        ulong n = 0;
        foreach (char c in k)
        {
            if (c < '0' || c > '9') return false;
            n = n * 10 + (ulong)(c - '0');
        }
        if (n > 4294967294UL) return false;
        index = (uint)n;
        return true;
    }

    public static string TypeOf(object? v) => v switch
    {
        null => "object",
        JsUndefined => "undefined",
        bool => "boolean",
        double or int or long => "number",
        string => "string",
        _ => "object",
    };

    public static bool Truthy(object? v) => v switch
    {
        null or JsUndefined => false,
        bool b => b,
        double d => !(d == 0 || double.IsNaN(d)),
        int i => i != 0,
        long l => l != 0,
        string s => s.Length > 0,
        _ => true,
    };

    // a || b
    public static object? Or(object? a, object? b) => Truthy(a) ? a : b;

    // ---------------------------------------------------------------- properties

    static string Describe(object? v) => v == null ? "null" : "undefined";

    // o[key] (reading). Strings answer "length" and their indices; arrays their items and
    // "length"; other primitives nothing (the prototype methods are not modelled).
    public static object? Get(object? o, string key)
    {
        switch (o)
        {
            case null:
            case JsUndefined:
                throw new JsTypeError("Cannot read properties of " + Describe(o) + " (reading '" + key + "')");
            case JsObject obj:
                return obj.Get(key);
            case JsArray arr:
                if (key == "length") return (double)arr.Count;
                if (IsArrayIndex(key, out uint i)) return i < arr.Count ? arr.Items[(int)i] : Undefined;
                return arr.Props?.Get(key) ?? Undefined;
            case string s:
                if (key == "length") return (double)s.Length;
                if (IsArrayIndex(key, out uint j)) return j < s.Length ? s[(int)j].ToString() : Undefined;
                return Undefined;
            default:
                return Undefined;
        }
    }

    // o[key] = v, in strict mode (every module concerned is "use strict").
    public static void Set(object? o, string key, object? v)
    {
        switch (o)
        {
            case null:
            case JsUndefined:
                throw new JsTypeError("Cannot set properties of " + Describe(o) + " (setting '" + key + "')");
            case JsObject obj:
                obj.Set(key, v);
                return;
            case JsArray arr:
                if (IsArrayIndex(key, out uint i))
                {
                    while (arr.Items.Count <= (int)i) arr.Items.Add(Undefined); // a hole reads as undefined
                    arr.Items[(int)i] = Norm(v);
                }
                else if (key == "length")
                {
                    double n = ToNumber(v);
                    if (n >= 0 && n == Math.Floor(n) && n < arr.Count) arr.Items.RemoveRange((int)n, arr.Count - (int)n);
                }
                else (arr.Props ??= new JsObject()).Set(key, v);
                return;
            case string s:
                if (key == "length" || IsArrayIndex(key, out uint j) && j < s.Length)
                    throw new JsTypeError("Cannot assign to read only property '" + key + "' of string '" + s + "'");
                throw new JsTypeError("Cannot create property '" + key + "' on string '" + s + "'");
            default:
                throw new JsTypeError("Cannot create property '" + key + "' on " + TypeOf(o) + " '" + ToStr(o) + "'");
        }
    }

    // Object.keys
    public static List<string> OwnKeys(object? o)
    {
        switch (o)
        {
            case null:
            case JsUndefined:
                throw new JsTypeError("Cannot convert undefined or null to object");
            case JsObject obj:
                return obj.Keys;
            case JsArray arr:
                {
                    var keys = new List<string>(arr.Count);
                    for (int i = 0; i < arr.Count; i++) keys.Add(i.ToString(CultureInfo.InvariantCulture));
                    if (arr.Props != null) keys.AddRange(arr.Props.Keys);
                    return keys;
                }
            case string s:
                {
                    var keys = new List<string>(s.Length);
                    for (int i = 0; i < s.Length; i++) keys.Add(i.ToString(CultureInfo.InvariantCulture));
                    return keys;
                }
            default:
                return new List<string>();
        }
    }

    // [...v]: an array's items, a string's code points; anything else is not iterable. `source`
    // is the expression as the JS has it, which V8 puts in its message.
    public static List<object?> Spread(object? v, string source)
    {
        if (v is JsArray a) return new List<object?>(a.Items);
        if (v is string s) return CodePoints(s).Select(c => (object?)c).ToList();
        throw new JsTypeError(source + " is not iterable");
    }

    public static IEnumerable<string> CodePoints(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                yield return s.Substring(i, 2);
                i++;
            }
            else yield return s[i].ToString();
        }
    }

    // Array.from(new Set(items)): the first of each, by SameValueZero
    public static JsArray UniqueArray(IEnumerable<object?> items)
    {
        var outList = new JsArray();
        foreach (var v in items)
        {
            bool seen = false;
            foreach (var w in outList.Items) if (SameValueZero(v, w)) { seen = true; break; }
            if (!seen) outList.Add(v);
        }
        return outList;
    }

    // ---------------------------------------------------------------- equality and arithmetic

    public static bool StrictEquals(object? a, object? b)
    {
        a = Norm(a); b = Norm(b);
        return (a, b) switch
        {
            (null, null) => true,
            (JsUndefined, JsUndefined) => true,
            (bool x, bool y) => x == y,
            (double x, double y) => x == y,
            (string x, string y) => string.Equals(x, y, StringComparison.Ordinal),
            _ => a != null && b != null && ReferenceEquals(a, b),
        };
    }

    public static bool SameValueZero(object? a, object? b)
    {
        a = Norm(a); b = Norm(b);
        if (a is double x && b is double y && double.IsNaN(x) && double.IsNaN(y)) return true;
        return StrictEquals(a, b);
    }

    // ToPrimitive (hint default/number): objects through their toString
    public static object? ToPrimitive(object? v) => v switch
    {
        JsArray or JsObject => ToStr(v),
        _ => Norm(v),
    };

    public static double ToNumber(object? v) => Norm(v) switch
    {
        null => 0,
        JsUndefined => double.NaN,
        bool b => b ? 1 : 0,
        double d => d,
        string s => StringToNumber(s),
        var o => StringToNumber(ToStr(o)),
    };

    public static int ToInt32(object? v) => JsMath.ToInt32(ToNumber(v));

    // a + b
    public static object Add(object? a, object? b)
    {
        var pa = ToPrimitive(a);
        var pb = ToPrimitive(b);
        if (pa is string || pb is string) return ToStr(pa) + ToStr(pb);
        return ToNumber(pa) + ToNumber(pb);
    }

    // a < b
    public static bool LessThan(object? a, object? b)
    {
        var pa = ToPrimitive(a);
        var pb = ToPrimitive(b);
        if (pa is string sa && pb is string sb) return string.CompareOrdinal(sa, sb) < 0;
        double x = ToNumber(pa), y = ToNumber(pb);
        return x < y; // false when either is NaN
    }

    // Math.max / Math.min over ToNumber'd values: NaN wins, +0 above -0
    public static double MathMax(params double[] xs)
    {
        double r = double.NegativeInfinity;
        foreach (var x in xs)
        {
            if (double.IsNaN(x)) return double.NaN;
            if (x > r || x == 0 && r == 0 && !double.IsNegative(x)) r = x;
        }
        return r;
    }

    public static double MathMin(params double[] xs)
    {
        double r = double.PositiveInfinity;
        foreach (var x in xs)
        {
            if (double.IsNaN(x)) return double.NaN;
            if (x < r || x == 0 && r == 0 && double.IsNegative(x)) r = x;
        }
        return r;
    }

    // ---------------------------------------------------------------- to string

    // String(v)
    public static string ToStr(object? v) => Norm(v) switch
    {
        null => "null",
        JsUndefined => "undefined",
        bool b => b ? "true" : "false",
        double d => NumberToString(d),
        string s => s,
        JsArray a => string.Join(",", a.Items.Select(x => IsNullish(x) ? "" : ToStr(x))),
        _ => "[object Object]",
    };

    // Number.prototype.toString(): the shortest digits that read back (as .NET's "R" gives
    // them), laid out by ECMAScript's Number::toString rules.
    public static string NumberToString(double v)
    {
        if (double.IsNaN(v)) return "NaN";
        if (v == 0) return "0";
        if (v < 0) return "-" + NumberToString(-v);
        if (double.IsPositiveInfinity(v)) return "Infinity";
        string r = v.ToString("R", CultureInfo.InvariantCulture);
        int e = r.IndexOfAny(new[] { 'E', 'e' });
        string mant = e < 0 ? r : r[..e];
        int exp = e < 0 ? 0 : int.Parse(r[(e + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        int dot = mant.IndexOf('.');
        string digits = dot < 0 ? mant : mant.Remove(dot, 1);
        int n = (dot < 0 ? mant.Length : dot) + exp; // the point sits after n digits
        int lead = 0;
        while (lead < digits.Length - 1 && digits[lead] == '0') lead++;
        digits = digits[lead..];
        n -= lead;
        digits = digits.TrimEnd('0');
        if (digits.Length == 0) return "0";
        int k = digits.Length;
        if (k <= n && n <= 21) return digits + new string('0', n - k);
        if (0 < n && n <= 21) return digits[..n] + "." + digits[n..];
        if (-6 < n && n <= 0) return "0." + new string('0', -n) + digits;
        int ex = n - 1;
        string es = (ex < 0 ? "-" : "+") + Math.Abs(ex).ToString(CultureInfo.InvariantCulture);
        return k == 1 ? digits + "e" + es : digits[0] + "." + digits[1..] + "e" + es;
    }

    // ---------------------------------------------------------------- from string

    static bool AllDigits(string s, int from, int to, Func<char, bool> ok)
    {
        if (to <= from) return false;
        for (int i = from; i < to; i++) if (!ok(s[i])) return false;
        return true;
    }

    // StringToNumber (Number("…"), the unary + and every arithmetic coercion)
    public static double StringToNumber(string str)
    {
        string s = JsString.Trim(str);
        if (s.Length == 0) return 0;
        if (s.Length > 2 && s[0] == '0')
        {
            char p = s[1];
            int radix = p is 'x' or 'X' ? 16 : p is 'o' or 'O' ? 8 : p is 'b' or 'B' ? 2 : 0;
            if (radix != 0)
            {
                Func<char, bool> ok = radix == 16 ? JsString.IsHexDigit : radix == 8 ? (c => c >= '0' && c <= '7') : (c => c is '0' or '1');
                if (!AllDigits(s, 2, s.Length, ok)) return double.NaN;
                BigInteger n = 0;
                for (int i = 2; i < s.Length; i++) n = n * radix + Convert.ToInt32(s[i].ToString(), 16);
                return (double)n;
            }
        }
        if (s == "Infinity" || s == "+Infinity") return double.PositiveInfinity;
        if (s == "-Infinity") return double.NegativeInfinity;
        int len = DecimalPrefix(s, 0);
        if (len != s.Length) return double.NaN;
        return double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    // The length of the StrDecimalLiteral (without Infinity) at `at`: [+-] (digits [. digits] | . digits) [e [+-] digits]; 0 when none
    static int DecimalPrefix(string s, int at)
    {
        int i = at;
        if (i < s.Length && (s[i] == '+' || s[i] == '-')) i++;
        int intStart = i;
        while (i < s.Length && JsString.IsAsciiDigit(s[i])) i++;
        int intDigits = i - intStart;
        int fracDigits = 0;
        if (i < s.Length && s[i] == '.')
        {
            int f = i + 1;
            while (f < s.Length && JsString.IsAsciiDigit(s[f])) f++;
            fracDigits = f - i - 1;
            if (intDigits > 0 || fracDigits > 0) i = f;
        }
        if (intDigits == 0 && fracDigits == 0) return 0;
        if (i < s.Length && (s[i] == 'e' || s[i] == 'E'))
        {
            int e = i + 1;
            if (e < s.Length && (s[e] == '+' || s[e] == '-')) e++;
            int ds = e;
            while (e < s.Length && JsString.IsAsciiDigit(s[e])) e++;
            if (e > ds) i = e;
        }
        return i - at;
    }

    // parseFloat
    public static double ParseFloat(string str)
    {
        int a = 0;
        while (a < str.Length && JsString.IsJsSpace(str[a])) a++;
        string s = str[a..];
        int sign = 0;
        if (s.Length > 0 && (s[0] == '+' || s[0] == '-')) sign = 1;
        if (s.AsSpan(sign).StartsWith("Infinity", StringComparison.Ordinal))
            return sign == 1 && s[0] == '-' ? double.NegativeInfinity : double.PositiveInfinity;
        int len = DecimalPrefix(s, 0);
        if (len == 0) return double.NaN;
        return double.Parse(s[..len], NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    // ---------------------------------------------------------------- case mapping

    // String.prototype.toLowerCase: Unicode's full mapping (U+0130 to two code points, a final
    // capital sigma to ς), per code point; lone surrogates stay.
    public static string ToLower(string s)
    {
        StringBuilder? sb = null;
        for (int i = 0; i < s.Length; i++)
        {
            int cp = s[i];
            int width = 1;
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { cp = char.ConvertToUtf32(s[i], s[i + 1]); width = 2; }
            string? m = null;
            if (cp == 0x130) m = "i̇";
            else if (cp == 0x3A3) m = FinalSigma(s, i) ? "ς" : "σ";
            else if (!(width == 1 && char.IsSurrogate(s[i])))
            {
                int l = CaseTables.Lower(cp);
                if (l != cp) m = char.ConvertFromUtf32(l);
            }
            if (m != null)
            {
                sb ??= new StringBuilder(s, 0, i, s.Length + 4);
                sb.Append(m);
            }
            else sb?.Append(s, i, width);
            i += width - 1;
        }
        return sb?.ToString() ?? s;
    }

    // String.prototype.toUpperCase: the full mapping (ß to SS, the ligatures, Greek with ypogegrammeni)
    public static string ToUpper(string s)
    {
        StringBuilder? sb = null;
        for (int i = 0; i < s.Length; i++)
        {
            int cp = s[i];
            int width = 1;
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { cp = char.ConvertToUtf32(s[i], s[i + 1]); width = 2; }
            string? m = null;
            if (!(width == 1 && char.IsSurrogate(s[i])))
            {
                m = CaseTables.SpecialUpper(cp);
                if (m == null)
                {
                    int u = CaseTables.Upper(cp);
                    if (u != cp) m = char.ConvertFromUtf32(u);
                }
            }
            if (m != null)
            {
                sb ??= new StringBuilder(s, 0, i, s.Length + 4);
                sb.Append(m);
            }
            else sb?.Append(s, i, width);
            i += width - 1;
        }
        return sb?.ToString() ?? s;
    }

    static int CodePointBefore(string s, int i, out int start)
    {
        start = i - 1;
        if (start > 0 && char.IsLowSurrogate(s[start]) && char.IsHighSurrogate(s[start - 1])) { start--; return char.ConvertToUtf32(s[start], s[start + 1]); }
        return s[start];
    }

    static int CodePointAt(string s, int i, out int width)
    {
        width = 1;
        if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { width = 2; return char.ConvertToUtf32(s[i], s[i + 1]); }
        return s[i];
    }

    // Final_Sigma: a cased letter before (case-ignorables skipped), none after
    static bool FinalSigma(string s, int at)
    {
        bool before = false;
        for (int i = at; i > 0;)
        {
            int cp = CodePointBefore(s, i, out int start);
            i = start;
            if (CaseTables.IsCaseIgnorable(cp)) continue;
            before = CaseTables.IsCased(cp);
            break;
        }
        if (!before) return false;
        for (int i = at + 1; i < s.Length;)
        {
            int cp = CodePointAt(s, i, out int w);
            i += w;
            if (CaseTables.IsCaseIgnorable(cp)) continue;
            return !CaseTables.IsCased(cp);
        }
        return true;
    }

    // ---------------------------------------------------------------- URIs

    // encodeURIComponent; a lone surrogate is a URIError
    public static string EncodeURIComponent(string s)
    {
        var sb = new StringBuilder(s.Length);
        Span<byte> buf = stackalloc byte[4];
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' || "-_.!~*'()".IndexOf(c) >= 0) { sb.Append(c); continue; }
            int cp;
            if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { cp = char.ConvertToUtf32(c, s[i + 1]); i++; }
            else if (char.IsSurrogate(c)) throw new JsUriError("URI malformed");
            else cp = c;
            int n = new Rune(cp).EncodeToUtf8(buf);
            for (int j = 0; j < n; j++) sb.Append('%').Append(buf[j].ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }
}

public sealed class JsUriError : Exception
{
    public JsUriError(string message) : base(message) { }
}
