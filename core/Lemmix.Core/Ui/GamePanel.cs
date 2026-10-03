using Lemmix.Engine;
using Lemmix.Io;

namespace Lemmix.Ui;

// web/lemmix/js/panel.js - the skill panel of a Lemmix game, drawn into the PixelCanvas the
// host hands over (GameBaseSkillPanel.pas, on a canvas the shape of the DOS panel's): an info
// strip along the top in the 8x16 panel font, then 16-px buttons from x = 0 - release rate down
// and up, ten skill slots (the level's skills with their counts in the 4x8 skill digits, the
// rest empty slots), pause, nuke, speed, then the four cells NeoLemmix's standard panel ends
// with - replay (restart), frame back over frame forward, direction left over direction right,
// clear physics over load replay (a split cell answers by its upper or lower half, y 16..26 and
// 28..37, with y 27 between them) - and after them NeoLemmix's minimap frame
// (minimap_region.png, 111x38 around a 104x34 window). The map itself is Minimap's; the panel
// only says where its window is (Layout.Minimap). Skill pictures are the lemming sprites.
//
// The info strip is NeoLemmix's 38-column string (CreateNewInfoString): the lemming under the
// pointer, the replay mark, the hatch, alive and saved counts behind their icons, the clock.
//
// The toolbar embosses the pictures and the counts; the panel says which pixels it drew as a
// picture and which as a digit (Layout.ReliefMasks).
//
// Presses come in through the canvas's mouse events and turn into the DOS commands the replay
// records. The hold-repeat of frame back/forward reads an injected clock (the JS reads
// performance.now()); the host calls Poll(now) every frame.

// The gfx/panel bitmaps (or a pack's own), by file name without ".png".
public sealed class PanelAssets
{
    public static readonly string[] PanelFiles =
    {
        "skill_panels", "empty_slot", "skill_count_digits", "skill_count_erase", "skill_selected",
        "icon_rr_minus", "icon_rr_plus", "icon_pause", "icon_nuke", "icon_ff", "icon_restart", "icon_frameskip",
        "icon_directional", "icon_cpm_and_replay", "panel_font", "panel_icons", "minimap_region",
    };
    static readonly HashSet<string> OptionalFiles = new(StringComparer.Ordinal) { "minimap_region" }; // drawn by hand when missing

    public readonly Dictionary<string, Bitmap?> Images = new(StringComparer.Ordinal);
    public Bitmap? this[string name] => Images.TryGetValue(name, out var b) ? b : null;

    static readonly Dictionary<(IFileSource, string), PanelAssets> Cache = new();

    // loadPanelAssets: the gfx/panel bitmaps, loaded once - or, for a pack that ships its own
    // (skill_panels.png and friends next to its levels.nxmi), that pack's. `packDir` is given
    // only for such a pack (the levels index's `panel` flag).
    public static PanelAssets Load(IFileSource io, string? packDir)
    {
        var key = (io, packDir ?? "");
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var cached)) return cached;
            var output = new PanelAssets();
            foreach (string n in PanelFiles)
            {
                Bitmap? bmp = packDir != null ? io.Image(packDir + "/" + n + ".png") : null;
                bmp ??= io.Image(StyleManager.AssetDir + "gfx/panel/" + n + ".png");
                output.Images[n] = bmp;
            }
            foreach (string n in PanelFiles)
                if (output.Images[n] == null && !OptionalFiles.Contains(n))
                    throw new FileNotFoundException("missing " + StyleManager.AssetDir + "gfx/panel/" + n + ".png - see neolemmix/README.md");
            Cache[key] = output;
            return output;
        }
    }
}

// layout.reliefMasks: which panel pixels are a picture (art) and which a count's digit
public sealed record ReliefMasks(byte[] Art, byte[] Digits);

// game.panelLayout: what the toolbar needs to know of the panel
public sealed class PanelLayout
{
    public int Buttons, DigitButtons, Width, Height;
    public required List<string> Cells;      // what each cell does, for the label a resting pointer gets
    public bool SharedBorder;                // each cell is its own tile
    public required MinimapSpec Minimap;
    public required List<int> SplitCells;    // the cells that are two buttons, one over the other
    public int HalfUpperBottom, HalfLowerTop;
    public bool ReliefFromMasks = true;
    public ReliefMasks? ReliefMasks;         // null until the graphics are in
}

