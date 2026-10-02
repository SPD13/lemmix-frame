using Lemmix.Util;

namespace Lemmix.Engine;

// physics map bits (LemRenderHelpers.pas)
public static class PM
{
    public const int SOLID = 0x0001, STEEL = 0x0002, ONEWAY = 0x0004, ONEWAYLEFT = 0x0008, ONEWAYRIGHT = 0x0010,
        ONEWAYDOWN = 0x0020, ONEWAYUP = 0x0040, NOCANCELSTEEL = 0x0080, ORIGSOLID = 0x0100;
    public const int TERRAIN = 0x01ff;
}

// A drawn frame (Lemmings.Frame / the node stand-in): ABGR words, a solidity mask and an offset.
public sealed class Frame
{
    public Frame(int width, int height, int offsetX, int offsetY)
    {
        Width = width; Height = height; OffsetX = offsetX; OffsetY = offsetY;
        Data = new uint[width * height];
        Mask = new sbyte[width * height];
    }
    public int Width { get; }
    public int Height { get; }
    public int OffsetX { get; }
    public int OffsetY { get; }
    public uint[] Data { get; }
    public sbyte[] Mask { get; }

    public static Frame FromBitmap(Bitmap bmp, int offsetX, int offsetY)
    {
        var frame = new Frame(bmp.Width, bmp.Height, offsetX, offsetY);
        bmp.Words().CopyTo(frame.Data);
        var d = bmp.Data;
        for (int i = 0, p = 3; i < frame.Mask.Length; i++, p += 4) frame.Mask[i] = (sbyte)(d[p] != 0 ? 1 : 0);
        return frame;
    }
}

public sealed record TriggerArea(int X0, int Y0, int X1, int Y1);

public sealed class GadgetAnimationState
{
    public required MetaAnimation Meta;
    public int Frame;
    public string State = "play";
    public bool Visible;
    public bool Primary;
}

// The MapObject shape the 3D layer draws (gadgetAsObject).
public sealed class GadgetObject
{
    public required Gadget Gadget;
    public int X, Y;
    public bool Behind, Decal, Low, OneWay; // drawProperties: noOverwrite (behind), onlyOverwrite (decal), low, oneWay
    List<Frame>? _frames;
    // every frame of the primary animation, rendered when first asked for (the JS renders them at build)
    public List<Frame> Frames
    {
        get
        {
            if (_frames != null) return _frames;
            var g = Gadget;
            var frames = new List<Frame>();
            int saved = g.CurrentFrame;
            for (int f = 0; f < g.FrameCount; f++) { g.CurrentFrame = f; frames.Add(g.Render()); }
            g.CurrentFrame = saved;
            return _frames = frames;
        }
    }
    public Frame CurrentFrame() => Gadget.Render();
    // forget the rendered frames (the pickup pictures were painted after they were made)
    public void ResetFrames() => _frames = null;
}

// A placed gadget: its metadata variation, trigger area and animation state (level.js Gadget).
public sealed class Gadget
{
    readonly Dictionary<string, Frame> _frameCache = new(StringComparer.Ordinal);

