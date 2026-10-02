using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using static Lemmix.App.Xr.VrManager;

namespace Lemmix.App.Ui.Windows;

// One entry of the catalog's list (web/3d/js/app.js loadVrCatalog's items): a directory row, the
// way back up, or a level tile. Filled from the library by Load, or handed in whole (SetList).
public sealed class CatalogItem
{
    public string Kind = "level";           // "level", "dir" or "back"
    public string Label = "";
    // a level
    public string? LevelId;
    public bool Playable = true;
    public string Name = "", Set = "";
    public double? Best;                    // seconds, once cleared
    public bool Solution, Favorite, Current;
    public Texture2D? Thumb;
    public bool ThumbReq;
    // a directory
    public string? Engine, Path;
    public int Count, Done;
}

// The library as the catalog sees it (web: library.js's tree, LevelProgress, Solutions,
// FavoriteLevels, RecentLevels and the library object's navigation). Integration answers it from
// core/Lemmix.Core/Library.
public sealed class CatalogNode
{
    public string Name = "", Path = "";
    public string? Engine;
    public int Count;
    public CatalogNode? Parent;
    public List<CatalogNode> Children = new();
    public List<string> Levels = new(); // level ids, in the order they are played
}

public interface ICatalogLibrary
{
    CatalogNode Root { get; }
    CatalogNode? CurrentNode();
    void Navigate(string path);
    void Up();
    bool Locked { get; }                    // no level chosen yet: the catalog cannot be closed
    string? CurrentLevelId { get; }         // state.levelId
    CatalogNode? NodeOf(string levelId);    // LevelTree.byId: the directory a level lives in
    bool CanLoad(string? engine);
    string LevelName(string levelId);
    string WorldOf(string levelId);
    double? Best(string levelId);
    int ClearedUnder(CatalogNode node);
    bool HasSolution(string levelId);
    bool IsFavorite(string levelId);
    IReadOnlyList<string> Recent();
    IReadOnlyList<string> Favorites();
    void EnsureNames(CatalogNode node);     // classic names come from a scan
}

// web/3d/js/app.js "world catalog (VR)": the headset's library, one canvas with the whole list
// laid out once, scrolled behind a window, picked by the ray's canvas pixel; the close and the two
// filters are separate buttons in its top-right corner.
public sealed class VrCatalog
{
    public const int VR_CAT_W = 1024, VR_CAT_H = 640;  // canvas pixels
    public const float VR_CAT_PAD = 26;                // margin round the list
    public const float VR_CAT_TOP = 92;                // below the heading
    public const float VR_CAT_GAP = 14;                // between tiles
    public const int VR_CAT_COLS = 4;
    public const float VR_CAT_THUMB_H = 26;            // a level is ~10:1, so it draws thin
    public const float VR_CAT_TILE_H = 126;            // one row of levels
    public const float VR_CAT_BAND_H = 48;             // the heading: where in the tree we are
    public const float VR_CAT_ROW_H = 60;              // a directory row
    public const float VR_CAT_BAR_W = 16;              // the scrollbar down the right edge
    public const float VR_CAT_BAR_GRAB = 12;           // slack either side of it, for the ray
    public const float VR_CAT_SCROLL = 900;            // canvas px/second at full stick
    public const float VR_CAT_VIEW_X = VR_CAT_PAD;
    public const float VR_CAT_VIEW_Y = VR_CAT_TOP;
    public const float VR_CAT_VIEW_W = VR_CAT_W - 2 * VR_CAT_PAD - VR_CAT_BAR_W - 8;
    public const float VR_CAT_VIEW_H = VR_CAT_H - VR_CAT_TOP - VR_CAT_PAD;

    public sealed record Cell(CatalogItem Item, int I, float X, float Y, float W, float H);
    public sealed record Band(string Label, float Y, float H);
    public readonly record struct ScrollBar(float X, float Y, float W, float H, float Max, float Thumb);
    public readonly record struct Pick(int Tile, bool ScrollBar, float ScrollAt);

    public List<CatalogItem> Items = new();   // what the directory holds: rows, or level tiles
    public List<Cell> Cells = new();          // one per item, in the list's own coordinates
    public List<Band> Bands = new();          // the heading above them
    public string Heading = "";               // the path down the tree to here
    public float ListHeight;                  // how tall the whole list is
    public float Scroll;                      // how far down it we are
    public int Hover = -1;                    // a tile, -2 the scrollbar
    public string Note = "";
    public string? Filter;                    // "recent" or "favorites": that list instead of a directory