public sealed class GamePanel : IGamePanel
{
    const int PanelW = 416, PanelH = 40;
    const int ButtonY = 16, Cell = 16;
    // a split cell's halves (HalfButtonRect): the upper ends at 26, the lower starts at 28
    const int HalfUpperBottom = 26, HalfLowerTop = 28;
    // the minimap frame's picture and the window inside it
    const int RegionW = 111, RegionH = 38, RegionX = 1, RegionY = 1;
    const int MinimapDx = 3, MinimapDy = 2, MinimapW = 104, MinimapH = 34, MinimapScale = 8;
    // how far a press on frame back / forward goes: left, middle, right button
    static readonly Dictionary<int, int> SkipByButton = new() { [0] = 1, [1] = 85, [2] = 17 };
    const double HoldDelayMs = 250, HoldRepeatMs = 100; // CheckFrameSkip's auto-repeat
    // the info string: 38 columns of 8 px; panel_icons.png supplies glyphs 38..44
    const int InfoLen = 38;
    const int IconReplay = 38, IconHatch = 39, IconAlive = 40, IconSaved = 41, IconClock = 42, IconClockLimit = 43, IconReplayInsert = 44;
    const int ColCursor = 1, ColReplay = 13, ColHatchIcon = 15, ColHatch = 16, ColAliveIcon = 21, ColAlive = 22, ColSavedIcon = 27,
        ColSaved = 28, ColClockIcon = 33, ColMin = 34, ColDash = 36, ColSec = 37;
    // LemmingActionStrings: what the strip calls a lemming doing this
    static readonly Dictionary<string, string> ActionWord = new(StringComparer.Ordinal)
    {
        ["walking"] = "WALKER", ["ascending"] = "ASCENDER", ["digging"] = "DIGGER", ["climbing"] = "CLIMBER", ["drowning"] = "DROWNER",
        ["hoisting"] = "HOISTER", ["building"] = "BUILDER", ["bashing"] = "BASHER", ["mining"] = "MINER", ["falling"] = "FALLER",
        ["floating"] = "FLOATER", ["splatting"] = "SPLATTER", ["exiting"] = "EXITER", ["vaporizing"] = "VAPORIZER", ["blocking"] = "BLOCKER",
        ["shrugging"] = "SHRUGGER", ["ohnoing"] = "EXPLODER", ["exploding"] = "EXPLODER", ["platforming"] = "PLATFORMER",
        ["stacking"] = "STACKER", ["stoning"] = "STONER", ["stonefinish"] = "STONER", ["swimming"] = "SWIMMER", ["gliding"] = "GLIDER",
        ["fixing"] = "DISARMER", ["cloning"] = "CLONER", ["fencing"] = "FENCER", ["reaching"] = "REACHER", ["shimmying"] = "SHIMMIER",
        ["jumping"] = "JUMPER", ["dehoisting"] = "DEHOISTER", ["sliding"] = "SLIDER", ["lasering"] = "LASERER",
    };
    static readonly HashSet<string> Working = new(StringComparer.Ordinal) { "building", "platforming", "stacking", "lasering", "bashing", "mining", "digging", "blocking" };
    static readonly string[] Athletes = { "", "", "ATHLETE", "TRIATHLETE", "QUADATHLETE", "QUINTATHLETE" };
    const int SkillSlots = 10; // MAX_SKILL_TYPES_PER_LEVEL: the panel always shows this many
    // sprite frame, and where its feet go inside the 16x23 button (SetSkillIcons)
    static readonly Dictionary<string, (string Sprite, int Dx, int Frame, int X, int Y)> SkillIcons = new(StringComparer.Ordinal)
    {
        ["WALKER"] = ("walker", 1, 1, 6, 21), ["JUMPER"] = ("jumper", 1, 0, 6, 20), ["SHIMMIER"] = ("shimmier", 1, 1, 7, 20),
        ["SLIDER"] = ("slider", -1, 0, 5, 21), ["CLIMBER"] = ("climber", 1, 3, 10, 22), ["SWIMMER"] = ("swimmer", 1, 2, 8, 19),
        ["FLOATER"] = ("floater", 1, 4, 7, 26), ["GLIDER"] = ("glider", 1, 4, 7, 26), ["DISARMER"] = ("disarmer", 1, 6, 4, 21),
        ["BOMBER"] = ("bomber", 1, 0, 8, 21), ["STONER"] = ("stoner", 1, 0, 8, 21), ["BLOCKER"] = ("blocker", 1, 0, 7, 21),
        ["PLATFORMER"] = ("platformer", 1, 1, 7, 20), ["BUILDER"] = ("builder", 1, 1, 7, 20), ["STACKER"] = ("stacker", 1, 0, 7, 21),
        ["LASERER"] = ("laserer", 1, 0, 8, 21), ["BASHER"] = ("basher", 1, 0, 8, 21), ["FENCER"] = ("fencer", 1, 1, 7, 21),
        ["MINER"] = ("miner", 1, 12, 4, 21), ["DIGGER"] = ("digger", 1, 4, 7, 21), ["CLONER"] = ("walker", -1, 1, 6, 21),
    };
    static readonly Dictionary<string, (int X, int Y)[]> SkillBricks = new(StringComparer.Ordinal)
    {
        ["PLATFORMER"] = new[] { (2, 21), (5, 21), (8, 21), (11, 21) },
        ["BUILDER"] = new[] { (4, 22), (6, 21), (8, 20), (10, 19) },
        ["STACKER"] = new[] { (10, 20), (10, 19), (10, 18), (10, 17) },
    };
    const int BrickColor = 0xf0d0d0;

