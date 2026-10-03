using System;
using System.IO;
using System.Linq;
using Godot;
using Lemmix.App.Board;
using Lemmix.App.Session;
using Lemmix.Engine;
using Lemmix.Io;
using Lemmix.Render;

namespace Lemmix.App.Test;

// The board and the session, structurally, headless: what loadLevel puts in the worldGroup, the
// frame loop's accumulator, the interpolation, where sounds are placed, a jump in time put back
// into the scene, and what each switch rebuilds. Needs the assets (WEB_ASSETS, by default the web
// working copy next to this repo); skipped without them.
public static class BoardTests
{
    const string Level = BoardShot.Builders;

    sealed class Rig : IDisposable
    {
        public double Now = 1000;
        public readonly Node3D Root = new() { Name = "test-diorama" };
        public readonly EnvironmentView Env = new();
        public readonly GameSession Session;
        public Rig(string query = "environment=none", string? replay = null)
        {
            var tree = (SceneTree)Godot.Engine.GetMainLoop();
            tree.Root.AddChild(Root);
            tree.Root.AddChild(Env);
            var io = new DiskFileSource(TerrainShot.Assets);
            Session = GameSession.Load(new SessionOptions
            {
                Assets = io, LevelId = Level, Switches = BoardShot.SwitchesFrom(query), DioramaRoot = Root, Environment = Env,
                EnvironmentInBackground = false, Clock = () => Now, ReplayText = replay, ReplayKind = "solution",
            });
        }
        public BoardScene Board => Session.Board;
        public Game Game => Session.Game;
        public void Dispose()
        {
            Session.Close();
            Session.Free();
            Root.QueueFree();
            Env.QueueFree();
        }
    }

    static bool HaveAssets()
    {
        bool ok = File.Exists(Path.Combine(TerrainShot.Assets, "levels", "index.json"));
        if (!ok) GD.Print("[test] (no assets: skipped)");
        return ok;
    }

    static string? Solution(string id) => new DiskFileSource(TerrainShot.Assets).Text("solutions/" + Path.ChangeExtension(id, ".nxrp"));

    // the worldGroup's transform, the chunks as nodes, an opening per entrance/exit, a stack and a
    // body per stretch of water, the overlays hidden, the lemmings' pool as the capture
    [AppTest]
    public static void BuildsTheWorldGroup()
    {
        if (!HaveAssets()) return;
        using var rig = new Rig();
        var b = rig.Board;
        var lvl = rig.Session.Level;
        Check.Near(new Vector3(0, lvl.Height, 0), b.Transform.Origin, "worldGroup at y = level height");
        Check.Near(new Vector3(1, -1, 1), new Vector3(b.Transform.Basis.X.X, b.Transform.Basis.Y.Y, b.Transform.Basis.Z.Z), "worldGroup flipped in y");
        int chunks = b.Terrain.ChunkMeshes.Count(c => c != null);
        int chunkNodes = b.TerrainView.GetChildren().OfType<MeshInstance3D>().Count(m => m.Mesh != null);
        Check.True(chunks > 0, "terrain has chunk meshes");
        Check.True(chunkNodes >= chunks, $"a node per chunk mesh ({chunkNodes} nodes, {chunks} chunks)");
        Check.Equal(2, b.Portals.Count, "the hatch and the exit are openings");
        Check.Equal(4, b.Stacks.Count, "four stretches of water drawn as stacks");
        Check.Equal(4, b.WaterMeshes.Count, "four water bodies");
        foreach (var s in b.Stacks) Check.Equal(Portals.WaveSliceCount(), b.StackMeshes(s).Count, "a mesh per slice");
        Check.True(!b.CpmOverlay.Visible, "clear physics layer hidden");
        Check.True(!b.Ring.Visible, "ring hidden");
        Check.True(b.PortalFlaps(b.Portals.First(p => p.Hatch != null))!.Count == 2, "a hatch has two flaps");
        Check.Equal(0, b.Lemmings.ActiveCount, "no lemming before the first tick");
        for (int i = 0; i < 120; i++) rig.Game.GameTimer.Tick();
        int alive = rig.Game.Sim.Lemmings.Count(L => !L.Removed);
        Check.True(alive > 0, "lemmings out after 120 ticks");
        Check.Equal(b.LemCapture.Items.Count, b.Lemmings.ActiveCount, "a pool mesh per captured lemming draw");
        Check.Equal(b.LemCapture.Items.Count, b.Lemmings.Meshes.Take(b.Lemmings.ActiveCount).Count(m => m.Visible), "every captured draw shown");
        // the objects drawn as openings or stacks are not also drawn flat
        Check.Equal(0, b.Objects.ActiveCount, "every object of this level is an opening or a stack");
    }