    public readonly Node3D Root = new() { Name = "vr-catalog", Visible = false };
    public readonly Panel3D Panel;
    public readonly IconButton Close, Recent, Fav;
    public readonly IconButton[] Tools;

    // a tile's miniature, asked for the first time it is painted (web: library.thumbnail, then a
    // repaint): (item, width, height); the answer goes in item.Thumb and Paint() is called again
    public Action<CatalogItem, int, int>? WantsThumb;

    public VrCatalog()
    {
        Panel = new Panel3D(VR_CAT_W, VR_CAT_H, 1f) { Name = "vr-worldpanel" };
        Panel.NoDepthTest = true;
        Panel.RenderPriority = IconButton.GUI_ORDER_MODAL;
        Root.AddChild(Panel);
        Close = new IconButton("catclose", BarIcons.Cross, IconButton.GUI_ORDER_MODAL_BTN);
        // the two filters, left of the close: the levels played and the favorites
        Recent = new IconButton("catrecent", BarIcons.Recent, IconButton.GUI_ORDER_MODAL_BTN);
        Fav = new IconButton("catfav", BarIcons.Favorite, IconButton.GUI_ORDER_MODAL_BTN);
        Tools = new[] { Close, Recent, Fav };
        foreach (var t in Tools) Root.AddChild(t);
        Paint();
    }

    // ---- the list's layout, once, in its own coordinates
    public void LayoutList()
    {
        Cells = new();
        Bands = new();
        float colW = VR_CAT_VIEW_W / VR_CAT_COLS;
        float y = 0;               // the top of whatever is placed next
        int col = VR_CAT_COLS;     // == VR_CAT_COLS: no row is open
        if (Heading != "")
        {
            Bands.Add(new Band(Heading, y, VR_CAT_BAND_H));
            y += VR_CAT_BAND_H;
        }
        for (int i = 0; i < Items.Count; i++)
        {
            var item = Items[i];
            if (item.Kind != "level")
            {
                // a directory (or the way back up) takes a row of its own
                if (col < VR_CAT_COLS) { y += VR_CAT_TILE_H; col = VR_CAT_COLS; }
                Cells.Add(new Cell(item, i, VR_CAT_VIEW_X + VR_CAT_GAP / 2, y + VR_CAT_GAP / 2,
                    VR_CAT_VIEW_W - VR_CAT_GAP, VR_CAT_ROW_H - VR_CAT_GAP));
                y += VR_CAT_ROW_H;
                continue;
            }
            if (col >= VR_CAT_COLS) col = 0; // this item opens a row at y
            Cells.Add(new Cell(item, i, VR_CAT_VIEW_X + col * colW + VR_CAT_GAP / 2, y + VR_CAT_GAP / 2,
                colW - VR_CAT_GAP, VR_CAT_TILE_H - VR_CAT_GAP));
            if (++col >= VR_CAT_COLS) { y += VR_CAT_TILE_H; col = VR_CAT_COLS; }
        }
        if (col < VR_CAT_COLS) y += VR_CAT_TILE_H; // the last row, left part-full
        ListHeight = y;
        ScrollTo(Scroll);
    }

    /** The scrollbar: where it is, how tall its thumb is, how far the list can travel. */
    public ScrollBar Bar()
    {
        float max = Math.Max(0, ListHeight - VR_CAT_VIEW_H);
        return new ScrollBar(VR_CAT_W - VR_CAT_PAD - VR_CAT_BAR_W, VR_CAT_VIEW_Y, VR_CAT_BAR_W, VR_CAT_VIEW_H, max,
            max > 0 ? Math.Max(40, VR_CAT_VIEW_H * VR_CAT_VIEW_H / ListHeight) : 0);
    }

    /** Where a press at this height down the bar puts the list. */
    public float ScrollFor(float canvasY)
    {
        var bar = Bar();
        float travel = bar.H - bar.Thumb;
        if (travel <= 0) return 0;
        // the thumb centres on the press, the way a scrollbar drag behaves
        return ((canvasY - bar.Y - bar.Thumb / 2) / travel) * bar.Max;
    }

    /** Move the list, clamped to its ends. True if it actually moved. */
    public bool ScrollTo(float next)
    {
        float max = Math.Max(0, ListHeight - VR_CAT_VIEW_H);
        float clamped = Math.Max(0, Math.Min(max, next));
        if (clamped == Scroll) return false;
        Scroll = clamped;
        return true;
    }

