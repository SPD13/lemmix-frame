using Lemmix.Io;
using Lemmix.Parse;

namespace Lemmix.Engine;

// web/lemmix/js/sprites.js - the lemming sprites of a NeoLemmix sprite set
// (styles/<set>/lemmings/): one PNG per animation, frames stacked vertically, the left-facing
// frames in the left half and the right-facing ones in the right half (LemAnimationSet.pas
// ReadData), with scheme.nxmi giving frame counts, the loop point and the foot position per
// direction. Frames come out offset by the foot, so drawing a frame at the lemming's position
// puts the feet there.
//
// State recolouring (scheme.nxmi $STATE_RECOLORING): an athlete, a zombie or a neutral swaps
// the listed colours, and the lemming under the cursor swaps the $SELECTED ones on top, the way
// TRecolorImage.SwapColors does: every applicable swap is tried against the sprite's own colour
// and the last one registered wins. Each variant is built once and kept.
//
// Also here: generatePickupIcons, and Lemming.prototype.render's choice of variant (game.js).
// The physics masks (loadMasks) are Masks.Load.

public sealed class SpriteSide
{
    public int FootX, FootY;
    public required List<Bitmap> Frames;
}

public sealed class SpriteAnim
{
    public int FrameCount, FrameDiff, Width, Height;
    public required SpriteSide Right, Left;
}

public sealed class SpriteSet
{
    // the file for each action (TBasicLemmingAction order), null where NeoLemmix has no sprite
    public static readonly string?[] ActionSprites =
    {
        null, "walker", "ascender", "digger", "climber", "drowner", "hoister", "builder",
        "basher", "miner", "faller", "floater", "splatter", "exiter", "burner", "blocker", "shrugger", "ohnoer",
        "bomber", "walker", "platformer", "stacker", "ohnoer", "stoner", "swimmer", "glider", "disarmer", null,
        "fencer", "reacher", "shimmier", "jumper", "dehoister", "slider", "laserer",
    };

    static readonly string[] AnimNames =
    {
        "walker", "ascender", "digger", "climber", "drowner", "hoister", "builder", "basher",
        "miner", "faller", "floater", "splatter", "exiter", "burner", "blocker", "shrugger", "ohnoer", "bomber",
        "platformer", "stoner", "swimmer", "glider", "disarmer", "stacker", "fencer", "reacher", "shimmier",
        "jumper", "dehoister", "slider", "laserer",
    };

    readonly IFileSource _io;
    public readonly Dictionary<string, SpriteAnim> Anims = new(StringComparer.Ordinal);
    public readonly Dictionary<string, List<(int From, int To)>> Recolor = new(StringComparer.Ordinal);
    readonly Dictionary<string, Frame> _variants = new(StringComparer.Ordinal);

    public SpriteSet(IFileSource io) { _io = io; }

    public SpriteSet Load(string setName)
    {
        string dir = StyleManager.AssetDir + "styles/" + setName + "/lemmings/";
        string? text = _io.Text(dir + "scheme.nxmi");
        string baseDir = dir;
        if (text == null) { baseDir = StyleManager.AssetDir + "styles/default/lemmings/"; text = _io.Text(baseDir + "scheme.nxmi"); }
        var nx = NxParser.Parse(text ?? "");
        var animsSec = nx.Section("ANIMATIONS");
        var recolor = nx.Section("STATE_RECOLORING");
        if (recolor != null)
        {
            foreach (string kind in new[] { "ATHLETE", "ZOMBIE", "NEUTRAL", "SELECTED" })
            {
                Recolor[kind.ToLowerInvariant()] = recolor.SectionsNamed(kind)
                    .Select(s => (NxParser.Color(s.Get("FROM")), NxParser.Color(s.Get("TO"))))
                    .Where(p => p.Item1 != null && p.Item2 != null)
                    .Select(p => (p.Item1!.Value, p.Item2!.Value)).ToList();
            }
        }
        foreach (string name in AnimNames)
        {
            var sec = animsSec?.Section(name);
            if (sec == null) continue;
            var image = _io.Image(baseDir + name + ".png");
            if (image == null) continue;
            int frameCount = sec.Int("FRAMES", 1);
            if (frameCount == 0) frameCount = 1; // `|| 1`
            int frameDiff = sec.Has("PEAK_FRAME") ? frameCount - sec.Int("PEAK_FRAME", 0) : frameCount - sec.Int("LOOP_TO_FRAME", 0);
            int w = image.Width >> 1, h = Util.JsMath.Floor(image.Height / (double)frameCount);
            SpriteSide Side(string dirName, int x0)
            {
                var d = sec.Section(dirName);
                var frames = new List<Bitmap>();
                for (int i = 0; i < frameCount; i++) frames.Add(image.Crop(x0, i * h, w, h));
                return new SpriteSide { FootX = d != null ? d.Int("FOOT_X", 0) : 0, FootY = d != null ? d.Int("FOOT_Y", 0) : 0, Frames = frames };
            }
            Anims[name] = new SpriteAnim { FrameCount = frameCount, FrameDiff = frameDiff, Width = w, Height = h, Right = Side("RIGHT", w), Left = Side("LEFT", 0) };
        }
        return this;
    }