    // the fixed step: 17 ticks a second times the speed, a stalled frame owes at most five ticks,
    // a paused clock owes nothing; a hold remembers whether the clock ran
    [AppTest]
    public static void AccumulatorTicksAtTheWebRate()
    {
        if (!HaveAssets()) return;
        using var rig = new Rig();
        var s = rig.Session;
        var timer = rig.Game.GameTimer;
        int Run(double seconds, double frameMs)
        {
            int n = 0;
            for (double t = 0; t < seconds * 1000 - 1e-9; t += frameMs) { rig.Now += frameMs; n += s.Step(rig.Now); }
            return n;
        }
        s.Step(rig.Now);
        int at1 = Run(2, 1000.0 / 90);
        Check.True(Math.Abs(at1 - 34) <= 1, $"speed 1: 34 ticks in 2 s at 90 Hz, got {at1}");
        timer.SpeedFactor = GameSession.FF_SPEED;
        int at4 = Run(1, 1000.0 / 90);
        Check.True(Math.Abs(at4 - 68) <= 1, $"fast forward: 68 ticks in 1 s, got {at4}");
        timer.SpeedFactor = GameSession.SLOWMO_SPEED;
        int slow = Run(4, 1000.0 / 72);
        Check.True(Math.Abs(slow - 17) <= 1, $"slow motion: 17 ticks in 4 s, got {slow}");
        timer.SpeedFactor = 1;
        rig.Now += 1000; // one stalled frame of a second
        int stalled = s.Step(rig.Now);
        Check.Equal(5, stalled, "a stalled frame runs five ticks at most");
        s.TogglePause();
        Check.Equal(0, Run(1, 1000.0 / 90), "paused: no tick");
        Check.Equal(0.0, s.TickDebt, "paused: no debt");
        s.Hold("catalog");
        Check.True(!s.Running, "held");
        s.Release("catalog");
        Check.True(!s.Running, "a game paused before the hold stays paused");
        s.TogglePause();
        s.Hold("catalog"); s.Hold("confirm");
        s.Release("catalog");
        Check.True(!s.Running, "still held by the other");
        s.Release("confirm");
        Check.True(s.Running, "the last holder puts the clock back");
        s.ToggleSpeed(GameSession.FF_SPEED);
        Check.Equal(GameSession.FF_SPEED, timer.SpeedFactor, "fast forward on");
        s.ToggleSpeed(GameSession.FF_SPEED);
        Check.Equal(1.0, timer.SpeedFactor, "fast forward off");
    }

    // a lemming slides between its last two tick positions by alpha = time since the tick / tick
    [AppTest]
    public static void InterpolatesBetweenTicks()
    {
        if (!HaveAssets()) return;
        using var rig = new Rig();
        var s = rig.Session;
        var b = rig.Board;
        rig.Game.GameTimer.Suspend();
        for (int i = 0; i < 150; i++) rig.Game.GameTimer.Tick();
        var before = Enumerable.Range(0, b.Lemmings.ActiveCount).Select(b.Lemmings.SlotPosition).ToList();
        rig.Game.GameTimer.Tick(); // at rig.Now
        var after = Enumerable.Range(0, b.Lemmings.ActiveCount).Select(b.Lemmings.SlotPosition).ToList();
        int moved = Enumerable.Range(0, Math.Min(before.Count, after.Count)).FirstOrDefault(i => before[i] != after[i], -1);
        Check.True(moved >= 0, "a lemming moved in a tick");
        rig.Game.GameTimer.Continue();
        double tickMs = rig.Game.GameTimer.TimePerFrameMs;
        rig.Now += tickMs * 0.5;
        s.Step(rig.Now - 0.0001); // the first Step: no debt yet
        s.Step(rig.Now);
        Check.True(Math.Abs(s.Alpha - 0.5) < 1e-3, $"alpha half a tick on, got {s.Alpha}");
        var mid = b.Lemmings.SlotPosition(moved);
        Check.True(Math.Abs(mid.X - (before[moved].X + after[moved].X) / 2) < 1e-3 && Math.Abs(mid.Y - (before[moved].Y + after[moved].Y) / 2) < 1e-3,
            $"half way: {before[moved]} -> {after[moved]} at {mid}");
    }

