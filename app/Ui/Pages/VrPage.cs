using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using Lemmix.App.Ui.Windows;
using static Lemmix.App.Xr.VrManager;

namespace Lemmix.App.Ui.Pages;

// what the beam is on, on a page: a region's id (null: nothing that answers), or its scrollbar
// (ScrollAt: where a press there puts the list)
public readonly record struct PagePick(string? Id, bool ScrollBar = false, float ScrollAt = 0);

// A desktop page of the web (setup.html, solutions.html, the controls dialog, ...) as a window in
// the headset, in the catalog's way: one canvas, a fixed head and a body that scrolls behind a
// window (the stick, or the scrollbar down the right edge), picked by the ray's canvas pixel. A
// paint lists what can be pressed (Regions); the beam's pick is looked up in that list. A
// <select> opens a popup list over the page (PagePopup); work that takes time (a download, an
// unzip) runs off the frame and reports back through Post, drained by Update once a frame.
public abstract class VrPage
{
    public const float PAGE_PX_METRES = 0.00056f;   // metres per canvas pixel in the headset
    public const float PAGE_BAR_W = 14, PAGE_BAR_GRAB = 12;
    public const float PAGE_SCROLL = 900;           // canvas px/second at full stick
    public const int GUI_ORDER_PAGE = IconButton.GUI_ORDER_MODAL - 2; // under the question
    public const int GUI_ORDER_PAGE_BTN = IconButton.GUI_ORDER_MODAL - 1;

    public readonly string Name;
    public readonly int W, H;
    public readonly float S;                         // canvas px per CSS px
    public readonly Node3D Root;
    public readonly Panel3D Panel;
    public readonly IconButton Close;
    public readonly List<Region> Regions = new();
    public string? Hover;                            // the region under the beam
    public bool BarHot;                              // the scrollbar under the beam
    public float Scroll;
    public float ContentHeight;                      // the scrolled body's height, as last painted
    public Rect2 View;                               // where the body scrolls, canvas px
    public PagePopup? Popup;
    public Action? Closed;                           // the page asked to close (its own close button)

    readonly ConcurrentQueue<Action> _posted = new();
    Task? _work;
    bool _inScroll;

    protected VrPage(string name, int w, int h, float s)
    {
        Name = name;
        W = w; H = h; S = s;
        Root = new Node3D { Name = "vr-page-" + name, Visible = false };
        Panel = new Panel3D(w, h, 1f) { Name = "vr-page-" + name + "-panel" };
        Panel.NoDepthTest = true;
        Panel.RenderPriority = GUI_ORDER_PAGE;
        Root.AddChild(Panel);
        Close = new IconButton("pageclose-" + name, BarIcons.Cross, GUI_ORDER_PAGE_BTN);
        Root.AddChild(Close);
    }

    protected Canvas2D cx => Panel.Canvas;

    // ---- CSS px to canvas px
    public float U(float css) => css * S;
    public string Font(float css, bool bold = false) => (bold ? "bold " : "") + Math.Round(css * S) + "px monospace";

    // ---- painting
    public void Paint()
    {
        Regions.Clear();
        cx.clearRect(0, 0, W, H);
        cx.textAlign = "left";
        cx.textBaseline = "middle";
        // the window: the page's ground in the VR windows' frame
        cx.fillStyle = Css.Ground;
        cx.beginPath();
        cx.roundRect(2, 2, W - 4, H - 4, 18);
        cx.fill();
        PaintPage();
        PaintScrollBar();
        if (Popup != null) PaintPopup(Popup);
        cx.strokeStyle = Css.Frame;
        cx.lineWidth = 4;
        cx.beginPath();
        cx.roundRect(2, 2, W - 4, H - 4, 18);
        cx.stroke();
        Panel.Commit();
    }

    protected abstract void PaintPage();

    /** The body from here on scrolls: clipped to View, slid by Scroll. */
    protected void BeginScroll()
    {
        cx.save();
        cx.beginPath();
        cx.rect(View.Position.X, View.Position.Y, View.Size.X, View.Size.Y);
        cx.clip();
        cx.translate(0, View.Position.Y - Scroll);
        _inScroll = true;
    }

    protected void EndScroll(float contentHeight)
    {
        cx.restore();
        _inScroll = false;
        ContentHeight = contentHeight;
        ScrollTo(Scroll);
    }

