using System;
using System.IO;
using Godot;
using Lemmix.App.Board;
using Lemmix.Engine;
using Lemmix.Io;
using Lemmix.Render;
using Lemmix.Ui;

namespace Lemmix.App.Test;

// The "skillbar" shot: a Game for a level (WEB_ASSETS; SHOT_LEVEL picks it), its skill bar placed
// as in a headset (0.78 m wide, 0.75 m ahead) with the relief on and flat skills, a skill selected,
// a button hovered (raised, with its label), seen from a little above. The shot prints what the
// bar holds and checks the ray hit through the hovered button's centre.
public static class SkillBarShot
{
    static string Assets => System.Environment.GetEnvironmentVariable("WEB_ASSETS")
        ?? Path.GetFullPath(Path.Combine(ProjectSettings.GlobalizePath("res://"), "..", "..", "LemmingsJS"));

    public static Viewport Make(Node root)
    {
        string id = System.Environment.GetEnvironmentVariable("SHOT_LEVEL") ?? "LemmingsPlus_All_20201114/Lemmings_Plus_III/Timid/Excavation_Expedition.nxlv";
        var io = new DiskFileSource(Assets);
        var styles = new StyleManager(io);
        var masks = Masks.Load(io);
        var (url, packDir) = Find(id);
        var level = LevelBuilder.Build(LevelBuilder.ParseLevel(io.Text(url)!), styles, id);
        string setName = level.Theme.Lemmings is { Length: > 0 } s ? s : "default";
        var sprites = new SpriteSet(io).Load(setName);
        var game = new Game(level, masks, l => SpriteSet.GeneratePickupIcons(l, sprites, l.Theme));
        var bar = new SkillBar(game, PanelAssets.Load(io, packDir), sprites);
        bar.SetRelief(true);
        bar.SetFlatSkills(true);
        bar.Place(0.6 * 1.3, 0, -0.75);
        game.Start();
        for (int i = 0; i < 150; i++) game.GameTimer.Tick();
        game.QueueCommand(new CommandSelectSkill(1));
        bar.SetViewRect(new LevelRect(0, 0, 320, 160));
        bar.Update();
        bar.SetHover(((5 * 16 + 8) / 416.0, 1 - 30 / 40.0));
        bar.Update();
        var view = new SkillBarView(bar);
        view.Sync();
        var tip = bar.HoverTip();
        GD.Print($"[lemmix] skillbar: {bar.TileReliefs?.Count ?? 0} relief parts, text {bar.TextMesh?.Geometry?.VertexCount ?? 0} vertices, hover {bar.HoverIndex} '{tip?.Text}'");

        var vp = new SubViewport { Size = new Vector2I(1280, 360), OwnWorld3D = true, TransparentBg = false, RenderTargetUpdateMode = SubViewport.UpdateMode.Always };
        var env = new WorldEnvironment { Environment = new Godot.Environment { BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new Color(0.1f, 0.12f, 0.16f), TonemapMode = Godot.Environment.ToneMapper.Linear } };
        vp.AddChild(env);
        vp.AddChild(view);
        var cam = new Camera3D { Fov = 21, Near = 0.01f, Far = 10, Position = new Vector3(0, 0.05f, 0) };
        vp.AddChild(cam);
        root.AddChild(vp);
        cam.LookAt(new Vector3(0, 0, -0.75f), Vector3.Up);
        cam.MakeCurrent();
        // the hit test from the camera through the hovered button's centre lands back on it
        var target = view.ToGlobal(new Vector3((float)((5 * 16 + 8) / 416.0 - 0.5) * 0.78f, (float)(0.5 - 30 / 40.0) * 0.78f * 40 / 416, -0.75f));
        var uv = view.HitUv(cam.GlobalPosition, (target - cam.GlobalPosition).Normalized());
        GD.Print($"[lemmix] skillbar hit: {uv}");
        return vp;
    }

    // the level's url and, for a pack with its own panel graphics, the pack's directory
    static (string Url, string? PackDir) Find(string id)
    {
        var index = Json.ParseString(File.ReadAllText(Path.Combine(Assets, "levels", "index.json"))).AsGodotDictionary();
        (string, string?)? Walk(Godot.Collections.Dictionary n, string? packDir)
        {
            if (n.TryGetValue("kind", out var k) && (string)k == "pack")
                packDir = n.TryGetValue("panel", out var p) && (bool)p && n.TryGetValue("dir", out var d) ? (string)d : null;
            if (n.TryGetValue("levels", out var lv))
                foreach (var l in lv.AsGodotArray()) { var e = l.AsGodotDictionary(); if ((string)e["id"] == id) return ((string)e["url"], packDir); }
            if (n.TryGetValue("children", out var ch))
                foreach (var c in ch.AsGodotArray()) { var r = Walk(c.AsGodotDictionary(), packDir); if (r != null) return r; }
            return null;
        }
        return Walk(index, null) ?? throw new InvalidDataException("no level " + id);
    }
}
