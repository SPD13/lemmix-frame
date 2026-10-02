using Lemmix.Store;

namespace Lemmix.Library;

// library.js RecentLevels: the levels played, latest first, at most 50 (lem3d-recent)
public sealed class RecentLevels
{
    public const string Key = "lem3d-recent";
    public const int Max = 50;

    readonly IStorage _store;
    public RecentLevels(IStorage store) { _store = store; }

    public JsArray List()
    {
        try { return JsJson.Parse(_store.GetItem(Key)) is JsArray a ? a : new JsArray(); }
        catch (JsSyntaxError) { return new JsArray(); }
    }

    // A level was played: it moves to the head of the list.
    public void Push(object? levelId)
    {
        var list = new JsArray(new[] { levelId }.Concat(List().Items.Where(id => !Js.StrictEquals(id, levelId))).Take(Max));
        try { _store.SetItem(Key, JsJson.Stringify(list)!); } catch (IOException) { }
    }
}
