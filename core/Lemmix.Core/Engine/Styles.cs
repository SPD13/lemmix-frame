using System.Text.Json;
using Lemmix.Io;
using Lemmix.Parse;
using Lemmix.Util;

namespace Lemmix.Engine;

// web/lemmix/js/styles.js - NeoLemmix styles: the graphics and metadata a level's pieces refer
// to, loaded lazily from neolemmix/styles/<style>/ and kept. Every piece is kept once in its
// natural orientation; rotated, flipped and inverted variations are derived on demand
// (LemMetaTerrain.DeriveVariation, LemGadgetsMeta.DeriveVariation). The JS loads through
// promises; the files here are local, so everything is synchronous with the same caching.

public sealed class Cut
{
    public int Left, Top, Right, Bottom;
    public Cut Copy() => (Cut)MemberwiseClone();
    public Pixels.Margins Margins => new(Left, Top, Right, Bottom);
}

public sealed class TerrainVariation
{
    public required Bitmap Image;
    public int Width, Height;
    public bool ResizeH, ResizeV;
    public int DefaultWidth, DefaultHeight;
    public required Cut Cut;
}

// A terrain piece's metadata and image, with its variations.
public sealed class MetaTerrain
{
    readonly Dictionary<int, TerrainVariation> _variations = new();

    public MetaTerrain(string gs, string piece, Bitmap image, NxSection? nxmt)
    {
        Gs = gs;
        Piece = piece;
        Steel = nxmt != null && nxmt.Has("STEEL");
        bool both = nxmt != null && nxmt.Has("RESIZE_BOTH");
        Base = new TerrainVariation
        {
            Image = image, Width = image.Width, Height = image.Height,
            ResizeH = both || nxmt != null && nxmt.Has("RESIZE_HORIZONTAL"),
            ResizeV = both || nxmt != null && nxmt.Has("RESIZE_VERTICAL"),
            DefaultWidth = nxmt != null ? nxmt.Int("DEFAULT_WIDTH", 0) : 0,
            DefaultHeight = nxmt != null ? nxmt.Int("DEFAULT_HEIGHT", 0) : 0,
            Cut = new Cut
            {
                Left = nxmt != null ? nxmt.Int("NINE_SLICE_LEFT", 0) : 0,
                Top = nxmt != null ? nxmt.Int("NINE_SLICE_TOP", 0) : 0,
                Right = nxmt != null ? nxmt.Int("NINE_SLICE_RIGHT", 0) : 0,
                Bottom = nxmt != null ? nxmt.Int("NINE_SLICE_BOTTOM", 0) : 0,
            },
        };
    }

    public string Gs { get; }
    public string Piece { get; }
    public bool Steel { get; }
    public TerrainVariation Base { get; }

    // The piece as drawn with these flags (rotate first, then flip, then invert).
    public TerrainVariation Variation(bool flip, bool invert, bool rotate)
    {
        int key = (flip ? 1 : 0) | (invert ? 2 : 0) | (rotate ? 4 : 0);
        if (key == 0) return Base;
        if (_variations.TryGetValue(key, out var cached)) return cached;
        var b = Base;
        var image = b.Image;
        if (rotate) image = image.Rotate90();
        if (flip) image = image.FlipHorizontal();
        if (invert) image = image.FlipVertical();
        var v = new TerrainVariation
        {
            Image = image, Width = image.Width, Height = image.Height,
            ResizeH = rotate ? b.ResizeV : b.ResizeH,
            ResizeV = rotate ? b.ResizeH : b.ResizeV,
            DefaultWidth = rotate ? b.DefaultHeight : b.DefaultWidth,
            DefaultHeight = rotate ? b.DefaultWidth : b.DefaultHeight,
            Cut = rotate
                ? new Cut { Left = b.Cut.Bottom, Top = b.Cut.Left, Right = b.Cut.Top, Bottom = b.Cut.Right }
                : b.Cut.Copy(),
        };
        if (flip) (v.Cut.Left, v.Cut.Right) = (v.Cut.Right, v.Cut.Left);
        if (invert) (v.Cut.Top, v.Cut.Bottom) = (v.Cut.Bottom, v.Cut.Top);
        _variations[key] = v;
        return v;
    }
}