    /** Scroll by a number of canvas pixels, repainting if anything moved. */
    public void ScrollBy(float delta) { if (ScrollTo(Scroll + delta)) Paint(); }

    /** The thumbsticks while the catalog is up (y: the stick's, down positive as vr.js reports it). */
    public void OnStick(float y, double seconds) => ScrollBy((float)(-y * VR_CAT_SCROLL * seconds));

    /** Put a level in view, roughly a third of the way down. */
    public void RevealItem(int index)
    {
        if (index < 0 || index >= Cells.Count) return;
        var cell = Cells[index];
        ScrollTo(cell.Y - VR_CAT_VIEW_H / 3);
    }

    // ---- painting
    Canvas2D cx => Panel.Canvas;

    /** Trim a label to the width it has, with an ellipsis when it is cut. */
    string Fit(string text, float maxW)
    {
        if (cx.measureText(text).width <= maxW) return text;
        string cut = text;
        while (cut.Length > 1 && cx.measureText(cut + "…").width > maxW) cut = cut[..^1];
        return cut + "…";
    }

    /** m:ss, the way the game's own clock reads (LevelProgress.format). */
    public static string FormatTime(double seconds)
    {
        double m = Math.Floor(seconds / 60), s = seconds % 60;
        string ss = Canvas2D.Num(s);
        return Canvas2D.Num(m) + ":" + (ss.Length < 2 ? ss.PadLeft(2, '0') : ss);
    }