    /** Is this body y (content space) in view, give or take `h`? Painting skips what is not. */
    protected bool InView(float y, float h) => y + h >= Scroll && y <= Scroll + View.Size.Y;

    /** A pressable rectangle; in the body, in the body's coordinates (clipped to the window). */
    protected void Hit(string id, float x, float y, float w, float h, bool enabled = true)
    {
        var r = new Rect2(x, y, w, h);
        if (_inScroll)
        {
            r.Position = new Vector2(x, y - Scroll + View.Position.Y);
            r = r.Intersection(View);
            if (r.Size.X <= 0 || r.Size.Y <= 0) return;
        }
        Regions.Add(new Region(id, r, enabled, false));
    }

    public bool Hot(string id) => Hover == id;

    // ---- the page's widgets, as the CSS draws them
    public float ButtonH => U(25);
    public float ButtonW(string label, float css = 11) { cx.font = Font(css); return cx.measureText(label).width + U(22); }

    /** A <button>: its ground, its border, its label; registered under `id`. */
    protected float Button(string id, string label, float x, float y, bool enabled = true, string kind = "", float css = 11, float? width = null)
    {
        float w = width ?? ButtonW(label, css), h = ButtonH;
        bool hot = enabled && Hot(id);
        string bg = Css.BtnBg, border = Css.BtnBorder, color = Css.Text;
        if (kind == "primary") { bg = hot ? "#26485c" : "#173442"; border = Css.Link; color = Css.Link; }
        else if (kind == "on") { bg = hot ? Css.BtnHot : Css.BtnBg; border = Css.Yellow; color = Css.Yellow; }
        else if (kind == "confirm") { bg = hot ? "#4a4326" : Css.BtnBg; border = Css.Yellow; color = Css.Yellow; }
        else if (kind == "warn") { border = "#6b3a2f"; color = Css.Red; if (hot) bg = Css.BtnHot; }
        else if (kind == "bare") { bg = hot ? Css.BtnHot : "rgba(0,0,0,0)"; border = "rgba(0,0,0,0)"; color = hot ? Css.Bright : Css.Dim; }
        else if (hot) bg = Css.BtnHot;
        cx.globalAlpha = enabled ? 1 : 0.4f;
        cx.fillStyle = bg;
        cx.beginPath();
        cx.roundRect(x, y, w, h, U(4));
        cx.fill();
        cx.strokeStyle = border;
        cx.lineWidth = Math.Max(1, U(1));
        cx.stroke();
        cx.fillStyle = color;
        cx.font = Font(css);
        cx.textAlign = "center";
        cx.fillText(label, x + w / 2, y + h / 2);
        cx.textAlign = "left";
        cx.globalAlpha = 1;
        Hit(id, x, y, w, h, enabled);
        return w;
    }

    /** A checkbox and its label (<label><input type=checkbox> text</label>). Returns its width. */
    protected float Checkbox(string id, string label, bool on, float x, float y, float h, bool enabled = true, string labelColor = Css.Dim, float css = 12)
    {
        float box = U(13), bx = x, by = y + (h - box) / 2;
        bool hot = enabled && Hot(id);
        cx.globalAlpha = enabled ? 1 : 0.4f;
        cx.fillStyle = on ? "#3b82f6" : (hot ? "#e8eef7" : "#ffffff");
        if (on) cx.fillStyle = "#1a73e8";
        cx.beginPath();
        cx.roundRect(bx, by, box, box, U(2));
        cx.fill();
        if (!on) { cx.strokeStyle = "#767676"; cx.lineWidth = Math.Max(1, U(1)); cx.stroke(); }
        else
        {
            cx.strokeStyle = "#ffffff";
            cx.lineWidth = U(2);
            cx.beginPath();
            cx.moveTo(bx + box * 0.22f, by + box * 0.52f);
            cx.lineTo(bx + box * 0.42f, by + box * 0.72f);
            cx.lineTo(bx + box * 0.78f, by + box * 0.3f);
            cx.stroke();
        }
        cx.font = Font(css);
        cx.fillStyle = labelColor;
        float tw = cx.measureText(label).width;
        cx.fillText(label, bx + box + U(5), y + h / 2);
        cx.globalAlpha = 1;
        float w = box + U(5) + tw;
        Hit(id, x, y, w, h, enabled);
        return w;
    }