    // The animation an action uses, and its frame count.
    public SpriteAnim? AnimationFor(int action)
    {
        string? name = action >= 0 && action < ActionSprites.Length ? ActionSprites[action] : null;
        return name != null && Anims.TryGetValue(name, out var a) ? a : null;
    }

    // The frame to draw for a lemming: `variant` is "normal", "athlete", "zombie" or "neutral",
    // with "+selected" appended for the lemming under the cursor, or "flat:RRGGBB" (clear
    // physics). Frames past the end wrap by FrameDiff, the way DrawThisLemming does.
    public Frame? Frame(int action, int dx, int frameIndex, string? variant)
    {
        var anim = AnimationFor(action);
        if (anim == null) return null;
        int f = frameIndex;
        int max = anim.FrameCount - 1;
        if (anim.FrameDiff > 0) while (f > max) f -= anim.FrameDiff;
        if (f > max || f < 0) f = ((f % anim.FrameCount) + anim.FrameCount) % anim.FrameCount;
        string v = string.IsNullOrEmpty(variant) ? "normal" : variant;
        string key = ActionSprites[action] + "/" + (dx > 0 ? "r" : "l") + "/" + f.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/" + v;
        lock (_variants) if (_variants.TryGetValue(key, out var cached)) return cached;
        var side = dx > 0 ? anim.Right : anim.Left;
        var bmp = side.Frames[f];
        if (v.StartsWith("flat:", StringComparison.Ordinal))
        {
            // clear physics: every pixel of the lemming in one colour (CombineLemmingPixels with ClearPhysics)
            bmp = Flat(bmp, ParseHex(v[5..]));
        }
        else
        {
            var pairs = new List<(int From, int To)>();
            foreach (string kind in v.Split('+'))
                if (kind != "normal" && Recolor.TryGetValue(kind, out var list)) pairs.AddRange(list);
            if (pairs.Count > 0) bmp = Recolored(bmp, pairs);
        }
        var frame = Engine.Frame.FromBitmap(bmp, -side.FootX, -side.FootY);
        lock (_variants) _variants[key] = frame;
        return frame;
    }

    // parseInt(s, 16): the leading hex digits (the variants are always six of them)
    static int ParseHex(string s)
    {
        int n = 0;
        foreach (char c in s)
        {
            int d = c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'f' ? c - 'a' + 10 : c >= 'A' && c <= 'F' ? c - 'A' + 10 : -1;
            if (d < 0) break;
            n = n * 16 + d;
        }
        return n;
    }

    // The sprite's shape in one colour.
    static Bitmap Flat(Bitmap bmp, int rgb)
    {
        var output = bmp.Clone();
        var d = output.Data;
        for (int i = 0; i < d.Length; i += 4)
        {
            if (d[i + 3] == 0) continue;
            d[i] = (byte)((rgb >> 16) & 255); d[i + 1] = (byte)((rgb >> 8) & 255); d[i + 2] = (byte)(rgb & 255);
        }
        return output;
    }

    // SwapColors: each swap is matched against the sprite's own colour; the last match wins.
    static Bitmap Recolored(Bitmap bmp, List<(int From, int To)> pairs)
    {
        var output = bmp.Clone();
        var d = output.Data;
        for (int i = 0; i < d.Length; i += 4)
        {
            if (d[i + 3] == 0) continue;
            int c = (d[i] << 16) | (d[i + 1] << 8) | d[i + 2];
            int to = -1;
            foreach (var pair in pairs) if (c == pair.From) to = pair.To;
            if (to >= 0) { d[i] = (byte)((to >> 16) & 255); d[i + 1] = (byte)((to >> 8) & 255); d[i + 2] = (byte)(to & 255); }
        }
        return output;
    }

    // ---- TGadgetAnimation.GeneratePickupSkills: the picture on a pickup skill is a lemming
    // sprite placed in a 24x24 box, plus bricks for the builders