public sealed record AnimationTrigger(string Condition, string State, bool Visible);

// One animation of a gadget: its frames and how they play.
public sealed class MetaAnimation
{
    public bool Primary;
    public string Name = "";
    public string Color = "";
    public int FrameCount;
    public bool HorizontalStrip;
    public int ZIndex;
    public int StartFrame;
    public int OffsetX, OffsetY;
    public Cut Cut = new();
    public int MainWidth, MainHeight;
    public int NxWidth, NxHeight; // *BLANK animations size themselves
    public List<Bitmap> Frames = new();
    public int Width, Height;
    public string BaseState = "play";
    public bool BaseVisible = true;
    public List<AnimationTrigger> Triggers = new();
    public string? Generated;

    MetaAnimation() { }

    public MetaAnimation(NxSection section, bool primary, int mainWidth, int mainHeight)
    {
        Primary = primary;
        Name = JsString.Upper(section.Get("NAME") ?? "");
        Color = JsString.Upper(section.Get("COLOR") ?? "");
        FrameCount = section.Int("FRAMES", 0) is var fc && fc != 0 ? fc : 1;
        HorizontalStrip = section.Has("HORIZONTAL_STRIP");
        ZIndex = primary && !section.Has("Z_INDEX") ? 1 : section.Int("Z_INDEX", 0);
        string initial = JsString.Upper(section.Get("INITIAL_FRAME") ?? "");
        StartFrame = initial == "RANDOM" ? -1 : section.Int("INITIAL_FRAME", 0);
        OffsetX = section.Int("OFFSET_X", 0);
        OffsetY = section.Int("OFFSET_Y", 0);
        Cut = new Cut
        {
            Left = section.Int("NINE_SLICE_LEFT", 0), Top = section.Int("NINE_SLICE_TOP", 0),
            Right = section.Int("NINE_SLICE_RIGHT", 0), Bottom = section.Int("NINE_SLICE_BOTTOM", 0),
        };
        MainWidth = mainWidth;
        MainHeight = mainHeight;
        NxWidth = section.Int("WIDTH", 0);
        NxHeight = section.Int("HEIGHT", 0);
        // how it plays until a trigger says otherwise
        string state = StyleManager.Lower(section.Get("STATE"));
        bool hide = section.Has("HIDE");
        BaseState = state == "pause" ? "pause" : state == "stop" ? "stop"
            : state == "looptozero" ? "looptozero" : state == "matchphysics" ? "matchphysics"
            : hide ? "pause" : "play";
        BaseVisible = !hide;
        if (primary) { BaseState = "pause"; BaseVisible = true; } // physics drive the primary
        if (!primary)
        {
            foreach (var t in section.SectionsNamed("TRIGGER"))
            {
                string cond = JsString.Upper(t.Get("CONDITION") ?? "");
                bool visible = !t.Has("HIDE");
                string st;
                if (!visible && !t.Has("STATE")) st = "pause";
                else
                {
                    string s = JsString.Upper(t.Get("STATE") ?? "");
                    st = s == "PAUSE" ? "pause" : s == "STOP" ? "stop" : s == "LOOPTOZERO" ? "looptozero"
                        : s == "MATCHPHYSICS" ? "matchphysics" : "play";
                }
                Triggers.Add(new AnimationTrigger(
                    cond is "READY" or "BUSY" or "DISABLED" or "EXHAUSTED" ? cond.ToLowerInvariant() : "unconditional",
                    st, visible));
            }
        }
    }

    public void SetFrames(List<Bitmap> frames, int width, int height)
    {
        Frames = frames;
        Width = width;
        Height = height;
        if (Primary) { MainWidth = width; MainHeight = height; }
    }

