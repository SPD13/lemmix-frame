using Lemmix.Engine;
using Lemmix.Util;

namespace Lemmix.Render;

// web/3d/js/portals.js, its data side: entrances as a ceiling square with two hinged flaps,
// exits as a funnel carved into a slab, the pieces built onto a door pulled into it, the water's
// body under its waves, and the surfaces drawn as a stack of slices one frame apart. Plus
// app.js's lemmixEngine.objectData, the DOS-shaped object list the functions read (window
// .__lem3dObjectData). Geometry is in sprite pixel space (y down) unless said otherwise.

// The DOS trigger ids objectData speaks in (Lemmings.TriggerTypes).
public static class TriggerTypes
{
    public const int NO_TRIGGER = 0, EXIT_LEVEL = 1, TRAP = 4, DROWN = 5, KILL = 6;
}

// objectImg[id]: an object's trigger, in object space (DOS terms)
public sealed record PortalObjectInfo(int TriggerEffectId, int TriggerLeft, int TriggerTop, int TriggerWidth, int TriggerHeight);

// window.__lem3dObjectData: an id per object (every window is 1, the rest 100 + index) and the
// trigger info per id - so every window reads the LAST window's info, as in the JS.
public sealed class PortalObjectData
{
    public required List<int> Ids;
    public required Dictionary<int, PortalObjectInfo> ObjectImg;

    public PortalObjectInfo? InfoAt(int i) => ObjectImg.TryGetValue(Ids[i], out var info) ? info : null;

    // app.js lemmixEngine.objectData
    public static PortalObjectData For(Level level)
    {
        var ids = new List<int>();
        for (int i = 0; i < level.Objects.Count; i++) ids.Add(level.Objects[i].Gadget.EffectBase == "WINDOW" ? 1 : 100 + i);
        var img = new Dictionary<int, PortalObjectInfo>();
        for (int i = 0; i < level.Objects.Count; i++)
        {
            var g = level.Objects[i].Gadget;
            var r = g.TriggerRect;
            int effect = g.Effect == "EXIT" || g.Effect == "LOCKEXIT" ? TriggerTypes.EXIT_LEVEL
                : g.Effect == "WATER" ? TriggerTypes.DROWN : g.Effect == "FIRE" ? TriggerTypes.KILL
                : g.Effect == "TRAP" || g.Effect == "TRAPONCE" ? TriggerTypes.TRAP : TriggerTypes.NO_TRIGGER;
            img[ids[i]] = new PortalObjectInfo(effect, r.X0 - g.X, r.Y0 - g.Y, r.X1 - r.X0, r.Y1 - r.Y0);
        }
        return new PortalObjectData { Ids = ids, ObjectImg = img };
    }
}

// A profile's objects.byId entry: { shape: "portal" | "ceiling" | "slab" | "flat", depth }
public sealed record PortalProfileEntry(string? Shape, double? Depth);

public sealed class PortalConfig
{
    public required string Shape;
    public double? Depth;
}

public sealed record DoorRow(int Y, int Min, int Max);

public sealed class HatchInfo
{
    public double LeftX, RightX, Y, HalfWidth, Depth;
    public required List<DoorRow> DoorRows;
}

public sealed class PortalRebuild
{
    public required Frame Frame;
    public double Depth;
    public byte[]? Opening;
}

public sealed class Portal
{
    public int Index, ObjectId;
    public required GeometryBuffers Geometry;
    public PortalRebuild? Rebuild;
    public double OriginX, OriginY;
    public required string Shape;
    public double SfxX, SfxY;
    public HatchInfo? Hatch;
    public double[]? Openness;
    public required Frame ClosedFrame;
    public required GadgetObject MapObject;
    public int Carved;
}

public sealed class PoolRun { public int X0, X1, Floor; }

public sealed class WaterPool
{
    public int Index;
    public required List<PoolRun> Runs;
    public int Y0;
    public int Colour;
    public double Z0, Z1;
}

public sealed class WaveStack
{
    public int Index;
    public required GadgetObject MapObject;
    public bool FlipY;
    public required ushort[] Phases;
}

// The quads of a geometry pushed in order, each vertex its own uv, one shade per quad.
sealed class QuadGeometry
{
    readonly List<double> _pos = new(), _col = new(), _uv = new();
    readonly List<int> _idx = new();
    public int IndexCount => _idx.Count;

    // verts: 4 x (px, py, pz, pu, pv)
    public void Quad(double shade, params double[] v)
    {
        int b = _pos.Count / 3;
        for (int i = 0; i < 4; i++)
        {
            _pos.Add(v[i * 5]); _pos.Add(v[i * 5 + 1]); _pos.Add(v[i * 5 + 2]);
            _col.Add(shade); _col.Add(shade); _col.Add(shade);
            _uv.Add(v[i * 5 + 3]); _uv.Add(v[i * 5 + 4]);
        }
        _idx.Add(b); _idx.Add(b + 1); _idx.Add(b + 2); _idx.Add(b); _idx.Add(b + 2); _idx.Add(b + 3);
    }

    // toBufferGeometry: null when nothing was pushed
    public GeometryBuffers? ToBuffers() => _idx.Count == 0 ? null : Build();
    public GeometryBuffers Build() => new()
    {
        Position = GeometryBuffers.ToFloat(_pos), Color = GeometryBuffers.ToFloat(_col),
        Uv = GeometryBuffers.ToFloat(_uv), Index = _idx.ToArray(),
    };
}

public static class Portals
{
    public const int PORTAL_ENTRANCE_ID = 1;
    public const double PORTAL_DEFAULT_DEPTH = 12;
    public const double PORTAL_EXIT_DEPTH = 2;
    public const int PORTAL_SKY_MIN = 40;
    public const double PORTAL_SKY_SAT = 0.35;
    public const double PORTAL_SKY_WARM = 0.2;
    public const int PORTAL_SKY_PATCH_MIN = 6;
    public const int PORTAL_DARK_MAX = 48;
    public const int PORTAL_DARK_PATCH_MIN = 12;
    public const int PORTAL_DARK_SIDE_MIN = 3;
    public const double PORTAL_DARK_FILL = 0.45;
    public const int PORTAL_DARK_REACH = 10;
    public const double PORTAL_FRAME_THICK = 1;
    public const double PORTAL_TUNNEL_SHADE = 0.3;
    public const int PORTAL_FUNNEL_RINGS = 3;
    public const int PORTAL_REVEAL_MIN = 8;
    public const int PORTAL_REVEAL_RATIO = 5;
    public const int PORTAL_SPAWN_OFFSET_X = 24;
    public const int PORTAL_PANEL_THICK = 1;
    public const int WATER_SURFACE_DROP = 2;
    public const double WATER_OPACITY = 0.55;
    public const int WAVE_PHASE_STEP = 1;
    public const double PORTAL_FLAP_THICK = 1;

