using Lemmix.Store;

namespace Lemmix.Library;

// app.js onGameEnd (~3490-3498): the talismans of a NeoLemmix level earned on a win (not when
// watching its solution), kept by level id in lem3d-talismans, each once, in the order earned.
// Any failure (an entry that is not an object, a level entry that is not a list) is swallowed.
public static class Talismans
{
    public const string Key = "lem3d-talismans";

    public static void RecordWin(IStorage store, string levelId, IEnumerable<object?> ids)
    {
        try
        {
            var raw = store.GetItem(Key);
            var all = JsJson.Parse(string.IsNullOrEmpty(raw) ? "{}" : raw);
            var cur = Js.Or(Js.Get(all, levelId), new JsArray());
            IEnumerable<object?> items = cur switch
            {
                // Array.prototype.concat: the list, then the ids
                JsArray a => a.Items.Concat(ids),
                // String.prototype.concat: the text and the ids joined by commas, then a Set of its code points
                string s => Js.CodePoints(s + Js.ToStr(new JsArray(ids))).Select(c => (object?)c),
                _ => throw new JsTypeError("concat is not a function"),
            };
            Js.Set(all, levelId, Js.UniqueArray(items));
            store.SetItem(Key, JsJson.Stringify(all)!);
        }
        catch (Exception e) when (e is JsTypeError or JsSyntaxError or IOException) { }
    }

    // the talismans recorded for a level (an empty list when none, or not a list)
    public static List<string> Of(IStorage store, string levelId)
    {
        try
        {
            var all = JsJson.Parse(store.GetItem(Key) ?? "{}");
            if (all is JsObject o && o.Get(levelId) is JsArray a) return a.Items.OfType<string>().ToList();
        }
        catch (JsSyntaxError) { }
        return new List<string>();
    }
}
