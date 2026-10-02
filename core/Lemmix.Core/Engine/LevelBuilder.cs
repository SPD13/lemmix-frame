using Lemmix.Util;

namespace Lemmix.Engine;

// web/lemmix/js/level.js build: the level's graphics, physics map and gadgets (LemRendering.pas,
// LemGadgets.pas, TLevel.Sanitize / PrepareForUse).
public static partial class LevelBuilder
{
    const int MaxSkillTypesPerLevel = 10;
    const int MinSi = 4, MaxSi = 102; // ReleaseRateToSpawnInterval(99), (1)
    const int ViewportWidth = 320;    // what the DOS display shows; START_X is a centre

    static readonly HashSet<string> NoFlipHorizontalTypes = new() { "PICKUP", "PORTAL" };
    static readonly HashSet<string> NoFlipVerticalTypes = new() { "WINDOW", "PICKUP", "UPDRAFT", "PORTAL" };
    static readonly HashSet<string> NoRotateTypes = new() { "WINDOW", "FORCELEFT", "FORCERIGHT", "PICKUP", "UPDRAFT", "FLIPPER", "PORTAL" };
    static readonly HashSet<string> OwwEffects = new() { "ONEWAYLEFT", "ONEWAYRIGHT", "ONEWAYDOWN", "ONEWAYUP" };

    // Lemmix.digitFont: the 4x5 countdown digits, set when the masks load (sprites.js loadMasks)
    public static Bitmap? DigitFont;

    // A small deterministic generator, so "random" initial frames replay.
    public static Func<double> SeededRandom(string seed)
    {
        uint s = 0;
        foreach (char c in seed) s = unchecked(s * 31 + c);
        if (s == 0) s = 1;
        return () =>
        {
            s ^= s << 13;
            s ^= s >> 17;
            s ^= s << 5;
            return s / 4294967296.0;
        };
    }

    public static int EvaluateResizable(int specified, int dflt, int baseSize, bool resizable)
    {
        if (!resizable) return baseSize;
        if (specified > 0) return specified;
        if (dflt > 0) return dflt;
        return baseSize;
    }

    // A flipped or rotated arrow points another way (TGadget.AdjustOWWDirection).
    public static string AdjustOwwDirection(string effect, GadgetSpec spec)
    {
        string[] dirs = { "ONEWAYLEFT", "ONEWAYUP", "ONEWAYRIGHT", "ONEWAYDOWN" };
        int d = Array.IndexOf(dirs, effect);
        if (d < 0) return effect;
        if (spec.Rotate) d += 1;
        if (spec.Flip && d % 2 == 0) d += 2;
        if (spec.Invert && d % 2 == 1) d += 2;
        return dirs[d % 4];
    }

    sealed record Resolved<T>(T? Meta, int DefWidth, int DefHeight) where T : class;

    // An unresolvable piece becomes default:fallback, as NeoLemmix does.
    static Resolved<MetaTerrain> ResolveTerrain(StyleManager styles, TerrainSpec t)
    {
        var d = styles.Dealias(t.Gs, t.Piece, "terrain");
        var meta = styles.Terrain(d.Gs, d.Piece);
        if (meta == null)
        {
            styles.Missing.Add(t.Gs + ":" + t.Piece);
            meta = styles.Terrain("default", "fallback");
        }
        return new(meta, d.DefWidth, d.DefHeight);
    }

    static Resolved<MetaGadget> ResolveGadget(StyleManager styles, GadgetSpec g)
    {
        var d = styles.Dealias(g.Gs, g.Piece, "gadget");
        var meta = styles.Gadget(d.Gs, d.Piece);
        if (meta == null)
        {
            styles.Missing.Add(g.Gs + ":" + g.Piece);
            meta = styles.Gadget("default", "fallback");
        }
        return new(meta, d.DefWidth, d.DefHeight);
    }

