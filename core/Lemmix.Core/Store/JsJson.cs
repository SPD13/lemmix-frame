using System.Globalization;
using System.Text;

namespace Lemmix.Store;

// JSON.parse and JSON.stringify as V8 has them, over the values of JsValue.cs. Not
// System.Text.Json: that one escapes differently (non-ASCII, <, >, +), rejects duplicate keys and
// lone surrogates, and does not order integer-like keys first - and the player's files are
// compared byte for byte with the web's.
public sealed class JsSyntaxError : Exception
{
    public JsSyntaxError(string message) : base(message) { }
}

public static class JsJson
{
    // ---------------------------------------------------------------- parse

    // JSON.parse(text). JSON.parse(null) parses "null" (localStorage.getItem of a missing key).
    public static object? Parse(string? text)
    {
        if (text == null) return null;
        var p = new Parser(text);
        p.Ws();
        var v = p.Value();
        p.Ws();
        if (p.At < text.Length) throw p.Error();
        return v;
    }

    sealed class Parser
    {
        readonly string _s;
        public int At;

        public Parser(string s) { _s = s; }

        public JsSyntaxError Error() => new("Unexpected token at position " + At);

        public void Ws()
        {
            while (At < _s.Length && _s[At] is ' ' or '\t' or '\n' or '\r') At++;
        }

        bool Lit(string word)
        {
            if (string.CompareOrdinal(_s, At, word, 0, word.Length) != 0 || At + word.Length > _s.Length) return false;
            At += word.Length;
            return true;
        }

        public object? Value()
        {
            if (At >= _s.Length) throw Error();
            char c = _s[At];
            switch (c)
            {
                case '{': return Obj();
                case '[': return Arr();
                case '"': return Str();
                case 't': if (Lit("true")) return true; throw Error();
                case 'f': if (Lit("false")) return false; throw Error();
                case 'n': if (Lit("null")) return null; throw Error();
                default:
                    if (c == '-' || c >= '0' && c <= '9') return Num();
                    throw Error();
            }
        }

        JsObject Obj()
        {
            At++;
            var o = new JsObject();
            Ws();
            if (At < _s.Length && _s[At] == '}') { At++; return o; }
            while (true)
            {
                Ws();
                if (At >= _s.Length || _s[At] != '"') throw Error();
                string k = Str();
                Ws();
                if (At >= _s.Length || _s[At] != ':') throw Error();
                At++;
                Ws();
                o.Set(k, Value()); // a repeated key keeps its first place and takes the last value
                Ws();
                if (At >= _s.Length) throw Error();
                if (_s[At] == ',') { At++; continue; }
                if (_s[At] == '}') { At++; return o; }
                throw Error();
            }
        }

        JsArray Arr()
        {
            At++;
            var a = new JsArray();
            Ws();
            if (At < _s.Length && _s[At] == ']') { At++; return a; }
            while (true)
            {
                Ws();
                a.Add(Value());
                Ws();
                if (At >= _s.Length) throw Error();
                if (_s[At] == ',') { At++; continue; }
                if (_s[At] == ']') { At++; return a; }
                throw Error();
            }
        }

        string Str()
        {
            At++;
            var sb = new StringBuilder();
            while (true)
            {
                if (At >= _s.Length) throw Error();
                char c = _s[At++];
                if (c == '"') return sb.ToString();
                if (c < 0x20) throw Error();
                if (c != '\\') { sb.Append(c); continue; }
                if (At >= _s.Length) throw Error();
                char e = _s[At++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (At + 4 > _s.Length) throw Error();
                        int v = 0;
                        for (int i = 0; i < 4; i++)
                        {
                            char h = _s[At + i];
                            int d = h >= '0' && h <= '9' ? h - '0' : h >= 'a' && h <= 'f' ? h - 'a' + 10 : h >= 'A' && h <= 'F' ? h - 'A' + 10 : -1;
                            if (d < 0) throw Error();
                            v = v * 16 + d;
                        }
                        At += 4;
                        sb.Append((char)v);
                        break;
                    default: throw Error();
                }
            }
        }

        double Num()
        {
            int start = At;
            if (_s[At] == '-') At++;
            if (At >= _s.Length) throw Error();
            if (_s[At] == '0') At++;
            else if (_s[At] >= '1' && _s[At] <= '9') { while (At < _s.Length && _s[At] >= '0' && _s[At] <= '9') At++; }
            else throw Error();
            if (At < _s.Length && _s[At] == '.')
            {
                At++;
                int d = At;
                while (At < _s.Length && _s[At] >= '0' && _s[At] <= '9') At++;
                if (At == d) throw Error();
            }
            if (At < _s.Length && (_s[At] == 'e' || _s[At] == 'E'))
            {
                At++;
                if (At < _s.Length && (_s[At] == '+' || _s[At] == '-')) At++;
                int d = At;
                while (At < _s.Length && _s[At] >= '0' && _s[At] <= '9') At++;
                if (At == d) throw Error();
            }
            return double.Parse(_s.AsSpan(start, At - start), NumberStyles.Float, CultureInfo.InvariantCulture);
        }
    }

    // ---------------------------------------------------------------- stringify

    // JSON.stringify(value) / JSON.stringify(value, null, indent): null for undefined (JS gives
    // undefined back). Undefined properties are left out, undefined items written null, numbers
    // that are not finite written null.
    public static string? Stringify(object? value, int indent = 0)
    {
        value = Js.Norm(value);
        if (value is JsUndefined) return null;
        var sb = new StringBuilder();
        Write(sb, value, new string(' ', Math.Min(10, indent)), "");
        return sb.ToString();
    }

    static void Write(StringBuilder sb, object? v, string gap, string indent)
    {
        switch (Js.Norm(v))
        {
            case null: sb.Append("null"); break;
            case bool b: sb.Append(b ? "true" : "false"); break;
            case double d: sb.Append(double.IsFinite(d) ? Js.NumberToString(d) : "null"); break;
            case string s: Quote(sb, s); break;
            case JsArray a:
                {
                    if (a.Count == 0) { sb.Append("[]"); break; }
                    string inner = indent + gap;
                    sb.Append('[');
                    for (int i = 0; i < a.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        if (gap.Length > 0) sb.Append('\n').Append(inner);
                        var x = a.Items[i];
                        if (x is JsUndefined) sb.Append("null");
                        else Write(sb, x, gap, inner);
                    }
                    if (gap.Length > 0) sb.Append('\n').Append(indent);
                    sb.Append(']');
                    break;
                }
            case JsObject o:
                {
                    string inner = indent + gap;
                    bool any = false;
                    sb.Append('{');
                    foreach (var k in o.Keys)
                    {
                        var x = o.Get(k);
                        if (x is JsUndefined) continue;
                        if (any) sb.Append(',');
                        any = true;
                        if (gap.Length > 0) sb.Append('\n').Append(inner);
                        Quote(sb, k);
                        sb.Append(gap.Length > 0 ? ": " : ":");
                        Write(sb, x, gap, inner);
                    }
                    if (any && gap.Length > 0) sb.Append('\n').Append(indent);
                    sb.Append('}');
                    break;
                }
            case JsUndefined: sb.Append("null"); break;
            default: throw new ArgumentException("not a JS value: " + v!.GetType().Name);
        }
    }

    // QuoteJSONString (well-formed: lone surrogates escaped)
    public static void Quote(StringBuilder sb, string s)
    {
        sb.Append('"');
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { sb.Append(c).Append(s[i + 1]); i++; }
                    else if (char.IsSurrogate(c)) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }
}