    // A transformed copy (TGadgetAnimation.Rotate90 / Flip / Invert).
    public MetaAnimation Transformed(bool flip, bool invert, bool rotate)
    {
        var a = (MetaAnimation)MemberwiseClone();
        a.Cut = Cut.Copy();
        a.Frames = Frames;
        if (rotate)
        {
            a.Frames = a.Frames.Select(f => f.Rotate90()).ToList();
            (a.Width, a.Height) = (a.Height, a.Width);
            (a.MainWidth, a.MainHeight) = (a.MainHeight, a.MainWidth);
            int oy = a.OffsetY; a.OffsetY = a.OffsetX; a.OffsetX = a.MainWidth - oy - a.Width;
            int t = a.Cut.Top; a.Cut.Top = a.Cut.Left; a.Cut.Left = a.Cut.Bottom; a.Cut.Bottom = a.Cut.Right; a.Cut.Right = t;
        }
        if (flip)
        {
            a.Frames = a.Frames.Select(f => f.FlipHorizontal()).ToList();
            a.OffsetX = a.MainWidth - a.OffsetX - a.Width;
            (a.Cut.Left, a.Cut.Right) = (a.Cut.Right, a.Cut.Left);
        }
        if (invert)
        {
            a.Frames = a.Frames.Select(f => f.FlipVertical()).ToList();
            a.OffsetY = a.MainHeight - a.OffsetY - a.Height;
            (a.Cut.Bottom, a.Cut.Top) = (a.Cut.Top, a.Cut.Bottom);
        }
        return a;
    }
}

public sealed class TriggerRect { public int X, Y, W, H; public TriggerRect Copy() => (TriggerRect)MemberwiseClone(); }
public sealed class DigitPlace { public int X, Y, Align; public DigitPlace Copy() => (DigitPlace)MemberwiseClone(); }

public sealed class GadgetVariation
{
    public required TriggerRect Trigger;
    public int DefaultWidth, DefaultHeight;
    public bool ResizeH, ResizeV;
    public required DigitPlace Digit;
    public List<MetaAnimation> Animations = new();
    public MetaAnimation? Primary;
    public int Width, Height;
}

// A gadget's metadata and animations, with its variations.
public sealed class MetaGadget
{
    static readonly HashSet<string> NoPositionAdjust = new() { "ONEWAYLEFT", "ONEWAYRIGHT", "ONEWAYDOWN", "ONEWAYUP" };
    readonly NxSection _nxmo;
    readonly Dictionary<int, GadgetVariation> _variations = new();

    public MetaGadget(string gs, string piece, NxSection nxmo)
    {
        Gs = gs;
        Piece = piece;
        string effect = JsString.Upper(nxmo.Get("EFFECT") ?? "");
        Effect = effect == "" ? "NONE" : effect;
        Effect = Effect switch
        {
            "TELEPORTER" => "TELEPORT", "ENTRANCE" => "WINDOW", "SPLITTER" => "FLIPPER", "PICKUPSKILL" => "PICKUP",
            "LOCKEDEXIT" => "LOCKEXIT", "UNLOCKBUTTON" => "BUTTON", "ANTISPLATPAD" => "NOSPLAT", "SPLATPAD" => "SPLAT",
            _ => Effect,
        };
        bool both = nxmo.Has("RESIZE_BOTH");
        SoundActivate = StyleManager.Or(nxmo.Get("SOUND_ACTIVATE"), nxmo.Get("SOUND"));
        SoundExhaust = StyleManager.Or(nxmo.Get("SOUND_EXHAUST"));
        KeyFrame = nxmo.Int("KEY_FRAME", 0);
        DigitMinLength = nxmo.Int("DIGIT_LENGTH", 1);
        _nxmo = nxmo;
        Base = new GadgetVariation
        {
            Trigger = new TriggerRect
            {
                X = nxmo.Int("TRIGGER_X", 0), Y = nxmo.Int("TRIGGER_Y", 0),
                W = nxmo.Int("TRIGGER_WIDTH", 0), H = nxmo.Int("TRIGGER_HEIGHT", 0),
            },
            DefaultWidth = nxmo.Int("DEFAULT_WIDTH", 0), DefaultHeight = nxmo.Int("DEFAULT_HEIGHT", 0),
            ResizeH = both || nxmo.Has("RESIZE_HORIZONTAL"), ResizeV = both || nxmo.Has("RESIZE_VERTICAL"),
            Digit = new DigitPlace { X = 0, Y = nxmo.Int("DIGIT_Y", -6), Align = 0 },
        };
        string align = StyleManager.Lower(nxmo.Get("DIGIT_ALIGNMENT"));
        char a0 = align.Length > 0 ? align[0] : '\0';
        Base.Digit.Align = a0 == 'l' ? -1 : a0 == 'r' ? 1 : 0;
        if (Effect is "NONE" or "BACKGROUND" or "PAINT") { Base.Trigger.W = 0; Base.Trigger.H = 0; }
        if (Effect is "RECEIVER" or "WINDOW") { Base.Trigger.W = 1; Base.Trigger.H = 1; }
    }