    public sealed class HoldState { public int Step; public double Next; }

    public readonly Game Game;
    public readonly PixelCanvas Display;
    public readonly SpriteSet? Sprites;   // game.sprites in the JS
    readonly Func<double> _now;           // performance.now()
    public PanelAssets? Assets;
    public readonly List<string> Skills;
    public readonly List<string> Cells;
    public readonly PanelLayout Layout;
    public int RrHeld;                    // a release-rate button held down: -1, 0 or 1
    public double RrNext;                 // when it next changes the rate (performance.now)
    public HoldState? Held;               // a frame back/forward half held down
    public bool FlatBackground;           // the buttons on one plain colour instead of skill_panels.png
    readonly Dictionary<string, Bitmap> _icons = new(StringComparer.Ordinal);
    public bool Dirty = true;
    public bool Disposed;
    Bitmap? _base;
    byte[]? _artMask;

    static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();

    // Game.setGuiDisplay: the game's panel replaced by one drawn into `display`.
    public static GamePanel SetGuiDisplay(Game game, PixelCanvas display, PanelAssets? assets, SpriteSet? sprites, Func<double>? now = null)
    {
        game.Gui?.Dispose();
        var panel = new GamePanel(game, display, sprites, now);
        game.Gui = panel;
        if (assets != null) panel.AssetsLoaded(assets);
        return panel;
    }

    // `assets` stands for loadPanelAssets(...).then(...): when given, the panel is built and drawn
    // at once (or later through AssetsLoaded). `now` is the clock in ms (default: a stopwatch).
    public GamePanel(Game game, PixelCanvas display, SpriteSet? sprites, Func<double>? now = null)
    {
        Game = game;
        Display = display;
        Sprites = sprites;
        _now = now ?? (() => Clock.Elapsed.TotalMilliseconds);
        Skills = game.Sim.ActiveSkills;
        // cell -> what it does
        var slots = Skills.Select(s => "skill:" + s).ToList();
        while (slots.Count < SkillSlots) slots.Add("empty");
        Cells = new List<string> { "rrminus", "rrplus" };
        Cells.AddRange(slots);
        Cells.AddRange(new[] { "pause", "nuke", "speed", "restart", "frameskip", "directional", "cpmreplay" });
        Layout = new PanelLayout
        {
            Buttons = Cells.Count, DigitButtons = 2 + SkillSlots, Width = PanelW, Height = PanelH,
            Cells = Cells, SharedBorder = false,
            Minimap = new MinimapSpec { X = Cells.Count * Cell + RegionX + MinimapDx, Y = RegionY + MinimapDy, W = MinimapW, H = MinimapH, ScaleX = MinimapScale, ScaleY = MinimapScale, Pad = 1 },
            SplitCells = new[] { "frameskip", "directional", "cpmreplay" }.Select(w => Cells.IndexOf(w)).ToList(),
            HalfUpperBottom = HalfUpperBottom, HalfLowerTop = HalfLowerTop,
            ReliefFromMasks = true, ReliefMasks = null,
        };
        display.InitSize(PanelW, PanelH);
        display.OnMouseDown.On(pos => HandleMouseDown(pos.X, pos.Y, pos.Button));
        display.OnMouseUp.On(_ => { RrHeld = 0; Held = null; });
        display.OnDoubleClick.On(pos => HandleDoubleClick(pos.X, pos.Y));
    }

    // the loadPanelAssets promise resolving
    public void AssetsLoaded(PanelAssets assets)
    {
        if (Disposed) return;
        Assets = assets;
        BuildBase();
        Render(true);
    }

    public void Dispose() { Disposed = true; }

