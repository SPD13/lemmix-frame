using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;
using Lemmix.App.Xr;
using Lemmix.Store;

namespace Lemmix.App.Shell;

// The command line (the web's URL parameters, 3d/README.md): `-- --level=<id> --nxrp=<file>
// --solution --speed=<n> --emboss[=0] --smooth --smoothterrain --colorblend=<off|soft|smooth>
// --doors --skillbar --flatskills --environment=<none|full> --shadows --music --assets=<dir>`,
// and the native `--scenery=off` (a gallery's scenery left out: envgen's rings, as the web).
// A switch named bare is on (?emboss), as the web's `setting` reads an empty value.
public sealed class ShellArgs
{
    // the switches app.js reads from the URL before localStorage (Preferences takes them as its params)
    public static readonly string[] SwitchNames =
        { "emboss", "smooth", "smoothterrain", "colorblend", "doors", "skillbar", "flatskills", "environment", "shadows", "music" };

    public string? Level;          // ?level=<id> (the tree's id, "pack/rank/file.nxlv")
    public string? Nxrp;           // ?nxrp=<file>: a NeoLemmix replay played from the start
    public bool Solution;          // ?solution: the level's stored solution, watched
    public double Speed = 1;       // ?speed=
    public string? Assets;         // the asset root (levels/, neolemmix/)
    public bool Scenery = true;    // --scenery=off: envgen's rings even where a gallery has a scenery
    public readonly Dictionary<string, string> Params = new(StringComparer.Ordinal);

    public static ShellArgs Parse(IEnumerable<string> args)
    {
        var a = new ShellArgs();
        foreach (var raw in args)
        {
            if (!raw.StartsWith("--", StringComparison.Ordinal)) continue;
            int eq = raw.IndexOf('=');
            string name = (eq < 0 ? raw[2..] : raw[2..eq]).ToLowerInvariant();
            string value = eq < 0 ? "" : raw[(eq + 1)..];
            switch (name)
            {
                case "level": a.Level = value.Length > 0 ? value : null; break;
                case "nxrp": a.Nxrp = value.Length > 0 ? value : null; break;
                case "solution": a.Solution = true; break;
                case "speed":
                    // parseFloat(params.get("speed") || "1")
                    a.Speed = double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && s > 0 ? s : 1;
                    break;
                case "assets": a.Assets = value.Length > 0 ? value : null; break;
                case "scenery": a.Scenery = value != "off" && value != "0"; break;
                default:
                    if (Array.IndexOf(SwitchNames, name) >= 0) a.Params[name] = value;
                    break;
            }
        }
        return a;
    }
}

// How the shell is built: the real thing (OpenXR, the store file, the user data's assets) or a
// test's / a shot's (scripted controllers, a memory store, a manual clock).
public sealed class AppOptions
{
    public ShellArgs Args = new();
    public IXrInput? Input;                 // null: OpenXR through the rig the app builds
    public Camera3D? Head;                  // with Input: the node that is the head (made if null)
    public IStorage? Store;                 // null: LocalStore at user://lem3d-store.json
    public string? AssetRoot;               // null: --assets, WEB_ASSETS, else <user data>/assets
    public string? UserDataDir;             // null: OS.GetUserDataDir()
    public Func<double>? Clock;             // ms; Time.GetTicksUsec by default
    public bool Manual;                     // no _Process: the caller runs Frame(now)
    public bool EnvironmentInBackground = true;
    public bool SpreadRestore;              // a jump's refresh spread over frames (the real app; tests check the board at once)

    public static AppOptions FromCommandLine(string[] userArgs) => new() { Args = ShellArgs.Parse(userArgs), SpreadRestore = true };

    // the asset root as the app resolves it: --assets, then WEB_ASSETS (tests, benchmarks), then the
    // user data's assets folder (where the setup page installs)
    public string ResolveAssetRoot(string userData)
    {
        if (!string.IsNullOrEmpty(AssetRoot)) return AssetRoot!;
        if (!string.IsNullOrEmpty(Args.Assets)) return Args.Assets!;
        var env = System.Environment.GetEnvironmentVariable("WEB_ASSETS");
        if (!string.IsNullOrEmpty(env)) return env;
        return System.IO.Path.Combine(userData, "assets");
    }
}
