using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;

namespace Lemmix.App.Ui;

// The subset of the HTML canvas 2D API the web version's VR windows paint with (app.js, gui.js,
// replay-markers.js: paths with lines, arcs, round rects and curves; fill, stroke, rects, text,
// images), recorded and replayed into a Godot CanvasItem. The paint code ports line for line;
// colours are CSS strings as there. Fonts: every window uses "<size>px monospace" or
// "bold <size>px monospace", drawn with the bundled Noto Sans Mono.
// A rectangular clip() is honoured (the drawing after it goes in a segment of its own, replayed in
// a Control that clips its contents); `Trace`, when set, records every call as the web's oracle
// records it (oracle/webshots.js), so a port's paint can be compared call for call.
public sealed class Canvas2D
{
    public int Width, Height;
    public Canvas2D(int width, int height) { Width = width; Height = height; }

    // ---- state (CanvasRenderingContext2D)
    sealed class State
    {
        public Color Fill = Colors.Black, Stroke = Colors.Black;
        public float LineWidth = 1, GlobalAlpha = 1;
        public string Font = "10px monospace", TextAlign = "start", TextBaseline = "alphabetic";
        public Transform2D Transform = Transform2D.Identity;
        public bool Smoothing = true;
        public string LineCap = "butt", LineJoin = "miter";
        public Rect2? Clip;
        public State Copy() => (State)MemberwiseClone();
    }

    State _s = new();
    readonly Stack<State> _stack = new();
    // what has been drawn: runs of operations under one clip (null: the whole canvas). An operation
    // draws into a CanvasItem whose origin is offset by the base transform it is given.
    internal sealed class Segment
    {
        public Rect2? Clip;
        public readonly List<Action<CanvasItem, Transform2D>> Ops = new();
    }
    readonly List<Segment> _segs = new();
    internal IReadOnlyList<Segment> Segments => _segs;
    void Add(Action<CanvasItem, Transform2D> op)
    {
        if (_segs.Count == 0 || _segs[^1].Clip != _s.Clip) _segs.Add(new Segment { Clip = _s.Clip });
        _segs[^1].Ops.Add(op);
    }

    // ---- the trace: "fillStyle=#fff", "roundRect(2,2,60,60,12)", "fillText(\"OK\",84,30)"
    public List<string>? Trace;
    void T(string name, params object?[] args)
    {
        if (Trace == null) return;
        var sb = new System.Text.StringBuilder(name).Append('(');
        for (int i = 0; i < args.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(args[i] switch { string str => Json(str), float f => Num(f), double d => Num(d), int n => Num(n), bool b => b ? "true" : "false", _ => Json("img") });
        }
        Trace.Add(sb.Append(')').ToString());
    }
    void TSet(string name, string value) => Trace?.Add(name + "=" + value);
    // as the oracle prints a number: rounded to 3 places (half up), no trailing zeros
    public static string Num(double v)
    {
        double r = Math.Floor(v * 1000 + 0.5) / 1000;
        return r == 0 ? "0" : r.ToString("0.###", CultureInfo.InvariantCulture);
    }
    static string Json(string str)
    {
        var sb = new System.Text.StringBuilder("\"");
        foreach (char c in str)
        {
            if (c == '"' || c == '\\') sb.Append('\\').Append(c);
            else if (c == '\n') sb.Append("\\n");
            else if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
            else sb.Append(c);
        }
        return sb.Append('"').ToString();
    }
    readonly List<List<Vector2>> _path = new();
    List<Vector2>? _current;
    readonly List<bool> _closed = new();