    // Build the level. `seed` makes the random initial frames repeatable (the level id by default).
    public static Level Build(LevelData data, StyleManager styles, string? seed = null)
    {
        var info = data.Info;
        var rand = SeededRandom(StyleManager.Or(seed, info.Id, info.Title));

        // ---- sanitize (TLevel.Sanitize)
        int width = Math.Max(1, info.Width), height = Math.Max(1, info.Height);
        int spawnInterval = Math.Max(MinSi, Math.Min(MaxSi, info.SpawnInterval));

        // ---- pieces
        var terrainMeta = data.Terrains.Select(t => ResolveTerrain(styles, t)).ToList();
        var gadgetMeta = data.Gadgets.Select(g => ResolveGadget(styles, g)).ToList();
        var theme = styles.Style(info.Theme != "" ? info.Theme : "default").Theme;

        // ---- terrain: the picture and the physics-prep map, in one pass
        var picture = new Bitmap(width, height);
        var prep = new Bitmap(width, height);
        var pieces = new List<PiecePlacement>(); // every placement, for the diorama's depth tagging
        var drawnCache = new Dictionary<string, DrawnPiece>(StringComparer.Ordinal);
        for (int i = 0; i < data.Terrains.Count; i++)
        {
            var t = data.Terrains[i];
            var (meta, defWidth, defHeight) = terrainMeta[i];
            if (meta == null) continue;
            var v = meta.Variation(t.Flip, t.Invert, t.Rotate);
            int w = EvaluateResizable(t.Width != 0 ? t.Width : defWidth, v.DefaultWidth, v.Width, v.ResizeH);
            int h = EvaluateResizable(t.Height != 0 ? t.Height : defHeight, v.DefaultHeight, v.Height, v.ResizeV);
            string key = meta.Gs + ":" + meta.Piece;
            string variantKey = key + "/" + (t.Flip ? "f" : "") + (t.Invert ? "i" : "") + (t.Rotate ? "r" : "") + "/" + w + "x" + h;
            if (!drawnCache.ContainsKey(variantKey))
            {
                var image = v.Image;
                if (w != v.Width || h != v.Height)
                {
                    image = new Bitmap(w, h);
                    Pixels.DrawNineSlice(image, 0, 0, w, h, v.Image, v.Cut.Margins, Pixels.CombineGadget);
                }
                drawnCache[variantKey] = new DrawnPiece(key, variantKey, image, w, h, meta.Steel);
            }
            pieces.Add(new PiecePlacement(t.X, t.Y, drawnCache[variantKey], t.NoOverwrite, t.Erase, t.OneWay));
            Combine combine = t.NoOverwrite ? Pixels.CombineTerrainNoOverwrite
                : t.Erase ? Pixels.CombineTerrainErase : Pixels.CombineTerrainDefault;
            Pixels.DrawNineSlice(picture, t.X, t.Y, w, h, v.Image, v.Cut.Margins, combine);
            string kind = t.Erase ? "erase" : meta.Steel ? "steel" : t.OneWay ? "oneway" : "standard";
            Pixels.DrawNineSlice(prep, t.X, t.Y, w, h, v.Image, v.Cut.Margins, Pixels.PhysicsCombiner(kind, t.NoOverwrite && !t.Erase));
        }

        // ---- the physics map (GeneratePhysicsMapFromInfoMap)
        var physics = new ushort[width * height];
        var pd = prep.Data;
        for (int i = 0, p = 0; i < physics.Length; i++, p += 4)
        {
            int sol = pd[p + 3];
            if (sol < Pixels.AlphaCutoff) continue;
            int c = PM.SOLID | PM.ORIGSOLID;
            double mod = sol / 255.0, cutoff = Pixels.AlphaCutoff * mod;
            if (pd[p] * mod >= cutoff) c |= PM.STEEL;
            else if (pd[p + 1] * mod >= cutoff) c |= PM.ONEWAY;
            physics[i] = (ushort)c;
        }

        // ---- gadgets
        var gadgets = new List<Gadget>();
        for (int i = 0; i < data.Gadgets.Count; i++)
        {
            var g = data.Gadgets[i];
            var (meta, defWidth, defHeight) = gadgetMeta[i];
            if (meta == null) continue;
            var spec = g.Copy();
            if (meta.Effect != "NONE")
            {
                if (NoFlipHorizontalTypes.Contains(meta.Effect)) spec.Flip = false;
                if (NoFlipVerticalTypes.Contains(meta.Effect)) spec.Invert = false;
                if (NoRotateTypes.Contains(meta.Effect)) spec.Rotate = false;
            }
            if (spec.Width == 0) spec.Width = defWidth;
            if (spec.Height == 0) spec.Height = defHeight;
            gadgets.Add(new Gadget(spec, meta, meta.Variation(spec.Flip, spec.Invert, spec.Rotate), i, rand));
        }
        FindReceivers(gadgets);

        // ---- one-way arrows onto the physics map (ApplyOWW, RemoveOverlappingOWWs, Validate)
        foreach (var g in gadgets)
        {
            int bit = g.Effect switch
            {
                "ONEWAYLEFT" => PM.ONEWAYLEFT, "ONEWAYRIGHT" => PM.ONEWAYRIGHT,
                "ONEWAYDOWN" => PM.ONEWAYDOWN, "ONEWAYUP" => PM.ONEWAYUP, _ => 0,
            };
            if (bit == 0) continue;
            var r = g.TriggerRect;
            for (int y = Math.Max(0, r.Y0); y < Math.Min(height, r.Y1); y++)
                for (int x = Math.Max(0, r.X0); x < Math.Min(width, r.X1); x++) physics[x + y * width] |= (ushort)bit;
        }
        const int AllOww = PM.ONEWAYLEFT | PM.ONEWAYRIGHT | PM.ONEWAYDOWN | PM.ONEWAYUP;
        for (int i = 0; i < physics.Length; i++)
        {
            int c = physics[i];
            int bits = c & AllOww;
            // RemoveOverlappingOWWs: a one-way pixel keeps its bits only under exactly one arrow
            if (bits == 0 || (bits & (bits - 1)) != 0) c &= ~(PM.ONEWAY | AllOww);
            if ((c & PM.SOLID) == 0) c &= ~PM.TERRAIN;
            if ((c & PM.STEEL) != 0) c &= ~PM.ONEWAY;
            if ((c & PM.ONEWAY) == 0) c &= ~AllOww;
            c &= ~PM.NOCANCELSTEEL;
            physics[i] = (ushort)c;
        }

        // ---- the picture keeps only solid pixels (ApplyRemovedTerrain)
        var mask = new sbyte[width * height];
        var words = picture.Words();
        for (int i = 0; i < physics.Length; i++)
        {
            if ((physics[i] & PM.SOLID) != 0) mask[i] = 1;
            else words[i] = 0;
        }

        // ---- background: the theme colour, and a tiled image if named
        int bgColor = StyleManager.ThemeColor(theme, "BACKGROUND");
        Bitmap? bgImage = null;
        if (info.Background != "" && info.Background != ":")
        {
            var id = StyleManager.SplitIdentifier(info.Background, info.Theme)!;
            bgImage = styles.Background(id.Gs, id.Piece) ?? styles.Background("default", "fallback");
        }

        // ---- the level
        var level = new Level(width, height)
        {
            Name = info.Title,
            Info = info,
            Theme = theme,
            ThemeName = info.Theme,
            GroundImage = picture.Data,
            Physics = physics,
            Gadgets = gadgets,
            Pieces = pieces,
            Preplaced = data.Lemmings,
            Talismans = data.Talismans,
            Pretext = data.Pretext,
            Posttext = data.Posttext,
            Background = new LevelBackground(bgColor, bgImage),
            SpawnInterval = spawnInterval,
            SpawnLocked = info.SpawnLocked,
            TimeLimitSeconds = Math.Min(5999, info.TimeLimit),
            MissingPieces = styles.Missing.ToList(),
        };
        level.GroundMask = new SolidLayer(width, height, mask);
        level.Objects = gadgets.Where(g => !g.OffMap).Select(GadgetAsObject).ToList();
        level.Entrances = gadgets.Where(g => g.EffectBase == "WINDOW").ToList();

        // ---- skills (Sanitize + PrepareForUse step 1)
        var pickupSkills = new HashSet<string>(gadgets.Where(g => g.EffectBase == "PICKUP").Select(g => g.SkillName));
        int types = 0;
        foreach (string name in Skills)
        {
            int idx = data.Skills.FindIndex(s => s.Key == name);
            if (idx < 0) continue;
            int count = Math.Max(0, Math.Min(100, data.Skills[idx].Value));
            if (++types > MaxSkillTypesPerLevel) continue;
            if (count == 0 && !pickupSkills.Contains(name)) continue;
            if (name == "CLONER" && count > 99) count = 99;
            level.Skills.Add(new SkillCount(name, count));
        }

        // ---- lemming count and spawn order (PrepareForUse steps 2-3)
        PrepareSpawn(level, info, gadgets, data.Lemmings);

        // ---- where the screen starts (START_X/Y are a centre; auto when absent)
        int startX = info.StartX, startY = info.StartY;
        if (info.StartAuto) (startX, startY) = AutoScreenStart(level, gadgets, data.Lemmings);
        startX = Math.Max(0, Math.Min(width - 1, startX));
        startY = Math.Max(0, Math.Min(height - 1, startY));
        level.StartX = startX;
        level.StartY = startY;
        level.ScreenPositionX = Math.Max(0, Math.Min(Math.Max(0, width - ViewportWidth), startX - (ViewportWidth >> 1)));
        return level;
    }