    public string Gs { get; }
    public string Piece { get; }
    public string Effect { get; }
    public string SoundActivate { get; }
    public string SoundExhaust { get; }
    public int KeyFrame { get; }
    public int DigitMinLength { get; }
    public GadgetVariation Base { get; }

    // Called once the animation images are in.
    public void Finish(List<MetaAnimation> animations)
    {
        var b = Base;
        b.Animations = animations.OrderBy(a => a.ZIndex).ToList(); // stable, like Array.prototype.sort
        b.Primary = animations.First(a => a.Primary);
        b.Width = b.Primary.Width;
        b.Height = b.Primary.Height;
        b.Digit.X = !_nxmo.Has("DIGIT_X") ? b.Width >> 1 : _nxmo.Int("DIGIT_X", 0);
    }

    // sprites.js generatePickupIcons: `g.meta._variations.clear()`
    public void ClearVariations() => _variations.Clear();

    public GadgetVariation Variation(bool flip, bool invert, bool rotate)
    {
        int key = (flip ? 1 : 0) | (invert ? 2 : 0) | (rotate ? 4 : 0);
        if (key == 0) return Base;
        if (_variations.TryGetValue(key, out var cached)) return cached;
        var s = Base;
        var v = new GadgetVariation
        {
            Trigger = s.Trigger.Copy(),
            DefaultWidth = s.DefaultWidth, DefaultHeight = s.DefaultHeight,
            ResizeH = s.ResizeH, ResizeV = s.ResizeV,
            Digit = s.Digit.Copy(),
            Animations = s.Animations.Select(a => a.Transformed(flip, invert, rotate)).ToList(),
        };
        v.Primary = v.Animations.First(a => a.Primary);
        v.Width = v.Primary.Width;
        v.Height = v.Primary.Height;
        bool adjust = !NoPositionAdjust.Contains(Effect);
        if (rotate)
        {
            v.Trigger.X = s.Primary!.Height - s.Trigger.Y - s.Trigger.H;
            v.Trigger.Y = s.Trigger.X;
            if (adjust) { v.Trigger.X += 4; v.Trigger.Y += 5; }
            v.Trigger.W = s.Trigger.H;
            v.Trigger.H = s.Trigger.W;
            v.DefaultWidth = s.DefaultHeight; v.DefaultHeight = s.DefaultWidth;
            v.ResizeH = s.ResizeV; v.ResizeV = s.ResizeH;
            v.Digit.Align = 0;
            v.Digit.X = s.Primary.Height - s.Digit.Y - 1;
            v.Digit.Y = s.Digit.X;
        }
        if (flip)
        {
            v.Trigger.X = v.Width - v.Trigger.X - v.Trigger.W;
            v.Digit.X = v.Width - v.Digit.X - 1;
            v.Digit.Align = -v.Digit.Align;
        }
        if (invert)
        {
            v.Trigger.Y = v.Height - v.Trigger.Y - v.Trigger.H;
            if (adjust) v.Trigger.Y += 10;
            v.Digit.Y = v.Height - v.Digit.Y - 1;
        }
        _variations[key] = v;
        return v;
    }
}