    // terrain.js / depth.js: the slab's front, the terrain band's back
    public const double TERRAIN_DEPTH = 16;
    public const double TERRAIN_BACK = 0;
    // depth.js DepthClass
    public const byte DEPTH_EMPTY = 0, DEPTH_TERRAIN = 2;

    // app.js's planes: LEMMING_Z = TERRAIN_DEPTH / 2 - SPRITE_DEPTH / 2, OBJECT_Z = LEMMING_Z - 0.8,
    // WAVE_FRONT_Z = OBJECT_DECAL_Z = TERRAIN_DEPTH + 0.25
    public const double LEMMING_Z = TERRAIN_DEPTH / 2 - SpriteBuild.SPRITE_DEPTH / 2.0;
    public static readonly double OBJECT_Z = LEMMING_Z - 0.8;
    public const double WAVE_FRONT_Z = TERRAIN_DEPTH + 0.25;

    // portalConfigFor: entrances lie flat overhead, exits tunnel into the scenery, the rest flat
    public static PortalConfig? PortalConfigFor(int objectId, PortalObjectInfo? info, IReadOnlyDictionary<int, PortalProfileEntry>? byId)
    {
        if (byId != null && byId.TryGetValue(objectId, out var entry) && !string.IsNullOrEmpty(entry.Shape))
        {
            if (entry.Shape == "flat") return null;
            return new PortalConfig { Shape = entry.Shape!, Depth = entry.Depth ?? (entry.Shape == "ceiling" ? PORTAL_DEFAULT_DEPTH : null) };
        }
        if (objectId == PORTAL_ENTRANCE_ID) return new PortalConfig { Shape = "ceiling", Depth = PORTAL_DEFAULT_DEPTH };
        if (info != null && info.TriggerEffectId == TriggerTypes.EXIT_LEVEL) return new PortalConfig { Shape = "portal", Depth = null };
        return null;
    }

    static bool TaggedFlat(IReadOnlyDictionary<int, PortalProfileEntry>? byId, int id) =>
        byId != null && byId.TryGetValue(id, out var e) && e.Shape == "flat";

    // waveSliceCount: enough slices, a sprite depth each, to fill the slab
    public static int WaveSliceCount() => (int)Math.Max(1, JsMath.Round(TERRAIN_DEPTH / SpriteBuild.SPRITE_DEPTH));

    // waveRandom: a small deterministic xorshift
    public static Func<double> WaveRandom(double seed)
    {
        uint s = JsMath.ToUint32(seed);
        if (s == 0) s = 1;
        return () =>
        {
            s ^= s << 13;
            s ^= s >> 17;
            s ^= s << 5;
            return s / 4294967296.0;
        };
    }

    // isStackedSurface: water always; fire where it is a stretch (resizable sideways)
    public static bool IsStackedSurface(PortalObjectInfo info, GadgetObject mapObject)
    {
        if (info.TriggerEffectId == TriggerTypes.DROWN) return true;
        if (info.TriggerEffectId != TriggerTypes.KILL) return false;
        return mapObject.Gadget.V.ResizeH;
    }

    // waveFrameCount: how many frames this object's own animation runs to
    public static int WaveFrameCount(GadgetObject mapObject)
    {
        int c = mapObject.Gadget.FrameCount;
        if (c != 0) return c;
        var frames = mapObject.Frames;
        return frames.Count > 0 ? frames.Count : 1;
    }

    // wavePhases: one step per slice, from a starting point seeded by the object
    public static ushort[] WavePhases(int frameCount, int slices, int seed)
    {
        var phases = new ushort[slices];
        if (frameCount <= 1) return phases;
        var rand = WaveRandom(seed * 2654435761.0 + 1);
        int bas = (int)(Math.Floor(rand() * frameCount) % frameCount);
        for (int k = 0; k < slices; k++) phases[k] = unchecked((ushort)((bas + k * WAVE_PHASE_STEP) % frameCount));
        return phases;
    }

    // stackedObjectsFrom: every object drawn as a stack of slices, with its slices' offsets
    public static List<WaveStack> StackedObjectsFrom(Level level, PortalObjectData data, IReadOnlyDictionary<int, PortalProfileEntry>? byId)
    {
        var output = new List<WaveStack>();
        int slices = WaveSliceCount();
        int count = Math.Min(level.Objects.Count, data.Ids.Count);
        for (int i = 0; i < count; i++)
        {
            int id = data.Ids[i];
            var info = data.InfoAt(i);
            if (info == null) continue;
            if (TaggedFlat(byId, id)) continue;
            var mapObject = level.Objects[i];
            if (!IsStackedSurface(info, mapObject)) continue;
            if (mapObject.Frames.Count == 0) continue;
            output.Add(new WaveStack { Index = i, MapObject = mapObject, FlipY = false, Phases = WavePhases(WaveFrameCount(mapObject), slices, i + 1) });
        }
        return output;
    }

    // frameAtPhase: a Lemmix gadget's counter wound on by `offset`, the composite taken, put back
    public static Frame FrameAtPhase(GadgetObject mapObject, int tick, int offset)
    {
        var g = mapObject.Gadget;
        int count = g.FrameCount != 0 ? g.FrameCount : 1;
        if (offset == 0 || count <= 1) return g.Render();
        int saved = g.CurrentFrame;
        g.CurrentFrame = (saved + offset) % count;
        var frame = g.Render();
        g.CurrentFrame = saved;
        return frame;
    }

    // waterObjectsFrom: the pool under each stretch of water, from its trigger down to the ground
    public static List<WaterPool> WaterObjectsFrom(Level level, PortalObjectData data, IReadOnlyDictionary<int, PortalProfileEntry>? byId)
    {
        var output = new List<WaterPool>();
        int count = Math.Min(level.Objects.Count, data.Ids.Count);
        for (int i = 0; i < count; i++)
        {
            int id = data.Ids[i];
            var info = data.InfoAt(i);
            if (info == null || info.TriggerEffectId != TriggerTypes.DROWN) continue;
            if (TaggedFlat(byId, id)) continue;
            var mapObject = level.Objects[i];
            if (mapObject.Frames.Count == 0) continue;
            var frame = mapObject.Frames[0];
            int x = mapObject.X + frame.OffsetX;
            int y = mapObject.Y + frame.OffsetY;
            int top = y + (info.TriggerHeight != 0 ? info.TriggerTop : 0);
            int surface = top + WATER_SURFACE_DROP;
            var runs = PoolRuns(level, x, x + frame.Width, surface);
            if (runs.Count == 0) continue;
            output.Add(new WaterPool
            {
                Index = i, Runs = runs, Y0 = surface, Colour = AverageFrameColour(frame),
                Z0 = TERRAIN_BACK, Z1 = TERRAIN_DEPTH,
            });
        }
        return output;
    }

