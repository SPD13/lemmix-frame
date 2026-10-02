using System.Text.RegularExpressions;
using Lemmix.Store;

namespace Lemmix.Library;

// web/3d/js/library.js LevelProgress: which levels were cleared, and how fast, in the store under
// lem3d-cleared: {levelId: {best: seconds, clears: n, saved?: most lemmings saved}}. A level counts
// as cleared when its record's best is a number. Every call reads the store afresh, as the web's.
public sealed class LevelProgress
{
    public const string Key = "lem3d-cleared";

    readonly IStorage _store;
    readonly LevelTree _tree;

    public LevelProgress(IStorage store, LevelTree tree)
    {
        _store = store;
        _tree = tree;
    }

    // JSON.parse(localStorage.getItem(key)) || {}, {} when it does not parse
    public object? All()
    {
        try { return Js.Or(JsJson.Parse(_store.GetItem(Key)), new JsObject()); }
        catch (JsSyntaxError) { return new JsObject(); }
    }

    void Write(object? all)
    {
        try { _store.SetItem(Key, JsJson.Stringify(all)!); } catch (IOException) { }
    }

    static readonly Regex OldKey = new("^([12])/([0-9]+)/([0-9]+)$", RegexOptions.CultureInvariant);

    // Records used to be keyed "<gameType>/<group>/<level>"; once the tree is known they move to
    // the classic ids those levels have now - or go, when the tree has no such level (the native
    // app has no classic packs: every such record goes).
    public void Migrate()
    {
        var all = All();
        bool changed = false;
        foreach (var key in Js.OwnKeys(all))
        {
            var m = OldKey.Match(key);
            if (!m.Success) continue;
            var id = _tree.ClassicId(Js.StringToNumber(m.Groups[1].Value), Js.StringToNumber(m.Groups[2].Value), Js.StringToNumber(m.Groups[3].Value));
            if (id != null && !Js.Truthy(Js.Get(all, id))) Js.Set(all, id, Js.Get(all, key));
            if (all is JsObject o) o.Remove(key);
            changed = true;
        }
        if (changed) Write(all);
    }

    // Best time in seconds, or null if this one has never been cleared.
    public double? Best(string levelId)
    {
        var rec = Js.Get(All(), levelId);
        return Js.Truthy(rec) && Js.Get(rec, "best") is double b ? b : null;
    }

    // Record a clear, keeping the fastest time and the most lemmings saved (`saved`, when it is a
    // number). Returns true if the time is a new best. A store entry that cannot take the record
    // (a number, a string) fails as the web's does.
    public bool Record(string levelId, double seconds, object? saved)
    {
        var all = All();
        var rec = Js.Or(Js.Get(all, levelId), new JsObject(("best", null), ("clears", 0)));
        var recBest = Js.Get(rec, "best");
        bool better = recBest == null || Js.LessThan(seconds, recBest);
        var next = new JsObject(("best", better ? seconds : recBest), ("clears", Js.Add(Js.Get(rec, "clears"), 1.0)));
        double most = Js.MathMax(Js.ToNumber(Js.Or(Js.Get(rec, "saved"), 0.0)), Js.Norm(saved) is double sv ? sv : 0);
        if (Js.Truthy(most)) next.Set("saved", most);
        Js.Set(all, levelId, next);
        Write(all);
        return better;
    }

    public bool Record(string levelId, double seconds, double? saved) => Record(levelId, seconds, saved.HasValue ? (object?)saved.Value : null);

    // The most lemmings saved on a level, or null.
    public double? Saved(string levelId)
    {
        var rec = Js.Get(All(), levelId);
        return Js.Truthy(rec) && Js.Get(rec, "saved") is double s ? s : null;
    }

    // How many levels under a node are cleared.
    public int ClearedUnder(LevelNode node)
    {
        var all = All();
        return LevelTree.LevelsOf(node).Count(l =>
        {
            var rec = Js.Get(all, l.Id ?? "undefined");
            return Js.Truthy(rec) && Js.Get(rec, "best") is double;
        });
    }

    // m:ss, the way the game's own clock reads.
    public static string Format(double seconds)
    {
        double m = Math.Floor(seconds / 60), s = seconds % 60;
        string ss = Js.NumberToString(s);
        return Js.NumberToString(m) + ":" + (ss.Length < 2 ? new string('0', 2 - ss.Length) + ss : ss);
    }
}
