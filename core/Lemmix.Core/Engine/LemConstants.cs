using Lemmix.Io;

namespace Lemmix.Engine;

// web/lemmix/js/lemgame.js constants (LemGame.pas, LemCore.pas, LemRenderHelpers.pas).

// TBasicLemmingAction
public static class BA
{
    public const int NONE = 0, WALKING = 1, ASCENDING = 2, DIGGING = 3, CLIMBING = 4, DROWNING = 5, HOISTING = 6, BUILDING = 7,
        BASHING = 8, MINING = 9, FALLING = 10, FLOATING = 11, SPLATTING = 12, EXITING = 13, VAPORIZING = 14,
        BLOCKING = 15, SHRUGGING = 16, OHNOING = 17, EXPLODING = 18, TOWALKING = 19, PLATFORMING = 20,
        STACKING = 21, STONING = 22, STONEFINISH = 23, SWIMMING = 24, GLIDING = 25, FIXING = 26, CLONING = 27,
        FENCING = 28, REACHING = 29, SHIMMYING = 30, JUMPING = 31, DEHOISTING = 32, SLIDING = 33, LASERING = 34;
}

public static class Lem
{
    public static readonly string[] ActionNames =
    {
        "none", "walking", "ascending", "digging", "climbing", "drowning", "hoisting",
        "building", "bashing", "mining", "falling", "floating", "splatting", "exiting", "vaporizing",
        "blocking", "shrugging", "ohnoing", "exploding", "towalking", "platforming", "stacking", "stoning",
        "stonefinish", "swimming", "gliding", "fixing", "cloning", "fencing", "reaching", "shimmying",
        "jumping", "dehoisting", "sliding", "lasering",
    };

    // number of physics frames per action (Transition's ANIM_FRAMECOUNT)
    public static readonly int[] AnimFrameCount =
    {
        0, 4, 1, 16, 8, 16, 8, 16, 16, 24, 4, 17, 16, 8, 14, 16, 8, 16, 1, 0, 16, 8,
        16, 1, 8, 17, 16, 0, 16, 8, 20, 13, 7, 1, 12,
    };

    // skill panel button (name) <-> action, in the JS object's key order
    public static readonly Dictionary<string, int> SkillToAction = new(StringComparer.Ordinal)
    {
        ["WALKER"] = BA.TOWALKING, ["JUMPER"] = BA.JUMPING, ["SHIMMIER"] = BA.SHIMMYING, ["SLIDER"] = BA.SLIDING,
        ["CLIMBER"] = BA.CLIMBING, ["SWIMMER"] = BA.SWIMMING, ["FLOATER"] = BA.FLOATING, ["GLIDER"] = BA.GLIDING,
        ["DISARMER"] = BA.FIXING, ["BOMBER"] = BA.EXPLODING, ["STONER"] = BA.STONING, ["BLOCKER"] = BA.BLOCKING,
        ["PLATFORMER"] = BA.PLATFORMING, ["BUILDER"] = BA.BUILDING, ["STACKER"] = BA.STACKING, ["LASERER"] = BA.LASERING,
        ["BASHER"] = BA.BASHING, ["FENCER"] = BA.FENCING, ["MINER"] = BA.MINING, ["DIGGER"] = BA.DIGGING, ["CLONER"] = BA.CLONING,
    };
    public static readonly Dictionary<int, string> ActionToSkill = SkillToAction.ToDictionary(kv => kv.Value, kv => kv.Key);
    public static readonly HashSet<int> PermSkillSet = new() { BA.SLIDING, BA.CLIMBING, BA.FLOATING, BA.GLIDING, BA.FIXING, BA.SWIMMING };
    public static readonly HashSet<int> Assignable = new(SkillToAction.Values);

    public const int MAX_FALLDISTANCE = 62;
    public const int LEMMING_MAX_Y = 9;
    public const int PARTICLE_FRAMECOUNT = 51;
    public const int NO_OBJECT = 65535;
    public const int MIN_SI = 4;

    // each step moves one pixel; horizontal steps are for a right-facing lemming
    public static readonly int[][][] JumpPatterns =
    {
        new[] { new[] { 0, -1 }, new[] { 0, -1 }, new[] { 1, 0 }, new[] { 0, -1 }, new[] { 0, -1 }, new[] { 1, 0 } },
        new[] { new[] { 0, -1 }, new[] { 1, 0 }, new[] { 0, -1 }, new[] { 1, 0 }, new[] { 0, -1 }, new[] { 1, 0 } },
        new[] { new[] { 0, -1 }, new[] { 1, 0 }, new[] { 0, -1 }, new[] { 1, 0 }, new[] { 1, 0 }, new[] { 0, 0 } },
        new[] { new[] { 0, -1 }, new[] { 1, 0 }, new[] { 1, 0 }, new[] { 0, -1 }, new[] { 1, 0 }, new[] { 0, 0 } },
        new[] { new[] { 1, 0 }, new[] { 1, 0 }, new[] { 1, 0 }, new[] { 1, 0 }, new[] { 0, 0 }, new[] { 0, 0 } },
        new[] { new[] { 1, 0 }, new[] { 0, 1 }, new[] { 1, 0 }, new[] { 1, 0 }, new[] { 0, 1 }, new[] { 0, 0 } },
        new[] { new[] { 1, 0 }, new[] { 1, 0 }, new[] { 0, 1 }, new[] { 1, 0 }, new[] { 0, 1 }, new[] { 0, 0 } },
        new[] { new[] { 1, 0 }, new[] { 0, 1 }, new[] { 1, 0 }, new[] { 0, 1 }, new[] { 1, 0 }, new[] { 0, 1 } },
        new[] { new[] { 1, 0 }, new[] { 0, 1 }, new[] { 0, 1 }, new[] { 1, 0 }, new[] { 0, 1 }, new[] { 0, 1 } },
    };