    // poolRuns: how far each column falls before it meets ground, equal neighbours merged
    public static List<PoolRun> PoolRuns(Level level, int x0, int x1, int surface)
    {
        var runs = new List<PoolRun>();
        int lo = Math.Max(0, x0), hi = Math.Min(level.Width, x1);
        for (int x = lo; x < hi; x++)
        {
            int y = surface;
            while (y < level.Height && !level.GroundMask.HasGroundAt(x, y)) y++;
            if (y <= surface) continue;
            var last = runs.Count > 0 ? runs[^1] : null;
            if (last != null && last.Floor == y && last.X1 == x) last.X1 = x + 1;
            else runs.Add(new PoolRun { X0 = x, X1 = x + 1, Floor = y });
        }
        return runs;
    }

    // buildPoolGeometry: a prism over the runs, only its outside, every face split at the same levels
    public static GeometryBuffers? BuildPoolGeometry(List<PoolRun> runs, int surface, double z0, double z1)
    {
        var positions = new List<double>();
        var indices = new List<int>();
        void Quad(double ax, double ay, double az, double bx, double by, double bz, double cx, double cy, double cz, double dx, double dy, double dz)
        {
            int b = positions.Count / 3;
            positions.Add(ax); positions.Add(ay); positions.Add(az); positions.Add(bx); positions.Add(by); positions.Add(bz);
            positions.Add(cx); positions.Add(cy); positions.Add(cz); positions.Add(dx); positions.Add(dy); positions.Add(dz);
            indices.Add(b); indices.Add(b + 1); indices.Add(b + 2); indices.Add(b); indices.Add(b + 2); indices.Add(b + 3);
        }
        var set = new List<int> { surface };
        foreach (var r in runs) if (!set.Contains(r.Floor)) set.Add(r.Floor);
        var levels = set.OrderBy(v => v).ToList();
        for (int i = 0; i < runs.Count; i++)
        {
            var run = runs[i];
            int x0 = run.X0, x1 = run.X1, floor = run.Floor;
            var prev = i > 0 ? runs[i - 1] : null;
            var next = i + 1 < runs.Count ? runs[i + 1] : null;
            int leftTop = prev != null && prev.X1 == x0 ? Math.Min(prev.Floor, floor) : surface;
            int rightTop = next != null && next.X0 == x1 ? Math.Min(next.Floor, floor) : surface;
            Quad(x0, surface, z1, x1, surface, z1, x1, surface, z0, x0, surface, z0);
            Quad(x0, floor, z0, x1, floor, z0, x1, floor, z1, x0, floor, z1);
            for (int b = 0; b + 1 < levels.Count; b++)
            {
                int ya = levels[b], yb = levels[b + 1];
                if (yb > floor) break;
                Quad(x0, ya, z1, x0, yb, z1, x1, yb, z1, x1, ya, z1);
                Quad(x1, ya, z0, x1, yb, z0, x0, yb, z0, x0, ya, z0);
                if (ya >= leftTop) Quad(x0, ya, z0, x0, yb, z0, x0, yb, z1, x0, ya, z1);
                if (ya >= rightTop) Quad(x1, ya, z1, x1, yb, z1, x1, yb, z0, x1, ya, z0);
            }
        }
        if (indices.Count == 0) return null;
        return new GeometryBuffers { Position = GeometryBuffers.ToFloat(positions), Index = indices.ToArray() };
    }

    // averageFrameColour: the mean of a frame's opaque pixels, 0xRRGGBB
    public static int AverageFrameColour(Frame frame)
    {
        var mask = frame.Mask; var buf = frame.Data;
        double r = 0, g = 0, b = 0; int n = 0;
        for (int i = 0; i < buf.Length; i++)
        {
            if (mask[i] == 0) continue;
            uint v = buf[i];
            r += v & 255; g += (v >> 8) & 255; b += (v >> 16) & 255; n++;
        }
        if (n == 0) return 0x1f4f8f;
        int Avg(double v) => (int)Math.Min(255, JsMath.Round(v / n));
        return (Avg(r) << 16) | (Avg(g) << 8) | Avg(b);
    }

    static long Pixel(Frame frame, int i) => frame.Mask[i] != 0 ? frame.Data[i] : -1;

    // hatchOpenness: per frame, how much of the hole is no longer covered by a door, 0..1
    public static double[] HatchOpenness(IReadOnlyList<Frame> frames, byte[] openingMask, int w)
    {
        var open = frames[^1];
        long OpenAt(int i) => open.Mask[i] != 0 ? open.Data[i] : -1;
        double MatchOver(Frame frame, byte[] region, int total)
        {
            if (total == 0) return 1;
            int same = 0;
            for (int i = 0; i < region.Length; i++)
            {
                if (region[i] == 0) continue;
                if (Pixel(frame, i) == OpenAt(i)) same++;
            }
            return (double)same / total;
        }
        int openingTotal = 0;
        for (int i = 0; i < openingMask.Length; i++) if (openingMask[i] != 0) openingTotal++;
        if (openingTotal == 0) return frames.Select(_ => 1.0).ToArray();

        var shut = frames[0]; double worst = double.PositiveInfinity;
        foreach (var frame in frames)
        {
            double r = MatchOver(frame, openingMask, openingTotal);
            if (r < worst) { worst = r; shut = frame; }
        }
        var hole = new byte[openingMask.Length];
        int holeTotal = 0;
        for (int i = 0; i < openingMask.Length; i++)
        {
            if (openingMask[i] == 0) continue;
            if (Pixel(shut, i) != OpenAt(i)) { hole[i] = 1; holeTotal++; }
        }
        if (holeTotal == 0) return frames.Select(_ => 1.0).ToArray();
        var ratios = frames.Select(frame =>
        {
            int uncovered = 0;
            for (int i = 0; i < hole.Length; i++)
                if (hole[i] != 0 && Pixel(frame, i) != Pixel(shut, i)) uncovered++;
            return (double)uncovered / holeTotal;
        }).ToArray();
        double hi = double.NegativeInfinity;
        foreach (double r in ratios) hi = Math.Max(hi, r);
        if (hi < 0.05) return ratios.Select(_ => 1.0).ToArray();
        return ratios.Select(r => Math.Min(1, r / hi)).ToArray();
    }

