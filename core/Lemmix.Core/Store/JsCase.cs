using System.Globalization;
using System.Text;

namespace Lemmix.Store;

// The case mappings of JS's toLowerCase/toUpperCase (V8, ICU's Unicode version) per code point.
// .NET's invariant Rune mapping is Unicode's simple mapping; on top of it: the unconditional
// one-to-many uppercasings of SpecialCasing.txt, and the code points whose mapping the two
// Unicode versions disagree on. The oracle (oracle/settings.js "primitives") lists every code
// point either mapping changes, so the test says when a table here is short.
static class CaseTables
{
    // cp:replacement code points (SpecialCasing.txt, unconditional uppercase)
    const string SpecialUpperList =
        "df:53 53,149:2bc 4e,1f0:4a 30c,390:399 308 301,3b0:3a5 308 301,587:535 552,1e96:48 331,1e97:54 308,1e98:57 30a," +
        "1e99:59 30a,1e9a:41 2be,1f50:3a5 313,1f52:3a5 313 300,1f54:3a5 313 301,1f56:3a5 313 342,1f80:1f08 399,1f81:1f09 399," +
        "1f82:1f0a 399,1f83:1f0b 399,1f84:1f0c 399,1f85:1f0d 399,1f86:1f0e 399,1f87:1f0f 399,1f88:1f08 399,1f89:1f09 399," +
        "1f8a:1f0a 399,1f8b:1f0b 399,1f8c:1f0c 399,1f8d:1f0d 399,1f8e:1f0e 399,1f8f:1f0f 399,1f90:1f28 399,1f91:1f29 399," +
        "1f92:1f2a 399,1f93:1f2b 399,1f94:1f2c 399,1f95:1f2d 399,1f96:1f2e 399,1f97:1f2f 399,1f98:1f28 399,1f99:1f29 399," +
        "1f9a:1f2a 399,1f9b:1f2b 399,1f9c:1f2c 399,1f9d:1f2d 399,1f9e:1f2e 399,1f9f:1f2f 399,1fa0:1f68 399,1fa1:1f69 399," +
        "1fa2:1f6a 399,1fa3:1f6b 399,1fa4:1f6c 399,1fa5:1f6d 399,1fa6:1f6e 399,1fa7:1f6f 399,1fa8:1f68 399,1fa9:1f69 399," +
        "1faa:1f6a 399,1fab:1f6b 399,1fac:1f6c 399,1fad:1f6d 399,1fae:1f6e 399,1faf:1f6f 399,1fb2:1fba 399,1fb3:391 399," +
        "1fb4:386 399,1fb6:391 342,1fb7:391 342 399,1fbc:391 399,1fc2:1fca 399,1fc3:397 399,1fc4:389 399,1fc6:397 342," +
        "1fc7:397 342 399,1fcc:397 399,1fd2:399 308 300,1fd3:399 308 301,1fd6:399 342,1fd7:399 308 342,1fe2:3a5 308 300," +
        "1fe3:3a5 308 301,1fe4:3a1 313,1fe6:3a5 342,1fe7:3a5 308 342,1ff2:1ffa 399,1ff3:3a9 399,1ff4:38f 399,1ff6:3a9 342," +
        "1ff7:3a9 342 399,1ffc:3a9 399,fb00:46 46,fb01:46 49,fb02:46 4c,fb03:46 46 49,fb04:46 46 4c,fb05:53 54,fb06:53 54," +
        "fb13:544 546,fb14:544 535,fb15:544 53b,fb16:54e 546,fb17:544 53d";

