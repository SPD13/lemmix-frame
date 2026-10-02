using Lemmix.Store;

namespace Lemmix.Library;

// library.js Solutions: the stored solutions (solutions/index.json, written by tools/nx-solve.js):
// which levels have one, and where its .nxrp is
public sealed class Solutions
{
    object? _index;

    // the index's text (null or unreadable: no solutions)
    public void Load(string? json)
    {
        object? index = null;
        try { index = json == null ? null : JsJson.Parse(json); } catch (JsSyntaxError) { }
        _index = Js.Truthy(index) && Js.Truthy(Js.Get(index, "levels")) ? index : new JsObject(("levels", new JsObject()));
    }

    // The index's record of a level when it is solved, or null.
    public object? Info(string levelId)
    {
        if (!Js.Truthy(_index)) return null;
        var rec = Js.Get(Js.Get(_index, "levels"), levelId);
        return Js.Truthy(rec) && Js.StrictEquals(Js.Get(rec, "status"), "solved") ? rec : null;
    }

    public bool Has(string levelId) => Info(levelId) != null;

    // The URL of a level's solution replay, relative to `root`; null without one.
    public string? Url(string root, string levelId)
    {
        var rec = Info(levelId);
        if (rec == null || !Js.Truthy(Js.Get(rec, "file"))) return null;
        return root + "solutions/" + string.Join("/", Js.ToStr(Js.Get(rec, "file")).Split('/').Select(Js.EncodeURIComponent));
    }
}