    // TGadgetList.FindReceiverID: each teleporter takes the next receiver with its pairing.
    static void FindReceivers(List<Gadget> gadgets)
    {
        int n = gadgets.Count;
        var used = Enumerable.Repeat(false, n).ToList();
        int pairCount = 0;
        for (int i = 0; i < n; i++)
        {
            var g = gadgets[i];
            if (g.Effect == "TELEPORT")
            {
                int test = i;
                Gadget found;
                do
                {
                    test++;
                    found = gadgets[test % n];
                } while (!((found.Effect == "RECEIVER" && found.Pairing == g.Pairing) || test == i + n));
                test %= n;
                if (test == i) { g.Effect = "NONE"; continue; }
                g.ReceiverId = test;
                if (used[test])
                {
                    // a receiver shared by teleporters is cloned for each
                    var clone = new Gadget(found.Spec, found.Meta, found.V, gadgets.Count, () => 0);
                    gadgets.Add(clone); used.Add(false);
                    g.ReceiverId = gadgets.Count - 1;
                    found = clone;
                }
                g.PairingId = pairCount; found.PairingId = pairCount; pairCount++;
                used[test] = true;
                found.FlipLemming = g.FlipLemming; // SetFlipOfReceiverTo
            }
        }
        for (int i = 0; i < n; i++)
        {
            var g = gadgets[i];
            if (g.Effect != "PORTAL" || used[i]) continue;
            int test = i;
            Gadget found;
            do { test++; found = gadgets[test % n]; } while (!((found.Effect == "PORTAL" && found.Pairing == g.Pairing) || test == i + n));
            test %= n;
            if (test == i) { g.Effect = "NONE"; continue; }
            g.ReceiverId = test; found.ReceiverId = i;
            used[i] = true; used[test] = true;
            g.PairingId = pairCount; found.PairingId = pairCount; pairCount++;
            found.FlipLemming = g.FlipLemming;
        }
        for (int i = 0; i < n; i++)
            if (gadgets[i].Effect == "RECEIVER" && !used[i]) gadgets[i].Effect = "NONE";
    }