    public string fillStyle { set { TSet("fillStyle", value); _s.Fill = ParseColor(value); } }
    public string strokeStyle { set { TSet("strokeStyle", value); _s.Stroke = ParseColor(value); } }
    public float lineWidth { get => _s.LineWidth; set { TSet("lineWidth", Num(value)); _s.LineWidth = value; } }
    public float globalAlpha { get => _s.GlobalAlpha; set { TSet("globalAlpha", Num(value)); _s.GlobalAlpha = value; } }
    public string font { get => _s.Font; set { TSet("font", value); _s.Font = value; } }
    public string textAlign { get => _s.TextAlign; set { TSet("textAlign", value); _s.TextAlign = value; } }
    public string textBaseline { get => _s.TextBaseline; set { TSet("textBaseline", value); _s.TextBaseline = value; } }
    public bool imageSmoothingEnabled { get => _s.Smoothing; set { TSet("imageSmoothingEnabled", value ? "true" : "false"); _s.Smoothing = value; } }
    public string lineCap { get => _s.LineCap; set { TSet("lineCap", value); _s.LineCap = value; } }
    public string lineJoin { get => _s.LineJoin; set { TSet("lineJoin", value); _s.LineJoin = value; } }

    public void save() { T("save"); _stack.Push(_s.Copy()); }
    public void restore() { T("restore"); if (_stack.Count > 0) _s = _stack.Pop(); }
    public void translate(float x, float y) { T("translate", x, y); Translate(x, y); }
    public void scale(float x, float y) { T("scale", x, y); Scale(x, y); }
    public void rotate(float a) { T("rotate", a); Rotate(a); }
    void Translate(float x, float y) => _s.Transform = _s.Transform * Transform2D.Identity.Translated(new Vector2(x, y));
    void Scale(float x, float y) => _s.Transform = _s.Transform * Transform2D.Identity.Scaled(new Vector2(x, y));
    void Rotate(float a) => _s.Transform = _s.Transform * Transform2D.Identity.Rotated(a);

    // everything recorded so far goes; the panel starts transparent again
    public void Reset() { _segs.Clear(); _path.Clear(); _closed.Clear(); _current = null; _s = new State(); _stack.Clear(); }

    // A new size, as `canvas.width = w` does: cleared, and the state back to its defaults.
    public void Resize(int width, int height) { Width = width; Height = height; Reset(); }

    // The clip is the bounding box of the current path (in canvas pixels), within the clip already
    // in force: what a clip to a rect needs, which is all the windows use.
    public void clip()
    {
        T("clip");
        bool any = false;
        Rect2 box = default;
        foreach (var sub in _path)
            foreach (var p in sub)
            {
                if (!any) { box = new Rect2(p, Vector2.Zero); any = true; }
                else box = box.Expand(p);
            }
        if (!any) box = new Rect2(0, 0, 0, 0);
        _s.Clip = _s.Clip is Rect2 old ? old.Intersection(box) : box;
    }

    // ---- paths
    public void beginPath() { T("beginPath"); _path.Clear(); _closed.Clear(); _current = null; }
    public void moveTo(float x, float y) { T("moveTo", x, y); MoveTo(x, y); }
    public void lineTo(float x, float y) { T("lineTo", x, y); LineTo(x, y); }
    public void closePath() { T("closePath"); ClosePath(); }
    void MoveTo(float x, float y) { _current = new List<Vector2> { P(x, y) }; _path.Add(_current); _closed.Add(false); }
    void LineTo(float x, float y) { if (_current == null) MoveTo(x, y); else _current.Add(P(x, y)); }
    void ClosePath() { if (_current != null) { _closed[^1] = true; var start = _current[0]; _current = new List<Vector2> { start }; _path.Add(_current); _closed.Add(false); } }
    Vector2 P(float x, float y) => _s.Transform * new Vector2(x, y);
    Vector2 Last => _current is { Count: > 0 } c ? _s.Transform.AffineInverse() * c[^1] : Vector2.Zero;

    public void rect(float x, float y, float w, float h)
    {
        T("rect", x, y, w, h);
        MoveTo(x, y); LineTo(x + w, y); LineTo(x + w, y + h); LineTo(x, y + h); ClosePath();
    }

    public void roundRect(float x, float y, float w, float h, float r)
    {
        T("roundRect", x, y, w, h, r);
        r = Math.Max(0, Math.Min(r, Math.Min(Math.Abs(w), Math.Abs(h)) / 2));
        MoveTo(x + r, y);
        LineTo(x + w - r, y); ArcPoints(x + w - r, y + r, r, -Mathf.Pi / 2, 0, false);
        LineTo(x + w, y + h - r); ArcPoints(x + w - r, y + h - r, r, 0, Mathf.Pi / 2, false);
        LineTo(x + r, y + h); ArcPoints(x + r, y + h - r, r, Mathf.Pi / 2, Mathf.Pi, false);
        LineTo(x, y + r); ArcPoints(x + r, y + r, r, Mathf.Pi, Mathf.Pi * 1.5f, false);
        ClosePath();
    }

