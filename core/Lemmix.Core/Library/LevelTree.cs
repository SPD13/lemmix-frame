using Lemmix.Store;

namespace Lemmix.Library;

// a directory of levels/index.json: the root, a pack, a rank, a folder of packs
public sealed class LevelNode
{
    public JsObject Raw { get; }
    public List<LevelNode> Children { get; } = new();
    public List<LevelEntry> Levels { get; } = new();
    public LevelNode? Parent { get; internal set; }
    public LevelNode? Pack { get; internal set; }

    internal LevelNode(JsObject raw)
    {
        Raw = raw;
        if (raw.Get("children") is JsArray ch) foreach (var c in ch.Items) if (c is JsObject o) Children.Add(new LevelNode(o));
        if (raw.Get("levels") is JsArray lv) foreach (var l in lv.Items) if (l is JsObject o) Levels.Add(new LevelEntry(o));
    }

    public string? Kind => Raw.Get("kind") as string;
    public string? Name => Raw.Get("name") as string;
    public string? Path => Raw.Get("path") as string;
    public string? Engine => Raw.Get("engine") as string;
    public object? Count => Raw.Get("count");
    public object? GameType => Raw.Get("gameType");
}

// a level record of the index: id, file, url, title, theme, styles, ...
public sealed class LevelEntry
{
    public JsObject Raw { get; }
    internal LevelEntry(JsObject raw) { Raw = raw; }

    public string? Id => Raw.Get("id") as string;
    public object? Title => Raw.Get("title");
    public object? Theme => Raw.Get("theme");
}

// LevelTree.byId's record: the level, its directory, its pack (null outside one), the directories above it
public sealed record LevelHit(LevelEntry Level, LevelNode Node, LevelNode? Pack, IReadOnlyList<LevelNode> Ancestors);

// LevelTree.describe: how a level is named in the browser and the status strip
public sealed record LevelDescription(LevelEntry Level, LevelNode Node, LevelNode? Pack, string? Engine, string Label, string PackName, object? Title);

// web/3d/js/library.js LevelTree: the level tree of levels/index.json (LevelsIndex.Build's, or a
// web install's file), and every way of looking a level up in it. As in the web, a forced reload
// indexes the new tree over the old maps without clearing them, so ids of a tree loaded before
// still resolve (to their old nodes).
public sealed class LevelTree
{
    public LevelNode? Root { get; private set; }
    public Dictionary<string, LevelHit> ById { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, LevelNode> ByPath { get; } = new(StringComparer.Ordinal);

    // the index (its JSON text, or the parsed object): load(root, force)
    public LevelNode Load(string indexJson, bool force = false) => Load(JsJson.Parse(indexJson), force);

    public LevelNode Load(object? index, bool force = false)
    {
        if (Root != null && !force) return Root;
        var raw = index as JsObject ?? throw new ArgumentException("levels/index.json is not an object");
        raw.Set("kind", "dir");
        raw.Set("name", "levels");
        raw.Set("path", "");
        Root = new LevelNode(raw);
        Index(Root, null, new List<LevelNode>());
        return Root;
    }

    void Index(LevelNode node, LevelNode? pack, List<LevelNode> ancestors)
    {
        if (node.Path != null) ByPath[node.Path] = node;
        node.Parent = ancestors.Count > 0 ? ancestors[^1] : null;
        if (node.Kind == "pack") pack = node;
        node.Pack = pack;
        foreach (var level in node.Levels)
            if (level.Id != null) ById[level.Id] = new LevelHit(level, node, pack, ancestors);
        foreach (var child in node.Children)
            Index(child, pack, new List<LevelNode>(ancestors) { node });
    }

    public LevelNode? NodeAt(string? path) => ByPath.TryGetValue(string.IsNullOrEmpty(path) ? "" : path, out var n) ? n : null;

    // Every level under a node, in the order it is played.
    public static List<LevelEntry> LevelsOf(LevelNode node)
    {
        var outList = new List<LevelEntry>();
        void Walk(LevelNode n)
        {
            outList.AddRange(n.Levels);
            foreach (var c in n.Children) Walk(c);
        }
        Walk(node);
        return outList;
    }

    // The level `delta` places on from this one within its pack, wrapping. A delta below minus
    // the pack's size falls off the list: the web fails there (TypeError), so does this.
    public string? Next(string levelId, int delta)
    {
        if (!ById.TryGetValue(levelId, out var hit)) return null;
        var list = LevelsOf(hit.Pack ?? Root!);
        int i = list.FindIndex(l => l.Id == levelId);
        if (i < 0) return null;
        long k = (i + (long)delta + list.Count) % list.Count;
        if (k < 0) throw new JsTypeError("Cannot read properties of undefined (reading 'id')");
        return list[(int)k].Id;
    }

    public LevelDescription? Describe(string levelId)
    {
        if (!ById.TryGetValue(levelId, out var hit)) return null;
        int ordinal = hit.Node.Levels.IndexOf(hit.Level) + 1;
        return new LevelDescription(hit.Level, hit.Node, hit.Pack, hit.Node.Engine,
            Js.ToStr(hit.Node.Name) + " " + ordinal,                    // "Fun 1", "Wimpy 3"
            hit.Pack != null ? Js.ToStr(hit.Pack.Name) : "",
            Js.Truthy(hit.Level.Title) ? hit.Level.Title : null);      // lemmix levels carry theirs
    }

    // The first level in the tree: what plays when nothing was asked for.
    public string? FirstLevelId()
    {
        var list = LevelsOf(Root!);
        return list.Count > 0 ? list[0].Id : null;
    }

    // A classic level id from the parameters the URLs (and old progress records) used.
    public string? ClassicId(double gameType, double group, double index)
    {
        var pack = Root!.Children.FirstOrDefault(n => n.Engine == "classic" && n.GameType is double g && g == gameType);
        if (pack == null) return null;
        LevelNode? rank = group >= 0 && group == Math.Floor(group) && group < pack.Children.Count ? pack.Children[(int)group] : null;
        if (rank == null) return null;
        if (rank.Raw.Get("levels") is not JsArray) throw new JsTypeError("Cannot read properties of undefined (reading '" + Js.NumberToString(index) + "')");
        LevelEntry? level = index >= 0 && index == Math.Floor(index) && index < rank.Levels.Count ? rank.Levels[(int)index] : null;
        return level?.Id;
    }
}