    // TLevel.PrepareForUse steps 2 and 3: which window releases each lemming, and the counts.
    static void PrepareSpawn(Level level, LevelInfo info, List<Gadget> gadgets, List<LemmingSpec> preplaced)
    {
        var windows = gadgets.Select(g => g.EffectBase == "WINDOW" ? (g.LemmingCap > 0 ? g.LemmingCap : -1) : 0).ToArray();
        bool hasWindow = windows.Any(w => w != 0);
        int lemmingsCount = Math.Max(info.Lemmings, preplaced.Count);
        int zombies = preplaced.Count(l => l.Zombie);
        int neutrals = preplaced.Count(l => !l.Zombie && l.Neutral);
        var spawnOrder = new List<int>();
        if (!hasWindow)
        {
            lemmingsCount = preplaced.Count;
        }
        else
        {
            int n = -1, spawned = preplaced.Count;
            int total = lemmingsCount - preplaced.Count;
            for (int i = 0; i < total; i++)
            {
                // SetNextWindow
                int initial = n == -1 ? gadgets.Count - 1 : n;
                bool dead = false;
                do
                {
                    n++;
                    if (n >= gadgets.Count) n = 0;
                    if (n == initial && windows[n] == 0) { dead = true; break; }
                } while (windows[n] == 0);
                if (dead) { lemmingsCount = spawned; break; }
                var g = gadgets[n];
                if (g.Presets.Zombie) zombies++;
                else if (g.Presets.Neutral) neutrals++;
                spawnOrder.Add(n);
                if (windows[n] > 0) windows[n]--;
                spawned++;
            }
        }
        int maxLemmings = lemmingsCount + (level.Skills.FirstOrDefault(s => s.Name == "CLONER")?.Count ?? 0) - zombies;
        int maxExit = 0;
        foreach (var g in gadgets)
        {
            if (g.EffectBase is "EXIT" or "LOCKEXIT")
            {
                if (g.LemmingCap > 0 && maxExit >= 0) maxExit += g.LemmingCap;
                else maxExit = -1;
            }
            if (g.EffectBase == "PICKUP" && g.SkillName == "CLONER") maxLemmings += g.SkillCount;
        }
        int save = Math.Max(0, info.Save);
        if (save > maxLemmings) save = maxLemmings;
        if (maxExit >= 0 && save > maxExit) save = maxExit;
        level.ReleaseCount = lemmingsCount;
        level.NeedCount = save;
        level.ZombieCount = zombies;
        level.NeutralCount = neutrals;
        level.SpawnOrder = spawnOrder;
    }