    const int PickupSize = 24, PickupMid = PickupSize / 2 - 1, PickupBaseline = PickupSize / 2 + 7;
    static readonly Dictionary<string, (string Sprite, int Dx, int Frame, int Ox, int Oy)[]> PickupIcons = new(StringComparer.Ordinal)
    {
        ["WALKER"] = new[] { ("walker", 1, 1, 0, -1) }, ["JUMPER"] = new[] { ("jumper", 1, 0, 0, -3) }, ["SHIMMIER"] = new[] { ("shimmier", 1, 1, 0, -4) },
        ["SLIDER"] = new[] { ("slider", -1, 0, -2, -2) }, ["CLIMBER"] = new[] { ("climber", 1, 3, 3, -1) }, ["SWIMMER"] = new[] { ("swimmer", 1, 2, 1, -6) },
        ["FLOATER"] = new[] { ("floater", 1, 4, -1, 6) }, ["GLIDER"] = new[] { ("glider", 1, 4, -1, 6) }, ["DISARMER"] = new[] { ("disarmer", 1, 6, -2, -3) },
        ["BOMBER"] = new[] { ("ohnoer", 1, 7, 0, -3) }, ["STONER"] = new[] { ("stoner", 1, 0, 1, -1) }, ["BLOCKER"] = new[] { ("blocker", 1, 0, 0, -1) },
        ["PLATFORMER"] = new[] { ("platformer", 1, 1, 0, -4) }, ["BUILDER"] = new[] { ("builder", 1, 1, 0, -3) }, ["STACKER"] = new[] { ("stacker", 1, 0, 0, -2) },
        ["LASERER"] = new[] { ("laserer", 1, 0, 1, -2) }, ["BASHER"] = new[] { ("basher", 1, 0, 1, -2) }, ["FENCER"] = new[] { ("fencer", 1, 1, 0, -2) },
        ["MINER"] = new[] { ("miner", 1, 12, -3, -2) }, ["DIGGER"] = new[] { ("digger", 1, 4, 1, -4) },
        ["CLONER"] = new[] { ("walker", -1, 1, -1, -1), ("walker", 1, 1, 2, -1) },
    };
    static readonly Dictionary<string, (int X, int Y)[]> PickupBricks = new(StringComparer.Ordinal)
    {
        ["PLATFORMER"] = new[] { (-5, -4), (-3, -4), (-1, -4), (1, -4), (3, -4) },
        ["BUILDER"] = new[] { (-3, -2), (-1, -3), (1, -4), (3, -5) },
        ["STACKER"] = new[] { (2, -2), (2, -3), (2, -4), (2, -5), (2, -6), (2, -7) },
    };

    // Paint the pickup gadgets' skill pictures from the sprite set: frame 2i is the picked-up
    // look, 2i+1 the available one, each with the style's skill_mask erased out of it (or blank
    // when there is no mask). Game's `prepareLevel` hook: new Game(level, masks, l => SpriteSet.GeneratePickupIcons(l, sprites, l.Theme)).
    public static void GeneratePickupIcons(Level level, SpriteSet sprites, Theme theme)
    {
        int brick = StyleManager.ThemeColor(theme, "PICKUP_BRICKS");
        if (brick == StyleManager.ThemeColor(theme, "MASK")) brick = 0xffffff;
        var done = new HashSet<MetaAnimation>(ReferenceEqualityComparer.Instance);
        foreach (var g in level.Gadgets)
        {
            if (g.EffectBase != "PICKUP") continue;
            var primary = g.Meta.Base.Primary;
            if (primary == null || primary.Generated != "pickup" || done.Contains(primary)) continue;
            done.Add(primary);
            var eraser = g.Meta.Base.Animations.FirstOrDefault(a => a.Name == "SKILL_MASK");
            var frames = new List<Bitmap>();
            foreach (string name in LevelBuilder.Skills)
            {
                var icon = new Bitmap(PickupSize, PickupSize);
                if (PickupIcons.TryGetValue(name, out var parts))
                    foreach (var (sprite, dx, frameIndex, ox, oy) in parts)
                    {
                        if (!sprites.Anims.TryGetValue(sprite, out var anim)) continue;
                        var side = dx > 0 ? anim.Right : anim.Left;
                        var f = side.Frames[Math.Min(frameIndex, anim.FrameCount - 1)];
                        Pixels.Blit(icon, PickupMid + ox - side.FootX, PickupBaseline + oy - side.FootY, f, 0, 0, f.Width, f.Height, Pixels.MergeOver);
                    }
                if (PickupBricks.TryGetValue(name, out var bricks))
                    foreach (var (bx, by) in bricks)
                        for (int o = 0; o < 2; o++)
                        {
                            int x = PickupMid + bx + o, y = PickupBaseline + by;
                            if (x < 0 || y < 0 || x >= PickupSize || y >= PickupSize) continue;
                            int p = (y * PickupSize + x) * 4;
                            icon.Data[p] = (byte)((brick >> 16) & 255); icon.Data[p + 1] = (byte)((brick >> 8) & 255); icon.Data[p + 2] = (byte)(brick & 255); icon.Data[p + 3] = 255;
                        }
                var used = icon.Clone();
                if (eraser != null && eraser.Frames.Count >= 2)
                {
                    Erase(used, eraser.Frames[0]);
                    Erase(icon, eraser.Frames[1]);
                }
                else Array.Clear(used.Data);
                frames.Add(used);
                frames.Add(icon);
            }
            primary.SetFrames(frames, PickupSize, PickupSize);
            // variations already derived from the blank frames are rebuilt on demand
            g.Meta.ClearVariations();
            foreach (var h in level.Gadgets) if (h.Meta == g.Meta) h.V = g.Meta.Variation(h.Flip, h.Invert, h.Rotate);
        }
        foreach (var g in level.Gadgets)
        {
            if (g.EffectBase != "PICKUP") continue;
            for (int i = 0; i < g.Animations.Count; i++) if (i < g.V.Animations.Count) g.Animations[i].Meta = g.V.Animations[i];
            g.ClearFrameCache();
            // the web version sets the object's frames to [g.render()]; here the object renders its frames when asked
            g.Object?.ResetFrames();
        }
    }