    public void Paint()
    {
        cx.clearRect(0, 0, VR_CAT_W, VR_CAT_H);
        cx.fillStyle = "rgba(10, 14, 22, 0.96)";
        cx.beginPath();
        cx.roundRect(2, 2, VR_CAT_W - 4, VR_CAT_H - 4, 18);
        cx.fill();
        cx.strokeStyle = "#ffd866";
        cx.lineWidth = 4;
        cx.stroke();

        cx.textAlign = "left";
        cx.fillStyle = "#f0f3f8";
        cx.font = "bold 36px monospace";
        cx.fillText("WORLDS", VR_CAT_PAD + 6, 58);
        cx.fillStyle = "#8fa1bb";
        cx.font = "22px monospace";
        cx.fillText(Note != "" ? Note : "stick up / down to scroll", VR_CAT_PAD + 190, 56);

        // the list itself, clipped to its window and slid by the scroll
        cx.save();
        cx.beginPath();
        cx.rect(VR_CAT_VIEW_X - 4, VR_CAT_VIEW_Y, VR_CAT_VIEW_W + 8, VR_CAT_VIEW_H);
        cx.clip();
        cx.translate(0, VR_CAT_VIEW_Y - Scroll);
        float top = Scroll, bottom = top + VR_CAT_VIEW_H;

        foreach (var band in Bands)
        {
            if (band.Y + band.H < top || band.Y > bottom) continue;
            cx.fillStyle = "#7fd6e8";
            cx.font = "bold 24px monospace";
            cx.fillText(band.Label, VR_CAT_VIEW_X + VR_CAT_GAP / 2, band.Y + 32);
            cx.strokeStyle = "rgba(127, 214, 232, 0.35)";
            cx.lineWidth = 2;
            cx.beginPath();
            cx.moveTo(VR_CAT_VIEW_X + VR_CAT_GAP / 2, band.Y + 42);
            cx.lineTo(VR_CAT_VIEW_X + VR_CAT_VIEW_W - VR_CAT_GAP / 2, band.Y + 42);
            cx.stroke();
        }

        foreach (var cell in Cells)
        {
            if (cell.Y + cell.H < top || cell.Y > bottom) continue;
            var it = cell.Item;
            bool hot = cell.I == Hover;
            if (it.Kind != "level")
            {
                // a directory row: name, classic/lemmix, how many levels, how many done
                bool all = it.Kind == "dir" && it.Done == it.Count && it.Count > 0;
                cx.fillStyle = it.Kind == "back" ? (hot ? "#232b3a" : "rgba(255,255,255,0.03)")
                    : all ? (hot ? "#2c7042" : "#1d4a2b") : (hot ? "#2b3548" : "#19202c");
                cx.beginPath();
                cx.roundRect(cell.X, cell.Y, cell.W, cell.H, 10);
                cx.fill();
                if (hot) { cx.strokeStyle = "#ffffff"; cx.lineWidth = 3; cx.stroke(); }
                cx.textAlign = "left";
                cx.fillStyle = it.Kind == "back" ? "#8fa1bb" : "#f0f3f8";
                cx.font = "bold 24px monospace";
                cx.fillText(Fit(it.Label, cell.W - 520), cell.X + 16, cell.Y + 31);
                if (it.Kind == "dir")
                {
                    cx.font = "16px monospace";
                    cx.fillStyle = it.Engine == "lemmix" ? "#ffb066" : "#7fd6e8";
                    cx.fillText((it.Engine ?? "").ToUpperInvariant(), cell.X + cell.W - 470, cell.Y + 30);
                    cx.textAlign = "right";
                    cx.fillStyle = "#cdd6e4";
                    cx.font = "20px monospace";
                    cx.fillText(it.Count + " levels", cell.X + cell.W - 230, cell.Y + 31);
                    cx.fillStyle = all ? "#6fce7e" : "#8fa1bb";
                    cx.fillText(it.Done + " / " + it.Count + " cleared", cell.X + cell.W - 16, cell.Y + 31);
                    cx.textAlign = "left";
                }
                continue;
            }
            bool done = it.Best != null;
            cx.fillStyle = done ? (hot ? "#2c7042" : "#1d4a2b") : (hot ? "#2b3548" : "#19202c");
            cx.beginPath();
            cx.roundRect(cell.X, cell.Y, cell.W, cell.H, 10);
            cx.fill();
            if (hot || it.Current)
            {
                cx.strokeStyle = hot ? "#ffffff" : "#ffd866";
                cx.lineWidth = 3;
                cx.stroke();
            }
            // the miniature, letterboxed across the tile's top
            float tw = cell.W - 20;
            if (it.Thumb != null)
            {
                cx.imageSmoothingEnabled = false;
                cx.drawImage(it.Thumb, cell.X + 10, cell.Y + 10, tw, VR_CAT_THUMB_H);
            }
            else
            {
                cx.fillStyle = "rgba(255,255,255,0.06)";
                cx.fillRect(cell.X + 10, cell.Y + 10, tw, VR_CAT_THUMB_H);
                WantThumb(it, (int)Math.Floor(tw + 0.5));
            }
            cx.fillStyle = "#f0f3f8";
            cx.font = "bold 24px monospace";
            cx.fillText(Fit(it.Label, tw - (it.Favorite ? 30 : 0)), cell.X + 10, cell.Y + 62);
            if (it.Solution)
            {
                // a stored solution: a small cyan play mark before the star's place
                cx.fillStyle = "#7fd6e8";
                float px = cell.X + cell.W - (it.Favorite ? 44 : 24), py = cell.Y + 54;
                cx.beginPath(); cx.moveTo(px - 6, py - 7); cx.lineTo(px - 6, py + 7); cx.lineTo(px + 6, py); cx.closePath(); cx.fill();
            }
            if (it.Favorite)
            {
                // a favorite: a full yellow star at the end of the label's line
                cx.fillStyle = "#ffd866";
                BarIcons.StarPath(cx, cell.X + cell.W - 22, cell.Y + 54, 10);
                cx.fill();
            }
            cx.fillStyle = "#c3ccda";
            cx.font = "17px monospace";
            cx.fillText(Fit(it.Name, tw), cell.X + 10, cell.Y + 84);
            // the world it is built from, and the record if there is one
            cx.fillStyle = done ? "#6fce7e" : "#8fa1bb";
            cx.fillText(Fit((done ? "✔ " + FormatTime(it.Best!.Value) + " · " : "") + it.Set, tw), cell.X + 10, cell.Y + 104);
        }
        cx.restore();

        // how far down the list we are - and a handle to drag
        var bar = Bar();
        if (bar.Max > 0)
        {
            float r = VR_CAT_BAR_W / 2;
            cx.fillStyle = "rgba(255,255,255,0.07)";
            cx.beginPath();
            cx.roundRect(bar.X, bar.Y, bar.W, bar.H, r);
            cx.fill();
            float ty = bar.Y + (bar.H - bar.Thumb) * (Scroll / bar.Max);
            cx.fillStyle = Hover == -2 ? "#a9e6f4" : "#7fd6e8";
            cx.beginPath();
            cx.roundRect(bar.X, ty, bar.W, bar.Thumb, r);
            cx.fill();
        }
        Panel.Commit();
    }