    public Gadget(GadgetSpec spec, MetaGadget meta, GadgetVariation v, int index, Func<double> rand)
    {
        Spec = spec;
        Meta = meta;
        V = v;
        Index = index;
        X = spec.X;
        Y = spec.Y;
        Flip = spec.Flip; Invert = spec.Invert; Rotate = spec.Rotate;
        NoOverwrite = spec.NoOverwrite;
        OnlyOnTerrain = spec.OnlyOnTerrain;
        Width = LevelBuilder.EvaluateResizable(spec.Width, v.DefaultWidth, v.Width, v.ResizeH);
        Height = LevelBuilder.EvaluateResizable(spec.Height, v.DefaultHeight, v.Height, v.ResizeV);
        WidthVariance = Width - v.Width;
        HeightVariance = Height - v.Height;
        EffectBase = meta.Effect;
        Effect = LevelBuilder.AdjustOwwDirection(meta.Effect, spec);
        if (spec.Flip)
        {
            if (Effect == "FORCELEFT") Effect = "FORCERIGHT";
            else if (Effect == "FORCERIGHT") Effect = "FORCELEFT";
        }
        LemmingCap = spec.LemmingCap;
        Pairing = spec.Pairing;
        SkillName = spec.Skill;
        Skill = Array.IndexOf(LevelBuilder.Skills, spec.Skill); // -1 = none
        SkillCount = spec.SkillCount;
        Presets = spec.Presets;
        FlipLemming = spec.Flip || spec.Direction == "l"; // windows face left; splitters start left
        if (meta.Effect == "FLIPPER") FlipLemming = spec.Direction == "l";
        AngleSegment = (((int)JsMath.Round(spec.Angle / 22.5) % 16) + 16) % 16;
        Speed = spec.Speed;
        RemainingLemmings = LemmingCap > 0 ? LemmingCap : -1;
        OffMap = spec.X <= -30000 || spec.Y <= -30000;
        TriggerRect = ComputeTriggerRect();
        // animation instances (TGadgetAnimationInstance.Create)
        Animations = v.Animations.Select(a =>
        {
            int frame;
            if (a.StartFrame < 0) frame = JsMath.Floor(rand() * a.FrameCount);
            else frame = a.StartFrame < a.FrameCount ? a.StartFrame : 0;
            if (a.Primary)
            {
                if (meta.Effect is "PICKUP" or "ADDSKILL") frame = Math.Max(Skill, 0) * 2 + 1;
                if (meta.Effect is "LOCKEXIT" or "BUTTON" or "WINDOW" or "TRAPONCE" or "ANIMONCE") frame = 1;
                if (meta.Effect == "FLIPPER" && FlipLemming) frame = 1;
            }
            return new GadgetAnimationState { Meta = a, Frame = frame, State = a.BaseState, Visible = a.BaseVisible, Primary = a.Primary };
        }).ToList();
        PrimaryAnimation = Animations.FirstOrDefault(a => a.Primary);
    }

    public GadgetSpec Spec { get; }
    public MetaGadget Meta { get; }
    public GadgetVariation V { get; set; } // set by SpriteSet.GeneratePickupIcons (sprites.js `h.v = ...`)
    public int Index { get; }
    public int X, Y;
    public bool Flip, Invert, Rotate, NoOverwrite, OnlyOnTerrain;
    public int Width, Height, WidthVariance, HeightVariance;
    public string EffectBase;
    public string Effect;
    public int LemmingCap, Pairing;
    public string SkillName;
    public int Skill, SkillCount;
    public GadgetPresets Presets;
    public bool FlipLemming;
    public int AngleSegment, Speed;
    public int ReceiverId = -1, PairingId = -1;
    public int RemainingLemmings;
    public bool HoldActive, Triggered, SecondariesTreatAsBusy;
    public bool OffMap;
    public TriggerArea TriggerRect;
    public List<GadgetAnimationState> Animations;
    public GadgetAnimationState? PrimaryAnimation;
    public GadgetObject? Object;
    // LemGame keeps per-gadget state of its own on the gadget (teleLem, zombieMode, ...)
    public int TeleLem = -1;
    public bool ZombieMode, NeutralMode;

    public int CurrentFrame
    {
        get => PrimaryAnimation?.Frame ?? 0;
        set { if (PrimaryAnimation != null) PrimaryAnimation.Frame = value; }
    }
    public int FrameCount => PrimaryAnimation?.Meta.FrameCount ?? 1;

    // TGadget.GetTriggerRect - half-open: right and bottom lines excluded.
    public TriggerArea ComputeTriggerRect()
    {
        var v = V;
        int x = X + v.Trigger.X, y = Y + v.Trigger.Y, w = v.Trigger.W, h = v.Trigger.H;
        if (v.ResizeH) w += Width - v.Width;
        if (v.ResizeV) h += Height - v.Height;
        if (EffectBase == "RECEIVER")
        {
            if (w > 1 || h > 1)
            {
                x += w >> 1;
                y = Y + Math.Min(Height, v.Trigger.Y + h - 1);
            }
            w = 1; h = 1;
        }
        return new TriggerArea(x, y, x + w, y + h);
    }