    /** A <select>: the chosen option's label and the arrow; a press opens its popup. */
    protected void Select(string id, string label, float x, float y, float w, float h, float css = 12, string bg = Css.Input, string color = Css.Bright, bool enabled = true)
    {
        bool hot = enabled && (Hot(id) || Popup?.Id == id);
        cx.globalAlpha = enabled ? 1 : 0.5f;
        cx.fillStyle = bg;
        cx.beginPath();
        cx.roundRect(x, y, w, h, U(4));
        cx.fill();
        cx.strokeStyle = hot ? Css.Green : Css.BtnBorder;
        cx.lineWidth = Math.Max(1, U(1));
        cx.stroke();
        cx.font = Font(css);
        cx.fillStyle = color;
        cx.fillText(PageText.Fit(cx, label, w - U(30)), x + U(8), y + h / 2);
        // the arrow
        float ax = x + w - U(14), ay = y + h / 2;
        cx.strokeStyle = color;
        cx.lineWidth = U(1.5f);
        cx.beginPath();
        cx.moveTo(ax - U(4), ay - U(2));
        cx.lineTo(ax, ay + U(2));
        cx.lineTo(ax + U(4), ay - U(2));
        cx.stroke();
        cx.globalAlpha = 1;
        Hit(id, x, y, w, h, enabled);
    }

    /** An <input>: its text (or the placeholder), a caret while the keyboard types into it, an
     *  × to clear it as type=search shows one. */
    protected void TextInput(string id, string text, string placeholder, float x, float y, float w, float h, float css = 12)
    {
        bool focused = FocusField == id, hot = Hot(id);
        cx.fillStyle = Css.Input;
        cx.beginPath();
        cx.roundRect(x, y, w, h, U(4));
        cx.fill();
        cx.strokeStyle = focused || hot ? Css.Green : Css.BtnBorder;
        cx.lineWidth = Math.Max(1, U(1));
        cx.stroke();
        cx.font = Font(css);
        float tx = x + U(8);
        if (text == "")
        {
            cx.fillStyle = "#6b7686";
            cx.fillText(PageText.Fit(cx, placeholder, w - U(16)), tx, y + h / 2);
        }
        else
        {
            cx.fillStyle = Css.Bright;
            string shown = PageText.Fit(cx, text, w - U(36));
            cx.fillText(shown, tx, y + h / 2);
            tx += cx.measureText(shown).width;
            // the search field's clear button
            cx.strokeStyle = "#3a6bd1";
            cx.lineWidth = U(1.6f);
            float cxp = x + w - U(16), cyp = y + h / 2, r = U(3.5f);
            cx.beginPath(); cx.moveTo(cxp - r, cyp - r); cx.lineTo(cxp + r, cyp + r); cx.moveTo(cxp + r, cyp - r); cx.lineTo(cxp - r, cyp + r); cx.stroke();
        }
        if (focused)
        {
            cx.fillStyle = Css.Bright;
            cx.fillRect(tx + U(1), y + h / 2 - U(7), Math.Max(1, U(1)), U(14));
        }
        Hit(id, x, y, w, h);
        if (text != "") Hit(id + ":clear", x + w - U(28), y, U(28), h);
    }

    /** A tag chip: the dialog's .tag, the setup's badges (outlined, upper case). */
    protected float Chip(string text, float x, float yMid, string bg, string color, float css = 10, string? border = null, bool upper = false, float spacing = 0)
    {
        cx.font = Font(css);
        string t = upper ? text.ToUpperInvariant() : text;
        float tw = PageText.Spaced(cx, t, 0, 0, U(spacing), false);
        float padX = U(border != null ? 6 : 5), h = U(css * 1.4f + (border != null ? 4 : 0));
        float w = tw + padX * 2;
        cx.fillStyle = bg;
        cx.beginPath();
        cx.roundRect(x, yMid - h / 2, w, h, U(3));
        cx.fill();
        if (border != null) { cx.strokeStyle = border; cx.lineWidth = Math.Max(1, U(1)); cx.stroke(); }
        cx.fillStyle = color;
        PageText.Spaced(cx, t, x + padX, yMid, U(spacing));
        return w;
    }

    // ---- the popup list of a select
    public float PopupRowH => U(20);