    // buildFlapGeometry: one door, a flat panel hinged at the origin, a strip per sprite row
    public static GeometryBuffers BuildFlapGeometry(Frame frame, List<DoorRow> doorRows, double halfWidth, double depth, int sign)
    {
        int w = frame.Width, h = frame.Height;
        int span = doorRows.Count;
        var g = new QuadGeometry();
        double xFar = sign * halfWidth;
        double yTop = 0, yBot = PORTAL_FLAP_THICK;
        double ZAt(int k) => depth / 2 - ((double)k / span) * depth;
        double UHingeOf(DoorRow row) => (double)(sign > 0 ? row.Min : row.Max + 1) / w;
        double UMidOf(DoorRow row) => ((row.Min + row.Max + 1) / 2.0) / w;
        for (int k = 0; k < doorRows.Count; k++)
        {
            var row = doorRows[k];
            double zNear = ZAt(k), zFar = ZAt(k + 1);
            double uh = UHingeOf(row), um = UMidOf(row);
            double v0 = (double)row.Y / h, v1 = (double)(row.Y + 1) / h;
            g.Quad(SpriteBuild.SPRITE_SHADE_FRONT, 0, yTop, zNear, uh, v0, xFar, yTop, zNear, um, v0, xFar, yTop, zFar, um, v1, 0, yTop, zFar, uh, v1);
            g.Quad(SpriteBuild.SPRITE_SHADE_FRONT, 0, yBot, zFar, uh, v1, xFar, yBot, zFar, um, v1, xFar, yBot, zNear, um, v0, 0, yBot, zNear, uh, v0);
            g.Quad(SpriteBuild.SPRITE_SHADE_LEFT, 0, yTop, zNear, uh, v0, 0, yTop, zFar, uh, v1, 0, yBot, zFar, uh, v1, 0, yBot, zNear, uh, v0);
            g.Quad(SpriteBuild.SPRITE_SHADE_RIGHT, xFar, yTop, zFar, um, v1, xFar, yTop, zNear, um, v0, xFar, yBot, zNear, um, v0, xFar, yBot, zFar, um, v1);
        }
        var first = doorRows[0]; var last = doorRows[span - 1];
        double zFront = ZAt(0), zBack = ZAt(span);
        double fv = (double)first.Y / h, lv = (double)(last.Y + 1) / h;
        g.Quad(SpriteBuild.SPRITE_SHADE_TOP,
            0, yTop, zFront, UHingeOf(first), fv, xFar, yTop, zFront, UMidOf(first), fv,
            xFar, yBot, zFront, UMidOf(first), fv, 0, yBot, zFront, UHingeOf(first), fv);
        g.Quad(SpriteBuild.SPRITE_SHADE_BOTTOM,
            xFar, yTop, zBack, UMidOf(last), lv, 0, yTop, zBack, UHingeOf(last), lv,
            0, yBot, zBack, UHingeOf(last), lv, xFar, yBot, zBack, UMidOf(last), lv);
        return g.Build();
    }

    public sealed record OpeningRow(int Min, int Max, int Width);

    // spriteOpeningRows: the opening row by row - the blob connected to the commonest colour the
    // open hatch reveals over the shut one; null when nothing is revealed
    public static OpeningRow?[]? SpriteOpeningRows(Frame open, Frame shut)
    {
        int w = open.Width, h = open.Height;
        var maskOpen = open.Mask; var bufOpen = open.Data;
        var maskShut = shut.Mask; var bufShut = shut.Data;
        // Maps iterate in insertion order: the colours in the order first seen
        var countOpen = new Dictionary<uint, int>(); var order = new List<uint>();
        var countShut = new Dictionary<uint, int>();
        for (int i = 0; i < w * h; i++)
        {
            if (maskOpen[i] != 0)
            {
                uint c = bufOpen[i];
                if (countOpen.TryGetValue(c, out int n)) countOpen[c] = n + 1; else { countOpen[c] = 1; order.Add(c); }
            }
            if (maskShut[i] != 0)
            {
                uint c = bufShut[i];
                countShut[c] = (countShut.TryGetValue(c, out int n) ? n : 0) + 1;
            }
        }
        var revealed = new HashSet<uint>();
        uint? sky = null; int skyCount = 0;
        foreach (uint colour in order)
        {
            int n = countOpen[colour];
            if (n < PORTAL_REVEAL_MIN) continue;
            if ((countShut.TryGetValue(colour, out int s) ? s : 0) * PORTAL_REVEAL_RATIO > n) continue;
            revealed.Add(colour);
            if (n > skyCount) { skyCount = n; sky = colour; }
        }
        if (sky == null) return null;

        var inOpening = new byte[w * h];
        var stack = new Stack<int>();
        for (int i = 0; i < w * h; i++)
            if (maskOpen[i] != 0 && bufOpen[i] == sky.Value) { inOpening[i] = 1; stack.Push(i); }
        while (stack.Count > 0)
        {
            int i = stack.Pop(), x = i % w, y = i / w;
            foreach (var (dx, dy) in Dirs4)
            {
                int nx = x + dx, ny = y + dy;
                if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                int j = ny * w + nx;
                if (inOpening[j] != 0 || maskOpen[j] == 0 || !revealed.Contains(bufOpen[j])) continue;
                inOpening[j] = 1;
                stack.Push(j);
            }
        }
        var rows = new OpeningRow?[h];
        for (int y = 0; y < h; y++)
        {
            int min = -1, max = -1;
            for (int x = 0; x < w; x++)
                if (inOpening[y * w + x] != 0) { if (min < 0) min = x; max = x; }
            rows[y] = min < 0 ? null : new OpeningRow(min, max, max - min + 1);
        }
        return rows;
    }

    static readonly (int, int)[] Dirs4 = { (1, 0), (-1, 0), (0, 1), (0, -1) };

    public sealed class CeilingBuild
    {
        public required GeometryBuffers? Geometry;
        public required byte[] OpeningMask;
        public required HatchInfo Hatch;
    }