    // a cue is played where its sim point is, on the lemmings' plane, through the board's placement
    [AppTest]
    public static void SoundCuesAtTheirPlace()
    {
        if (!HaveAssets()) return;
        using var rig = new Rig();
        var s = rig.Session;
        var lvl = s.Level;
        Check.Near(new Vector3(100, lvl.Height - 50, (float)BoardZ.LEMMING_Z), s.SfxPos(100, 50), "desktop: pixels, y up");
        rig.Root.Transform = new Transform3D(Basis.FromScale(Vector3.One * VrManagerScale), new Vector3(0.2f, 1.1f, -0.8f));
        var p = s.SfxPos(100, 50);
        Check.Near(new Vector3(0.2f + 100 * VrManagerScale, 1.1f + (lvl.Height - 50) * VrManagerScale, -0.8f + (float)BoardZ.LEMMING_Z * VrManagerScale), p, "headset: through the diorama's placement");
        rig.Root.Transform = Transform3D.Identity;
        // the sim's own cues arrive with the lemming's place
        for (int i = 0; i < 400 && s.LastCue == null; i++) rig.Game.GameTimer.Tick();
        Check.True(s.LastCue != null, "a cue in the first ticks");
    }
    const float VrManagerScale = Lemmix.App.Xr.VrManager.VR_PIXEL_SCALE;

    // a jump in time: the terrain resynced from the sim's own arrays, the pool's memory dropped,
    // the frame drawn as a tick would, the shell told
    [AppTest]
    public static void RestoreResyncsTheScene()
    {
        if (!HaveAssets()) return;
        using var rig = new Rig(replay: Solution(Level));
        var b = rig.Board;
        int restored = 0;
        rig.Session.Restored += _ => restored++;
        rig.Game.GameTimer.Suspend();
        for (int i = 0; i < 500; i++) rig.Game.GameTimer.Tick();
        var dug = (byte[])rig.Session.Level.GroundImage.Clone();
        int syncs = b.SyncCount;
        rig.Game.GotoFrame(10, true);
        Check.Equal(1, restored, "the shell hears of the jump");
        Check.True(b.SyncCount > syncs, "the frame is drawn again");
        Check.True(!dug.SequenceEqual(rig.Session.Level.GroundImage), "the builders' bricks are gone at frame 10");
        // the terrain texture follows the level picture again
        var tex = b.Terrain.TexData; var g = rig.Session.Level.GroundImage;
        int off = 0;
        for (int i = 0; i < g.Length; i += 4) if ((g[i + 3] != 0) != (tex[i + 3] != 0)) off++;
        Check.Equal(0, off, "texture alpha = the level's solidity after the jump");
        Check.Equal(rig.Game.Sim.Lemmings.Count(L => !L.Removed), b.Lemmings.ActiveCount, "the lemmings of frame 10");
    }

    // each switch rebuilds what the web's rebuilds: relief and smoothing re-mesh the terrain, the
    // colour blend re-dresses the scenery, edge smoothing re-cuts the openings, the doors ask for a
    // reload, the room goes and comes, clear physics shows its layer
    [AppTest]
    public static void SwitchesRebuildWhatTheWebRebuilds()
    {
        if (!HaveAssets()) return;
        using var rig = new Rig();
        var s = rig.Session;
        var b = rig.Board;
        for (int i = 0; i < 60; i++) rig.Game.GameTimer.Tick();
        int rebuilt = 0;
        b.Terrain.ChunkRebuilt += _ => rebuilt++;
        s.SetSmooth(false);
        Check.True(rebuilt > 0, "smooth relief off re-meshes the terrain");
        rebuilt = 0;
        s.SetEmboss(false);
        Check.True(rebuilt >= 0, "emboss off sets the relief"); // a level with no embossed piece may change nothing
        var portal = b.Portals.First(p => p.Rebuild != null);
        var mesh = b.PortalMesh(portal).Mesh;
        s.SetSmoothTerrain(false);
        Check.True(!ReferenceEquals(mesh, b.PortalMesh(portal).Mesh), "edge smoothing re-cuts the opening");
        var mat = b.PortalMesh(portal).MaterialOverride;
        s.SetColorBlend("off");
        Check.Equal(0.0, b.Cache.BlendSoftness, "colour blend off");
        Check.True(!ReferenceEquals(mat, b.PortalMesh(portal).MaterialOverride), "the opening wears its plain material");
        bool reload = false;
        s.ReloadRequested += () => reload = true;
        s.SetDoors(false);
        Check.True(reload, "the doors switch asks for the level again");
        s.SetEnvironment("full");
        Check.True(rig.Env.Visible && rig.Env.Ready, "the room is up");
        Check.True(rig.Env.SceneColor != EnvironmentView.ENV_SCENE_COLOR, "the background is the fog");
        s.SetEnvironment("fog");
        Check.True(rig.Env.Visible && rig.Env.Ready, "the fog room is up");
        Check.True(rig.Env.CurrentGallery?.FogOnly == true && rig.Env.CurrentGallery.Scenery == null, "fog: the haze alone, no scenery");
        Check.True(rig.Env.Scenery.Shown == null, "fog: no strips shown");
        Check.True(rig.Env.SceneColor != EnvironmentView.ENV_SCENE_COLOR, "fog: the background is the fog");
        s.SetEnvironment("full");
        Check.True(rig.Env.Ready && (rig.Env.Scenery.Shown == null) == (rig.Env.CurrentGallery?.Scenery == null), "full again: the scenery when the gallery has one");
        if (rig.Env.Scenery.Shown != null)
            Check.True(!rig.Env.GetChildren().OfType<MeshInstance3D>().Any(m => m.Visible && m.Name.ToString().StartsWith("env-")), "full again: no fog planes under the scenery");
        s.SetEnvironment("fog");
        Check.True(rig.Env.Scenery.Shown == null, "fog again: the strips down");
        s.SetEnvironment("none");
        Check.True(!rig.Env.Visible, "the room is gone");
        Check.Equal(EnvironmentView.ENV_SCENE_COLOR, rig.Env.SceneColor, "the page's background again");
        rig.Game.GameTimer.Suspend();
        s.SetClearPhysics(true);
        Check.True(b.CpmOverlay.Visible, "clear physics shows its layer (paused: drawn at once)");
        s.SetClearPhysics(false);
        Check.True(!b.CpmOverlay.Visible, "and hides it");
    }