    public void arc(float x, float y, float r, float a0, float a1, bool? ccw = null)
    {
        if (ccw is bool c) T("arc", x, y, r, a0, a1, c); else T("arc", x, y, r, a0, a1);
        Arc(x, y, r, a0, a1, ccw == true);
    }

    void Arc(float x, float y, float r, float a0, float a1, bool ccw)
    {
        var start = new Vector2(x + r * Mathf.Cos(a0), y + r * Mathf.Sin(a0));
        if (_current == null) MoveTo(start.X, start.Y); else LineTo(start.X, start.Y);
        ArcPoints(x, y, r, a0, a1, ccw);
    }

    public void ellipse(float x, float y, float rx, float ry, float rotation, float a0, float a1, bool? ccw = null)
    {
        if (ccw is bool c) T("ellipse", x, y, rx, ry, rotation, a0, a1, c); else T("ellipse", x, y, rx, ry, rotation, a0, a1);
        var keep = _s.Transform;
        Translate(x, y); Rotate(rotation); Scale(rx, ry);
        Arc(0, 0, 1, a0, a1, ccw == true);
        _s.Transform = keep;
    }

    void ArcPoints(float x, float y, float r, float a0, float a1, bool ccw)
    {
        float sweep = a1 - a0;
        if (!ccw && sweep < 0) sweep = sweep % (Mathf.Tau) + Mathf.Tau;
        if (ccw && sweep > 0) sweep = sweep % (Mathf.Tau) - Mathf.Tau;
        if (!ccw && a1 - a0 >= Mathf.Tau) sweep = Mathf.Tau;
        if (ccw && a0 - a1 >= Mathf.Tau) sweep = -Mathf.Tau;
        int steps = Math.Max(2, (int)Math.Ceiling(Math.Abs(sweep) / (Mathf.Pi / 24) * Math.Max(1, r / 24)));
        for (int i = 1; i <= steps; i++)
        {
            float a = a0 + sweep * i / steps;
            LineTo(x + r * Mathf.Cos(a), y + r * Mathf.Sin(a));
        }
    }

    public void quadraticCurveTo(float cx, float cy, float x, float y)
    {
        T("quadraticCurveTo", cx, cy, x, y);
        var p0 = Last;
        for (int i = 1; i <= 16; i++)
        {
            float t = i / 16f, u = 1 - t;
            LineTo(u * u * p0.X + 2 * u * t * cx + t * t * x, u * u * p0.Y + 2 * u * t * cy + t * t * y);
        }
    }

    public void bezierCurveTo(float c1x, float c1y, float c2x, float c2y, float x, float y)
    {
        T("bezierCurveTo", c1x, c1y, c2x, c2y, x, y);
        var p0 = Last;
        for (int i = 1; i <= 20; i++)
        {
            float t = i / 20f, u = 1 - t;
            LineTo(u * u * u * p0.X + 3 * u * u * t * c1x + 3 * u * t * t * c2x + t * t * t * x,
                   u * u * u * p0.Y + 3 * u * u * t * c1y + 3 * u * t * t * c2y + t * t * t * y);
        }
    }

    public void fill()
    {
        T("fill");
        var color = WithAlpha(_s.Fill);
        foreach (var poly in Polygons())
        {
            var pts = poly;
            var idx = Geometry2D.TriangulatePolygon(pts);
            if (idx.Length == 0) continue;
            Add((ci, b) =>
            {
                ci.DrawSetTransformMatrix(b);
                RenderingServer.CanvasItemAddTriangleArray(ci.GetCanvasItem(), idx, pts, new[] { color });
                ci.DrawSetTransformMatrix(Transform2D.Identity);
            });
        }
    }