    void WantThumb(CatalogItem item, int width)
    {
        if (item.ThumbReq) return;
        item.ThumbReq = true;
        WantsThumb?.Invoke(item, width, (int)VR_CAT_THUMB_H);
    }

    // ---- picking: canvas pixels of the panel (null: off it)
    public Pick PickAt(Vector2? px)
    {
        var bar = Bar();
        float x = px?.X ?? -1, y = px?.Y ?? -1;
        bool onBar = px != null && bar.Max > 0 &&
            x >= bar.X - VR_CAT_BAR_GRAB && x <= bar.X + bar.W + VR_CAT_BAR_GRAB &&
            y >= bar.Y && y <= bar.Y + bar.H;
        return new Pick(onBar ? -1 : TileAt(px), onBar, ScrollFor(y));
    }

    /** Which tile the beam is on. */
    public int TileAt(Vector2? px)
    {
        if (px is not Vector2 p) return -1;
        float x = p.X, py = p.Y;
        if (py < VR_CAT_VIEW_Y || py > VR_CAT_VIEW_Y + VR_CAT_VIEW_H) return -1;
        float y = py - VR_CAT_VIEW_Y + Scroll; // into the list's own space
        foreach (var cell in Cells)
            if (x >= cell.X && x <= cell.X + cell.W && y >= cell.Y && y <= cell.Y + cell.H) return cell.I;
        return -1;
    }

    public void SetHover(int index)
    {
        if (Hover == index) return;
        Hover = index;
        Paint();
    }

    /** Which list the catalog is on (null: the directory); the buttons say so. */
    public void ApplyFilter(string? filter)
    {
        Filter = filter;
        Recent.SetState(on: filter == "recent");
        Fav.SetState(on: filter == "favorites");
    }

    /** A filter button pressed: its list, or - pressed again - the directory back. */
    public void SetFilter(string filter, ICatalogLibrary lib)
    {
        ApplyFilter(Filter == filter ? null : filter);
        Load(lib, false);
    }

    /** The list as given (a fixture, or a caller that built it): laid out from the top. */
    public void SetList(List<CatalogItem> items, string heading, string note)
    {
        Items = items;
        Heading = heading;
        Note = note;
        Scroll = 0;
        LayoutList();
    }

    /**
     * loadVrCatalog: the directory the library is looking at, as the headset's list - a row per
     * subdirectory, or the level tiles where it holds levels; or, with a filter, the levels played
     * or the favorites from any pack. Opening (`landing`) goes to the level being played.
     */
    public void Load(ICatalogLibrary library, bool landing)
    {
        Note = "loading…";
        Paint();
        CatalogNode node;
        try
        {
            if (landing)
            {
                ApplyFilter(null); // opens on the directory, not a list
                var hit = library.CurrentLevelId != null ? library.NodeOf(library.CurrentLevelId) : null;
                if (hit != null) library.Navigate(hit.Path);
            }
            node = library.CurrentNode() ?? library.Root;
        }
        catch (Exception e)
        {
            Note = "catalog unavailable";
            Paint();
            GD.PushError("[catalog] " + e.Message);
            return;
        }
        var items = new List<CatalogItem>();
        // a level from anywhere in the tree as a tile, labelled with where it lives
        CatalogItem TileOf(string levelId, CatalogNode n, bool full)
        {
            int i = n.Levels.IndexOf(levelId);
            var where = new List<string>();
            if (full) for (var p = n.Parent; p != null && p.Parent != null; p = p.Parent) where.Insert(0, p.Name);
            where.Add(n.Name + " " + (i + 1));
            return new CatalogItem
            {
                Kind = "level", LevelId = levelId, Playable = library.CanLoad(n.Engine),
                Label = string.Join(" › ", where),
                Name = library.LevelName(levelId),
                Set = library.WorldOf(levelId),
                Best = library.Best(levelId),
                Solution = library.HasSolution(levelId),
                Favorite = library.IsFavorite(levelId),
                Current = levelId == library.CurrentLevelId,
            };
        }
        if (Filter != null)
        {
            // the levels played, or the favorites: tiles from any pack, and a back row
            bool recent = Filter == "recent";
            var hits = (recent ? library.Recent() : library.Favorites())
                .Select(id => (id, node: library.NodeOf(id))).Where(h => h.node != null).ToList();
            var classic = hits.Select(h => h.node!).Where(n => n.Engine == "classic").Distinct().ToList();
            if (classic.Count > 0)
            {
                Note = "scanning levels…";
                Paint();
                foreach (var n in classic) library.EnsureNames(n);
            }
            Heading = (recent ? "recently played" : "favorites") + " · " + hits.Count + (hits.Count == 1 ? " level" : " levels");
            items.Add(new CatalogItem { Kind = "back", Label = "‹ back" });
            foreach (var (id, n) in hits) items.Add(TileOf(id, n!, true));
            Note = hits.Count > 0 ? "" : recent ? "no level played yet" : "no favorite yet - star a level to keep it here";
            Items = items;
            Scroll = 0;
            LayoutList();
            Paint();
            return;
        }
        var chain = new List<string>();
        for (var n = node; n != null; n = n.Parent) chain.Insert(0, n.Name);
        Heading = string.Join(" › ", chain);
        if (node.Parent != null) items.Add(new CatalogItem { Kind = "back", Label = "‹ back" });
        if (node.Levels.Count > 0)
        {
            if (node.Engine == "classic")
            {
                Note = "scanning levels…";
                Paint();
                library.EnsureNames(node);
            }
            bool playable = library.CanLoad(node.Engine);
            foreach (var level in node.Levels) items.Add(TileOf(level, node, false));
            Note = playable ? "" : "needs the Lemmix engine";
        }
        else
        {
            foreach (var child in node.Children)
                items.Add(new CatalogItem
                {
                    Kind = "dir", Path = child.Path, Label = child.Name, Engine = child.Engine,
                    Count = child.Count, Done = library.ClearedUnder(child),
                });
            Note = "";
        }
        Items = items;
        Scroll = 0;
        LayoutList();
        // open on the level being played, so the list starts where the player is
        int here = items.FindIndex(it => it.Current);
        if (here >= 0) RevealItem(here);
        if (library.Locked && Note == "") Note = "choose a level to play";
        Paint();
    }