    // The buttons' background as one plain colour (the mean of the pack's skill_panels.png)
    // instead of the texture, so the pictures and counts have nothing to compete with.
    public void SetFlatBackground(bool on)
    {
        if (FlatBackground == on) return;
        FlatBackground = on;
        if (Assets == null || Disposed) return; // applied when the graphics are in
        BuildBase();
        Render(true);
    }

    // The mean of a bitmap's opaque pixels, lifted off black if need be.
    static (int R, int G, int B) MeanColor(Bitmap bmp)
    {
        double r = 0, g = 0, b = 0; long n = 0;
        for (int i = 0; i < bmp.Data.Length; i += 4)
        {
            if (bmp.Data[i + 3] == 0) continue;
            r += bmp.Data[i]; g += bmp.Data[i + 1]; b += bmp.Data[i + 2]; n++;
        }
        if (n == 0) return (96, 96, 96);
        int ri = (int)Util.JsMath.Round(r / n), gi = (int)Util.JsMath.Round(g / n), bi = (int)Util.JsMath.Round(b / n);
        int lift = 48 - (ri + gi + bi);
        if (lift > 0)
        {
            int up = (int)Math.Ceiling(lift / 3.0);
            ri += up; gi += up; bi += up;
        }
        return (ri, gi, bi);
    }

    // The panel with its buttons and pictures, before any counts.
    void BuildBase()
    {
        var a = Assets!;
        var bse = new Bitmap(PanelW, PanelH);
        bse.Words().Fill(0xff000000);
        int n = Cells.Count;
        // button backgrounds tile skill_panels.png across the row (DrawBlankPanel)
        var blank = a["skill_panels"]!;
        if (FlatBackground)
        {
            // ...or one plain colour where the tiles would go
            var (r, g, b) = MeanColor(blank);
            for (int y = 0; y < blank.Height && ButtonY + y < PanelH; y++)
                for (int x = 0; x < n * Cell; x++)
                {
                    int p = ((ButtonY + y) * PanelW + x) * 4;
                    bse.Data[p] = Util.JsMath.ClampU8(r); bse.Data[p + 1] = Util.JsMath.ClampU8(g); bse.Data[p + 2] = Util.JsMath.ClampU8(b); bse.Data[p + 3] = 255;
                }
        }
        else
        {
            for (int x = 0; x < n * Cell; x += blank.Width)
                Pixels.Blit(bse, x, ButtonY, blank, 0, 0, Math.Min(blank.Width, n * Cell - x), blank.Height, Pixels.CombineGadget);
        }
        // which pixels are a picture, for the toolbar to raise: where an icon has paint
        var art = new byte[PanelW * PanelH];
        void Mark(int cell, Bitmap bmp, byte value)
        {
            for (int y = 0; y < bmp.Height && ButtonY + y < PanelH; y++)
                for (int x = 0; x < bmp.Width; x++)
                    if (bmp.Data[(y * bmp.Width + x) * 4 + 3] != 0)
                    {
                        int i = (ButtonY + y) * PanelW + cell * Cell + x;
                        if (i < art.Length) art[i] = value; // a typed array drops writes past its end
                    }
        }
        void Icon(int cell, Bitmap bmp)
        {
            Pixels.Blit(bse, cell * Cell, ButtonY, bmp, 0, 0, bmp.Width, bmp.Height, Pixels.CombineGadget);
            Mark(cell, bmp, 1);
        }
        for (int i = 0; i < Cells.Count; i++)
        {
            string what = Cells[i];
            switch (what)
            {
                case "rrminus": Icon(i, a["icon_rr_minus"]!); break;
                case "rrplus": Icon(i, a["icon_rr_plus"]!); break;
                case "pause": Icon(i, a["icon_pause"]!); break;
                case "nuke": Icon(i, a["icon_nuke"]!); break;
                case "speed": Icon(i, a["icon_ff"]!); break;
                case "restart": Icon(i, a["icon_restart"]!); break;
                case "frameskip": Icon(i, a["icon_frameskip"]!); break;
                case "directional": Icon(i, a["icon_directional"]!); break;
                case "cpmreplay": Icon(i, a["icon_cpm_and_replay"]!); break;
                case "empty":
                    // an unused slot: black, with the empty-slot picture merged over it (SetSkillIcons)
                    for (int y = 0; y < 23; y++)
                        for (int x = 0; x < Cell; x++)
                        {
                            int p = ((ButtonY + y) * PanelW + i * Cell + x) * 4;
                            bse.Data[p] = bse.Data[p + 1] = bse.Data[p + 2] = 0; bse.Data[p + 3] = 255;
                        }
                    Icon(i, a["empty_slot"]!);
                    Mark(i, a["empty_slot"]!, 0); // a background, not a picture
                    break;
                default: Icon(i, SkillIcon(what[6..])); break;
            }
            // the count's box is drawn over the picture on every render
            if (what == "rrminus" || what == "rrplus" || what.StartsWith("skill:", StringComparison.Ordinal)) Mark(i, a["skill_count_erase"]!, 0);
        }
        _artMask = art;
        // the minimap's frame after the last cell (the map is drawn over it by the host)
        var region = a["minimap_region"] ?? RedFrame();
        Pixels.Blit(bse, n * Cell + RegionX, RegionY, region, 0, 0, region.Width, region.Height, Pixels.CombineGadget);
        _base = bse;
    }