    // blocker map field effects
    public const int BM_NONE = 0, BM_FORCELEFT = 2, BM_FORCERIGHT = 3, BM_BLOCKER = 10;

    public static readonly HashSet<string> AlwaysAnimate = new(StringComparer.Ordinal)
    {
        "NONE", "EXIT", "FORCELEFT", "FORCERIGHT", "WATER", "FIRE", "ONEWAYLEFT",
        "ONEWAYRIGHT", "ONEWAYDOWN", "UPDRAFT", "NOSPLAT", "SPLAT", "BACKGROUND", "PAINT", "PORTAL",
        "NEUTRALIZER", "DENEUTRALIZER", "REMOVESKILLS",
    };

    // TR.* by gadget effect
    public static readonly Dictionary<string, int> EffectTrigger = new(StringComparer.Ordinal)
    {
        ["EXIT"] = TR.EXIT, ["LOCKEXIT"] = TR.LOCKEDEXIT, ["WATER"] = TR.WATER, ["FIRE"] = TR.FIRE, ["TRAP"] = TR.TRAP,
        ["TRAPONCE"] = TR.TRAP, ["TELEPORT"] = TR.TELEPORT, ["UPDRAFT"] = TR.UPDRAFT, ["PICKUP"] = TR.PICKUP, ["BUTTON"] = TR.BUTTON,
        ["FLIPPER"] = TR.FLIPPER, ["NOSPLAT"] = TR.NOSPLAT, ["SPLAT"] = TR.SPLAT, ["FORCELEFT"] = TR.FORCELEFT,
        ["FORCERIGHT"] = TR.FORCERIGHT, ["ANIMATION"] = TR.ANIM, ["ANIMONCE"] = TR.ANIM, ["PORTAL"] = TR.PORTAL,
        ["NEUTRALIZER"] = TR.NEUTRALIZER, ["DENEUTRALIZER"] = TR.DENEUTRALIZER, ["ADDSKILL"] = TR.ADDSKILL,
        ["REMOVESKILLS"] = TR.REMOVESKILLS,
    };
}

// RemoveLemming modes
public static class RM { public const int NEUTRAL = 0, SAVE = 1, KILL = 2, ZOMBIE = 3; }

// trigger map bits (one map, a bit per TTriggerTypes entry that is a gadget area)
public static class TR
{
    public const int EXIT = 1, LOCKEDEXIT = 2, WATER = 4, FIRE = 8, TRAP = 16, TELEPORT = 32, UPDRAFT = 64, PICKUP = 128,
        BUTTON = 256, FLIPPER = 512, NOSPLAT = 1024, SPLAT = 2048, FORCELEFT = 4096, FORCERIGHT = 8192,
        ANIM = 16384, PORTAL = 32768, NEUTRALIZER = 65536, DENEUTRALIZER = 131072, ADDSKILL = 262144,
        REMOVESKILLS = 524288;
}

// sound cue names (the sound/ file names NeoLemmix uses)
public static class SFX
{
    public const string ASSIGN_SKILL = "mousepre", ASSIGN_FAIL = "assignfail", HITS_STEEL = "chink", LETSGO = "letsgo",
        ENTRANCE = "door", YIPPEE = "yippee", OHNO = "ohno", EXPLOSION = "explode", SPLAT = "splat",
        DROWNING = "glug", VAPORIZING = "fire", SWIMMING = "splash", FALLOUT = "die", ZOMBIE = "zombie",
        PICKUP = "oing2", EXIT_OPEN = "exitopen", BUILDER_WARNING = "ting", FIXING = "wrench",
        TIMEUP = "timeup", ADD_SKILL = "skill_add", REMOVE_SKILLS = "skill_remove", NEUTRALIZE = "neutralize",
        DENEUTRALIZE = "deneutralize", PORTAL = "portal", SKILLBUTTON = "changeop";
}

// The gfx/mask bitmaps the game carves with (sprites.js loadMasks).
public sealed class Masks
{
    public required Bitmap Bomber, Stoner, Basher, Fencer, Miner, Laser, Countdown;

    public static Masks Load(IFileSource io)
    {
        Bitmap Get(string n) => io.Image(StyleManager.AssetDir + "gfx/mask/" + n + ".png")
            ?? throw new FileNotFoundException("missing " + StyleManager.AssetDir + "gfx/mask/" + n + ".png - see neolemmix/README.md");
        var masks = new Masks
        {
            Bomber = Get("bomber"), Stoner = Get("stoner"), Basher = Get("basher"), Fencer = Get("fencer"),
            Miner = Get("miner"), Laser = Get("laser"), Countdown = Get("countdown"),
        };
        LevelBuilder.DigitFont = masks.Countdown; // 4x5 digits, used on gadgets and over nuked lemmings
        return masks;
    }
}