    /** A press on the panel: the scrollbar moves the list, a row descends or ascends, a tile is
     *  the level to enter (returned, for the caller to close the catalog and enter it). */
    public string? Press(Pick p, bool scrubbing, ICatalogLibrary? library)
    {
        if (p.ScrollBar || scrubbing)
        {
            if (ScrollTo(p.ScrollAt)) Paint();
            return null;
        }
        var item = p.Tile >= 0 && p.Tile < Items.Count ? Items[p.Tile] : null;
        if (item == null || library == null) return null;
        if (item.Kind == "back")
        {
            if (Filter != null) ApplyFilter(null); // out of the list, back to the directory
            else library.Up();
            Load(library, false);
        }
        else if (item.Kind == "dir")
        {
            library.Navigate(item.Path!);
            Load(library, false);
        }
        else if (item.Playable) return item.LevelId;
        return null;
    }

    // ---- layoutVrCatalog: the window and its buttons, in metres of the windows' frame
    public readonly record struct Placement(Vector3 Pos, float ScaleX, float ScaleY);
    public static Placement PanelPlacement()
    {
        float w = VR_CATALOG_WIDTH, h = w * VR_CAT_H / VR_CAT_W;
        return new Placement(new Vector3(0, VR_CATALOG_Y, VR_MODAL_Z), w, h);
    }
    public static Placement ToolPlacement(int i, bool hot)
    {
        var (_, w, h) = PanelPlacement();
        float size = VR_BAR_TOOL_SIZE;
        // the close in the corner, the two filters in a row to its left
        return new Placement(new Vector3(
            w / 2 - size * 0.6f - i * size * 1.15f - (i != 0 ? size * 0.3f : 0),
            VR_CATALOG_Y + h / 2 - size * 0.6f,
            VR_MODAL_Z + (hot ? size * 0.25f : 0.001f)), size * (hot ? VR_BAR_TOOL_HOVER : 1), size * (hot ? VR_BAR_TOOL_HOVER : 1));
    }

    public void Layout()
    {
        var p = PanelPlacement();
        Planes.Set(Panel, p.Pos, p.ScaleX, p.ScaleY);
        for (int i = 0; i < Tools.Length; i++)
        {
            var t = ToolPlacement(i, Tools[i].State.Hovered);
            Tools[i].Position = t.Pos;
            Tools[i].Size = t.ScaleX;
        }
    }
}