public sealed record PieceId(string Gs, string Piece);
public sealed record StyleAlias(string Kind, PieceId From, PieceId To, int Width, int Height);

public sealed class Theme
{
    public string Lemmings = "default";
    public Dictionary<string, int> Colors = new(StringComparer.Ordinal);
}

public sealed class StyleInfo
{
    public required string Name;
    public required Theme Theme;
    public required List<StyleAlias> Aliases;
    public bool Exists;
}

public sealed record Dealiased(string Gs, string Piece, int DefWidth, int DefHeight);

public sealed class StyleManager
{
    public const string AssetDir = "neolemmix/";
    const string StylesDir = AssetDir + "styles/";
    const int PickupAutoGfxSize = 24;
    const int SkillButtonCount = 21; // TSkillPanelButton, walker .. cloner
    const int ThemeDefaultColor = 0x808080; // TNeoTheme DEFAULT_COLOR

    readonly IFileSource _io;
    readonly Dictionary<string, StyleInfo> _styles = new();
    readonly Dictionary<string, MetaTerrain?> _terrain = new();
    readonly Dictionary<string, MetaGadget?> _gadgets = new();
    readonly Dictionary<string, Bitmap?> _images = new();
    Dictionary<string, JsonElement>? _index;
    bool _indexLoaded;
    readonly Dictionary<string, HashSet<string>> _metaSets = new();

    public StyleManager(IFileSource io) { _io = io; }

    public IFileSource Io => _io;
    public HashSet<string> Missing { get; } = new(); // "gs:piece" references that fell back

    // String(s || "").trim().toLowerCase()
    public static string Lower(string? s) => JsString.Trim(s ?? "").ToLowerInvariant();

    // a || b || "" over strings, where "" is falsy
    public static string Or(params string?[] values)
    {
        foreach (var v in values) if (!string.IsNullOrEmpty(v)) return v;
        return "";
    }

