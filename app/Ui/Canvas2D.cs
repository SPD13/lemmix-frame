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
        public State Copy() => (State)MemberwiseClone();
    }

    State _s = new();
    readonly Stack<State> _stack = new();
    readonly List<Action<CanvasItem>> _ops = new();
    readonly List<List<Vector2>> _path = new();
    List<Vector2>? _current;
    readonly List<bool> _closed = new();

    public string fillStyle { set => _s.Fill = ParseColor(value); }
    public string strokeStyle { set => _s.Stroke = ParseColor(value); }
    public float lineWidth { get => _s.LineWidth; set => _s.LineWidth = value; }
    public float globalAlpha { get => _s.GlobalAlpha; set => _s.GlobalAlpha = value; }
    public string font { get => _s.Font; set => _s.Font = value; }
    public string textAlign { get => _s.TextAlign; set => _s.TextAlign = value; }
    public string textBaseline { get => _s.TextBaseline; set => _s.TextBaseline = value; }
    public bool imageSmoothingEnabled { get => _s.Smoothing; set => _s.Smoothing = value; }
    public string lineCap { get; set; } = "butt";
    public string lineJoin { get; set; } = "miter";

    public void save() => _stack.Push(_s.Copy());
    public void restore() { if (_stack.Count > 0) _s = _stack.Pop(); }
    public void translate(float x, float y) => _s.Transform = _s.Transform * Transform2D.Identity.Translated(new Vector2(x, y));
    public void scale(float x, float y) => _s.Transform = _s.Transform * Transform2D.Identity.Scaled(new Vector2(x, y));
    public void rotate(float a) => _s.Transform = _s.Transform * Transform2D.Identity.Rotated(a);

    // everything recorded so far goes; the panel starts transparent again
    public void Reset() { _ops.Clear(); _path.Clear(); _closed.Clear(); _current = null; _s = new State(); _stack.Clear(); }

    // ---- paths
    public void beginPath() { _path.Clear(); _closed.Clear(); _current = null; }
    public void moveTo(float x, float y) { _current = new List<Vector2> { P(x, y) }; _path.Add(_current); _closed.Add(false); }
    public void lineTo(float x, float y) { if (_current == null) moveTo(x, y); else _current.Add(P(x, y)); }
    public void closePath() { if (_current != null) { _closed[^1] = true; var start = _current[0]; _current = new List<Vector2> { start }; _path.Add(_current); _closed.Add(false); } }
    Vector2 P(float x, float y) => _s.Transform * new Vector2(x, y);
    Vector2 Last => _current is { Count: > 0 } c ? _s.Transform.AffineInverse() * c[^1] : Vector2.Zero;

    public void rect(float x, float y, float w, float h)
    {
        moveTo(x, y); lineTo(x + w, y); lineTo(x + w, y + h); lineTo(x, y + h); closePath();
    }

    public void roundRect(float x, float y, float w, float h, float r)
    {
        r = Math.Max(0, Math.Min(r, Math.Min(Math.Abs(w), Math.Abs(h)) / 2));
        moveTo(x + r, y);
        lineTo(x + w - r, y); ArcPoints(x + w - r, y + r, r, -Mathf.Pi / 2, 0, false);
        lineTo(x + w, y + h - r); ArcPoints(x + w - r, y + h - r, r, 0, Mathf.Pi / 2, false);
        lineTo(x + r, y + h); ArcPoints(x + r, y + h - r, r, Mathf.Pi / 2, Mathf.Pi, false);
        lineTo(x, y + r); ArcPoints(x + r, y + r, r, Mathf.Pi, Mathf.Pi * 1.5f, false);
        closePath();
    }

    public void arc(float x, float y, float r, float a0, float a1, bool ccw = false)
    {
        var start = new Vector2(x + r * Mathf.Cos(a0), y + r * Mathf.Sin(a0));
        if (_current == null) moveTo(start.X, start.Y); else lineTo(start.X, start.Y);
        ArcPoints(x, y, r, a0, a1, ccw);
    }

    public void ellipse(float x, float y, float rx, float ry, float rotation, float a0, float a1, bool ccw = false)
    {
        save(); translate(x, y); rotate(rotation); scale(rx, ry);
        arc(0, 0, 1, a0, a1, ccw);
        restore();
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
            lineTo(x + r * Mathf.Cos(a), y + r * Mathf.Sin(a));
        }
    }

    public void quadraticCurveTo(float cx, float cy, float x, float y)
    {
        var p0 = Last;
        for (int i = 1; i <= 16; i++)
        {
            float t = i / 16f, u = 1 - t;
            lineTo(u * u * p0.X + 2 * u * t * cx + t * t * x, u * u * p0.Y + 2 * u * t * cy + t * t * y);
        }
    }

    public void bezierCurveTo(float c1x, float c1y, float c2x, float c2y, float x, float y)
    {
        var p0 = Last;
        for (int i = 1; i <= 20; i++)
        {
            float t = i / 20f, u = 1 - t;
            lineTo(u * u * u * p0.X + 3 * u * u * t * c1x + 3 * u * t * t * c2x + t * t * t * x,
                   u * u * u * p0.Y + 3 * u * u * t * c1y + 3 * u * t * t * c2y + t * t * t * y);
        }
    }

    public void fill()
    {
        var color = WithAlpha(_s.Fill);
        foreach (var poly in Polygons(closeAll: true))
        {
            var pts = poly.ToArray();
            _ops.Add(ci =>
            {
                var idx = Geometry2D.TriangulatePolygon(pts);
                if (idx.Length == 0) return;
                for (int i = 0; i < idx.Length; i += 3)
                    ci.DrawColoredPolygon(new[] { pts[idx[i]], pts[idx[i + 1]], pts[idx[i + 2]] }, color);
            });
        }
    }

    public void stroke()
    {
        var color = WithAlpha(_s.Stroke);
        float w = _s.LineWidth * ScaleOf(_s.Transform);
        for (int k = 0; k < _path.Count; k++)
        {
            var sub = _path[k];
            if (sub.Count < 2) continue;
            var pts = new List<Vector2>(sub);
            if (_closed[k]) pts.Add(sub[0]);
            var arr = pts.ToArray();
            _ops.Add(ci => ci.DrawPolyline(arr, color, w, true));
        }
    }

    IEnumerable<List<Vector2>> Polygons(bool closeAll)
    {
        for (int k = 0; k < _path.Count; k++) if (_path[k].Count >= 3) yield return _path[k];
    }

    static float ScaleOf(Transform2D t) => (t.X.Length() + t.Y.Length()) / 2;

    // ---- rects
    public void fillRect(float x, float y, float w, float h)
    {
        var color = WithAlpha(_s.Fill);
        var xf = _s.Transform;
        _ops.Add(ci => { ci.DrawSetTransformMatrix(xf); ci.DrawRect(new Rect2(x, y, w, h), color); ci.DrawSetTransformMatrix(Transform2D.Identity); });
    }

    public void strokeRect(float x, float y, float w, float h) { beginPath(); rect(x, y, w, h); stroke(); beginPath(); }

    // the panel is redrawn from scratch on every paint (Reset); a clear of the whole canvas is that
    public void clearRect(float x, float y, float w, float h)
    {
        if (x <= 0 && y <= 0 && w >= Width && h >= Height) { _ops.Clear(); return; }
        var xf = _s.Transform;
        _ops.Add(ci => { ci.DrawSetTransformMatrix(xf); ci.DrawRect(new Rect2(x, y, w, h), new Color(0, 0, 0, 0)); ci.DrawSetTransformMatrix(Transform2D.Identity); });
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
        var (f, size) = ParseFont();
        float w = f.GetStringSize(text, HorizontalAlignment.Left, -1, size).X;
        float sx = 1;
        if (maxWidth is float mw && w > mw && w > 0) sx = mw / w;
        float dx = _s.TextAlign switch { "center" => -w * sx / 2, "right" or "end" => -w * sx, _ => 0 };
        float ascent = f.GetAscent(size), descent = f.GetDescent(size);
        float dy = _s.TextBaseline switch
        {
            "top" or "hanging" => ascent,
            "middle" => (ascent - descent) / 2,
            "bottom" or "ideographic" => -descent,
            _ => 0, // alphabetic: y is the baseline, where Godot draws
        };
        var color = WithAlpha(_s.Fill);
        var xf = _s.Transform * Transform2D.Identity.Translated(new Vector2(x, y)) * Transform2D.Identity.Scaled(new Vector2(sx, 1));
        _ops.Add(ci =>
        {
            ci.DrawSetTransformMatrix(xf);
            ci.DrawString(f, new Vector2(dx / sx, dy), text, HorizontalAlignment.Left, -1, size, color);
            ci.DrawSetTransformMatrix(Transform2D.Identity);
        });
    }

    // ---- images: a texture at (dx, dy), optionally scaled and from a source rectangle
    public void drawImage(Texture2D img, float dx, float dy) => drawImage(img, 0, 0, img.GetWidth(), img.GetHeight(), dx, dy, img.GetWidth(), img.GetHeight());
    public void drawImage(Texture2D img, float dx, float dy, float dw, float dh) => drawImage(img, 0, 0, img.GetWidth(), img.GetHeight(), dx, dy, dw, dh);
    public void drawImage(Texture2D img, float sx, float sy, float sw, float sh, float dx, float dy, float dw, float dh)
    {
        var xf = _s.Transform;
        var mod = new Color(1, 1, 1, _s.GlobalAlpha);
        _ops.Add(ci =>
        {
            ci.DrawSetTransformMatrix(xf);
            ci.DrawTextureRectRegion(img, new Rect2(dx, dy, dw, dh), new Rect2(sx, sy, sw, sh), mod);
            ci.DrawSetTransformMatrix(Transform2D.Identity);
        });
    }

    // putImageData: RGBA bytes drawn 1:1 at (x, y)
    public void putImageData(byte[] rgba, int w, int h, float x, float y)
    {
        var tex = ImageTexture.CreateFromImage(Image.CreateFromData(w, h, false, Image.Format.Rgba8, rgba));
        drawImage(tex, x, y);
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

    // what a CanvasHost draws
    internal void Replay(CanvasItem ci)
    {
        foreach (var op in _ops) op(ci);
    }

    public int OpCount => _ops.Count;
}

// The Control a Canvas2D replays into, inside the panel's SubViewport.
public partial class CanvasHost : Control
{
    public Canvas2D? Canvas;
    public override void _Draw() => Canvas?.Replay(this);
}