    protected void OpenPopup(string id, List<(string Value, string Label)> options, string? current, Rect2 anchor, int maxRows = 14)
    {
        var p = new PagePopup(id, options, current) { RowH = PopupRowH };
        int rows = Math.Min(options.Count, maxRows);
        float h = rows * p.RowH + U(4);
        float y = anchor.End.Y + U(2);
        if (y + h > H - 8) y = Math.Max(8, anchor.Position.Y - h - U(2)); // no room below: above
        float w = Math.Max(anchor.Size.X, 0);
        float x = Math.Max(8, Math.Min(anchor.Position.X, W - 8 - w));   // inside the window
        p.Box = new Rect2(x, y, w, h);
        int at = options.FindIndex(o => o.Value == current);
        if (at >= 0) p.Scroll = Math.Clamp(at * p.RowH - h / 2, 0, p.MaxScroll);
        Popup = p;
    }

    void PaintPopup(PagePopup p)
    {
        var b = p.Box;
        cx.fillStyle = "#1c2432";
        cx.beginPath();
        cx.roundRect(b.Position.X, b.Position.Y, b.Size.X, b.Size.Y, U(4));
        cx.fill();
        cx.strokeStyle = Css.Green;
        cx.lineWidth = Math.Max(1, U(1));
        cx.stroke();
        cx.save();
        cx.beginPath();
        cx.rect(b.Position.X, b.Position.Y + U(2), b.Size.X, b.Size.Y - U(4));
        cx.clip();
        cx.font = Font(12);
        for (int i = 0; i < p.Options.Count; i++)
        {
            float y = b.Position.Y + U(2) + i * p.RowH - p.Scroll;
            if (y + p.RowH < b.Position.Y || y > b.End.Y) continue;
            bool hot = i == p.Hover, cur = p.Options[i].Value == p.Current;
            if (hot || cur)
            {
                cx.fillStyle = hot ? "#3a64b8" : "#33405a";
                cx.fillRect(b.Position.X + U(2), y, b.Size.X - U(4) - (p.MaxScroll > 0 ? PAGE_BAR_W : 0), p.RowH);
            }
            cx.fillStyle = hot ? "#ffffff" : Css.Bright;
            cx.fillText(PageText.Fit(cx, p.Options[i].Label, b.Size.X - U(16) - (p.MaxScroll > 0 ? PAGE_BAR_W : 0)), b.Position.X + U(8), y + p.RowH / 2);
            var r = new Rect2(b.Position.X, y, b.Size.X - (p.MaxScroll > 0 ? PAGE_BAR_W + PAGE_BAR_GRAB : 0), p.RowH).Intersection(b);
            if (r.Size.Y > 0) Regions.Add(new Region("popup:" + i, r, true, true));
        }
        cx.restore();
        if (p.MaxScroll > 0)
        {
            float trackH = b.Size.Y - U(4), thumb = Math.Max(30, trackH * trackH / (p.Options.Count * p.RowH));
            float x = b.End.X - PAGE_BAR_W - U(2), ty = b.Position.Y + U(2) + (trackH - thumb) * (p.Scroll / p.MaxScroll);
            cx.fillStyle = "rgba(255,255,255,0.07)";
            cx.fillRect(x, b.Position.Y + U(2), PAGE_BAR_W, trackH);
            cx.fillStyle = Css.Link;
            cx.beginPath();
            cx.roundRect(x, ty, PAGE_BAR_W, thumb, PAGE_BAR_W / 2);
            cx.fill();
        }
    }

    // ---- the scrollbar
    public readonly record struct ScrollBarRect(float X, float Y, float W, float H, float Max, float Thumb);

    /** Where the scrollbar runs: down the window's right edge, or inside a list's box. */
    protected virtual float BarX => W - 10 - PAGE_BAR_W;

    public ScrollBarRect Bar()
    {
        float max = Math.Max(0, ContentHeight - View.Size.Y);
        return new ScrollBarRect(BarX, View.Position.Y + 4, PAGE_BAR_W, View.Size.Y - 8, max,
            max > 0 ? Math.Max(40, (View.Size.Y - 8) * View.Size.Y / ContentHeight) : 0);
    }