    // The number NeoLemmix writes on this gadget, if any (pickup count, exit/window capacity).
    public (int Value, int Min)? Digits()
    {
        if (Effect == "PICKUP" || EffectBase == "PICKUP")
        {
            if (SkillCount > 1 || Meta.DigitMinLength >= 1) return (SkillCount, Meta.DigitMinLength);
        }
        else if (EffectBase is "EXIT" or "LOCKEXIT" or "WINDOW" && RemainingLemmings >= 0)
            return (RemainingLemmings, Meta.DigitMinLength);
        return null;
    }

    // sprites.js generatePickupIcons: `g._frameCache.clear()`
    public void ClearFrameCache() => _frameCache.Clear();

    // The composite picture of every visible animation, at this moment.
    public Frame Render()
    {
        var digits = LevelBuilder.DigitFont != null ? Digits() : null;
        string digitText = digits is { } dg && (dg.Value > 0 || dg.Min > 0) ? dg.Value.ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(dg.Min, '0') : "";
        string key = string.Join(",", Animations.Select(a => a.Visible || a.State != "pause" ? a.Frame.ToString(System.Globalization.CultureInfo.InvariantCulture) : "-")) + "|" + digitText;
        if (_frameCache.TryGetValue(key, out var cached)) return cached;
        // the composite spans every animation's box, offsets included
        int x0 = 0, y0 = 0, x1 = Width, y1 = Height;
        (int Left, int Top, int Right, int Bottom) digitBox = default;
        if (digitText != "")
        {
            // DrawNumberWithCountdownDigits: 4x5 digits 5 px apart, at DIGIT_X/Y with the alignment
            int dw = digitText.Length * 5;
            int dx = V.Digit.X, dy = V.Digit.Y - 2;
            int left = V.Digit.Align < 0 ? dx : V.Digit.Align > 0 ? dx - dw + 1 : dx - (dw >> 1) + 1;
            digitBox = (left - 1, dy, left + dw + 1, dy + 7);
            x0 = Math.Min(x0, digitBox.Left); y0 = Math.Min(y0, digitBox.Top);
            x1 = Math.Max(x1, digitBox.Right); y1 = Math.Max(y1, digitBox.Bottom);
        }
        foreach (var a in Animations)
        {
            x0 = Math.Min(x0, a.Meta.OffsetX); y0 = Math.Min(y0, a.Meta.OffsetY);
            x1 = Math.Max(x1, a.Meta.OffsetX + a.Meta.Width + WidthVariance);
            y1 = Math.Max(y1, a.Meta.OffsetY + a.Meta.Height + HeightVariance);
        }
        var bmp = new Bitmap(x1 - x0, y1 - y0);
        foreach (var a in Animations)
        {
            if (!a.Visible && a.State == "pause") continue;
            var src = a.Meta.Frames[a.Frame % a.Meta.Frames.Count];
            Pixels.DrawNineSlice(bmp, a.Meta.OffsetX - x0, a.Meta.OffsetY - y0,
                a.Meta.Width + WidthVariance, a.Meta.Height + HeightVariance, src, a.Meta.Cut.Margins, Pixels.CombineGadget);
        }
        if (digitText != "")
        {
            var font = LevelBuilder.DigitFont!;
            int cx = digitBox.Left + 1 - x0;
            foreach (char ch in digitText)
            {
                int d = ch - 48;
                void Shadow(int ox, int oy) => Pixels.Blit(bmp, cx + ox, digitBox.Top + 1 + oy - y0, font, d * 4, 0, 4, 5, (fd, fi, bd, bi) =>
                {
                    if (fd[fi + 3] != 0) { bd[bi] = 0x20; bd[bi + 1] = 0x20; bd[bi + 2] = 0x20; bd[bi + 3] = 255; }
                });
                Shadow(-1, 1); Shadow(0, 0); Shadow(0, 1);
                Pixels.Blit(bmp, cx - 1, digitBox.Top + 1 - y0, font, d * 4, 0, 4, 5, Pixels.MergeOver);
                cx += 5;
            }
        }
        var frame = Frame.FromBitmap(bmp, x0, y0);
        _frameCache[key] = frame;
        return frame;
    }
}

