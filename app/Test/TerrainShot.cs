using System;
using System.IO;
using Godot;
using Lemmix.App.Board;
using Lemmix.Engine;
using Lemmix.Io;
using Lemmix.Render;

namespace Lemmix.App.Test;

// The "terrain" shot: a level's terrain in Godot, looked at square-on through an orthographic
// camera, one screen pixel per level pixel. The front faces are at full shade, so the picture must
// be the level's own (Level.GroundImage) wherever the level is solid: the shot writes the diff
// count, which checks the orientation, the UVs and the colour pipeline (three's raw multiply).
public static class TerrainShot
{
    public static string Assets => System.Environment.GetEnvironmentVariable("WEB_ASSETS")
        ?? Path.GetFullPath(Path.Combine(ProjectSettings.GlobalizePath("res://"), "..", "..", "LemmingsJS"));
    public static string Profiles => ProjectSettings.GlobalizePath("res://Data/profiles");

    public static Viewport Make(Node root)
    {
        string id = System.Environment.GetEnvironmentVariable("SHOT_LEVEL") ?? "Lemmings_Redux/Gentle/A_Beast_of_a_level.nxlv";
        var io = new DiskFileSource(Assets);
        var styles = new StyleManager(io);
        Masks.Load(io);
        string url = FindUrl(id);
        var level = LevelBuilder.Build(LevelBuilder.ParseLevel(io.Text(url)!), styles, id);
        var gd = GroundData.FromLevel(level);
        var profile = DepthProfile.Load(Profiles, gd);
        var depthMap = Depth.BuildDepthMap(level, gd, profile);
        var tm = new TerrainMesh(level, depthMap, null, null, null, null);
        tm.FlushDirty(int.MaxValue);
        var view = new TerrainView(tm);
        view.Sync();
        int meshes = 0, verts = 0;
        foreach (var c in tm.ChunkMeshes) if (c != null) { meshes++; verts += c.VertexCount; }
        GD.Print($"[lemmix] terrain: {meshes} chunk meshes, {verts} vertices, {view.GetChildCount()} nodes");

        var vp = new SubViewport { Size = new Vector2I(level.Width, level.Height), OwnWorld3D = true, TransparentBg = false, RenderTargetUpdateMode = SubViewport.UpdateMode.Always };
        var env = new WorldEnvironment { Environment = new Godot.Environment { BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = Colors.Black, TonemapMode = Godot.Environment.ToneMapper.Linear } };
        vp.AddChild(env);
        var board = new Node3D { Scale = new Vector3(1, -1, 1) }; // game pixel space, y down, as the web's group
        board.AddChild(view);
        vp.AddChild(board);
        var cam = new Camera3D
        {
            Projection = Camera3D.ProjectionType.Orthogonal, Size = level.Height, Near = 0.1f, Far = 1000,
            Position = new Vector3(level.Width / 2f, -level.Height / 2f, 200),
        };
        vp.AddChild(cam);
        root.AddChild(vp);
        cam.MakeCurrent(); // only once it is in the tree
        Shots.After = img => Compare(img, level);
        return vp;
    }

    static string FindUrl(string id)
    {
        var index = Godot.Json.ParseString(File.ReadAllText(Path.Combine(Assets, "levels", "index.json"))).AsGodotDictionary();
        string? Walk(Godot.Collections.Dictionary n)
        {
            if (n.TryGetValue("levels", out var lv))
                foreach (var l in lv.AsGodotArray()) { var d = l.AsGodotDictionary(); if ((string)d["id"] == id) return (string)d["url"]; }
            if (n.TryGetValue("children", out var ch))
                foreach (var c in ch.AsGodotArray()) { var r = Walk(c.AsGodotDictionary()); if (r != null) return r; }
            return null;
        }
        return Walk(index) ?? throw new InvalidDataException("no level " + id);
    }

    static void Compare(Image img, Level level)
    {
        int solid = 0, off = 0;
        var g = level.GroundImage;
        for (int y = 0; y < level.Height; y++)
            for (int x = 0; x < level.Width; x++)
            {
                int i = (y * level.Width + x) * 4;
                if (g[i + 3] == 0) continue;
                solid++;
                var c = img.GetPixel(x, y);
                int dr = Math.Abs((int)Math.Round(c.R * 255) - g[i]), dg = Math.Abs((int)Math.Round(c.G * 255) - g[i + 1]), db = Math.Abs((int)Math.Round(c.B * 255) - g[i + 2]);
                if (Math.Max(dr, Math.Max(dg, db)) > 3) off++;
            }
        GD.Print($"[lemmix] terrain compare: {off} of {solid} solid pixels differ from the level picture");
        var expected = Image.CreateFromData(level.Width, level.Height, false, Image.Format.Rgba8, level.GroundImage);
        expected.SavePng("/out/terrain-expected.png");
        int shown = 0;
        for (int y = 0; y < level.Height && shown < 6; y += 23)
            for (int x = 0; x < level.Width && shown < 6; x += 37)
            {
                int i = (y * level.Width + x) * 4;
                if (g[i + 3] == 0) continue;
                var c = img.GetPixel(x, y);
                GD.Print($"[lemmix]   ({x},{y}) level {g[i]},{g[i + 1]},{g[i + 2]}  render {(int)Math.Round(c.R * 255)},{(int)Math.Round(c.G * 255)},{(int)Math.Round(c.B * 255)}");
                shown++;
            }
    }
}