    // DrawHighlight: skill_selected's border around a rect (a nine-slice with 3-px corners).
    void Highlight(Bitmap output, int x, int y, int w, int h)
    {
        var s = Assets!["skill_selected"]!;
        const int m = 3;
        void Put(int dx, int dy, int sx, int sy, int cw, int ch) => Pixels.Blit(output, x + dx, y + dy, s, sx, sy, cw, ch, Pixels.CombineGadget);
        Put(0, 0, 0, 0, m, m); Put(w - m, 0, s.Width - m, 0, m, m);
        Put(0, h - m, 0, s.Height - m, m, m); Put(w - m, h - m, s.Width - m, s.Height - m, m, m);
        for (int i = m; i < w - m; i++) { Put(i, 0, m, 0, 1, m); Put(i, h - m, m, s.Height - m, 1, m); }
        for (int j = m; j < h - m; j++) { Put(0, j, 0, m, m, 1); Put(w - m, j, s.Width - m, m, m, 1); }
    }

    // minimap_region.png by hand: black, with its 1-px red frame around the window.
    static Bitmap RedFrame()
    {
        var bmp = new Bitmap(RegionW, RegionH);
        bmp.Words().Fill(0xff000000);
        int x0 = MinimapDx - 1, x1 = MinimapDx + MinimapW, y0 = MinimapDy - 1, y1 = MinimapDy + MinimapH;
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
                if (x == x0 || y == y0 || x == x1 || y == y1)
                {
                    int p = (y * RegionW + x) * 4;
                    bmp.Data[p] = 240; bmp.Data[p + 1] = 32; bmp.Data[p + 2] = 32; bmp.Data[p + 3] = 255;
                }
        return bmp;
    }

    // A skill's picture: the lemming sprite NeoLemmix puts on the button (public: the replay
    // markers wear it, as replay-markers.js reads gui._skillIcon).
    public Bitmap SkillIcon(string name)
    {
        if (_icons.TryGetValue(name, out var cached)) return cached;
        var bmp = new Bitmap(Cell, 23);
        if (SkillIcons.TryGetValue(name, out var spec) && Sprites != null)
        {
            if (Sprites.Anims.TryGetValue(spec.Sprite, out var anim))
            {
                var side = spec.Dx > 0 ? anim.Right : anim.Left;
                var frame = side.Frames[Math.Min(spec.Frame, anim.FrameCount - 1)];
                Pixels.Blit(bmp, spec.X - side.FootX, spec.Y - side.FootY, frame, 0, 0, frame.Width, frame.Height, Pixels.CombineGadget);
            }
        }
        if (SkillBricks.TryGetValue(name, out var bricks))
            foreach (var (bx, by) in bricks)
                for (int o = 0; o < 2; o++)
                {
                    int x = bx + o, y = by;
                    if (x < 0 || y < 0 || x >= bmp.Width || y >= bmp.Height) continue;
                    int p = (y * bmp.Width + x) * 4;
                    bmp.Data[p] = (BrickColor >> 16) & 255; bmp.Data[p + 1] = (BrickColor >> 8) & 255; bmp.Data[p + 2] = BrickColor & 255; bmp.Data[p + 3] = 255;
                }
        _icons[name] = bmp;
        return bmp;
    }

    // A cell's count, and (in `mask`) which pixels the digits painted.
    void DrawDigits(Bitmap output, int cell, int number, byte[] mask)
    {
        var a = Assets!;
        int x0 = cell * Cell, y0 = ButtonY;
        var erase = a["skill_count_erase"]!;
        Pixels.Blit(output, x0, y0, erase, 0, 0, erase.Width, erase.Height, Pixels.CombineGadget);
        if (number <= 0) return;
        var digits = a["skill_count_digits"]!;
        void Digit(int d, int x)
        {
            Pixels.Blit(output, x, y0 + 1, digits, d * 4, 0, 4, 8, Pixels.CombineGadget);
            for (int dy = 0; dy < 8; dy++)
                for (int dx = 0; dx < 4; dx++)
                {
                    int si = dy * digits.Width + d * 4 + dx;
                    if (si * 4 + 3 < digits.Data.Length && digits.Data[si * 4 + 3] != 0)
                    {
                        int i = (y0 + 1 + dy) * PanelW + x + dx;
                        if (i < mask.Length) mask[i] = 1;
                    }
                }
        }
        if (number > 99) { Digit(9, x0 + 3); Digit(9, x0 + 7); return; }
        if (number < 10) Digit(number, x0 + 5);
        else { Digit(number / 10, x0 + 3); Digit(number % 10, x0 + 7); }
    }