// level.js SolidLayerFallback (the classic SolidLayer behaves the same)
public sealed class SolidLayer
{
    public SolidLayer(int width, int height, sbyte[] mask) { Width = width; Height = height; GroundMask = mask; }
    public int Width { get; }
    public int Height { get; }
    public sbyte[] GroundMask { get; }
    public bool HasGroundAt(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height && GroundMask[x + y * Width] != 0;
    public void SetGroundAt(int x, int y) => GroundMask[x + y * Width] = 1;
    public void ClearGroundAt(int x, int y) => GroundMask[x + y * Width] = 0;
}

public sealed record DrawnPiece(string Key, string VariantKey, Bitmap Image, int Width, int Height, bool Steel);
public sealed record PiecePlacement(int X, int Y, DrawnPiece Drawn, bool NoOverwrite, bool Erase, bool OneWay);
public sealed record SkillCount(string Name, int Count);
public sealed record LevelBackground(int Color, Bitmap? Image);

public sealed class Level
{
    public Level(int width, int height) { Width = width; Height = height; }

    public int Width { get; }
    public int Height { get; }
    public string Name = "";
    public LevelInfo Info = new();
    public Theme Theme = new();
    public string ThemeName = "";
    public byte[] GroundImage = Array.Empty<byte>(); // RGBA bytes, width*height*4
    public SolidLayer GroundMask = null!;
    public ushort[] Physics = Array.Empty<ushort>();  // PM bits
    public List<GadgetObject> Objects = new();        // gadgets on the map, for drawing
    public List<Gadget> Gadgets = new();              // every gadget, for physics
    public List<Gadget> Entrances = new();            // window gadgets
    public List<PiecePlacement> Pieces = new();
    public List<LemmingSpec> Preplaced = new();
    public List<Talisman> Talismans = new();
    public List<string> Pretext = new(), Posttext = new();
    public LevelBackground Background = new(0, null);
    public int ReleaseCount, NeedCount, ZombieCount, NeutralCount;
    public List<int> SpawnOrder = new();
    public int SpawnInterval;
    public bool SpawnLocked;
    public int TimeLimitSeconds;
    public List<SkillCount> Skills = new();
    public List<string> MissingPieces = new();
    public int StartX, StartY, ScreenPositionX;

    public bool HasGroundAt(int x, int y) => GroundMask.HasGroundAt(x, y);
    public bool HasSteelAt(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height && (Physics[x + y * Width] & PM.STEEL) != 0;
    public int OneWayAt(int x, int y)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height) return 0;
        return Physics[x + y * Width] & (PM.ONEWAYLEFT | PM.ONEWAYRIGHT | PM.ONEWAYDOWN | PM.ONEWAYUP);
    }

    // Raised on every picture change, so a renderer can follow (terrain.js wraps these in the web version).
    public event Action<int, int>? GroundChanged;

    // Add a pixel of ground in this ABGR colour (a brick, a stoner).
    public void SetGroundAt(int x, int y, uint color)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height) return;
        int i = x + y * Width;
        GroundMask.SetGroundAt(x, y);
        Physics[i] |= PM.SOLID;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(GroundImage.AsSpan(i * 4, 4), color);
        GroundChanged?.Invoke(x, y);
    }

    public void ClearGroundAt(int x, int y)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height) return;
        int i = x + y * Width;
        GroundMask.ClearGroundAt(x, y);
        Physics[i] = (ushort)(Physics[i] & ~PM.TERRAIN);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(GroundImage.AsSpan(i * 4, 4), 0);
        GroundChanged?.Invoke(x, y);
    }
}