    // buildCeilingGeometry: the hatch as one flat square lying parallel to the ground, a depth
    // slice per sprite row stretched to the square's width (undoing the drawn perspective)
    public static CeilingBuild? BuildCeilingGeometry(Frame frame, Frame? shutFrame)
    {
        int w = frame.Width, h = frame.Height;
        var mask = frame.Mask;
        if (shutFrame == null || ReferenceEquals(shutFrame, frame)) return null;
        var rows = SpriteOpeningRows(frame, shutFrame);
        if (rows == null) return null;

        int nearY = -1, nearW = 0;
        for (int y = 0; y < h; y++)
            if (rows[y] != null && rows[y]!.Width > nearW) { nearW = rows[y]!.Width; nearY = y; }
        if (nearY < 0) return null;
        int farY = nearY;
        for (int y = nearY + 1; y < h; y++)
        {
            if (rows[y] == null || rows[y]!.Width > rows[y - 1]!.Width) break;
            if (rows[y]!.Width < nearW * 0.4) break;
            farY = y;
        }
        if (farY - nearY < 2) return null;

        var doorRows = new List<DoorRow>();
        for (int y = nearY; y <= farY; y++) doorRows.Add(new DoorRow(y, rows[y]!.Min, rows[y]!.Max));

        int side = rows[nearY]!.Width;
        double hatchDepth = TERRAIN_DEPTH;
        double centre = (rows[nearY]!.Min + rows[nearY]!.Max + 1) / 2.0;
        int span = farY - nearY + 1;
        double ZOf(int y) => hatchDepth / 2 - ((double)(y - nearY) / span) * hatchDepth;

        var panel = new QuadGeometry();
        var inPanel = new byte[w * h];
        double yTop = nearY, yBot = nearY + PORTAL_PANEL_THICK;
        double xl = centre - side / 2.0, xr = centre + side / 2.0;
        void Strip(OpeningRow r, int y, double zNear, double zFar)
        {
            for (int x = r.Min; x <= r.Max; x++) if (mask[y * w + x] != 0) inPanel[y * w + x] = 1;
            double u0 = (double)r.Min / w, u1 = (double)(r.Max + 1) / w, v0 = (double)y / h, v1 = (double)(y + 1) / h;
            panel.Quad(SpriteBuild.SPRITE_SHADE_FRONT, xl, yTop, zNear, u0, v0, xr, yTop, zNear, u1, v0, xr, yTop, zFar, u1, v1, xl, yTop, zFar, u0, v1);
            panel.Quad(SpriteBuild.SPRITE_SHADE_FRONT, xl, yBot, zFar, u0, v1, xr, yBot, zFar, u1, v1, xr, yBot, zNear, u1, v0, xl, yBot, zNear, u0, v0);
            panel.Quad(SpriteBuild.SPRITE_SHADE_LEFT, xl, yTop, zNear, u0, v0, xl, yTop, zFar, u0, v1, xl, yBot, zFar, u0, v1, xl, yBot, zNear, u0, v0);
            panel.Quad(SpriteBuild.SPRITE_SHADE_RIGHT, xr, yTop, zFar, u1, v1, xr, yTop, zNear, u1, v0, xr, yBot, zNear, u1, v0, xr, yBot, zFar, u1, v1);
        }
        for (int y = nearY; y <= farY; y++) Strip(rows[y]!, y, ZOf(y), ZOf(y + 1));
        if (panel.IndexCount == 0) return null;

        var rNear = rows[nearY]!; var rFar = rows[farY]!;
        double zNearEnd = ZOf(nearY), zFarEnd = ZOf(farY + 1);
        double uNear0 = (double)rNear.Min / w, uNear1 = (double)(rNear.Max + 1) / w, vNear = (double)nearY / h;
        double uFar0 = (double)rFar.Min / w, uFar1 = (double)(rFar.Max + 1) / w, vFar = (double)(farY + 1) / h;
        panel.Quad(SpriteBuild.SPRITE_SHADE_TOP, xl, yTop, zNearEnd, uNear0, vNear, xr, yTop, zNearEnd, uNear1, vNear,
            xr, yBot, zNearEnd, uNear1, vNear, xl, yBot, zNearEnd, uNear0, vNear);
        panel.Quad(SpriteBuild.SPRITE_SHADE_BOTTOM, xr, yTop, zFarEnd, uFar1, vFar, xl, yTop, zFarEnd, uFar0, vFar,
            xl, yBot, zFarEnd, uFar0, vFar, xr, yBot, zFarEnd, uFar1, vFar);

        return new CeilingBuild
        {
            Geometry = panel.ToBuffers(),
            OpeningMask = inPanel,
            Hatch = new HatchInfo
            {
                LeftX = centre - side / 2.0, RightX = centre + side / 2.0,
                Y = nearY + PORTAL_PANEL_THICK, HalfWidth = side / 2.0, Depth = hatchDepth, DoorRows = doorRows,
            },
        };
    }

    // isSkyColour: properly saturated, not led by red, the lead standing clear of red
    public static bool IsSkyColour(uint v)
    {
        int r = (int)(v & 255), g = (int)((v >> 8) & 255), b = (int)((v >> 16) & 255);
        int max = Math.Max(r, Math.Max(g, b));
        if (max < PORTAL_SKY_MIN) return false;
        if (max - Math.Min(r, Math.Min(g, b)) < PORTAL_SKY_SAT * max) return false;
        if (max == r) return false;
        return max - r >= PORTAL_SKY_WARM * max;
    }

    // isDarkColour: under PORTAL_DARK_MAX in every channel
    public static bool IsDarkColour(uint v)
    {
        int r = (int)(v & 255), g = (int)((v >> 8) & 255), b = (int)((v >> 16) & 255);
        return Math.Max(r, Math.Max(g, b)) < PORTAL_DARK_MAX;
    }

    // maskPatches: the 4-connected patches of a mask, each its pixel indices in visiting order
    public static List<List<int>> MaskPatches(byte[] candidate, int w, int h)
    {
        var seen = new byte[w * h];
        var patches = new List<List<int>>();
        var stack = new Stack<int>();
        for (int start = 0; start < w * h; start++)
        {
            if (candidate[start] == 0 || seen[start] != 0) continue;
            var blob = new List<int>();
            stack.Push(start);
            seen[start] = 1;
            while (stack.Count > 0)
            {
                int i = stack.Pop(), x = i % w, y = i / w;
                blob.Add(i);
                foreach (var (ox, oy) in Dirs4)
                {
                    int nx = x + ox, ny = y + oy;
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                    int j = ny * w + nx;
                    if (seen[j] != 0 || candidate[j] == 0) continue;
                    seen[j] = 1;
                    stack.Push(j);
                }
            }
            patches.Add(blob);
        }
        return patches;
    }