    // The info strip's columns: a character, or an icon (38..44) as Icon + id.
    const int IconTag = 0x10000;

    // DrawNewStr: the strip's columns, characters from panel_font, icons (38..44) from panel_icons.
    void DrawText(Bitmap output, int[] cols)
    {
        var font = Assets!["panel_font"]!;
        var icons = Assets!["panel_icons"];
        int limit = Cells.Count * Cell; // the strip stops where the minimap frame starts
        for (int i = 0; i < cols.Length && i * 8 < limit; i++)
        {
            int ch = cols[i];
            int id = -1;
            if (ch >= IconTag) id = ch - IconTag;
            else if (ch == '%') id = 0;
            else if (ch >= '0' && ch <= '9') id = ch - 48 + 1;
            else if (ch == '-') id = 11;
            else if (ch >= 'A' && ch <= 'Z') id = ch - 65 + 12;
            if (id < 0) continue;
            if (id >= IconReplay)
            {
                if (icons != null) Pixels.Blit(output, i * 8, 0, icons, (id - IconReplay) * 8, 0, 8, 16, Pixels.CombineGadget);
            }
            else Pixels.Blit(output, i * 8, 0, font, id * 8, 0, 8, 16, Pixels.CombineGadget);
        }
    }

    // GetSkillString: what the strip calls the lemming under the pointer.
    public string CursorWord(Lemming? L)
    {
        if (L == null || L.Removed) return "";
        string name = L.Action >= 0 && L.Action < Lem.ActionNames.Length ? Lem.ActionNames[L.Action] : "";
        string word = ActionWord.TryGetValue(name, out var w0) ? w0 : "";
        if (L.HasPermanentSkills && Game.ShowAthleteInfo)
        {
            // the Show Athlete Info hotkey: one letter per permanent skill, a dash for each it lacks
            var w = "-------".ToCharArray();
            if (L.IsSlider) w[0] = 'L'; if (L.IsClimber) w[1] = 'C'; if (L.IsSwimmer) w[2] = 'S';
            if (L.IsFloater) w[3] = 'F'; if (L.IsGlider) w[3] = 'G'; if (L.IsDisarmer) w[4] = 'D';
            if (L.IsZombie) w[5] = 'Z'; if (L.IsNeutral) w[6] = 'N';
            return new string(w);
        }
        if (L.HasPermanentSkills && !Working.Contains(name))
        {
            var perms = new List<string>();
            if (L.IsSlider) perms.Add("SLIDER"); if (L.IsClimber) perms.Add("CLIMBER"); if (L.IsSwimmer) perms.Add("SWIMMER");
            if (L.IsFloater) perms.Add("FLOATER"); if (L.IsGlider) perms.Add("GLIDER"); if (L.IsDisarmer) perms.Add("DISARMER");
            word = perms.Count == 1 ? perms[0] : Athletes[Math.Min(perms.Count, 5)];
        }
        return word;
    }

    static string Str(int n) => n.ToString(System.Globalization.CultureInfo.InvariantCulture);

