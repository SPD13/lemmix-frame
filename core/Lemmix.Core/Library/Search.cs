using Lemmix.Store;
using Lemmix.Util;

namespace Lemmix.Library;

// a search's outcome as the library shows it: the header line, the matches best first (all of
// them; the view lists the first SearchMax), the status line
public sealed record SearchResult(string Title, List<(double Score, LevelHit Hit)> Matches, IReadOnlyList<LevelHit> Shown, string Status);

// web/3d/js/library.js fuzzyScore and WorldLibrary._renderSearch's matching: every word of the
// query a subsequence of the level's text (its name, the directories above it, its rank and
// number, its theme), letters that run on or open a word counting for more; best matches first,
// ties in tree order. The classic packs' scanned names are the web's only (the native app has no
// classic packs): a level's name is its title.
public static class Search
{
    public const int SearchMax = 200; // the most matches the search view lists

    static bool IsWordStartBefore(string hay, int i) => !(hay[i] is >= 'a' and <= 'z' or >= '0' and <= '9'); // /[^a-z0-9]/.test(hay[i])

    // the query's words: toLowerCase().split(/\s+/).filter(Boolean)
    static List<string> Words(string query)
    {
        var words = new List<string>();
        string q = Js.ToLower(query);
        int i = 0;
        while (i < q.Length)
        {
            while (i < q.Length && JsString.IsJsSpace(q[i])) i++;
            int start = i;
            while (i < q.Length && !JsString.IsJsSpace(q[i])) i++;
            if (i > start) words.Add(q[start..i]);
        }
        return words;
    }

    // A score, higher is better, or -1 when a word does not match at all.
    public static double FuzzyScore(string query, string text)
    {
        string hay = Js.ToLower(text);
        double total = 0;
        foreach (var word in Words(query))
        {
            int at = hay.IndexOf(word, StringComparison.Ordinal);
            if (at >= 0) // a plain substring: best of all
            {
                total += 100 + word.Length * 10 + (at == 0 || IsWordStartBefore(hay, at - 1) ? 20 : 0);
                continue;
            }
            double score = 0;
            int pos = -1, last = -2;
            foreach (var ch in Js.CodePoints(word))
            {
                pos = pos + 1 > hay.Length ? -1 : hay.IndexOf(ch, pos + 1, StringComparison.Ordinal);
                if (pos < 0) return -1;
                if (pos == last + 1) score += 8;                                   // runs on from the previous letter
                else if (pos == 0 || IsWordStartBefore(hay, pos - 1)) score += 6; // opens a word
                else score += 1;
                score -= Math.Min(pos - last - 1, 10) * 0.2;                       // the gap it skipped
                last = pos;
            }
            total += score;
        }
        return total;
    }

    // the text a level is matched on
    public static string TextOf(LevelTree tree, LevelHit hit)
    {
        string name = Js.ToStr(Js.Or(hit.Level.Title, ""));
        string world = Js.ToStr(Js.Or(hit.Level.Theme, ""));
        int ordinal = hit.Node.Levels.IndexOf(hit.Level) + 1;
        var parts = new List<string> { name };
        parts.AddRange(hit.Ancestors.Where(a => a != tree.Root).Select(a => a.Name ?? ""));
        parts.Add(Js.ToStr(hit.Node.Name) + " " + ordinal);
        parts.Add(world);
        return string.Join(" ", parts);
    }

    public static SearchResult Run(LevelTree tree, string query)
    {
        var matches = new List<(double, LevelHit)>();
        foreach (var level in LevelTree.LevelsOf(tree.Root!))
        {
            var hit = tree.ById[level.Id!];
            double score = FuzzyScore(query, TextOf(tree, hit));
            if (score < 0) continue;
            matches.Add((score, hit));
        }
        matches = matches.OrderByDescending(m => m.Item1).ToList(); // stable, as Array.prototype.sort
        var shown = matches.Take(SearchMax).Select(m => m.Item2).ToList();
        string title = "matching “" + query + "” · " + matches.Count + (matches.Count == 1 ? " level" : " levels") +
            (matches.Count > shown.Count ? " (first " + shown.Count + " shown)" : "");
        return new SearchResult(title, matches, shown, matches.Count == 0 ? "no level matches" : "");
    }
}