    // Clear the pixels of `bmp` where `mask` has any.
    static void Erase(Bitmap bmp, Bitmap mask)
    {
        for (int y = 0; y < Math.Min(bmp.Height, mask.Height); y++)
            for (int x = 0; x < Math.Min(bmp.Width, mask.Width); x++)
                if (mask.Data[(y * mask.Width + x) * 4 + 3] != 0)
                {
                    int p = (y * bmp.Width + x) * 4;
                    bmp.Data[p] = bmp.Data[p + 1] = bmp.Data[p + 2] = bmp.Data[p + 3] = 0;
                }
    }

    // ---- game.js Lemming.prototype.render: what the display and the diorama draw for a lemming

    // The variant a lemming is drawn in: its state, "+selected" when it is the cursor's, or one
    // flat colour per state under clear physics (TRecolorImage.SwapColors with ClearPhysics).
    public static string VariantOf(Lemming L, bool selected, bool clearPhysics)
    {
        string variant = L.IsZombie ? "zombie" : L.IsNeutral ? "neutral" : L.HasPermanentSkills ? "athlete" : "normal";
        if (selected) variant += "+selected"; // the one the skill would go to
        if (clearPhysics)
        {
            int c = L.HasPermanentSkills ? 0x00FFFF : 0x0000FF;
            if (selected) c |= 0x7F0000;
            if (L.IsNeutral) c ^= 0xFFFFFF;
            if (L.IsZombie) c = (c | 0x007F00) & ~0x0000C0;
            variant = "flat:" + c.ToString("x6", System.Globalization.CultureInfo.InvariantCulture);
        }
        return variant;
    }

    // Lemming.render(display): `drawFrame(frame, x, y)` gets the sprite, then the countdown over
    // a lemming about to blow (DrawLemmingCountdown). The JS also skips a lemming without a
    // `game`; every lemming of a Game has one.
    public void RenderLemming(Game game, Lemming L, Action<Frame, int, int> drawFrame)
    {
        if (L.Removed || L.Teleporting) return;
        if (L.PortalWarpFrame >= 3 && L.PortalWarpFrame <= 4) return; // mid-warp
        string variant = VariantOf(L, game.CursorLemming == L, game.ClearPhysics);
        var frame = Frame(L.Action, L.Dx, L.Frame, variant);
        if (frame != null) drawFrame(frame, L.X, L.Y);
        if (L.ExplosionTimer > 0 && !L.HideCountdown)
        {
            int n = Util.JsMath.Floor(L.ExplosionTimer / 17.0) + 1;
            var digit = game.CountdownFrame(n);
            if (digit != null) drawFrame(digit, L.X - (L.Dx < 0 ? 2 : 1), L.Y - 17);
        }
    }
}