    // CreateNewInfoString, as the standard panel lays it out.
    public int[] InfoColumns()
    {
        var sim = Game.Sim;
        var cols = Enumerable.Repeat((int)' ', InfoLen).ToArray();
        void Put(int col, string text) { for (int i = 0; i < text.Length && col - 1 + i < InfoLen; i++) cols[col - 1 + i] = text[i]; }
        // PadL(PadR(S, 3), 4): a space, then the number left-aligned in three
        static string Count(int n) { string s = Str(n); if (s.Length < 4) s = (s + "   ")[..3]; s = " " + s; return s[^4..]; }
        string cw = CursorWord(Game.CursorLemming).PadRight(12);
        Put(ColCursor, cw[..12]);
        if (sim.Replaying) cols[ColReplay - 1] = IconTag + (sim.ReplayInsert ? IconReplayInsert : IconReplay);
        cols[ColHatchIcon - 1] = IconTag + IconHatch;
        Put(ColHatch, Count(Math.Max(0, sim.LemmingsToRelease - sim.SpawnedDead)));
        cols[ColAliveIcon - 1] = IconTag + IconAlive;
        Put(ColAlive, Count(Math.Max(0, sim.LemmingsToRelease + sim.LemmingsOut - sim.SpawnedDead)));
        cols[ColSavedIcon - 1] = IconTag + IconSaved;
        Put(ColSaved, Count(sim.LemmingsIn - sim.Level.NeedCount));
        cols[ColClockIcon - 1] = IconTag + (sim.HasTimeLimit ? IconClockLimit : IconClock);
        int time = Util.JsMath.Floor(sim.CurrentIteration / 17.0);
        if (sim.HasTimeLimit) time = Math.Abs(sim.Level.TimeLimitSeconds - time);
        Put(ColMin, Str(Util.JsMath.Floor(time / 60.0)).PadLeft(2, ' '));
        cols[ColDash - 1] = '-';
        Put(ColSec, Str(time % 60).PadLeft(2, '0'));
        return cols;
    }

    // Redraw when something changed (every frame while the counters move).
    // Render's buffers, reused: the picture and digit mask drawn now, the last ones shown, the
    // frame handed to the display
    Bitmap? _out;
    byte[]? _digits, _shownOut, _shownDigits, _shownArt, _shownData;
    Engine.Frame? _frame;
    int _shownVersion = -1;

    public void Render(bool force = false)
    {
        if (Assets == null || Disposed) return;
        var sim = Game.Sim;
        var bse = _base!;
        if (_out == null || _out.Width != bse.Width || _out.Height != bse.Height) _out = new Bitmap(bse.Width, bse.Height);
        var output = _out;
        Buffer.BlockCopy(bse.Data, 0, output.Data, 0, bse.Data.Length);
        if (_digits == null || _digits.Length != PanelW * PanelH) _digits = new byte[PanelW * PanelH];
        else Array.Clear(_digits);
        var digits = _digits;
        for (int i = 0; i < Cells.Count; i++)
        {
            string what = Cells[i];
            if (what == "rrminus") DrawDigits(output, i, sim.MinReleaseRate, digits);
            else if (what == "rrplus") DrawDigits(output, i, sim.ReleaseRate, digits);
            else if (what.StartsWith("skill:", StringComparison.Ordinal)) DrawDigits(output, i, sim.SkillCountOf(what[6..]), digits);
        }
        int sel = sim.SelectedSkill == null ? -1 : Cells.IndexOf("skill:" + sim.SelectedSkill);
        if (sel >= 0)
        {
            var s = Assets["skill_selected"]!;
            Pixels.Blit(output, sel * Cell, ButtonY, s, 0, 0, s.Width, s.Height, Pixels.CombineGadget);
        }
        // the selectors NeoLemmix lights: pause while paused, speed while fast, a direction while chosen
        var timer = Game.GameTimer;
        void Lit(string what) { int i = Cells.IndexOf(what); if (i >= 0) Highlight(output, i * Cell, ButtonY, 15, 24); }
        if (!timer.IsRunning()) Lit("pause");
        if (timer.SpeedFactor > 1) Lit("speed");
        if (Game.ClearPhysics)
        {
            int i = Cells.IndexOf("cpmreplay");
            if (i >= 0) Highlight(output, i * Cell, ButtonY, 15, HalfUpperBottom - ButtonY + 1);
        }
        if (sim.SelectDx != 0)
        {
            int i = Cells.IndexOf("directional");
            if (i >= 0)
            {
                if (sim.SelectDx < 0) Highlight(output, i * Cell, ButtonY, 15, HalfUpperBottom - ButtonY + 1);
                else Highlight(output, i * Cell, HalfLowerTop, 15, ButtonY + 24 - HalfLowerTop);
            }
        }
        DrawText(output, InfoColumns());
        // the same picture and digits as the display shows from the last render (nothing else
        // drew on it since): the display, its redraw and the masks stay as they are
        if (Display.Data != null && Display.Version == _shownVersion && ReferenceEquals(Display.Data, _shownData) && ReferenceEquals(_shownArt, _artMask)
            && _shownOut != null && output.Data.AsSpan().SequenceEqual(_shownOut)
            && _shownDigits != null && digits.AsSpan().SequenceEqual(_shownDigits))
            return;
        var shownDigits = (byte[])digits.Clone(); // the masks' own copy, never written again
        Layout.ReliefMasks = new ReliefMasks(_artMask!, shownDigits);
        // onto the display (Frame.FromBitmap with every pixel drawn), and tell the host it changed
        if (_frame == null || _frame.Width != output.Width || _frame.Height != output.Height)
        {
            _frame = new Engine.Frame(output.Width, output.Height, 0, 0);
            Array.Fill(_frame.Mask, (sbyte)1);
        }
        output.Words().CopyTo(_frame.Data);
        Display.DrawFrame(_frame, 0, 0);
        Display.Redraw();
        _shownVersion = Display.Version;
        _shownArt = _artMask;
        _shownData = Display.Data;
        _shownOut ??= new byte[output.Data.Length];
        if (_shownOut.Length != output.Data.Length) _shownOut = new byte[output.Data.Length];
        output.Data.CopyTo(_shownOut, 0);
        _shownDigits = shownDigits;
    }