    public void stroke()
    {
        T("stroke");
        var color = WithAlpha(_s.Stroke);
        float w = _s.LineWidth * ScaleOf(_s.Transform);
        bool roundJoin = _s.LineJoin == "round", roundCap = _s.LineCap == "round", squareCap = _s.LineCap == "square";
        for (int k = 0; k < _path.Count; k++)
        {
            var sub = Dedup(_path[k], false);
            if (sub.Count < 2) continue;
            bool closed = _closed[k];
            var pts = new List<Vector2>(sub);
            if (closed) pts.Add(sub[0]);
            else if (squareCap)
            {
                pts[0] -= (pts[1] - pts[0]).Normalized() * w / 2;
                pts[^1] += (pts[^1] - pts[^2]).Normalized() * w / 2;
            }
            var arr = pts.ToArray();
            // the round joins and caps: a disc at each corner the line turns (and at its two ends)
            var dots = new List<Vector2>();
            if (roundJoin) for (int i = closed ? 0 : 1; i < sub.Count - (closed ? 0 : 1); i++) dots.Add(sub[i]);
            if (roundCap && !closed) { dots.Add(sub[0]); dots.Add(sub[^1]); }
            var dotArr = dots.ToArray();
            Add((ci, b) =>
            {
                ci.DrawSetTransformMatrix(b);
                ci.DrawPolyline(arr, color, w, true);
                foreach (var d in dotArr) ci.DrawCircle(d, w / 2, color, true, -1, true);
                ci.DrawSetTransformMatrix(Transform2D.Identity);
            });
        }
    }

    // the subpaths to fill, each closed implicitly, without repeated points (a full arc ends on
    // the point it started from, which the triangulator refuses)
    IEnumerable<Vector2[]> Polygons()
    {
        foreach (var sub in _path)
        {
            var pts = Dedup(sub, true);
            if (pts.Count >= 3) yield return pts.ToArray();
        }
    }

    static List<Vector2> Dedup(List<Vector2> sub, bool ring)
    {
        var o = new List<Vector2>(sub.Count);
        foreach (var p in sub) if (o.Count == 0 || o[^1].DistanceSquaredTo(p) > 1e-8f) o.Add(p);
        if (ring) while (o.Count > 1 && o[0].DistanceSquaredTo(o[^1]) <= 1e-8f) o.RemoveAt(o.Count - 1);
        return o;
    }

    static float ScaleOf(Transform2D t) => (t.X.Length() + t.Y.Length()) / 2;

    // ---- rects
    public void fillRect(float x, float y, float w, float h)
    {
        T("fillRect", x, y, w, h);
        var color = WithAlpha(_s.Fill);
        var xf = _s.Transform;
        Add((ci, b) => { ci.DrawSetTransformMatrix(b * xf); ci.DrawRect(new Rect2(x, y, w, h), color); ci.DrawSetTransformMatrix(Transform2D.Identity); });
    }

    // strokeRect leaves the current path alone, as the canvas does
    public void strokeRect(float x, float y, float w, float h)
    {
        T("strokeRect", x, y, w, h);
        var (path, closed, current, trace) = (new List<List<Vector2>>(_path), new List<bool>(_closed), _current, Trace);
        Trace = null;
        _path.Clear(); _closed.Clear(); _current = null;
        rect(x, y, w, h); stroke();
        _path.Clear(); _path.AddRange(path); _closed.Clear(); _closed.AddRange(closed); _current = current;
        Trace = trace;
    }

    // the panel is redrawn from scratch on every paint (Reset); a clear of the whole canvas is that
    public void clearRect(float x, float y, float w, float h)
    {
        T("clearRect", x, y, w, h);
        if (x <= 0 && y <= 0 && w >= Width && h >= Height && _s.Transform == Transform2D.Identity) { _segs.Clear(); return; }
        var xf = _s.Transform;
        Add((ci, b) => { ci.DrawSetTransformMatrix(b * xf); ci.DrawRect(new Rect2(x, y, w, h), new Color(0, 0, 0, 0)); ci.DrawSetTransformMatrix(Transform2D.Identity); });
    }

