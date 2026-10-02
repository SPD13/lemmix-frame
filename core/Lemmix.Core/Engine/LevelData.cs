using Lemmix.Parse;
using Lemmix.Util;

namespace Lemmix.Engine;

// web/lemmix/js/level.js parseLevel - the level file as plain data, before any graphics.
public sealed class LevelInfo
{
    public string Title = "", Author = "", Theme = "", Music = "", Id = "";
    public int Lemmings, Save, TimeLimit, SpawnInterval;
    public bool SpawnLocked;
    public int Width, Height, StartX, StartY;
    public bool StartAuto;
    public string Background = "";
}

public sealed class TerrainSpec
{
    public string Gs = "", Piece = "";
    public int X, Y, Width, Height;
    public bool OneWay, Rotate, Flip, Invert, NoOverwrite, Erase;
}

public sealed class GadgetPresets
{
    public bool Slider, Climber, Swimmer, Floater, Glider, Disarmer, Zombie, Neutral;
}

public sealed class GadgetSpec
{
    public string Gs = "", Piece = "";
    public int X, Y, Width, Height;
    public bool Rotate, Flip, Invert, NoOverwrite, OnlyOnTerrain;
    public int LemmingCap, Pairing;
    public string Skill = "";
    public int SkillCount;
    public string Direction = "";
    public GadgetPresets Presets = new();
    public int Angle, Speed;

    public GadgetSpec Copy() => (GadgetSpec)MemberwiseClone(); // Object.assign({}, g): presets shared
}

public sealed class LemmingSpec
{
    public int X, Y, Dx;
    public bool Shimmier, Slider, Climber, Swimmer, Floater, Glider, Disarmer, Zombie, Neutral, Blocker;
}

public sealed class Talisman
{
    public string Title = "";
    public int Id;
    public string Color = "bronze";
    public int Save, TimeLimit, SkillLimit, SkillTypeLimit, SkillEachLimit;
    public string? UseOnlySkill;
    public List<KeyValuePair<string, int>> Limits = new(); // in SKILLS order
}

public sealed class LevelData
{
    public required LevelInfo Info;
    public required List<KeyValuePair<string, int>> Skills; // in SKILLS order, as the JS object's keys
    public required List<TerrainSpec> Terrains;
    public required List<GadgetSpec> Gadgets;
    public required List<LemmingSpec> Lemmings;
    public required List<Talisman> Talismans;
    public required List<string> Pretext, Posttext;

    public bool HasSkill(string name) => Skills.Any(s => s.Key == name);
}

public static partial class LevelBuilder
{
    // TSkillPanelButton, in panel order
    public static readonly string[] Skills =
    {
        "WALKER", "JUMPER", "SHIMMIER", "SLIDER", "CLIMBER", "SWIMMER", "FLOATER", "GLIDER",
        "DISARMER", "BOMBER", "STONER", "BLOCKER", "PLATFORMER", "BUILDER", "STACKER", "LASERER", "BASHER",
        "FENCER", "MINER", "DIGGER", "CLONER",
    };

    static string T(string? s) => JsString.Trim(s ?? "");
    static string TL(string? s) => JsString.Trim(s ?? "").ToLowerInvariant();
    static char FirstLower(string? s) { string t = TL(s); return t.Length > 0 ? t[0] : '\0'; }