    // openMask: eroded by a pixel and grown back - a line a pixel wide drops out
    public static byte[] OpenMask(byte[] candidate, int w, int h)
    {
        bool Inside(byte[] m, int x, int y) => x >= 0 && x < w && y >= 0 && y < h && m[y * w + x] != 0;
        var eroded = new byte[w * h];
        var output = new byte[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (candidate[y * w + x] != 0 && Inside(candidate, x - 1, y) && Inside(candidate, x + 1, y)
                    && Inside(candidate, x, y - 1) && Inside(candidate, x, y + 1)) eroded[y * w + x] = 1;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                if (candidate[y * w + x] == 0) continue;
                if (eroded[y * w + x] != 0 || Inside(eroded, x - 1, y) || Inside(eroded, x + 1, y)
                    || Inside(eroded, x, y - 1) || Inside(eroded, x, y + 1)) output[y * w + x] = 1;
            }
        return output;
    }

    public sealed record PatchShape(int Width, int Height, int X0, int X1, double D2);

    // patchShape: a patch's extent, and how near it comes to the trigger (squared)
    public static PatchShape PatchShapeOf(List<int> blob, int w, double triggerX, double triggerY)
    {
        int x0 = int.MaxValue, x1 = int.MinValue, y0 = int.MaxValue, y1 = int.MinValue;
        double d2 = double.PositiveInfinity;
        foreach (int i in blob)
        {
            int x = i % w, y = i / w;
            if (x < x0) x0 = x;
            if (x > x1) x1 = x;
            if (y < y0) y0 = y;
            if (y > y1) y1 = y;
            double dx = x - triggerX, dy = y - triggerY;
            d2 = Math.Min(d2, dx * dx + dy * dy);
        }
        return new PatchShape(x1 - x0 + 1, y1 - y0 + 1, x0, x1, d2);
    }

    // nearestPatch: the patch nearest the trigger among those `accept` lets through
    public static (List<int> Blob, double D2)? NearestPatch(byte[] candidate, int w, int h, double triggerX, double triggerY,
        Func<List<int>, PatchShape, bool> accept)
    {
        (List<int> Blob, double D2)? best = null;
        foreach (var blob in MaskPatches(candidate, w, h))
        {
            var shape = PatchShapeOf(blob, w, triggerX, triggerY);
            if (!accept(blob, shape)) continue;
            if (best == null || shape.D2 < best.Value.D2) best = (blob, shape.D2);
        }
        return best;
    }

    // spriteOpeningMask: the doorway's pixels - the patch of sky, or of dark, nearest the trigger
    public static byte[]? SpriteOpeningMask(Frame frame, double triggerX, double triggerY)
    {
        int w = frame.Width, h = frame.Height;
        var mask = frame.Mask; var buf = frame.Data;
        var sky = new byte[w * h]; var dark = new byte[w * h];
        for (int i = 0; i < w * h; i++)
        {
            if (mask[i] == 0) continue;
            if (IsSkyColour(buf[i])) sky[i] = 1;
            if (IsDarkColour(buf[i])) dark[i] = 1;
        }
        var skyBest = NearestPatch(sky, w, h, triggerX, triggerY, (blob, _) => blob.Count >= PORTAL_SKY_PATCH_MIN);
        double reach2 = PORTAL_DARK_REACH * PORTAL_DARK_REACH;
        var darkBest = NearestPatch(OpenMask(dark, w, h), w, h, triggerX, triggerY, (blob, shape) =>
            blob.Count >= PORTAL_DARK_PATCH_MIN
            && Math.Min(shape.Width, shape.Height) >= PORTAL_DARK_SIDE_MIN
            && blob.Count >= PORTAL_DARK_FILL * shape.Width * shape.Height
            && shape.D2 <= reach2
            && shape.X0 <= triggerX && triggerX <= shape.X1 + 1);
        var best = skyBest;
        if (darkBest != null && (best == null || darkBest.Value.D2 < best.Value.D2)) best = darkBest;
        if (best == null) return null;
        var output = new byte[w * h];
        foreach (int i in best.Value.Blob) output[i] = 1;
        return output;
    }

    // buildPortalGeometry: one exit (or a slab with no opening), its face stepping down into the
    // opening as a funnel, gaps inside the outline filled, the outline's corners slid along their
    // diagonals when `smooth`
    public static GeometryBuffers? BuildPortalGeometry(Frame frame, double depth, byte[]? opening, bool smooth)
    {
        int w = frame.Width, h = frame.Height;
        var mask = frame.Mask;
        opening ??= new byte[w * h];
        double thick = PORTAL_FRAME_THICK + depth;

        var solid = new byte[w * h];
        var source = new int[w * h];
        Array.Fill(source, -1);
        {
            var outside = new byte[w * h];
            var stack = new Stack<int>();
            void Open(int i) { if (mask[i] == 0 && outside[i] == 0) { outside[i] = 1; stack.Push(i); } }
            for (int x = 0; x < w; x++) { Open(x); Open((h - 1) * w + x); }
            for (int y = 0; y < h; y++) { Open(y * w); Open(y * w + w - 1); }
            while (stack.Count > 0)
            {
                int i = stack.Pop(), x = i % w, y = i / w;
                foreach (var (dx, dy) in Dirs4)
                {
                    int nx = x + dx, ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                    int j = ny * w + nx;
                    if (outside[j] != 0 || mask[j] != 0) continue;
                    outside[j] = 1;
                    stack.Push(j);
                }
            }
            var fringe = new List<int>();
            for (int i = 0; i < w * h; i++)
            {
                if (mask[i] != 0) { solid[i] = 1; source[i] = i; fringe.Add(i); }
                else if (outside[i] == 0) solid[i] = 1;
            }
            for (int k = 0; k < fringe.Count; k++)
            {
                int i = fringe[k], x = i % w, y = i / w;
                foreach (var (dx, dy) in Dirs4)
                {
                    int nx = x + dx, ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                    int j = ny * w + nx;
                    if (solid[j] == 0 || source[j] != -1) continue;
                    source[j] = source[i];
                    fringe.Add(j);
                }
            }
        }

        bool IsFrame(int x, int y) => x >= 0 && x < w && y >= 0 && y < h && solid[y * w + x] != 0 && opening[y * w + x] == 0;

        int rings = depth > 0 ? (int)Math.Max(PORTAL_FUNNEL_RINGS, JsMath.Round(thick)) : 0;
        const double DIAG = 1.4142135623730951; // Math.SQRT2
        const float FAR = 1e6f;
        var dist = new float[w * h]; // a Float32Array: every distance is rounded to float as it is stored
        for (int i = 0; i < w * h; i++) dist[i] = solid[i] != 0 && opening[i] != 0 ? 0 : FAR;
        void Relax(int i, int j, double cost)
        {
            double d = dist[j] + cost;
            if (d < dist[i]) dist[i] = (float)d;
        }
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                if (solid[i] == 0 || dist[i] == 0) continue;
                if (x > 0) Relax(i, i - 1, 1);
                if (y > 0) Relax(i, i - w, 1);
                if (x > 0 && y > 0) Relax(i, i - w - 1, DIAG);
                if (x < w - 1 && y > 0) Relax(i, i - w + 1, DIAG);
            }
        for (int y = h - 1; y >= 0; y--)
            for (int x = w - 1; x >= 0; x--)
            {
                int i = y * w + x;
                if (solid[i] == 0 || dist[i] == 0) continue;
                if (x < w - 1) Relax(i, i + 1, 1);
                if (y < h - 1) Relax(i, i + w, 1);
                if (x < w - 1 && y < h - 1) Relax(i, i + w + 1, DIAG);
                if (x > 0 && y < h - 1) Relax(i, i + w - 1, DIAG);
            }