    // ---- text
    static Font? _regular, _bold;
    static Font Regular => _regular ??= GD.Load<FontFile>("res://Ui/Fonts/NotoSansMono-Regular.ttf");
    static Font Bold => _bold ??= GD.Load<FontFile>("res://Ui/Fonts/NotoSansMono-Bold.ttf");

    // "bold 24px monospace" -> (bold font, 24)
    (Font Font, int Size) ParseFont()
    {
        bool bold = _s.Font.Contains("bold", StringComparison.Ordinal);
        int size = 10;
        foreach (string part in _s.Font.Split(' '))
            if (part.EndsWith("px", StringComparison.Ordinal) && float.TryParse(part[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out float px))
                size = (int)Math.Round(px);
        return (bold ? Bold : Regular, size);
    }

    public sealed record TextMetrics(float width);
    public TextMetrics measureText(string text)
    {
        var (f, size) = ParseFont();
        return new TextMetrics(f.GetStringSize(text, HorizontalAlignment.Left, -1, size).X);
    }

    public void fillText(string text, float x, float y, float? maxWidth = null)
    {
        if (maxWidth is float m) T("fillText", text, x, y, m); else T("fillText", text, x, y);
        var (f, size) = ParseFont();
        float w = f.GetStringSize(text, HorizontalAlignment.Left, -1, size).X;
        float sx = 1;
        if (maxWidth is float mw && w > mw && w > 0) sx = mw / w;
        float dx = _s.TextAlign switch { "center" => -w * sx / 2, "right" or "end" => -w * sx, _ => 0 };
        // the canvas's top / middle / bottom are the em box's: the font's ascent and descent
        // scaled to add up to the font size, as Chrome measures them
        float a0 = f.GetAscent(size), d0 = f.GetDescent(size);
        float ascent = a0 + d0 > 0 ? size * a0 / (a0 + d0) : a0, descent = a0 + d0 > 0 ? size * d0 / (a0 + d0) : d0;
        float dy = _s.TextBaseline switch
        {
            "top" or "hanging" => ascent,
            "middle" => (ascent - descent) / 2,
            "bottom" or "ideographic" => -descent,
            _ => 0, // alphabetic: y is the baseline, where Godot draws
        };
        var color = WithAlpha(_s.Fill);
        var xf = _s.Transform * Transform2D.Identity.Translated(new Vector2(x, y)) * Transform2D.Identity.Scaled(new Vector2(sx, 1));
        Add((ci, b) =>
        {
            ci.DrawSetTransformMatrix(b * xf);
            ci.DrawString(f, new Vector2(dx / sx, dy), text, HorizontalAlignment.Left, -1, size, color);
            ci.DrawSetTransformMatrix(Transform2D.Identity);
        });
    }

    // ---- images: a texture at (dx, dy), optionally scaled and from a source rectangle
    public void drawImage(Texture2D img, float dx, float dy) { T("drawImage", img, dx, dy); DrawImage(img, 0, 0, img.GetWidth(), img.GetHeight(), dx, dy, img.GetWidth(), img.GetHeight()); }
    public void drawImage(Texture2D img, float dx, float dy, float dw, float dh) { T("drawImage", img, dx, dy, dw, dh); DrawImage(img, 0, 0, img.GetWidth(), img.GetHeight(), dx, dy, dw, dh); }
    public void drawImage(Texture2D img, float sx, float sy, float sw, float sh, float dx, float dy, float dw, float dh)
    {
        T("drawImage", img, sx, sy, sw, sh, dx, dy, dw, dh);
        DrawImage(img, sx, sy, sw, sh, dx, dy, dw, dh);
    }

    void DrawImage(Texture2D img, float sx, float sy, float sw, float sh, float dx, float dy, float dw, float dh)
    {
        var xf = _s.Transform;
        var mod = new Color(1, 1, 1, _s.GlobalAlpha);
        // imageSmoothingEnabled is not per draw here: the panel draws its textures nearest, which is
        // what the windows ask for (imageSmoothingEnabled = false on the catalog's miniatures)
        Add((ci, b) =>
        {
            ci.DrawSetTransformMatrix(b * xf);
            ci.DrawTextureRectRegion(img, new Rect2(dx, dy, dw, dh), new Rect2(sx, sy, sw, sh), mod);
            ci.DrawSetTransformMatrix(Transform2D.Identity);
        });
    }

    // putImageData: RGBA bytes drawn 1:1 at (x, y)
    public void putImageData(byte[] rgba, int w, int h, float x, float y)
    {
        Trace?.Add("putImageData(\"img\"," + Num(x) + "," + Num(y) + ")");
        var tex = ImageTexture.CreateFromImage(Image.CreateFromData(w, h, false, Image.Format.Rgba8, rgba));
        DrawImage(tex, 0, 0, w, h, x, y, w, h);
    }

    Color WithAlpha(Color c) => new(c.R, c.G, c.B, c.A * _s.GlobalAlpha);

    // CSS colours: #rgb, #rrggbb, #rrggbbaa, rgb()/rgba(), and the few names the windows use
    public static Color ParseColor(string css)
    {
        string s = css.Trim().ToLowerInvariant();
        if (s.StartsWith('#'))
        {
            string h = s[1..];
            if (h.Length == 3) h = string.Concat(h[0], h[0], h[1], h[1], h[2], h[2]);
            uint v = Convert.ToUInt32(h, 16);
            return h.Length == 8
                ? new Color(((v >> 24) & 255) / 255f, ((v >> 16) & 255) / 255f, ((v >> 8) & 255) / 255f, (v & 255) / 255f)
                : new Color(((v >> 16) & 255) / 255f, ((v >> 8) & 255) / 255f, (v & 255) / 255f);
        }
        if (s.StartsWith("rgb", StringComparison.Ordinal))
        {
            var parts = s[(s.IndexOf('(') + 1)..s.IndexOf(')')].Split(',');
            float F(int i) => float.Parse(parts[i].Trim(), CultureInfo.InvariantCulture);
            return new Color(F(0) / 255f, F(1) / 255f, F(2) / 255f, parts.Length > 3 ? F(3) : 1);
        }
        return s switch
        {
            "white" => Colors.White, "black" => Colors.Black, "transparent" => new Color(0, 0, 0, 0),
            "red" => Colors.Red, "yellow" => Colors.Yellow, "green" => new Color(0, 128 / 255f, 0),
            _ => Colors.Magenta, // an unknown colour shows itself
        };
    }

    // what a CanvasHost draws, every segment in order, the clips ignored (CanvasHost.Sync honours them)
    internal void Replay(CanvasItem ci)
    {
        foreach (var seg in _segs) foreach (var op in seg.Ops) op(ci, Transform2D.Identity);
    }

    public int OpCount { get { int n = 0; foreach (var seg in _segs) n += seg.Ops.Count; return n; } }
}

// The Control a Canvas2D replays into, inside the panel's SubViewport: one child per segment of the
// drawing, in order, a clipped segment in a Control the size of its clip that clips its contents.
public partial class CanvasHost : Control
{
    public Canvas2D? Canvas;

    sealed partial class SegmentHost : Control
    {
        public Canvas2D.Segment? Seg;
        public override void _Draw()
        {
            if (Seg == null) return;
            var b = Transform2D.Identity.Translated(-Position);
            foreach (var op in Seg.Ops) op(this, b);
        }
    }

    // after painting: hand each segment to a child and redraw it
    public void Sync()
    {
        if (Canvas == null) return;
        var segs = Canvas.Segments;
        while (GetChildCount() < segs.Count) AddChild(new SegmentHost { MouseFilter = MouseFilterEnum.Ignore });
        for (int i = 0; i < GetChildCount(); i++)
        {
            var host = GetChild<SegmentHost>(i);
            if (i >= segs.Count) { host.Visible = false; host.Seg = null; continue; }
            var seg = segs[i];
            var r = seg.Clip ?? new Rect2(0, 0, Canvas.Width, Canvas.Height);
            host.Visible = true;
            host.Seg = seg;
            host.ClipContents = seg.Clip != null;
            host.Position = r.Position;
            host.Size = r.Size;
            host.QueueRedraw();
        }
    }
}