    public static LevelData ParseLevel(string text)
    {
        var nx = NxParser.Parse(text);
        var info = new LevelInfo
        {
            Title = T(nx.Get("TITLE")),
            Author = T(nx.Get("AUTHOR")),
            Theme = TL(nx.Get("THEME")),
            Music = T(nx.Get("MUSIC")),
            Id = T(nx.Get("ID")),
            Lemmings = nx.Int("LEMMINGS", 1),
            Save = nx.Int("SAVE_REQUIREMENT", 1),
            // seconds; absent or INFINITE = no limit
            TimeLimit = nx.Has("TIME_LIMIT") && (nx.Get("TIME_LIMIT") ?? "null").ToLowerInvariant() != "infinite"
                ? nx.Int("TIME_LIMIT", 1) : 0,
            SpawnInterval = nx.Has("MAX_SPAWN_INTERVAL") ? nx.Int("MAX_SPAWN_INTERVAL", 53)
                : nx.Has("RELEASE_RATE") ? 53 - (nx.Int("RELEASE_RATE", 0) >> 1) : 53,
            SpawnLocked = nx.Has("SPAWN_INTERVAL_LOCKED") || nx.Has("RELEASE_RATE_LOCKED"),
            Width = nx.Int("WIDTH", 320),
            Height = nx.Int("HEIGHT", 160),
            StartX = nx.Int("START_X", 0),
            StartY = nx.Int("START_Y", 0),
            StartAuto = !nx.Has("START_X") || !nx.Has("START_Y"),
            Background = TL(nx.Get("BACKGROUND")),
        };

        var skills = new List<KeyValuePair<string, int>>();
        var skillset = nx.Section("SKILLSET");
        if (skillset != null)
            foreach (string name in Skills)
            {
                if (!skillset.Has(name)) continue;
                string v = TL(skillset.Get(name) ?? "null");
                skills.Add(new(name, v == "infinite" ? 100 : skillset.Int(name, 0)));
            }

        static string StyleOf(NxSection s) => TL(s.Get("STYLE") ?? StyleManager.Or(s.Get("COLLECTION")));
        static string PieceOf(NxSection s) => TL(s.Get("PIECE"));

        var terrains = nx.SectionsNamed("TERRAIN").Select(s => new TerrainSpec
        {
            Gs = StyleOf(s), Piece = PieceOf(s),
            X = s.Int16("X", 0), Y = s.Int16("Y", 0),
            Width = s.Int("WIDTH", 0), Height = s.Int("HEIGHT", 0),
            OneWay = s.Has("ONE_WAY"), Rotate = s.Has("ROTATE"), Flip = s.Has("FLIP_HORIZONTAL"),
            Invert = s.Has("FLIP_VERTICAL"), NoOverwrite = s.Has("NO_OVERWRITE"), Erase = s.Has("ERASE"),
        }).ToList();

        var gadgets = new List<GadgetSpec>();
        foreach (var s in nx.SectionsNamed("GADGET").Concat(nx.SectionsNamed("OBJECT")))
        {
            char dir = FirstLower(s.Get("DIRECTION"));
            gadgets.Add(new GadgetSpec
            {
                Gs = StyleOf(s), Piece = PieceOf(s),
                X = s.Int16("X", 0), Y = s.Int16("Y", 0),
                Width = s.Int("WIDTH", 0), Height = s.Int("HEIGHT", 0),
                Rotate = s.Has("ROTATE"), Flip = s.Has("FLIP_HORIZONTAL"), Invert = s.Has("FLIP_VERTICAL"),
                NoOverwrite = s.Has("NO_OVERWRITE"), OnlyOnTerrain = s.Has("ONLY_ON_TERRAIN"),
                LemmingCap = s.Int("LEMMINGS", 0),
                Pairing = s.Int("PAIRING", 0),
                Skill = JsString.Upper(T(s.Get("SKILL"))),
                SkillCount = Math.Max(s.Has("SKILL_COUNT") ? s.Int("SKILL_COUNT", 1) : s.Int("SKILLCOUNT", 1), 1),
                Direction = dir == 'l' ? "l" : dir == 'r' ? "r" : "",
                Presets = new GadgetPresets
                {
                    Slider = s.Has("SLIDER"), Climber = s.Has("CLIMBER"), Swimmer = s.Has("SWIMMER"),
                    Floater = s.Has("FLOATER"), Glider = s.Has("GLIDER"), Disarmer = s.Has("DISARMER"),
                    Zombie = s.Has("ZOMBIE"), Neutral = s.Has("NEUTRAL"),
                },
                Angle = s.Int("ANGLE", 0), Speed = s.Int("SPEED", 0),
            });
        }

        var lemmings = nx.SectionsNamed("LEMMING").Select(s =>
        {
            char dir = FirstLower(s.Get("DIRECTION"));
            return new LemmingSpec
            {
                X = s.Int16("X", 0), Y = s.Int16("Y", 0),
                Dx = s.Has("FLIP_HORIZONTAL") || dir == 'l' ? -1 : 1,
                Shimmier = s.Has("SHIMMIER"), Slider = s.Has("SLIDER"), Climber = s.Has("CLIMBER"),
                Swimmer = s.Has("SWIMMER"), Floater = s.Has("FLOATER"), Glider = s.Has("GLIDER"),
                Disarmer = s.Has("DISARMER"), Zombie = s.Has("ZOMBIE"), Neutral = s.Has("NEUTRAL"),
                Blocker = s.Has("BLOCKER"),
            };
        }).ToList();

        var talismans = nx.SectionsNamed("TALISMAN").Select(s =>
        {
            string only = JsString.Upper(T(s.Get("USE_ONLY_SKILL")));
            var t = new Talisman
            {
                Title = T(s.Get("TITLE")), Id = s.Int("ID", 0),
                Color = TL(StyleManager.Or(s.Get("COLOR"), "bronze")),
                Save = s.Has("SAVE_REQUIREMENT") ? s.Int("SAVE_REQUIREMENT", -1) : s.Int("SAVE", -1),
                TimeLimit = s.Int("TIME_LIMIT", -1), // frames
                SkillLimit = s.Int("SKILL_LIMIT", -1), SkillTypeLimit = s.Int("SKILL_TYPE_LIMIT", -1),
                SkillEachLimit = s.Int("SKILL_EACH_LIMIT", -1),
                UseOnlySkill = only == "" ? null : only,
            };
            foreach (string name in Skills) if (s.Has(name + "_LIMIT")) t.Limits.Add(new(name, s.Int(name + "_LIMIT", -1)));
            return t;
        }).ToList();

        List<string> Lines(string name) => nx.SectionsNamed(name).SelectMany(s => s.GetAll("LINE")).ToList();
        return new LevelData
        {
            Info = info, Skills = skills, Terrains = terrains, Gadgets = gadgets, Lemmings = lemmings,
            Talismans = talismans, Pretext = Lines("PRETEXT"), Posttext = Lines("POSTTEXT"),
        };
    }
}