        double FaceAt(int x, int y)
        {
            if (x < 0 || x >= w || y < 0 || y >= h) return 0;
            int i = y * w + x;
            if (solid[i] == 0) return 0;
            if (opening[i] != 0) return thick;
            double t = dist[i] / (double)(rings + 1);
            if (t >= 1) return 0;
            return thick * (1 - t * t * (3 - 2 * t));
        }
        double CornerZ(int x, int y)
        {
            double sum = 0;
            sum += FaceAt(x - 1, y - 1); sum += FaceAt(x, y - 1); sum += FaceAt(x - 1, y); sum += FaceAt(x, y);
            return -sum / 4;
        }

        var corners = new (double X, double Y)?[(w + 1) * (h + 1)];
        bool SolidAt(int x, int y) => x >= 0 && x < w && y >= 0 && y < h && solid[y * w + x] != 0;
        (double X, double Y) CornerAt(int x, int y)
        {
            int slot = y * (w + 1) + x;
            if (corners[slot] is { } hit) return hit;
            (double, double) c = (x, y);
            if (smooth)
            {
                bool a = SolidAt(x - 1, y - 1), b = SolidAt(x, y - 1);
                bool cc = SolidAt(x - 1, y), d = SolidAt(x, y);
                int n = (a ? 1 : 0) + (b ? 1 : 0) + (cc ? 1 : 0) + (d ? 1 : 0);
                if (n == 1 || n == 3)
                {
                    bool o0, o1, o2;
                    if (n == 1) { o0 = a; o1 = b; o2 = cc; } else { o0 = !a; o1 = !b; o2 = !cc; }
                    c = (x + ((o0 || o2) ? -1 : 1) * SpriteBuild.SPRITE_SMOOTH_PULL,
                         y + ((o0 || o1) ? -1 : 1) * SpriteBuild.SPRITE_SMOOTH_PULL);
                }
            }
            corners[slot] = c;
            return c;
        }

        var g = new QuadGeometry();
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int here = y * w + x;
                if (solid[here] == 0) continue;
                var c00 = CornerAt(x, y); var c10 = CornerAt(x + 1, y);
                var c11 = CornerAt(x + 1, y + 1); var c01 = CornerAt(x, y + 1);
                int src = source[here] < 0 ? here : source[here];
                int sx = src % w, sy = src / w;
                double uc = (sx + 0.5) / w, vc = (sy + 0.5) / h;
                bool drawn = mask[here] != 0;
                double u0 = drawn ? (double)x / w : uc, u1 = drawn ? (double)(x + 1) / w : uc;
                double v0 = drawn ? (double)y / h : vc, v1 = drawn ? (double)(y + 1) / h : vc;

                g.Quad(1 - PORTAL_TUNNEL_SHADE,
                    c00.X, c00.Y, -thick, u0, v0, c10.X, c10.Y, -thick, u1, v0,
                    c11.X, c11.Y, -thick, u1, v1, c01.X, c01.Y, -thick, u0, v1);

