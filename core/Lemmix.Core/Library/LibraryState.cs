using Lemmix.Store;

namespace Lemmix.Library;

// a list of levels from anywhere in the tree as the library shows it (the recent and favorites
// views): the header line, the levels that are still in the tree, the status line
public sealed record PickedView(string Title, IReadOnlyList<LevelHit> Hits, string Status);

// web/3d/js/library.js WorldLibrary, its data side (the DOM is the app's): the classic order
// switch (lem3d-lib-order), the directory the library was left on (lem3d-lib-path), the level
// being played (which pushes it on the recent list), the recent and favorites views, a level's
// name. The catalog itself (rows, tiles, breadcrumb) is drawn by the app from LevelTree.
public sealed class LibraryState
{
    public const string OrderKey = "lem3d-lib-order";
    public const string PathKey = "lem3d-lib-path";

    readonly IStorage _store;
    readonly LevelTree _tree;
    public RecentLevels Recent { get; }
    public FavoriteLevels Favorites { get; }

    public string Order { get; private set; }   // "level" or "world"
    public string Path { get; private set; }
    public string? CurrentLevelId { get; private set; }

    public LibraryState(IStorage store, LevelTree tree)
    {
        _store = store;
        _tree = tree;
        Recent = new RecentLevels(store);
        Favorites = new FavoriteLevels(store);
        string? saved = null;
        try { saved = store.GetItem(OrderKey); } catch (IOException) { }
        Order = saved == "world" ? "world" : "level";
        string path = "";
        try { path = store.GetItem(PathKey) is { Length: > 0 } p ? p : ""; } catch (IOException) { }
        Path = path;
    }

    // the order button
    public string ToggleOrder()
    {
        Order = Order == "level" ? "world" : "level";
        try { _store.SetItem(OrderKey, Order); } catch (IOException) { }
        return Order;
    }

    public void Navigate(string? path)
    {
        Path = string.IsNullOrEmpty(path) ? "" : path;
        try { _store.SetItem(PathKey, Path); } catch (IOException) { }
    }

    // The directory being looked at (the root until the tree is loaded).
    public LevelNode? CurrentNode() => _tree.Root != null ? _tree.NodeAt(Path) ?? _tree.Root : null;

    public void Up()
    {
        var node = CurrentNode();
        if (node?.Parent != null) Navigate(node.Parent.Path);
    }

    // The level being played: the library opens on its directory, and the recent list remembers it.
    public void SetCurrent(string? levelId)
    {
        if (!string.IsNullOrEmpty(levelId) && levelId != CurrentLevelId) Recent.Push(levelId);
        CurrentLevelId = levelId;
    }

    // A level's name if known ("" when not): its title (the classic packs' scan is the web's).
    public string LevelName(string levelId)
    {
        if (!_tree.ById.TryGetValue(levelId, out var hit)) return "";
        if (Js.Truthy(hit.Level.Title)) return Js.ToStr(hit.Level.Title);
        return "";
    }

    PickedView Picked(JsArray ids, string label, string empty)
    {
        var hits = new List<LevelHit>();
        foreach (var id in ids.Items) if (id is string s && _tree.ById.TryGetValue(s, out var h)) hits.Add(h); // gone packs drop out
        string title = label + " · " + hits.Count + (hits.Count == 1 ? " level" : " levels");
        return new PickedView(title, hits, hits.Count > 0 ? "" : empty);
    }

    // the recent view: the levels played, latest first, from any pack
    public PickedView RecentView() => Picked(Recent.List(), "recently played", "no level played yet");

    // the favorites view: the levels starred, from any pack
    public PickedView FavoritesView() => Picked(Favorites.List(), "favorites", "no favorite yet - star a level to keep it here");
}