    // The styles index (tools/styles-index.js), by style name, or null without one.
    Dictionary<string, JsonElement>? Index()
    {
        if (_indexLoaded) return _index;
        _indexLoaded = true;
        string? text = _io.Text(StylesDir + "index.json");
        if (text == null) return null;
        try
        {
            var byName = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("styles", out var styles) && styles.ValueKind == JsonValueKind.Array)
                foreach (var s in styles.EnumerateArray())
                    byName[Lower(s.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : "")] = s.Clone();
            _index = byName;
        }
        catch (JsonException) { _index = null; }
        return _index;
    }

    // Whether a style has an optional file, from the index; null when unknown.
    bool? Has(string gs, string what, string? piece = null)
    {
        var byName = Index();
        if (byName == null || !byName.TryGetValue(Lower(gs), out var entry)) return null;
        if (what != "meta")
            return entry.TryGetProperty(what, out var b) && (b.ValueKind == JsonValueKind.True || b.ValueKind == JsonValueKind.False) ? b.GetBoolean() : null;
        if (!entry.TryGetProperty("metas", out var metas) || metas.ValueKind != JsonValueKind.Array) return null;
        string key = Lower(gs);
        if (!_metaSets.TryGetValue(key, out var set))
        {
            set = new HashSet<string>(metas.EnumerateArray().Select(m => Lower(m.ValueKind == JsonValueKind.String ? m.GetString() : m.ToString())), StringComparer.Ordinal);
            _metaSets[key] = set;
        }
        return set.Contains(Lower(piece));
    }

    Bitmap? CachedImage(string url)
    {
        if (!_images.TryGetValue(url, out var img)) { img = _io.Image(url); _images[url] = img; }
        return img;
    }

    // A style's theme and aliases (an unknown style resolves with Exists false).
    public StyleInfo Style(string name)
    {
        name = Lower(name);
        if (_styles.TryGetValue(name, out var cached)) return cached;
        string dir = StylesDir + name + "/";
        bool? hasTheme = Has(name, "hasTheme"), hasAlias = Has(name, "hasAlias");
        string? themeText = hasTheme == false ? null : _io.Text(dir + "theme.nxtm");
        string? aliasText = hasAlias == false ? null : _io.Text(dir + "alias.nxmi");
        var theme = new Theme();
        if (themeText != null)
        {
            var nx = NxParser.Parse(themeText);
            string lem = Lower(nx.Get("LEMMINGS"));
            theme.Lemmings = lem == "" ? "default" : lem;
            var colors = nx.Section("COLORS");
            if (colors != null)
                foreach (var e in colors.Entries)
                {
                    int? c = NxParser.Color(e.Value);
                    if (c != null) theme.Colors[e.Key] = c.Value;
                }
        }
        var aliases = new List<StyleAlias>();
        if (aliasText != null)
        {
            var nx = NxParser.Parse(aliasText);
            foreach (var sec in nx.Sections)
            {
                string? kind = sec.Name switch
                {
                    "STYLE" => "style", "GADGET" => "gadget", "TERRAIN" => "terrain", "BACKGROUND" => "background", "LEMMINGS" => "lemmings",
                    _ => null,
                };
                if (kind == null) continue;
                PieceId? from = SplitIdentifier(sec.Get("FROM"), name), to = SplitIdentifier(sec.Get("TO"), name);
                if (from == null || to == null) continue;
                aliases.Add(new StyleAlias(kind, from, to, sec.Int("WIDTH", 0), sec.Int("HEIGHT", 0)));
            }
        }
        var style = new StyleInfo { Name = name, Theme = theme, Aliases = aliases, Exists = themeText != null };
        _styles[name] = style;
        return style;
    }

    // A theme colour by name (TNeoTheme.GetColor's fallbacks).
    public static int ThemeColor(Theme theme, string? name)
    {
        name = JsString.Upper(name ?? "");
        if (theme.Colors.TryGetValue(name, out int c)) return c;
        if (name == "BACKGROUND") return 0x000000;
        if (theme.Colors.TryGetValue("MASK", out int m)) return m;
        return ThemeDefaultColor;
    }

    // Follow renames until the name stops changing (LemNeoPieceManager.Dealias).
    public Dealiased Dealias(string gs, string piece, string kind)
    {
        var cur = new PieceId(Lower(gs), Lower(piece));
        int defWidth = 0, defHeight = 0;
        for (int guard = 0; guard < 16; guard++)
        {
            var last = cur;
            var style = Style(cur.Gs);
            foreach (var a in style.Aliases)
            {
                if (a.From.Gs != cur.Gs) continue;
                if (a.Kind == kind && a.From.Piece == cur.Piece)
                {
                    cur = new PieceId(a.To.Gs, a.To.Piece);
                    defWidth = a.Width; defHeight = a.Height;
                }
            }
            foreach (var a in style.Aliases)
                if (a.From.Gs == cur.Gs && a.Kind == "style") cur = new PieceId(a.To.Gs, cur.Piece);
            if (cur.Gs == last.Gs && cur.Piece == last.Piece) break;
        }
        return new Dealiased(cur.Gs, cur.Piece, defWidth, defHeight);
    }

    // A terrain piece, or null when the style has no such piece.
    public MetaTerrain? Terrain(string gs, string piece)
    {
        string key = Lower(gs) + ":" + Lower(piece);
        if (_terrain.TryGetValue(key, out var cached)) return cached;
        string dir = StylesDir + Lower(gs) + "/terrain/" + Lower(piece);
        bool? hasMeta = Has(gs, "meta", piece);
        var image = CachedImage(dir + ".png");
        string? nxmtText = hasMeta == false ? null : _io.Text(dir + ".nxmt");
        var t = image == null ? null : new MetaTerrain(Lower(gs), Lower(piece), image, nxmtText != null ? NxParser.Parse(nxmtText) : null);
        _terrain[key] = t;
        return t;
    }

    // A gadget, or null when the style has no such gadget.
    public MetaGadget? Gadget(string gs, string piece)
    {
        string key = Lower(gs) + ":" + Lower(piece);
        if (_gadgets.TryGetValue(key, out var cached)) return cached;
        MetaGadget? meta = null;
        string dir = StylesDir + Lower(gs) + "/objects/" + Lower(piece);
        string? nxmoText = _io.Text(dir + ".nxmo");
        if (nxmoText != null)
        {
            var nxmo = NxParser.Parse(nxmoText);
            var primarySection = nxmo.Section("PRIMARY_ANIMATION");
            if (primarySection != null) // else the pre-12.7 format
            {
                meta = new MetaGadget(Lower(gs), Lower(piece), nxmo);
                var style = Style(gs);
                var primary = new MetaAnimation(primarySection, true, 0, 0);
                LoadAnimation(primary, dir, style.Theme);
                var animations = new List<MetaAnimation> { primary };
                foreach (var sec in nxmo.SectionsNamed("ANIMATION"))
                {
                    var anim = new MetaAnimation(sec, false, primary.Width, primary.Height);
                    LoadAnimation(anim, dir, style.Theme);
                    animations.Add(anim);
                }
                meta.Finish(animations);
            }
        }
        _gadgets[key] = meta;
        return meta;
    }

    void LoadAnimation(MetaAnimation anim, string dir, Theme theme)
    {
        List<Bitmap> frames;
        int width, height;
        if (!anim.Name.StartsWith('*'))
        {
            string url = dir + (anim.Name != "" ? "_" + anim.Name.ToLowerInvariant() : "") + ".png";
            var image = CachedImage(url);
            if (image == null)
            {
                frames = new List<Bitmap> { new Bitmap(1, 1) }; width = 1; height = 1; anim.FrameCount = 1;
            }
            else
            {
                if (anim.HorizontalStrip) { width = JsMath.Floor(image.Width / (double)anim.FrameCount); height = image.Height; }
                else { width = image.Width; height = JsMath.Floor(image.Height / (double)anim.FrameCount); }
                frames = image.Frames(anim.FrameCount, anim.HorizontalStrip);
            }
        }
        else if (anim.Name == "*BLANK")
        {
            width = anim.NxWidth != 0 ? anim.NxWidth : 1; height = anim.NxHeight != 0 ? anim.NxHeight : 1;
            frames = new List<Bitmap>();
            for (int i = 0; i < anim.FrameCount; i++) frames.Add(new Bitmap(width, height));
        }
        else if (anim.Name == "*PICKUP")
        {
            // the skill pictures are painted from the lemming sprites; until then the pickup is
            // its coloured frame alone (as the web version does)
            width = PickupAutoGfxSize; height = PickupAutoGfxSize;
            anim.FrameCount = SkillButtonCount * 2;
            frames = new List<Bitmap>();
            for (int i = 0; i < anim.FrameCount; i++) frames.Add(new Bitmap(width, height));
            anim.Generated = "pickup";
        }
        else
        {
            frames = new List<Bitmap> { new Bitmap(1, 1) }; width = 1; height = 1; anim.FrameCount = 1;
        }
        if (anim.Color != "")
        {
            // MaskImageFromImage: the image tinted by the theme colour, merged over itself
            int rgb = ThemeColor(theme, anim.Color);
            frames = frames.Select(f =>
            {
                var output = f.Clone();
                var tint = f.Tinted(rgb);
                Pixels.Blit(output, 0, 0, tint, 0, 0, f.Width, f.Height, Pixels.MergeOver);
                return output;
            }).ToList();
        }
        anim.SetFrames(frames, width, height);
    }

    // A background image, or null.
    public Bitmap? Background(string gs, string name) => CachedImage(StylesDir + Lower(gs) + "/backgrounds/" + Lower(name) + ".png");

    // "style:piece" (or ":piece" within defaultGs) into its two halves.
    public static PieceId? SplitIdentifier(string? id, string defaultGs)
    {
        if (string.IsNullOrEmpty(id)) return null;
        int i = id.IndexOf(':');
        if (i < 0) return new PieceId(Lower(defaultGs), Lower(id));
        return new PieceId(Lower(i == 0 ? defaultGs : id[..i]), Lower(id[(i + 1)..]));
    }
}
