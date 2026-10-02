using Lemmix.Store;

namespace Lemmix.Library;

// library.js FavoriteLevels: the levels starred, in the order they were starred (lem3d-favorites,
// a preference: it travels in the preferences file)
public sealed class FavoriteLevels
{
    public const string Key = "lem3d-favorites";

    readonly IStorage _store;
    public FavoriteLevels(IStorage store) { _store = store; }

    public JsArray List()
    {
        try { return JsJson.Parse(_store.GetItem(Key)) is JsArray a ? a : new JsArray(); }
        catch (JsSyntaxError) { return new JsArray(); }
    }

    // list.includes(levelId)
    public bool Has(object? levelId) => List().Items.Any(x => Js.SameValueZero(x, levelId));

    // Star or unstar a level; returns whether it is a favorite now.
    public bool Toggle(object? levelId)
    {
        var list = List();
        int at = list.Items.FindIndex(x => Js.StrictEquals(x, levelId));
        if (at >= 0) list.Items.RemoveAt(at);
        else list.Add(levelId);
        try { _store.SetItem(Key, JsJson.Stringify(list)!); } catch (IOException) { }
        return at < 0;
    }
}