    // a ray to the board lands on the level point it crosses the lemmings' plane at; the hover
    // puts the ring on the lemming and marks it for the skill
    [AppTest]
    public static void PicksAndHovers()
    {
        if (!HaveAssets()) return;
        using var rig = new Rig();
        var s = rig.Session;
        var lvl = s.Level;
        var target = s.SfxPos(300, 100);
        var eye = new Vector3(lvl.Width / 2f, lvl.Height / 2f + 120, 420);
        var hit = s.Pick(eye, (target - eye).Normalized());
        Check.Equal(new Vector2I(300, 100), hit!.Value, "the ray lands on (300, 100)");
        Check.True(s.Pick(eye, Vector3.Back) == null, "a ray away from the board misses");
        rig.Game.GameTimer.Suspend();
        for (int i = 0; i < 150; i++) rig.Game.GameTimer.Tick();
        var lem = rig.Game.Sim.Lemmings.First(L => !L.Removed && L.Action == BA.WALKING);
        s.SetPointer(new Vector2I(lem.X, lem.Y - 5));
        Check.True(ReferenceEquals(lem, s.Hovered), "hovered");
        Check.True(rig.Board.Ring.Visible, "the ring is on it");
        Check.Near(new Vector3(lem.X, lem.Y - 5, (float)(BoardZ.LEMMING_Z + 2)), rig.Board.Ring.Position, "where the ring is");
        s.GameCursorInUse = true;
        s.SetPointer(new Vector2I(lem.X, lem.Y - 5));
        Check.True(!rig.Board.Ring.Visible, "with NeoLemmix's cursor the square is the mark, not the ring");
        s.SetPointer(null);
        Check.True(s.Hovered == null && rig.Game.CursorLemming == null, "off the board: nothing hovered or marked");
        // an assignment from a click, paused: the frame runs now
        rig.Game.Skills.SetSelectedSkill(rig.Game.Sim.ActiveSkills.IndexOf("BUILDER"));
        int tick = rig.Game.GameTimer.GetGameTicks();
        Check.True(s.AssignAt(lem.X, lem.Y - 5), "assigned");
        Check.Equal(tick + 1, rig.Game.GameTimer.GetGameTicks(), "paused: one frame forced");
        Check.Equal(BA.BUILDING, lem.Action, "the lemming builds");
    }

    // the clear-physics hue walk is three's setHSL(...).getHex()
    [AppTest]
    public static void ClearPhysicsHue()
    {
        Check.Equal(0x00bfbf, BoardScene.HslHex(0.5, 1, 0.375), "hue 0.5");
        Check.Equal(0xbf0000, BoardScene.HslHex(0, 1, 0.375), "hue 0");
        Check.Equal(0x5fbf00, BoardScene.HslHex(0.25, 1, 0.375), "hue 0.25");
    }
}
