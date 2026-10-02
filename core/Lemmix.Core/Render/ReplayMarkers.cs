using Lemmix.Engine;
using Lemmix.Util;

namespace Lemmix.Render;

// web/3d/js/replay-markers.js, its data side: while a replay is engaged every entry of the
// record stands on the board from frame 0 - an assignment as a ring where the lemming stood with
// the skill's picture beside it, a release-rate change and the nuke at the hatch with the
// panel's icons. A marker still ahead is translucent with the seconds until it, the next one
// pulses, a played one turns solid. Everything here is in worldGroup's pixel space (y down);
// each plane is flipped back (scale y -1) so its picture stands upright.
// The pictures: OutlinedPicture is exactly the ImageData the JS writes into its canvas. The
// countdown and release-rate labels are text the browser rasterises in its own monospace font,
// so they are not pixels here but a LabelSpec: the canvas size, the box, the colour, the font,
// the text and where it is anchored - the Godot side draws it with its own font.
public static class ReplayMarkers
{
    public const int ICON_W = 16, ICON_H = 23;
    public const double FUTURE_OPACITY = 0.45;
    public const int MARKER_COLOR = 0xffd866;

    // outlinedCanvas: the picture with a one-pixel dark outline round its opaque pixels
    // (10, 10, 14 at alpha 230), every opaque pixel made fully opaque
    public static Bitmap OutlinedPicture(Bitmap bmp)
    {
        int w = bmp.Width + 2, h = bmp.Height + 2;
        var img = new Bitmap(w, h);
        var src = bmp.Data; int sw = bmp.Width;
        int AlphaAt(int x, int y) => (x < 0 || y < 0 || x >= bmp.Width || y >= bmp.Height) ? 0 : src[(x + y * sw) * 4 + 3];
        var d = img.Data;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int sx = x - 1, sy = y - 1, o = (x + y * w) * 4;
                int a = AlphaAt(sx, sy);
                if (a > 0) { int i = (sx + sy * sw) * 4; d[o] = src[i]; d[o + 1] = src[i + 1]; d[o + 2] = src[i + 2]; d[o + 3] = 255; }
                else if (AlphaAt(sx - 1, sy) != 0 || AlphaAt(sx + 1, sy) != 0 || AlphaAt(sx, sy - 1) != 0 || AlphaAt(sx, sy + 1) != 0)
                { d[o] = 10; d[o + 1] = 10; d[o + 2] = 14; d[o + 3] = 230; }
            }
        return img;
    }

    // labelCanvas: `text` centred in a 48 x 20 box filled rgba(10, 10, 14, 0.8), in #ffd866 bold
    // 15px monospace, anchored at the box's middle one pixel down (textAlign center, baseline middle)
    public sealed record LabelSpec(int Width, int Height, string Background, string Color, string Font, string Align, string Baseline, double X, double Y, string Text);

    public static LabelSpec LabelCanvas(string text) =>
        new(48, 20, "rgba(10, 10, 14, 0.8)", "#ffd866", "bold 15px monospace", "center", "middle", 48 / 2.0, 20 / 2.0 + 1, text);

    // THREE.RingGeometry (r147): the marker's pin, (2.5, 4, 20)
    public static GeometryBuffers RingGeometry(double innerRadius = 0.5, double outerRadius = 1, int thetaSegments = 32, int phiSegments = 1,
        double thetaStart = 0, double thetaLength = Math.PI * 2)
    {
        thetaSegments = Math.Max(3, thetaSegments);
        phiSegments = Math.Max(1, phiSegments);
        var pos = new List<double>(); var uv = new List<double>(); var idx = new List<int>();
        double radius = innerRadius;
        double radiusStep = (outerRadius - innerRadius) / phiSegments;
        for (int j = 0; j <= phiSegments; j++)
        {
            for (int i = 0; i <= thetaSegments; i++)
            {
                double segment = thetaStart + (double)i / thetaSegments * thetaLength;
                double vx = radius * V8Math.Cos(segment), vy = radius * V8Math.Sin(segment);
                pos.Add(vx); pos.Add(vy); pos.Add(0);
                uv.Add((vx / outerRadius + 1) / 2); uv.Add((vy / outerRadius + 1) / 2);
            }
            radius += radiusStep;
        }
        for (int j = 0; j < phiSegments; j++)
        {
            int level = j * (thetaSegments + 1);
            for (int i = 0; i < thetaSegments; i++)
            {
                int segment = i + level;
                int a = segment, b = segment + thetaSegments + 1, c = segment + thetaSegments + 2, d = segment + 1;
                idx.Add(a); idx.Add(b); idx.Add(d);
                idx.Add(b); idx.Add(c); idx.Add(d);
            }
        }
        return new GeometryBuffers { Position = GeometryBuffers.ToFloat(pos), Uv = GeometryBuffers.ToFloat(uv), Index = idx.ToArray() };
    }

    // THREE.PlaneGeometry (r147), one segment each way
    public static GeometryBuffers PlaneGeometry(double width, double height)
    {
        double hw = width / 2, hh = height / 2;
        var pos = new List<double>(); var uv = new List<double>();
        for (int iy = 0; iy < 2; iy++)
        {
            double y = iy * height - hh;
            for (int ix = 0; ix < 2; ix++)
            {
                double x = ix * width - hw;
                pos.Add(x); pos.Add(-y); pos.Add(0);
                uv.Add(ix); uv.Add(1 - iy);
            }
        }
        return new GeometryBuffers { Position = GeometryBuffers.ToFloat(pos), Uv = GeometryBuffers.ToFloat(uv), Index = new[] { 0, 2, 1, 2, 3, 1 } };
    }

    public sealed class Icon
    {
        public required string Key;
        public required Bitmap Picture;   // the outlined picture (the canvas)
        public int W, H;
    }

    // A plane: where it stands (z the lemmings' plane + 2), its size, its draw order; scale y -1.
    public sealed record Plane(double X, double Y, double Z, double W, double H, int RenderOrder);

    public sealed class Marker
    {
        public required ReplayEntry Entry;
        public int Frame;
        public required Plane Ring;          // the ring geometry's own size; x, y, z and the order
        public Icon? Icon;
        public Plane? IconPlane;
        public string? TextOnly;             // the release rate written under a hatch's icon
        public LabelSpec? TextLabel;
        public Plane? TextPlane;
        public required (double X, double Y) IconAt;
        public required Plane Countdown;     // the countdown label, 12 x 5
        public int LabelSeconds = -1;
        public bool LabelVisible;
        public double LabelOpacity = 1;
        // ring, icon, text label: the materials the JS restyles each frame, in that order
        public readonly List<double> Opacities = new();
        public readonly List<bool> HasMap = new();
    }

    // Where a record entry's marker stands, and the picture it wears (_place)
    public sealed record Placement(double X, double Y, Icon? Icon, int Dx, string? Text);

    public sealed class Set
    {
        public readonly Level Level;
        public readonly double Z;
        readonly Func<string, Bitmap?>? _skillIcon;     // gui._skillIcon (the panel's button picture)
        readonly Func<string, Bitmap?>? _asset;         // gui.assets[name] (icon_nuke, icon_rr_plus, icon_rr_minus)
        public readonly Dictionary<string, Icon> Textures = new(StringComparer.Ordinal);
        public readonly List<Icon> TextureOrder = new();  // the pictures in the order first made (the JS Map's)
        public readonly List<Marker> Markers = new();
        public readonly GeometryBuffers RingGeometry = ReplayMarkers.RingGeometry(2.5, 4, 20);
        public int BuiltFor = -1;
        public bool Engaged, Visible, Hidden;

        // `skillIcon` / `asset` null: the panel has no pictures and the pins stand alone
        public Set(Level level, double z, Func<string, Bitmap?>? skillIcon, Func<string, Bitmap?>? asset)
        {
            Level = level; Z = z; _skillIcon = skillIcon; _asset = asset;
        }

        Icon? IconTexture(string key, Func<Bitmap?> bitmapOf)
        {
            if (Textures.TryGetValue(key, out var entry)) return entry;
            var bmp = bitmapOf();
            if (bmp == null) return null;
            var cv = OutlinedPicture(bmp);
            entry = new Icon { Key = key, Picture = cv, W = cv.Width, H = cv.Height };
            Textures[key] = entry;
            TextureOrder.Add(entry);
            return entry;
        }

        public Placement Place(ReplayEntry entry)
        {
            if (entry.Type == "assignment")
            {
                var icon = _skillIcon != null ? IconTexture("skill:" + entry.Skill, () => _skillIcon(entry.Skill!)) : null;
                return new Placement(entry.X, entry.Y, icon, entry.Dx, null);
            }
            var hatches = Level.Entrances;
            double hx = Level.Width / 2.0, hy = Level.Height / 2.0;
            if (hatches.Count > 0)
            {
                Gadget? g = null;
                if (entry.Type == "spawn_interval" && Level.SpawnOrder.Count > 0)
                {
                    int pos = Math.Max(0, entry.Spawned - Level.Preplaced.Count);
                    int ix = Level.SpawnOrder[Math.Min(pos, Level.SpawnOrder.Count - 1)];
                    g = ix >= 0 ? Level.Gadgets[ix] : null;
                }
                if (g != null) { hx = g.TriggerRect.X0; hy = g.TriggerRect.Y0; }
                else
                {
                    hx = 0; hy = 0;
                    foreach (var h in hatches) { hx += h.TriggerRect.X0; hy += h.TriggerRect.Y0; }
                    hx /= hatches.Count; hy /= hatches.Count;
                }
                hy += 10;
            }
            Icon? ic = null;
            if (_asset != null)
            {
                if (entry.Type == "nuke") ic = IconTexture("nuke", () => _asset("icon_nuke"));
                else
                {
                    bool faster = entry.Interval < Level.SpawnInterval;
                    ic = IconTexture(faster ? "rr+" : "rr-", () => _asset(faster ? "icon_rr_plus" : "icon_rr_minus"));
                }
            }
            return new Placement(hx, hy, ic, 1, entry.Type == "spawn_interval" ? (103 - entry.Interval).ToString(System.Globalization.CultureInfo.InvariantCulture) : null);
        }

        // _build: the markers of the record as it stands, markers at one spot stacking upward
        public void Build(IReadOnlyList<ReplayEntry> record, int recordVersion)
        {
            Markers.Clear();
            var recorded = record.OrderBy(e => e.Frame).ToList(); // stable, as Array.prototype.sort
            var stacks = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var entry in recorded)
            {
                var at = Place(entry);
                string key = (JsMath.ToInt32(at.X) >> 3) + ":" + (JsMath.ToInt32(at.Y) >> 3);
                int n = stacks.TryGetValue(key, out int c) ? c : 0;
                stacks[key] = n + 1;
                double lift = n * (ICON_H + 3);
                Plane? iconPlane = null, textPlane = null;
                LabelSpec? textLabel = null;
                (double X, double Y) iconAt;
                var m = new Marker
                {
                    Entry = entry, Frame = entry.Frame, TextOnly = at.Text,
                    Ring = new Plane(at.X, at.Y - 1, Z, 4, 4, 20), IconAt = default, Countdown = null!,
                };
                m.Opacities.Add(1); m.HasMap.Add(false);
                if (at.Icon != null)
                {
                    int side = at.Dx < 0 ? -1 : 1;
                    iconPlane = new Plane(at.X + side * (ICON_W / 2.0 + 6), at.Y - 6 - ICON_H / 2.0 - lift, Z, at.Icon.W, at.Icon.H, 21);
                    m.Opacities.Add(1); m.HasMap.Add(true);
                    iconAt = (iconPlane.X, iconPlane.Y);
                    if (at.Text != null)
                    {
                        textLabel = LabelCanvas(at.Text);
                        textPlane = new Plane(iconPlane.X, iconPlane.Y + ICON_H / 2.0 + 4, Z, 12, 5, 22);
                        m.Opacities.Add(1); m.HasMap.Add(true);
                    }
                }
                else iconAt = (at.X, at.Y - 8 - lift);
                m.Icon = at.Icon; m.IconPlane = iconPlane; m.TextLabel = textLabel; m.TextPlane = textPlane; m.IconAt = iconAt;
                m.Countdown = new Plane(iconAt.X, iconAt.Y - ICON_H / 2.0 - 4, Z, 12, 5, 22);
                Markers.Add(m);
            }
            BuiltFor = recordVersion;
        }

        // update: build, clear or restyle the markers to the game's state; `now` in ms for the pulse
        public void Update(bool replayEngaged, IReadOnlyList<ReplayEntry> record, int recordVersion, int frame, double now)
        {
            bool engaged = replayEngaged && record.Count > 0;
            if (!engaged)
            {
                if (Markers.Count > 0) { Markers.Clear(); BuiltFor = -1; }
                Visible = false;
                Engaged = false;
                return;
            }
            Engaged = true;
            if (BuiltFor != recordVersion) Build(record, recordVersion);
            Visible = true;
            Marker? next = null;
            foreach (var m in Markers) if (m.Frame >= frame && (next == null || m.Frame < next.Frame)) next = m;
            double pulse = 0.65 + 0.35 * Math.Abs(V8Math.Sin(now / 250));
            foreach (var m in Markers)
            {
                bool played = m.Frame < frame;
                double opacity = played ? 1 : ReferenceEquals(m, next) ? pulse : FUTURE_OPACITY;
                for (int i = 0; i < m.Opacities.Count; i++)
                {
                    m.Opacities[i] = opacity;
                    if (m.HasMap[i] && Hidden) m.Opacities[i] = 0;
                }
                if (played) { m.LabelVisible = false; continue; }
                int seconds = (int)Math.Ceiling((m.Frame - frame) / 17.0);
                if (seconds != m.LabelSeconds) m.LabelSeconds = seconds;
                m.LabelOpacity = opacity;
                m.LabelVisible = !Hidden;
            }
        }

        // the countdown's picture: "<seconds>s"
        public static LabelSpec CountdownLabel(int seconds) => LabelCanvas(seconds.ToString(System.Globalization.CultureInfo.InvariantCulture) + "s");
    }
}
