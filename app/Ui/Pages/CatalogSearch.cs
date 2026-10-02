using System;
using System.Collections.Generic;
using System.Linq;
using Lemmix.App.Ui.Windows;
using Lemmix.Library;

namespace Lemmix.App.Ui.Pages;

// web/3d/js/library.js's search in the headset's catalog: a query replaces the directory with
// the levels matching it from every pack, best first (library.js _renderSearch, core's
// Search.Run), as level tiles under a "matching “…” · N levels" heading; an empty query brings the
// directory back. Typing turns the recent and favorites filters off, as there.
public sealed class CatalogSearch
{
    readonly VrCatalog _catalog;
    readonly ICatalogLibrary _library;
    readonly Func<string, (List<string> Ids, int Total)> _search;
    public string Query = "";
    public List<string> Matches = new();

    // `search`: the query's matches, best first, as many as are shown (Search.SearchMax), and how many matched in all
    public CatalogSearch(VrCatalog catalog, ICatalogLibrary library, Func<string, (List<string> Ids, int Total)> search)
    {
        _catalog = catalog;
        _library = library;
        _search = search;
    }

    /** The search over a level tree, as the web's. */
    public static Func<string, (List<string>, int)> Over(LevelTree tree) => q =>
    {
        var r = Search.Run(tree, q);
        return (r.Shown.Select(h => h.Level.Id!).ToList(), r.Matches.Count);
    };

    public void SetQuery(string text)
    {
        Query = text.Trim();
        if (Query == "")
        {
            Matches = new();
            _catalog.Load(_library, false);
            return;
        }
        _catalog.ApplyFilter(null);
        var (ids, total) = _search(Query);
        Matches = ids;
        var items = new List<CatalogItem>();
        foreach (var id in ids)
        {
            var n = _library.NodeOf(id);
            if (n == null) continue;
            int i = n.Levels.IndexOf(id);
            var where = new List<string>();
            for (var p = n.Parent; p != null && p.Parent != null; p = p.Parent) where.Insert(0, p.Name);
            where.Add(n.Name + " " + (i + 1));
            items.Add(new CatalogItem
            {
                Kind = "level", LevelId = id, Playable = _library.CanLoad(n.Engine), Label = string.Join(" › ", where),
                Name = _library.LevelName(id), Set = _library.WorldOf(id), Best = _library.Best(id),
                Solution = _library.HasSolution(id), Favorite = _library.IsFavorite(id), Current = id == _library.CurrentLevelId,
            });
        }
        string heading = "matching “" + Query + "” · " + total + (total == 1 ? " level" : " levels") +
            (total > ids.Count ? " (first " + ids.Count + " shown)" : "");
        _catalog.SetList(items, heading, total == 0 ? "no level matches" : "");
        _catalog.Paint();
    }

    /** Enter: the first match plays (when it can). */
    public string? FirstMatch()
    {
        if (Query == "") return null;
        var first = _catalog.Items.FirstOrDefault(it => it.Kind == "level");
        return first != null && first.Playable ? first.LevelId : null;
    }

    /** Escape: a query is cleared (true); with none, the caller closes the library (false). */
    public bool Escape()
    {
        if (Query == "") return false;
        SetQuery("");
        return true;
    }
}