    void PaintScrollBar()
    {
        var bar = Bar();
        if (bar.Max <= 0) return;
        float r = PAGE_BAR_W / 2;
        cx.fillStyle = "rgba(255,255,255,0.07)";
        cx.beginPath();
        cx.roundRect(bar.X, bar.Y, bar.W, bar.H, r);
        cx.fill();
        float ty = bar.Y + (bar.H - bar.Thumb) * (Scroll / bar.Max);
        cx.fillStyle = BarHot ? Css.LinkHot : Css.Link;
        cx.beginPath();
        cx.roundRect(bar.X, ty, bar.W, bar.Thumb, r);
        cx.fill();
    }

    public float ScrollFor(float canvasY)
    {
        var bar = Bar();
        float travel = bar.H - bar.Thumb;
        if (travel <= 0) return 0;
        return (canvasY - bar.Y - bar.Thumb / 2) / travel * bar.Max;
    }

    public bool ScrollTo(float next)
    {
        float max = Math.Max(0, ContentHeight - View.Size.Y);
        float c = Math.Max(0, Math.Min(max, next));
        if (c == Scroll) return false;
        Scroll = c;
        return true;
    }

    /** Bring a body rectangle (content coordinates) into the window. */
    public void Reveal(float y, float h)
    {
        if (y < Scroll) ScrollTo(y - U(4));
        else if (y + h > Scroll + View.Size.Y) ScrollTo(y + h - View.Size.Y + U(4));
    }

    /** The thumbsticks while the page is up: the popup list if one is open, else the page. */
    public void OnStick(float y, double seconds)
    {
        float d = (float)(-y * PAGE_SCROLL * seconds);
        if (Popup != null)
        {
            float next = Math.Clamp(Popup.Scroll + d, 0, Popup.MaxScroll);
            if (next != Popup.Scroll) { Popup.Scroll = next; Paint(); }
            return;
        }
        if (ScrollTo(Scroll + d)) Paint();
    }

    // ---- picking: canvas pixels of the panel (null: off it)
    public PagePick PickAt(Vector2? px)
    {
        if (px is not Vector2 p) return new PagePick(null);
        if (Popup != null)
        {
            var b = Popup.Box;
            if (b.HasPoint(p))
            {
                if (Popup.MaxScroll > 0 && p.X >= b.End.X - PAGE_BAR_W - PAGE_BAR_GRAB)
                {
                    float f = Math.Clamp((p.Y - b.Position.Y) / b.Size.Y, 0, 1);
                    return new PagePick("popupbar", false, f * Popup.MaxScroll);
                }
                for (int i = Regions.Count - 1; i >= 0; i--)
                    if (Regions[i].Overlay && Regions[i].Rect.HasPoint(p)) return new PagePick(Regions[i].Id);
                return new PagePick("popup");
            }
            return new PagePick("popupout"); // a press beside the list closes it
        }
        var bar = Bar();
        if (bar.Max > 0 && p.X >= bar.X - PAGE_BAR_GRAB && p.X <= bar.X + bar.W + PAGE_BAR_GRAB && p.Y >= bar.Y && p.Y <= bar.Y + bar.H)
            return new PagePick(null, true, ScrollFor(p.Y));
        for (int i = Regions.Count - 1; i >= 0; i--)
        {
            var r = Regions[i];
            if (r.Rect.HasPoint(p)) return new PagePick(r.Enabled ? r.Id : null);
        }
        return new PagePick(null);
    }

    /** Where a region is (canvas px), for a test's ray or a popup's anchor. */
    public Rect2? RegionRect(string id)
    {
        foreach (var r in Regions) if (r.Id == id) return r.Rect;
        return null;
    }

    public void SetHover(PagePick p)
    {
        string? id = p.ScrollBar ? null : p.Id;
        bool bar = p.ScrollBar;
        int popupHover = Popup != null && id != null && id.StartsWith("popup:", StringComparison.Ordinal) ? int.Parse(id[6..]) : -1;
        if (id == Hover && bar == BarHot && (Popup == null || Popup.Hover == popupHover)) return;
        Hover = id;
        BarHot = bar;
        if (Popup != null) Popup.Hover = popupHover;
        Paint();
    }

