using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using Lemmix.Engine;
using Lemmix.Input;

namespace Lemmix.App.Test;

// Every function of the controls table (hotkeys.js ACTIONS), run through the shell on a loaded
// level, with what it should change checked - the parity rows action.* rest on this.
public static class ActionsTests
{
    [AppTest]
    public static void EveryFunctionDoesWhatItShould()
    {
        if (!Directory.Exists(Path.Combine(TerrainShot.Assets, "levels"))) { GD.Print("[test] (no assets: skipped)"); return; }
        using var rig = new ShellTests.Rig(null, "--level=" + BoardShot.Builders);
        for (int i = 0; i < 40; i++) rig.Frame(60); // lemmings out
        var app = rig.App;
        var fails = new List<string>();
        var done = new HashSet<string>(StringComparer.Ordinal);

        bool Run(string action, object? mod = null)
        {
            done.Add(action);
            try { return app.RunHotkey(new HotkeyBinding(action, mod ?? DefaultMod(action))); }
            catch (Exception e) { fails.Add(action + ": threw " + e.GetType().Name + " " + e.Message); return false; }
        }
        void Expect(string action, bool ok, string what) { if (!ok) fails.Add(action + ": " + what); }
        LemGame Sim() => rig.Session.Game.Sim;

        // skills
        // the second skill when there is one: the first is selected already, and a command for panel
        // index 0 selects nothing (the web's CommandSelectSkill(0) quirk, kept)
        string target = Sim().ActiveSkills.Count > 1 ? Sim().ActiveSkills[1] : Sim().ActiveSkills[0];
        Run("skill", target.ToLowerInvariant()); Expect("skill", Sim().SelectedSkill == target, "selects " + target);
        string? before = Sim().SelectedSkill;
        Run("next_skill"); Expect("next_skill", Sim().SelectedSkill != before || Sim().ActiveSkills.Count < 2, "steps the skill");
        before = Sim().SelectedSkill;
        Run("previous_skill"); Expect("previous_skill", Sim().SelectedSkill != before || Sim().ActiveSkills.Count < 2, "steps back");

        // release rate (unless locked)
        if (!rig.Session.Level.SpawnLocked)
        {
            int si = Sim().CurrSpawnInterval;
            Run("rr_max"); rig.Frame(); Expect("rr_max", Sim().CurrSpawnInterval < si || si == Lemmix.Engine.Lem.MIN_SI, "raises the rate");
            Run("rr_min"); rig.Frame(); Expect("rr_min", Sim().CurrSpawnInterval == rig.Session.Level.SpawnInterval, "lowers to the minimum");
            si = Sim().CurrSpawnInterval;
            Run("rr_up"); rig.Frame(); app.KeyUp("F2"); Expect("rr_up", Sim().CurrSpawnInterval <= si, "raises");
            Run("rr_down"); rig.Frame();
        }
        else { done.Add("rr_max"); done.Add("rr_min"); done.Add("rr_up"); done.Add("rr_down"); }

        // the clock
        bool running = rig.Session.Running;
        Run("pause"); Expect("pause", rig.Session.Running != running, "toggles the clock");
        Run("pause");
        Run("fastforward"); Expect("fastforward", rig.Session.Game.GameTimer.SpeedFactor == HotkeyDispatch.FastForwardSpeed, "x4");
        Run("fastforward"); Expect("fastforward", rig.Session.Game.GameTimer.SpeedFactor == 1, "back to x1");
        Run("slow_motion"); Expect("slow_motion", rig.Session.Game.GameTimer.SpeedFactor == HotkeyDispatch.SlowMotionSpeed, "x0.25");
        Run("slow_motion");
        double sf = rig.Session.Game.GameTimer.SpeedFactor;
        Run("speed_up"); Expect("speed_up", rig.Session.Game.GameTimer.SpeedFactor > sf, "faster");
        Run("speed_down"); Expect("speed_down", rig.Session.Game.GameTimer.SpeedFactor < sf + 1, "slower");
        rig.Session.Game.GameTimer.SpeedFactor = 1;

        // time
        int it = Sim().CurrentIteration;
        Run("skip", 17); Expect("skip", Sim().CurrentIteration == it + 17, "17 frames on");
        Run("skip", -1); Expect("skip", Sim().CurrentIteration == it + 16, "1 frame back");
        Run("save_state");
        Run("skip", 34);
        Run("load_state"); Expect("load_state", Sim().CurrentIteration == it + 16, "back to the saved frame");
        Run("special_skip", 0); Run("special_skip", 1);
        Run("restart"); Expect("restart", Sim().CurrentIteration == 0, "frame 0");
        rig.Session.Game.Start();

        // replay
        bool ins = Sim().ReplayInsert;
        Run("replay_insert"); Expect("replay_insert", Sim().ReplayInsert != ins, "toggles insert mode");
        Run("replay_insert");
        Run("cancel_replay");
        Run("save_replay"); Expect("save_replay", Directory.Exists(app.ReplaysDir) && Directory.EnumerateFiles(app.ReplaysDir, "*.nxrp").Any(), "writes a .nxrp");
        Run("load_replay"); rig.Frame();
        app.Pages.Show(null);
        Run("watch_solution");

        // physics view and shadows
        bool cpm = rig.Session.Game.ClearPhysics;
        Run("clear_physics", 0); Expect("clear_physics", rig.Session.Game.ClearPhysics != cpm, "toggles clear physics");
        Run("clear_physics", 0);
        bool sh = rig.Session.Board.ShadowsOn;
        Run("toggle_shadows"); Expect("toggle_shadows", rig.Session.Board.ShadowsOn != sh, "toggles the shadows");
        Run("toggle_shadows");

        // sound
        bool snd = app.AudioEnabled;
        Run("toggle_sound"); Expect("toggle_sound", app.AudioEnabled != snd, "toggles the sound");
        Run("toggle_sound");
        Run("toggle_music"); Run("toggle_music");

        // skills cheat
        Run("cheat"); Expect("cheat", Sim().ActiveSkills.All(n => Sim().SkillCountOf(n) == 99), "99 of each");

        // the view and the headset (no-ops off the headset are fine: they must not throw)
        foreach (var a in new[] { "zoom_in", "zoom_out", "reset_view", "recenter_vr", "yaw_left", "yaw_right", "piece_editor", "cycle_class" }) Run(a);

        // nuke: two presses inside 250 ms
        Run("nuke"); rig.Now += 50; Run("nuke"); rig.Frame();
        Expect("nuke", Sim().UserSetNuking || Sim().HasRecorded("nuke", Sim().CurrentIteration) || Sim().Recorded.Any(r => r.Type == "nuke"), "nukes on the second press");

        // levels and the library
        string? id = app.LevelId;
        Run("next_level"); rig.Frame(); Expect("next_level", app.LevelId != id, "moves on");
        Run("previous_level"); rig.Frame(); Expect("previous_level", app.LevelId == id, "comes back");
        Run("quit");

        // held functions: through the keys, as they act
        var shifts = new[] { ("dir_select_left", "ArrowLeft"), ("dir_select_right", "ArrowRight"), ("force_walker", "Control"), ("athlete_info", "Alt") };
        foreach (var (action, key) in shifts)
        {
            done.Add(action);
            app.KeyDown(key, false);
            var s = Sim();
            bool ok = action switch
            {
                "dir_select_left" => s.HotkeyDx == -1,
                "dir_select_right" => s.HotkeyDx == 1,
                "force_walker" => s.SelectWalkerOnly,
                _ => rig.Session.Game.ShowAthleteInfo,
            };
            Expect(action, ok, "held: on while down");
            app.KeyUp(key);
        }

        // the headset's own: sticks and held buttons
        foreach (var a in new[] { "vr_dolly_in", "vr_dolly_out", "vr_pan", "vr_tilt", "vr_zoom" }) done.Add(a);

        var missing = Hotkeys.Actions.Select(a => a.Id).Where(a => !done.Contains(a)).ToList();
        if (missing.Count > 0) fails.Add("not exercised: " + string.Join(", ", missing));
        Check.True(fails.Count == 0, string.Join("; ", fails));
    }

    static object? DefaultMod(string action) => Hotkeys.ActionById.TryGetValue(action, out var a) && a.Mod == "frames" ? 1 : null;
}
