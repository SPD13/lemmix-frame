namespace Lemmix.Util;

// JavaScript string semantics the parsers rely on.
public static class JsString
{
    // String.prototype.trim: ECMAScript WhiteSpace + LineTerminator. Unlike .NET's Trim it
    // removes U+FEFF (a BOM) and keeps U+0085.
    public static bool IsJsSpace(char c)
    {
        int u = c;
        return u is 0x09 or 0x0A or 0x0B or 0x0C or 0x0D or 0x20 or 0xA0 or 0x1680 or 0x2028 or 0x2029
            or 0x202F or 0x205F or 0x3000 or 0xFEFF
            || u >= 0x2000 && u <= 0x200A;
    }

    public static string Trim(string s)
    {
        int a = 0, b = s.Length;
        while (a < b && IsJsSpace(s[a])) a++;
        while (b > a && IsJsSpace(s[b - 1])) b--;
        return a == 0 && b == s.Length ? s : s.Substring(a, b - a);
    }

    // toUpperCase on the ASCII-only keys of the NeoLemmix formats. (JS maps ß to SS and a few
    // other one-to-many cases that invariant upper-casing keeps; no key or name uses them.)
    public static string Upper(string s) => s.ToUpperInvariant();

    public static bool IsAsciiDigit(char c) => c >= '0' && c <= '9';
    public static bool IsHexDigit(char c) => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
}
