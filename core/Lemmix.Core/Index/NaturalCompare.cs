namespace Lemmix.Index;

// a.localeCompare(b, "en", {numeric: true, sensitivity: "base"}) - ICU's root collation as the
// browser and Node apply it to file names, which the index builders sort with:
// - primary strength only: case and accents do not count ("A" = "a", "é" = "e");
// - digit runs compare by value, leading zeros ignored ("file9" < "file10", "01" = "1");
// - ASCII punctuation and symbols in ICU's order, all before the digits, the digits before the
//   letters (the table is ICU's, read off Node's localeCompare: oracle/collate.js checks it).
public sealed class NaturalCompare : IComparer<string>
{
    public static readonly NaturalCompare Instance = new();

    // ICU's order of the printable ASCII characters (equal ones share a weight), letters excluded
    const string Order = " _-,;:!?.'\"()[]{}@*/\\&#%`^+<=>|~$";
    static readonly int[] Weight = BuildWeights();
    const int DigitWeight = 1000, LetterBase = 2000, OtherBase = 10000;

    static int[] BuildWeights()
    {
        var w = new int[128];
        for (int i = 0; i < w.Length; i++) w[i] = -1;
        for (int i = 0; i < Order.Length; i++) w[Order[i]] = 10 + i;
        for (char c = 'a'; c <= 'z'; c++) { w[c] = LetterBase + (c - 'a'); w[char.ToUpperInvariant(c)] = LetterBase + (c - 'a'); }
        return w;
    }

    // the base letter of U+00C0..U+017F (Latin-1 Supplement, Latin Extended-A), '\0' for none:
    // normalisation is not available with invariant globalization, so the table is spelled out
    const string LatinBase = "aaaaaa\0ceeeeiiii\0nooooo\0\0uuuuy\0\0aaaaaa\0ceeeeiiii\0nooooo\0\0uuuuy\0yaaaaaaccccccccdd\0\0eeeeeeeeeegggggggghh\0\0iiiiiiiii\0\0\0jjkk\0llllll\0\0\0\0nnnnnn\0\0\0oooooo\0\0rrrrrrsssssssstttt\0\0uuuuuuuuuuuuwwyyyzzzzzz\0";

    // a non-ASCII character: its base letter when it has one (é -> e), else after everything
    // ASCII, by code point
    static int CharWeight(char c)
    {
        if (c < 128) return Weight[c] >= 0 ? Weight[c] : c; // control characters: first
        if (c >= 0xC0 && c < 0x180 && LatinBase[c - 0xC0] != '\0') return Weight[LatinBase[c - 0xC0]];
        return OtherBase + char.ToLowerInvariant(c);
    }

    static bool IsDigit(char c) => c >= '0' && c <= '9';

    public int Compare(string? x, string? y)
    {
        x ??= ""; y ??= "";
        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            char a = x[i], b = y[j];
            if (IsDigit(a) && IsDigit(b))
            {
                int si = i, sj = j;
                while (i < x.Length && IsDigit(x[i])) i++;
                while (j < y.Length && IsDigit(y[j])) j++;
                string na = x[si..i].TrimStart('0'), nb = y[sj..j].TrimStart('0');
                if (na.Length != nb.Length) return na.Length < nb.Length ? -1 : 1;
                int c = string.CompareOrdinal(na, nb);
                if (c != 0) return c < 0 ? -1 : 1;
                continue;
            }
            int wa = IsDigit(a) ? DigitWeight : CharWeight(a);
            int wb = IsDigit(b) ? DigitWeight : CharWeight(b);
            if (wa != wb) return wa < wb ? -1 : 1;
            i++; j++;
        }
        bool xe = i >= x.Length, ye = j >= y.Length;
        return xe && ye ? 0 : xe ? -1 : 1;
    }

    // Array.prototype.sort with it: stable
    public static List<string> Sort(IEnumerable<string> items) => items.OrderBy(s => s, Instance).ToList();
}
