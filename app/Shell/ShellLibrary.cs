using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using Lemmix.App.Ui.Windows;
using Lemmix.Engine;
using Lemmix.Io;
using Lemmix.Library;
using Lemmix.Store;

namespace Lemmix.App.Shell;

// The library as the headset's catalog sees it (ICatalogLibrary), over the core's library data:
// the tree (LevelTree, levels/index.json) mirrored as CatalogNodes, the directory the library was
// left on and the recent list (LibraryState), the clears (LevelProgress), the stored solutions,
// the favorites; library.js's canLoad (the Lemmix engine only: the classic DOS engine is not part
// of the native app) and its miniatures (thumbnail: the level's ground image with its mask as
// alpha, scaled down without smoothing), drawn one after another on a worker.
public sealed class ShellLibrary : ICatalogLibrary
{
    readonly App _app;
    readonly Dictionary<string, CatalogNode> _byPath = new(StringComparer.Ordinal);
    CatalogNode _root = new();

    public ShellLibrary(App app) { _app = app; Rebuild(); }

    // the tree loaded (again): its mirror
    public void Rebuild()
    {
        _byPath.Clear();
        var tree = _app.Tree;
        _root = tree.Root != null ? Mirror(tree.Root, null) : new CatalogNode { Name = "levels" };
        _thumbs.Clear();
    }

    CatalogNode Mirror(LevelNode n, CatalogNode? parent)
    {
        var c = new CatalogNode
        {
            Name = n.Name ?? "", Path = n.Path ?? "", Engine = n.Engine, Parent = parent,
            Count = n.Count is double d ? (int)d : 0,
        };
        foreach (var l in n.Levels) if (l.Id != null) c.Levels.Add(l.Id);
        foreach (var ch in n.Children) c.Children.Add(Mirror(ch, c));
        _byPath[c.Path] = c;
        return c;
    }

    public CatalogNode Root => _root;
    public CatalogNode? CurrentNode()
    {
        var n = _app.Library.CurrentNode();
        return n != null && _byPath.TryGetValue(n.Path ?? "", out var c) ? c : null;
    }
    public void Navigate(string path) => _app.Library.Navigate(path);
    public void Up() => _app.Library.Up();
    // the lobby stands behind the catalog: it can always be closed, back to the lobby without a level
    public bool Locked => false;
    public string? CurrentLevelId => _app.LevelId;
    public CatalogNode? NodeOf(string levelId) =>
        _app.Tree.ById.TryGetValue(levelId, out var hit) && _byPath.TryGetValue(hit.Node.Path ?? "", out var c) ? c : null;
    public bool CanLoad(string? engine) => engine == "lemmix";
    public string LevelName(string levelId) => _app.Library.LevelName(levelId);
    public string WorldOf(string levelId) =>
        _app.Tree.ById.TryGetValue(levelId, out var hit) && Js.Truthy(hit.Level.Theme) ? Js.ToStr(hit.Level.Theme) : "";
    public double? Best(string levelId) => _app.Progress.Best(levelId);
    public int ClearedUnder(CatalogNode node) =>
        _app.Tree.NodeAt(node.Path) is { } n ? _app.Progress.ClearedUnder(n) : 0;
    public bool HasSolution(string levelId) => _app.Solutions.Has(levelId);
    public bool IsFavorite(string levelId) => _app.Library.Favorites.Has(levelId);
    public IReadOnlyList<string> Recent() => Ids(_app.Library.Recent.List());
    public IReadOnlyList<string> Favorites() => Ids(_app.Library.Favorites.List());
    public void EnsureNames(CatalogNode node) { } // the classic packs' scan: no classic engine here

    static List<string> Ids(JsArray a)
    {
        var l = new List<string>();
        foreach (var x in a.Items) if (x is string s) l.Add(s);
        return l;
    }

    // ------------------------------------------------------------ miniatures
    // library.thumbnail: a level's miniature at w x h, built on a worker from its own file source
    // (the caches of the board's are not shared across threads), handed back on the main thread
    readonly Dictionary<string, Texture2D?> _thumbs = new(StringComparer.Ordinal);
    readonly ConcurrentQueue<(CatalogItem Item, string Key, Image? Image)> _done = new();
    readonly Queue<(CatalogItem Item, Texture2D? Tex)> _cached = new(); // asked mid-paint: in on the next frame
    Task _chain = Task.CompletedTask;
    DiskFileSource? _workerIo;
    StyleManager? _workerStyles;

    public void WantThumb(CatalogItem item, int w, int h)
    {
        // (VrCatalog marks the tile asked before it asks)
        if (item.LevelId == null) return;
        string key = item.LevelId + "|" + w + "|" + h;
        if (_thumbs.TryGetValue(key, out var have)) { _cached.Enqueue((item, have)); return; }
        if (!_app.Tree.ById.TryGetValue(item.LevelId, out var hit) || hit.Level.Raw.Get("url") is not string url) return;
        string root = _app.AssetRoot;
        _chain = _chain.ContinueWith(_ =>
        {
            Image? img = null;
            try
            {
                _workerIo ??= new DiskFileSource(root);
                _workerStyles ??= new StyleManager(_workerIo);
                var text = _workerIo.Text(url);
                if (text != null)
                {
                    var level = LevelBuilder.Build(LevelBuilder.ParseLevel(text), _workerStyles, item.LevelId);
                    img = Miniature(level, w, h);
                }
            }
            catch (Exception e) { GD.PushWarning("[catalog] miniature of " + item.LevelId + ": " + e.Message); }
            _done.Enqueue((item, key, img));
        }, TaskScheduler.Default);
    }

    // _drawMiniature: the ground image, the mask as alpha, drawn into w x h without smoothing
    static Image Miniature(Level level, int w, int h)
    {
        int lw = level.Width, lh = level.Height;
        var data = new byte[lw * lh * 4];
        Buffer.BlockCopy(level.GroundImage, 0, data, 0, Math.Min(data.Length, level.GroundImage.Length));
        var mask = level.GroundMask.GroundMask;
        for (int i = 0; i < lw * lh; i++) data[i * 4 + 3] = mask[i] != 0 ? (byte)255 : (byte)0;
        var img = Image.CreateFromData(lw, lh, false, Image.Format.Rgba8, data);
        img.Resize(Math.Max(1, w), Math.Max(1, h), Image.Interpolation.Nearest);
        return img;
    }

    // per frame: the miniatures that came in, into their tiles; true when the catalog wants a repaint
    public bool PollThumbs()
    {
        bool any = false;
        while (_cached.TryDequeue(out var c)) { c.Item.Thumb = c.Tex; any |= c.Tex != null; }
        while (_done.TryDequeue(out var d))
        {
            var tex = d.Image != null ? ImageTexture.CreateFromImage(d.Image) : null;
            _thumbs[d.Key] = tex;
            d.Item.Thumb = tex;
            any |= tex != null;
        }
        return any;
    }
}