    // cp:mapped, where V8 (ICU 78, Unicode 17) and .NET 8's invariant casing disagree on a simple
    // mapping: the case pairs Unicode 16 added, and dotless i / long s, which .NET does not uppercase
    const string LowerFixList =
        "1c89:1c8a,a7cb:264,a7cc:a7cd,a7ce:a7cf,a7d2:a7d3,a7d4:a7d5,a7da:a7db,a7dc:19b,10d50:10d70,10d51:10d71,10d52:10d72,10d53:10d73," +
        "10d54:10d74,10d55:10d75,10d56:10d76,10d57:10d77,10d58:10d78,10d59:10d79,10d5a:10d7a,10d5b:10d7b,10d5c:10d7c,10d5d:10d7d,10d5e:10d7e,10d5f:10d7f," +
        "10d60:10d80,10d61:10d81,10d62:10d82,10d63:10d83,10d64:10d84,10d65:10d85,16ea0:16ebb,16ea1:16ebc,16ea2:16ebd,16ea3:16ebe,16ea4:16ebf,16ea5:16ec0," +
        "16ea6:16ec1,16ea7:16ec2,16ea8:16ec3,16ea9:16ec4,16eaa:16ec5,16eab:16ec6,16eac:16ec7,16ead:16ec8,16eae:16ec9,16eaf:16eca,16eb0:16ecb,16eb1:16ecc," +
        "16eb2:16ecd,16eb3:16ece,16eb4:16ecf,16eb5:16ed0,16eb6:16ed1,16eb7:16ed2,16eb8:16ed3";
    const string UpperFixList =
        "131:49,17f:53,19b:a7dc,264:a7cb,1c8a:1c89,a7cd:a7cc,a7cf:a7ce,a7d3:a7d2,a7d5:a7d4,a7db:a7da,10d70:10d50,10d71:10d51," +
        "10d72:10d52,10d73:10d53,10d74:10d54,10d75:10d55,10d76:10d56,10d77:10d57,10d78:10d58,10d79:10d59,10d7a:10d5a,10d7b:10d5b,10d7c:10d5c,10d7d:10d5d," +
        "10d7e:10d5e,10d7f:10d5f,10d80:10d60,10d81:10d61,10d82:10d62,10d83:10d63,10d84:10d64,10d85:10d65,16ebb:16ea0,16ebc:16ea1,16ebd:16ea2,16ebe:16ea3," +
        "16ebf:16ea4,16ec0:16ea5,16ec1:16ea6,16ec2:16ea7,16ec3:16ea8,16ec4:16ea9,16ec5:16eaa,16ec6:16eab,16ec7:16eac,16ec8:16ead,16ec9:16eae,16eca:16eaf," +
        "16ecb:16eb0,16ecc:16eb1,16ecd:16eb2,16ece:16eb3,16ecf:16eb4,16ed0:16eb5,16ed1:16eb6,16ed2:16eb7,16ed3:16eb8";

    static readonly Dictionary<int, string> SpecialUpperMap = ParseMulti(SpecialUpperList);
    static readonly Dictionary<int, int> LowerFix = ParseSingle(LowerFixList);
    static readonly Dictionary<int, int> UpperFix = ParseSingle(UpperFixList);

    static Dictionary<int, string> ParseMulti(string list)
    {
        var d = new Dictionary<int, string>();
        foreach (var e in list.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = e.Split(':');
            var sb = new StringBuilder();
            foreach (var h in kv[1].Split(' ')) sb.Append(char.ConvertFromUtf32(int.Parse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture)));
            d[int.Parse(kv[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture)] = sb.ToString();
        }
        return d;
    }

    static Dictionary<int, int> ParseSingle(string list)
    {
        var d = new Dictionary<int, int>();
        foreach (var e in list.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = e.Split(':');
            d[int.Parse(kv[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture)] = int.Parse(kv[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }
        return d;
    }

    public static string? SpecialUpper(int cp) => SpecialUpperMap.TryGetValue(cp, out var s) ? s : null;

    public static int Lower(int cp) => LowerFix.TryGetValue(cp, out int f) ? f : Rune.ToLowerInvariant(new Rune(cp)).Value;
    public static int Upper(int cp) => UpperFix.TryGetValue(cp, out int f) ? f : Rune.ToUpperInvariant(new Rune(cp)).Value;

    public static bool IsCased(int cp)
    {
        if (cp is >= 0xD800 and <= 0xDFFF) return false; // a lone surrogate
        var cat = Rune.GetUnicodeCategory(new Rune(cp));
        if (cat is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter) return true;
        return Lower(cp) != cp || Upper(cp) != cp;
    }

    public static bool IsCaseIgnorable(int cp)
    {
        if (cp is >= 0xD800 and <= 0xDFFF) return false;
        if (cp is 0x27 or 0x2E or 0x3A or 0x5E or 0x60 or 0xA8 or 0xAD or 0xAF or 0xB4 or 0xB7 or 0xB8 or 0x2018 or 0x2019 or 0x2024 or 0x2027
            or 0xFE13 or 0xFE52 or 0xFE55 or 0xFF07 or 0xFF0E or 0xFF1A or 0x5F4) return true;
        var cat = Rune.GetUnicodeCategory(new Rune(cp));
        return cat is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format
            or UnicodeCategory.ModifierLetter or UnicodeCategory.ModifierSymbol;
    }
}