    // TLevel.CalculateAutoScreenStart.
    static (int, int) AutoScreenStart(Level level, List<Gadget> gadgets, List<LemmingSpec> preplaced)
    {
        var entrances = gadgets.Where(g => g.EffectBase == "WINDOW" && !g.Presets.Zombie && !g.Presets.Neutral).ToList();
        var exits = gadgets.Where(g => g.EffectBase is "EXIT" or "LOCKEXIT").ToList();
        var lems = preplaced.Where(l => !l.Zombie && !l.Neutral).ToList();
        int hx = level.Width >> 1, hy = level.Height >> 1, targetDx = 0;
        bool vertShift = false;
        double ax = 0, ay = 0;
        if (entrances.Count > 0)
        {
            foreach (var g in entrances) { ax += g.TriggerRect.X0; ay += g.TriggerRect.Y0; }
            ax /= entrances.Count; ay /= entrances.Count;
        }
        else if (lems.Count > 0)
        {
            foreach (var l in lems) { ax += l.X; ay += l.Y; }
            ax /= lems.Count; ay /= lems.Count;
        }
        double closest = -1;
        void TryPos(int x, int y, int dx, bool shift)
        {
            double d = Math.Sqrt((x - ax) * (x - ax) + (y - ay) * (y - ay));
            if (d < closest || closest < 0) { closest = d; hx = x; hy = y; targetDx = dx; vertShift = shift; }
        }
        foreach (var g in entrances) TryPos(g.TriggerRect.X0, g.TriggerRect.Y0, g.FlipLemming ? -1 : 1, true);
        foreach (var g in exits) TryPos(g.TriggerRect.X0 + ((g.TriggerRect.X1 - g.TriggerRect.X0) >> 1),
            g.TriggerRect.Y0 + ((g.TriggerRect.Y1 - g.TriggerRect.Y0) >> 1), 0, false);
        foreach (var l in lems) TryPos(l.X, l.Y, l.Dx, false);
        if (targetDx != 0) hx += 48 * targetDx;
        hy += vertShift ? 20 : -12;
        return (hx, hy);
    }

    // The MapObject shape the 3D layer draws: position, animation frames, draw flags.
    static GadgetObject GadgetAsObject(Gadget g)
    {
        // NeoLemmix's layers (LemRendering.pas, DrawGadgetsOnLayer): see level.js gadgetAsObject
        bool oneWay = OwwEffects.Contains(g.Effect);
        bool behind = g.EffectBase == "BACKGROUND" && !g.OnlyOnTerrain && !oneWay;
        bool low = g.NoOverwrite && !g.OnlyOnTerrain && !behind && !oneWay;
        bool decal = oneWay || (g.OnlyOnTerrain && !g.NoOverwrite);
        var obj = new GadgetObject { Gadget = g, X = g.X, Y = g.Y, Behind = behind, Decal = decal, Low = low, OneWay = oneWay };
        g.Object = obj;
        return obj;
    }
}
