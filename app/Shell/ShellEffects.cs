using System;
using Lemmix.App.Board;
using Lemmix.App.Ui.Windows;
using Lemmix.Store;

namespace Lemmix.App.Shell;

// app.js `state` for the render switches and the toggle* functions (~2384-2560, 5478-5489): read
// once from the command line or the store (Preferences: the URL first, then lem3d-*), kept in
// memory, written back the moment one is toggled, and applied to the level on the board. The VR
// settings window's rows act on it (IVrEffects).
public sealed class ShellEffects : IVrEffects
{
    readonly App _app;
    readonly Preferences _prefs;

    public bool Emboss { get; private set; }
    public bool Doors { get; private set; }
    public bool Smooth { get; private set; }
    public bool SmoothTerrain { get; private set; }
    public string ColorBlend { get; private set; }
    public bool SkillBar { get; private set; }
    public bool FlatSkills { get; private set; }
    public string Environment { get; private set; }
    public bool Shadows { get; private set; }
    public bool Music { get; private set; }

    public ShellEffects(App app, Preferences prefs)
    {
        _app = app;
        _prefs = prefs;
        Emboss = prefs.Emboss;
        Doors = prefs.Doors;
        Smooth = prefs.Smooth;
        SmoothTerrain = prefs.SmoothTerrain;
        ColorBlend = prefs.ColorBlend;
        SkillBar = prefs.SkillBar;
        FlatSkills = prefs.FlatSkills;
        Environment = prefs.Environment;
        Shadows = prefs.Shadows;
        Music = prefs.Music;
    }

    // what a level is built with
    public BoardSwitches Switches() => new()
    {
        Emboss = Emboss, Smooth = Smooth, SmoothTerrain = SmoothTerrain, Doors = Doors, Shadows = Shadows, Music = Music,
        ColorBlend = ColorBlend, Environment = Environment,
    };

    public void ToggleEmboss()
    {
        Emboss = !Emboss;
        _prefs.Emboss = Emboss;
        _app.Session?.SetEmboss(Emboss);
    }

    // the openings carve the terrain as the level is built: the level is built again
    public void ToggleDoors()
    {
        Doors = !Doors;
        _prefs.Doors = Doors;
        if (_app.LevelId != null) _app.RequestReload();
    }

    public void ToggleSmooth()
    {
        Smooth = !Smooth;
        _prefs.Smooth = Smooth;
        _app.Session?.SetSmooth(Smooth);
    }

    public void ToggleSmoothTerrain()
    {
        SmoothTerrain = !SmoothTerrain;
        _prefs.SmoothTerrain = SmoothTerrain;
        _app.Session?.SetSmoothTerrain(SmoothTerrain);
    }

    // COLOR_BLEND_LEVELS, round: off -> soft -> smooth
    public void ToggleColorBlend()
    {
        var levels = Preferences.ColorBlendLevels;
        int i = -1;
        for (int k = 0; k < levels.Count; k++) if (levels[k] == ColorBlend) i = k;
        ColorBlend = levels[(i + 1) % levels.Count];
        _prefs.ColorBlend = ColorBlend;
        _app.Session?.SetColorBlend(ColorBlend);
    }

    // the relief stands off the panel only in a headset
    public void ToggleSkillBar()
    {
        SkillBar = !SkillBar;
        _prefs.SkillBar = SkillBar;
        _app.Bar?.SetRelief(SkillBar && _app.Presenting);
    }

    public void ToggleFlatSkills()
    {
        FlatSkills = !FlatSkills;
        _prefs.FlatSkills = FlatSkills;
        _app.Bar?.SetFlatSkills(FlatSkills);
    }

    // Environment.MODES, round
    public void ToggleEnvironment()
    {
        var modes = EnvironmentView.Modes;
        int i = Array.IndexOf(modes, Environment);
        Environment = modes[(i + 1) % modes.Length];
        _prefs.Environment = Environment;
        if (_app.Session != null) _app.Session.SetEnvironment(Environment);
        else _app.Env.SetMode(Environment);
    }

    public void Recenter() => _app.Recenter();

    // the hotkeys' two (toggleShadows, toggleMusic)
    public void ToggleShadows()
    {
        Shadows = !Shadows;
        _prefs.Shadows = Shadows;
        _app.Session?.SetShadows(Shadows);
    }

    public void ToggleMusic()
    {
        Music = !Music;
        _prefs.Music = Music;
        _app.Session?.SetMusic(Music);
    }
}