    /** A press on the panel (the trigger), or a drag of the scrollbar (`scrubbing`). */
    public void Press(PagePick p, bool scrubbing = false)
    {
        if (Popup != null)
        {
            var pop = Popup;
            if (p.Id == "popupbar" || (scrubbing && p.Id != null && p.Id.StartsWith("popup", StringComparison.Ordinal)))
            {
                if (p.Id == "popupbar") pop.Scroll = Math.Clamp(p.ScrollAt, 0, pop.MaxScroll);
            }
            else if (p.Id != null && p.Id.StartsWith("popup:", StringComparison.Ordinal))
            {
                int i = int.Parse(p.Id[6..]);
                Popup = null;
                if (i >= 0 && i < pop.Options.Count) Choose(pop.Id, pop.Options[i].Value);
            }
            else if (p.Id != "popup") Popup = null;
            Paint();
            return;
        }
        if (p.ScrollBar || scrubbing)
        {
            if (p.ScrollBar && ScrollTo(p.ScrollAt)) Paint();
            return;
        }
        if (p.Id == null) return;
        OnPress(p.Id);
        Paint();
    }

    /** A region pressed. */
    protected abstract void OnPress(string id);

    /** An option chosen in the popup a select opened. */
    protected virtual void Choose(string popupId, string value) { }

    /** A key on a Bluetooth keyboard while the page is up (KeyboardEvent.code, the character it
     *  types or null). True when the page took it. */
    public virtual bool OnKey(string code, string? text) => false;

    /** A controller button while the page is up (the table's Vr* code). True when taken. */
    public virtual bool OnVrButton(string code) => false;

    // ---- text fields: a press on one asks for the VR keyboard (VrPages opens it on the field);
    // what is typed comes back here, key by key, as the web's input events
    public Action<VrPage, string, string, bool>? EditText;   // (page, field, its text, numeric)
    public string? FocusField;                               // the field the keyboard types into

    protected void Edit(string field, string text, bool numeric = false)
    {
        FocusField = field;
        EditText?.Invoke(this, field, text, numeric);
    }

    public virtual void TextChanged(string field, string text) { }
    public virtual void TextEnter(string field) { }
    public virtual void TextEscape(string field) { }
    public virtual void TextDone(string field) { if (FocusField == field) FocusField = null; }

    /** The page is shown (again): read what it lists afresh. */
    public virtual void Opened() { }

    // ---- work off the frame
    /** Run this on the frame's thread at the next Update. */
    public void Post(Action a) => _posted.Enqueue(a);

    public bool Working => _work != null && !_work.IsCompleted;

    protected void RunBackground(Func<Task> work)
    {
        _work = Task.Run(async () =>
        {
            try { await work(); }
            catch (Exception e) { Post(() => GD.PushError("[page " + Name + "] " + e)); }
        });
    }

    /** Once a frame: what the background work posted, then a repaint if anything came. */
    public bool Update()
    {
        bool any = Poll();
        while (_posted.TryDequeue(out var a)) { a(); any = true; }
        if (any) Paint();
        return any;
    }

    /** Once a frame, before the posted work: true when the page must be painted again. */
    protected virtual bool Poll() => false;

    /** Tests: wait for the background work and run what it posted. */
    public void WaitIdle(int timeoutMs = 20000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < until)
        {
            Update();
            if (!Working && _posted.IsEmpty) { Update(); return; }
            System.Threading.Thread.Sleep(5);
        }
        throw new TimeoutException("page " + Name + ": background work did not finish");
    }

    // ---- placement, in metres of the windows' frame (as the catalog's)
    public readonly record struct Placement(Vector3 Pos, float ScaleX, float ScaleY);

    public float WidthMetres => W * PAGE_PX_METRES;
    public virtual float CentreY => VR_CATALOG_Y;
    public virtual float PlaneZ => VR_MODAL_Z - 0.05f;

    public Placement PanelPlacement()
    {
        float w = WidthMetres, h = w * H / W;
        return new Placement(new Vector3(0, CentreY, PlaneZ), w, h);
    }

    public Placement ClosePlacement(bool hot)
    {
        var (_, w, h) = PanelPlacement();
        float size = VR_BAR_TOOL_SIZE;
        float s = size * (hot ? VR_BAR_TOOL_HOVER : 1);
        return new Placement(new Vector3(w / 2 - size * 0.6f, CentreY + h / 2 - size * 0.6f, PlaneZ + (hot ? size * 0.25f : 0.001f)), s, s);
    }

    public void Layout()
    {
        var p = PanelPlacement();
        Planes.Set(Panel, p.Pos, p.ScaleX, p.ScaleY);
        var c = ClosePlacement(Close.State.Hovered);
        Close.Size = c.ScaleX;
        Close.Position = c.Pos;
    }
}
