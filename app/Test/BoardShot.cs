using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using Lemmix.App.Board;
using Lemmix.App.Session;
using Lemmix.Engine;
using Lemmix.Io;

namespace Lemmix.App.Test;

// The "board*" shots: a level's diorama after N ticks, seen through the web desktop's default
// framing (frameDesktopCamera: the camera 120 px above and 420 px in front of the board's
// middle, looking at the slab's middle, 50 degrees of vertical field), so oracle/boardshots.js
// can shoot the web page identically. Presets name the level, the ticks, the switches (the web's
// URL syntax), clear physics, a hovered lemming with a skill (the shadows) and whether the
// level's stored solution replays (the markers). SHOT_LEVEL / SHOT_TICKS / SHOT_QUERY / SHOT_W /
// SHOT_H override. The clock is frozen at 2500 ms (the hue clear physics walks, the markers' pulse).
public static class BoardShot
{
    public sealed record Preset(string Level, int Ticks, string Query, bool Cpm = false, string? HoverSkill = null, bool Solution = true);

    public const string Builders = "Lemmings_Redux/Gentle/Builders_will_help_you_here.nxlv";
    public const string Beast = "Lemmings_Redux/Gentle/A_Beast_of_a_level.nxlv";
    public const double FrozenClock = 2500;

    public static readonly Dictionary<string, Preset> Presets = new(StringComparer.Ordinal)
    {
        ["board"] = new(Builders, 420, "environment=none"),
        ["board-env"] = new(Builders, 420, "environment=full"),
        ["board-cpm"] = new(Builders, 420, "environment=none", Cpm: true),
        ["board-shadows"] = new(Builders, 300, "environment=none", HoverSkill: "BUILDER"),
        ["board-plain"] = new(Builders, 420, "environment=none&emboss=0&smooth=0&smoothterrain=0&colorblend=off&doors=0"),
        ["board-beast"] = new(Beast, 300, "environment=full"),
    };

    static string Env(string name, string dflt) => System.Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : dflt;

    // the web's URL switches (app.js `setting`, colorblend and environment names)
    public static BoardSwitches SwitchesFrom(string query)
    {
        var s = new BoardSwitches();
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            string k = kv[0].ToLowerInvariant(), v = kv.Length > 1 ? kv[1].ToLowerInvariant() : "";
            bool on = v is "" or "1" or "on" or "true" or "yes";
            switch (k)
            {
                case "emboss": s.Emboss = on; break;
                case "smooth": s.Smooth = on; break;
                case "smoothterrain": s.SmoothTerrain = on; break;
                case "doors": s.Doors = on; break;
                case "shadows": s.Shadows = on; break;
                case "music": s.Music = on; break;
                case "colorblend": s.ColorBlend = v switch { "off" or "0" => "off", "smooth" => "smooth", _ => "soft" }; break;
                case "environment": s.Environment = v is "none" or "off" or "0" or "false" ? "none" : v == "fog" ? "fog" : "full"; break;
            }
        }
        return s;
    }

    public static GameSession? Last;

    public static Viewport Make(Node root, string name)
    {
        var p = Presets[name];
        string id = Env("SHOT_LEVEL", p.Level);
        int ticks = int.Parse(Env("SHOT_TICKS", p.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture)), System.Globalization.CultureInfo.InvariantCulture);
        string query = Env("SHOT_QUERY", p.Query);
        int w = int.Parse(Env("SHOT_W", "1280"), System.Globalization.CultureInfo.InvariantCulture);
        int h = int.Parse(Env("SHOT_H", "720"), System.Globalization.CultureInfo.InvariantCulture);
        var io = new DiskFileSource(TerrainShot.Assets);

        var vp = new SubViewport
        {
            Size = new Vector2I(w, h), OwnWorld3D = true, TransparentBg = false, Msaa3D = Env("SHOT_MSAA", "4") == "0" ? Viewport.Msaa.Disabled : Viewport.Msaa.Msaa4X,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
        };
        var world = new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = BasicShader.Rgb(EnvironmentView.ENV_SCENE_COLOR),
                TonemapMode = Godot.Environment.ToneMapper.Linear,
            },
        };
        vp.AddChild(world);
        var diorama = new Node3D { Name = "dioramaRoot" };
        vp.AddChild(diorama);
        var env = new EnvironmentView { SceneEnvironment = world.Environment };
        vp.AddChild(env);
        var cam = new Camera3D { Fov = 50, Near = 1, Far = 10000 };
        vp.AddChild(cam);
        root.AddChild(vp);

        string? solution = p.Solution ? io.Text("solutions/" + Path.ChangeExtension(id, ".nxrp")) : null;
        if (p.Solution && solution == null) GD.Print("[lemmix] board shot: no stored solution for " + id);
        var loc = LevelLocation.Find(io.Text("levels/index.json")!, id)!;
        var session = GameSession.Load(new SessionOptions
        {
            Assets = io, LevelId = id, Switches = SwitchesFrom(query), DioramaRoot = diorama, Environment = env,
            EnvironmentInBackground = false, Clock = () => FrozenClock, ReplayText = solution, ReplayKind = "solution",
            PanelAssets = Lemmix.Ui.PanelAssets.Load(io, loc.PackDir), ProfilesDir = TerrainShot.Profiles,
        });
        vp.AddChild(session);
        session.Eye = cam;
        Last = session;
        var level = session.Level;
        var game = session.Game;

        // frameDesktopCamera
        cam.Position = new Vector3(level.Width / 2f, level.Height / 2f + 120, 420);
        cam.LookAt(new Vector3(level.Width / 2f, level.Height / 2f, (float)(BoardZ.TERRAIN_DEPTH / 2)), Vector3.Up);
        cam.MakeCurrent();

        // paused at frame 0, then N ticks (the web: gotoFrame(0, true) and timer.tick() N times)
        game.GameTimer.Suspend();
        for (int i = 0; i < ticks; i++) game.GameTimer.Tick();
        if (p.Cpm) game.SetClearPhysics(true);
        if (p.HoverSkill != null)
        {
            int k = game.Sim.ActiveSkills.IndexOf(p.HoverSkill);
            game.Skills.SetSelectedSkill(k);
            var lem = game.Sim.Lemmings.FirstOrDefault(L => !L.Removed && L.Action == BA.WALKING);
            if (lem != null) session.SetPointer(new Vector2I(lem.X, lem.Y - 5));
            GD.Print($"[lemmix] board shot: hover lemming {lem?.Id} at {lem?.X},{lem?.Y} with {p.HoverSkill} (skill {k}), shadow parts {(session.Board.ShadowOverlay.Cuts.Visible ? 1 : 0)}/{(session.Board.ShadowOverlay.Bricks.Visible ? 1 : 0)}/{(session.Board.ShadowOverlay.Paths.Visible ? 1 : 0)}");
        }
        session.Step(FrozenClock);
        var b = session.Board;
        GD.Print($"[lemmix] board shot {name}: {id} tick {game.GameTimer.GetGameTicks()} lemmings {b.Lemmings.ActiveCount} objects {b.Objects.ActiveCount} portals {b.Portals.Count} stacks {b.Stacks.Count} water {b.WaterMeshes.Count} markers {b.Markers.MarkerCount} env {env.Mode}/{env.CurrentGallery?.Source}/{(env.CurrentGallery?.FogOnly == true ? "fog" : "pictures")} scene #{env.SceneColor:x6}");
        return vp;
    }
}