                if (opening[y * w + x] != 0) continue;
                double z00 = CornerZ(x, y), z10 = CornerZ(x + 1, y);
                double z11 = CornerZ(x + 1, y + 1), z01 = CornerZ(x, y + 1);
                double sunk = thick <= 0 ? 0 : Math.Min(1, (-(z00 + z10 + z11 + z01) / 4) / thick);
                g.Quad(SpriteBuild.SPRITE_SHADE_FRONT - PORTAL_TUNNEL_SHADE * sunk,
                    c00.X, c00.Y, z00, u0, v0, c10.X, c10.Y, z10, u1, v0,
                    c11.X, c11.Y, z11, u1, v1, c01.X, c01.Y, z01, u0, v1);
                void Edge(int dx, int dy, (double X, double Y) a, double za, (double X, double Y) b, double zb, double shade)
                {
                    if (IsFrame(x + dx, y + dy)) return;
                    g.Quad(shade, a.X, a.Y, za, uc, vc, b.X, b.Y, zb, uc, vc, b.X, b.Y, -thick, uc, vc, a.X, a.Y, -thick, uc, vc);
                }
                Edge(-1, 0, c00, z00, c01, z01, SpriteBuild.SPRITE_SHADE_LEFT);
                Edge(1, 0, c11, z11, c10, z10, SpriteBuild.SPRITE_SHADE_RIGHT);
                Edge(0, -1, c10, z10, c00, z00, SpriteBuild.SPRITE_SHADE_TOP);
                Edge(0, 1, c01, z01, c11, z11, SpriteBuild.SPRITE_SHADE_BOTTOM);
            }
        }
        return g.ToBuffers();
    }

    // carveTerrainForPortal: clear the render-only depth behind every pixel the sprite covers.
    // A fractional origin (a hatch centred on a half pixel) indexes the typed array off its
    // integer keys: the JS reads undefined there (!== EMPTY), counts the pixel and stores nothing.
    public static int CarveTerrainForPortal(byte[] depthMap, Level level, double originX, double originY, Frame frame)
    {
        int w = frame.Width, h = frame.Height;
        var mask = frame.Mask;
        int W = level.Width, H = level.Height;
        int carved = 0;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                if (mask[i] == 0) continue;
                double tx = originX + x, ty = originY + y;
                if (tx < 0 || tx >= W || ty < 0 || ty >= H) continue;
                double ti = ty * W + tx;
                if (ti != Math.Floor(ti)) { carved++; continue; }
                int k = (int)ti;
                if (depthMap[k] != DEPTH_EMPTY) { depthMap[k] = DEPTH_EMPTY; carved++; }
            }
        }
        return carved;
    }

    // isPortalCapCandidate: nothing drawn on the terrain, nothing with an effect of its own
    public static bool IsPortalCapCandidate(GadgetObject mapObject)
    {
        if (mapObject.Decal || mapObject.OneWay) return false;
        if (mapObject.Gadget.EffectBase != "NONE") return false;
        return true;
    }

    public sealed record ObjectRect(int X0, int Y0, int X1, int Y1);

    // portalObjectRect: the rectangle an object's first frame covers, in level pixels
    public static ObjectRect? PortalObjectRect(GadgetObject mapObject)
    {
        if (mapObject.Frames.Count == 0) return null;
        var frame = mapObject.Frames[0];
        int x = mapObject.X + frame.OffsetX, y = mapObject.Y + frame.OffsetY;
        return new ObjectRect(x, y, x + frame.Width, y + frame.Height);
    }

    // buildPortals: every object rendered as an opening, with its geometry; carves `depthMap`.
    // `objectZ` is the plane objects sit on, where a door's face goes.
    public static List<Portal> BuildPortals(Level level, PortalObjectData data, IReadOnlyDictionary<int, PortalProfileEntry>? byId,
        byte[] depthMap, double objectZ, bool smooth)
    {
        var portals = new List<Portal>();
        double exitDepth = Math.Max(PORTAL_EXIT_DEPTH, objectZ - TERRAIN_BACK - PORTAL_FRAME_THICK);
        int count = Math.Min(level.Objects.Count, data.Ids.Count);
        var configs = new PortalConfig?[count];
        for (int i = 0; i < count; i++)
        {
            configs[i] = PortalConfigFor(data.Ids[i], data.InfoAt(i), byId);
            if (configs[i] != null && configs[i]!.Depth == null) configs[i]!.Depth = exitDepth;
        }
        for (int i = 0; i < count; i++)
        {
            if (configs[i] != null) continue;
            if (!IsPortalCapCandidate(level.Objects[i])) continue;
            var rect = PortalObjectRect(level.Objects[i]);
            if (rect == null) continue;
            for (int j = 0; j < count; j++)
            {
                if (j == i || configs[j] == null || configs[j]!.Shape != "portal") continue;
                var other = PortalObjectRect(level.Objects[j]);
                if (other == null) continue;
                int across = Math.Min(rect.X1, other.X1) - Math.Max(rect.X0, other.X0);
                int down = Math.Min(rect.Y1, other.Y1) - Math.Max(rect.Y0, other.Y0);
                if (across < 0 || down < 0) continue;
                int sideways = Math.Min(rect.X1 - rect.X0, other.X1 - other.X0);
                int upright = Math.Min(rect.Y1 - rect.Y0, other.Y1 - other.Y0);
                if (across < sideways * 0.5 && down < upright * 0.5) continue;
                configs[i] = new PortalConfig { Shape = "slab", Depth = configs[j]!.Depth };
                break;
            }
        }

        for (int i = 0; i < count; i++)
        {
            int objectId = data.Ids[i];
            var info = data.InfoAt(i);
            var config = configs[i];
            if (config == null) continue;
            var mapObject = level.Objects[i];
            var frames = mapObject.Frames;
            if (frames.Count == 0) continue;
            var frame = frames[0];
            GeometryBuffers? geometry;
            HatchInfo? hatch = null;
            double[]? openness = null;
            PortalRebuild? rebuild = null;
            if (config.Shape == "ceiling")
            {
                var built = BuildCeilingGeometry(frame, frames.Count > 1 ? frames[1] : null);
                if (built == null) continue;
                geometry = built.Geometry;
                hatch = built.Hatch;
                openness = HatchOpenness(frames, built.OpeningMask, frame.Width);
            }
            else if (config.Shape == "slab")
            {
                rebuild = new PortalRebuild { Frame = frame, Depth = config.Depth!.Value, Opening = null };
                geometry = BuildPortalGeometry(frame, config.Depth!.Value, null, smooth);
            }
            else
            {
                double tx = (info != null ? info.TriggerLeft + info.TriggerWidth / 2.0 : frame.Width / 2.0) - frame.OffsetX;
                double ty = (info != null ? info.TriggerTop + info.TriggerHeight / 2.0 : frame.Height / 2.0) - frame.OffsetY;
                rebuild = new PortalRebuild { Frame = frame, Depth = config.Depth!.Value, Opening = SpriteOpeningMask(frame, tx, ty) };
                geometry = BuildPortalGeometry(frame, config.Depth!.Value, rebuild.Opening, smooth);
            }
            if (geometry == null) continue;

            double originX = mapObject.X + frame.OffsetX;
            double originY = mapObject.Y + frame.OffsetY;
            if (hatch != null)
            {
                double spawnLocalX = mapObject.X + PORTAL_SPAWN_OFFSET_X - originX;
                originX += spawnLocalX - (hatch.LeftX + hatch.HalfWidth);
            }
            portals.Add(new Portal
            {
                Index = i, ObjectId = objectId, Geometry = geometry, Rebuild = rebuild, OriginX = originX, OriginY = originY,
                Shape = config.Shape, SfxX = originX + frame.Width / 2.0, SfxY = originY + frame.Height / 2.0,
                Hatch = hatch, Openness = openness, ClosedFrame = frames.Count > 1 ? frames[1] : frame, MapObject = mapObject,
                Carved = CarveTerrainForPortal(depthMap, level, originX, originY, frame),
            });
        }
        return portals;
    }

    // The hinge angle a hatch's doors stand at on the frame the animation shows (app.js):
    // openness[indexOf(frame)] quarter turns, the frame not found counting as the first.
    public static double FlapAngle(Portal portal, Frame shown)
    {
        if (portal.Openness == null) return 0;
        int idx = Math.Max(0, portal.MapObject.Frames.IndexOf(shown));
        double o = idx < portal.Openness.Length ? portal.Openness[idx] : 0;
        return o * Math.PI / 2;
    }
}