    // A release-rate button (or key) held down: one change on the press, then one per game tick
    // once it has been held for HoldDelayMs - a click changes the rate once.
    public void SetRrHeld(int dir, double? now = null)
    {
        RrHeld = dir;
        RrNext = (now ?? _now()) + HoldDelayMs;
    }

    // One game tick (Game.OnGameTimerTick): a held release-rate button repeats.
    public void Tick() => Tick(null);
    public void Tick(double? now)
    {
        if (RrHeld == 0) return;
        if ((now ?? _now()) < RrNext) return;
        Game.QueueCommand(RrHeld > 0 ? new CommandReleaseRateIncrease(1) : new CommandReleaseRateDecrease(1));
    }

    // A held frame back/forward half repeats (CheckFrameSkip): the host polls this every frame.
    public void Poll(double now)
    {
        if (Held == null || now < Held.Next) return;
        Held.Next = now + HoldRepeatMs;
        Skip(Held.Step);
    }

    void Skip(int step)
    {
        if (step < 0) Game.BackFrames(-step);
        else Game.ForwardFrames(step);
        Render();
    }

    public void HandleMouseDown(double x, double y, int button)
    {
        if (y < ButtonY || x >= Cells.Count * Cell) return; // the minimap is the host's
        int cell = (int)Math.Truncate(x / Cell);              // -0 for -16 < x < 0: cell 0, as in the JS
        if (cell < 0 || cell >= Cells.Count) return;
        string what = Cells[cell];
        var game = Game;
        // the split cells answer by half; the line between answers to nobody
        bool upper = y <= HalfUpperBottom, lower = y >= HalfLowerTop;
        if (what == "restart") game.RestartReplay();
        else if (what == "frameskip")
        {
            int n = SkipByButton.TryGetValue(button, out int k) ? k : 1;
            if (upper) { Skip(-n); if (n == 1) Held = new HoldState { Step = -1, Next = _now() + HoldDelayMs }; }
            else if (lower) { Skip(n); if (n == 1) Held = new HoldState { Step = 1, Next = _now() + HoldDelayMs }; }
            return;
        }
        else if (what == "directional")
        {
            if (upper) game.SetSelectDx(game.Sim.SelectDx == -1 ? 0 : -1);
            else if (lower) game.SetSelectDx(game.Sim.SelectDx == 1 ? 0 : 1);
        }
        else if (what == "cpmreplay")
        {
            if (upper) game.ToggleClearPhysics();
            else if (lower) game.RequestLoadReplay();
        }
        else if (what == "rrminus") { SetRrHeld(-1); game.QueueCommand(new CommandReleaseRateDecrease(1)); }
        else if (what == "rrplus") { SetRrHeld(1); game.QueueCommand(new CommandReleaseRateIncrease(1)); }
        else if (what == "pause") game.GameTimer.Toggle();
        else if (what == "nuke")
        {
            if (game.NukePrepared) { game.QueueCommand(new CommandNuke()); game.NukePrepared = false; }
            else game.NukePrepared = true;
        }
        else if (what == "speed")
        {
            var timer = game.GameTimer;
            timer.SpeedFactor = timer.SpeedFactor >= 8 ? 1 : timer.SpeedFactor * 2;
        }
        else if (what.StartsWith("skill:", StringComparison.Ordinal))
        {
            game.QueueCommand(new CommandSelectSkill(cell - 2));
        }
        Render();
    }

    public void HandleDoubleClick(double x, double y)
    {
        if (y < ButtonY || x >= Cells.Count * Cell) return;
        int cell = (int)Math.Truncate(x / Cell);
        if (cell >= 0 && cell < Cells.Count && Cells[cell] == "nuke") { Game.QueueCommand(new CommandNuke()); Game.NukePrepared = false; }
    }
}
