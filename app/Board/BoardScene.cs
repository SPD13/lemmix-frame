using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Lemmix.Engine;
using Lemmix.Render;

namespace Lemmix.App.Board;

// app.js's planes (z in the worldGroup, the slab's back at 0, its front at TERRAIN_DEPTH)
public static class BoardZ
{
    public const double TERRAIN_DEPTH = TerrainMesh.TERRAIN_DEPTH;
    public const double SPRITE_DEPTH = SpriteBuild.SPRITE_DEPTH;
    // lemmings embedded mid-slab, the objects just behind them
    public const double LEMMING_Z = TERRAIN_DEPTH / 2 - SPRITE_DEPTH / 2;
    public const double OBJECT_Z = LEMMING_Z - 0.8;
    // a NO_OVERWRITE gadget: a hair behind the other objects (the diorama's plane)
    public const double OBJECT_LOW_Z = OBJECT_Z - 0.3;
    public const double OBJECT_BG_Z = -1.4;
    public const double OBJECT_DECAL_Z = TERRAIN_DEPTH + 0.25;
    public const double WAVE_FRONT_Z = OBJECT_DECAL_Z;
    // a lemming that falls out through the bottom edge keeps falling down to the room's floor
    public const int FALL_OUT_REACH = 1 << 16;
    public const int FALL_OUT_MARGIN = 12;
    public const int FALL_SPEED = 3;
    public const double WATER_OPACITY = Portals.WATER_OPACITY;
    public const int HOVER_RING_RADIUS = 9;
}

// The switches the board is built with (app.js state: emboss, smooth, smoothterrain, colorblend,
// doors, environment, shadows, music) - all on by default, colour blend "soft".
public sealed class BoardSwitches
{
    public bool Emboss = true, Smooth = true, SmoothTerrain = true, Doors = true, Shadows = true, Music = true;
    public string ColorBlend = "soft";       // off | soft | smooth
    public string Environment = "full";      // none | full
    public static double Softness(string level) => level switch { "off" => 0, "smooth" => 1, _ => 0.5 };
    public BoardSwitches Clone() => (BoardSwitches)MemberwiseClone();
}

// What the board is built from: the level and its game, the lemmings' sprite set, the depth
// profile (depth.js) and the 3D profile (portals' object tags, the environment's hints).
public sealed class BoardInputs
{
    public required Level Level;
    public required Game Game;
    public required SpriteSet Sprites;
    public required DepthProfile Profile;
    public EnvProfile? EnvProfile;
    public required BoardSwitches Switches;
    public ShaderMaterial? BackdropMaterial;   // the environment's (EnvironmentView.BackdropMaterial)
}

// web/3d/js/app.js loadLevel, the worldGroup: everything game-sized, in game pixel space (x right,
// y down, z out of the board), flipped into the world by this node's transform (scale y -1, y =
// level height). The terrain slab (TerrainView over core's TerrainMesh), the backdrop behind it,
// the openings, flaps, water bodies and wave slices (portals.js), the objects and lemmings as
// extruded sprites (bridge.js pools), particles, the clear-physics layer, the skill shadows, the
// replay markers and the hover ring. SyncScene is the per-tick bridge (syncScene), registered on
// the game timer after the game's own handler; ApplyInterpolation slides the lemmings between
// ticks.
public sealed partial class BoardScene : Node3D
{
    public readonly Level Level;
    public readonly Game Game;
    public readonly SpriteSet Sprites;
    public readonly BoardSwitches Switches;
    public readonly DepthProfile Profile;
    public readonly GroundData GroundData;
    public readonly PortalObjectData ObjectData;
    public readonly ushort[] PieceMap;
    public readonly byte[] DepthMap;
    public readonly TerrainMesh Terrain;
    public readonly TerrainView TerrainView;
    public readonly TerrainDecals? Decals;
    public readonly SpriteGeometryCache Cache = new();
    public readonly BoardMaterials Materials = new();
    public readonly SpritePool Lemmings, Objects;
    public readonly ParticleCloud Particles;
    public readonly SpriteCapture LemCapture = new(), ObjCapture = new();
    public readonly MeshInstance3D Backdrop, Ring;
    public readonly ClearPhysicsOverlay CpmOverlay;
    public readonly ShadowOverlay ShadowOverlay;
    public readonly MarkersView Markers;
    public readonly List<Portal> Portals;
    readonly HashSet<int> _portalIndices;
    public readonly List<WaveStack> Stacks;
    readonly HashSet<int> _stackIndices;
    readonly List<WaterPool> _pools;

    // the scene halves of the portals: an opening's mesh, its flaps, the water bodies, the slices
    sealed class PortalNodes { public required MeshInstance3D Mesh; public List<(MeshInstance3D Mesh, int Sign)>? Flaps; }
    readonly Dictionary<Portal, PortalNodes> _portalNodes = new(ReferenceEqualityComparer.Instance);
    public readonly List<(MeshInstance3D Mesh, ShaderMaterial Material, int Colour)> WaterMeshes = new();
    readonly Dictionary<WaveStack, List<MeshInstance3D>> _stackMeshes = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<MeshInstance3D, bool> _emptySlice = new(ReferenceEqualityComparer.Instance);

    // what the portals' and slices' nodes were last given: the engine is called only on a change
    sealed class NodeShown { public bool Visible = true; public Mesh? Mesh; public Material? Material; public Transform3D Transform; public bool Placed; }
    readonly Dictionary<MeshInstance3D, NodeShown> _nodeShown = new(ReferenceEqualityComparer.Instance);
    NodeShown ShownOf(MeshInstance3D m)
    {
        if (!_nodeShown.TryGetValue(m, out var s))
        {
            _nodeShown[m] = s = new NodeShown { Visible = m.Visible, Mesh = m.Mesh, Material = m.MaterialOverride, Transform = m.Transform, Placed = true };
        }
        return s;
    }
    void SetVisible(MeshInstance3D m, bool v) { var s = ShownOf(m); if (s.Visible != v) { m.Visible = v; s.Visible = v; } }
    void SetMesh(MeshInstance3D m, Mesh? mesh) { var s = ShownOf(m); if (!ReferenceEquals(s.Mesh, mesh)) { m.Mesh = mesh; s.Mesh = mesh; } }
    void SetMaterial(MeshInstance3D m, Material? mat) { var s = ShownOf(m); if (!ReferenceEquals(s.Material, mat)) { m.MaterialOverride = mat; s.Material = mat; } }
    void SetTransform(MeshInstance3D m, in Transform3D t) { var s = ShownOf(m); if (s.Transform != t) { m.Transform = t; s.Transform = t; } }

    // what the page tells the bridge
    public (int X, int Y)? CursorSim;              // the pointer on the board (cursorSim)
    public bool ShadowsOn = true;                  // state.shadows
    public Action<SoundCue>? OnCue;                // a sound cue of this tick (sim coordinates)
    public Func<double>? FloorYInLevel;            // the room's floor in level pixels (y down)
    public double LastTickMs;                      // lastTickTime
    public Func<double> Now = () => Time.GetTicksUsec() / 1000.0;
    public int SyncCount { get; private set; }
    public static int ReservedStates = 8;          // saved states made at the load (SaveStates.Reserve)

    // the bridge's memory (resetSceneMemory)
    bool _doorSfxPlayed;
    public bool DoorsOpened => _doorSfxPlayed;
    sealed class Faller { public required Lemming Lem; public string? Action; public double X, Y, Dx, Dy; public int Frame; }
    readonly Dictionary<int, Faller> _fallers = new();
    readonly Dictionary<int, Faller> _nearBottom = new();
    static readonly HashSet<string> FallActions = new(StringComparer.Ordinal) { "falling", "floating", "gliding" };

    // reused per tick
    readonly List<CapturedDraw> _objectItems = new();
    readonly List<CapturedDraw> _standing = new();
    readonly List<DecalItem> _decalItems = new();
    readonly Action<Frame, int, int> _drawLemming;
    readonly Func<int, double> _objectZ = layer =>
        layer < -1 ? BoardZ.OBJECT_BG_Z : layer < 0 ? BoardZ.OBJECT_LOW_Z : layer > 0 ? BoardZ.OBJECT_DECAL_Z : BoardZ.OBJECT_Z;
    readonly Func<int, double> _lemmingZ = _ => BoardZ.LEMMING_Z;
    readonly List<Faller> _fallerScratch = new();
    readonly Frame?[] _shown = new Frame?[16];

    public BoardScene(BoardInputs inp)
    {
        Name = "worldGroup";
        Level = inp.Level; Game = inp.Game; Sprites = inp.Sprites; Switches = inp.Switches; Profile = inp.Profile;
        var level = Level;
        // pixel-space group: x right, y down (like the sim); flipped into world
        Transform = new Transform3D(Basis.FromScale(new Vector3(1, -1, 1)), new Vector3(0, level.Height, 0));

        // depth compositing: per-pixel classes, relief, surface blend, colour blend (depth.js)
        GroundData = GroundData.FromLevel(level);
        ObjectData = PortalObjectData.For(level);
        DepthMap = Depth.BuildDepthMap(level, GroundData, Profile);
        PieceMap = Depth.BuildPieceMap(level, GroundData);
        var relief = Depth.BuildReliefMap(level, PieceMap, Profile, Switches.Emboss, GroundData);
        var blendMap = Depth.BuildBlendMap(level, PieceMap, Profile, GroundData);
        var colorMap = Depth.BuildColorBlendMap(level, PieceMap, Profile, Switches.ColorBlend != "off", GroundData);
        double softness = BoardSwitches.Softness(Switches.ColorBlend);
        var byId = inp.EnvProfile?.ObjectsById;
        // entrances/exits become openings, carving the terrain behind them (before the mesher reads the depth)
        Portals = Switches.Doors ? Render.Portals.BuildPortals(level, ObjectData, byId, DepthMap, BoardZ.OBJECT_Z, Switches.SmoothTerrain) : new List<Portal>();
        _portalIndices = new HashSet<int>(Portals.Select(p => p.Index));

        Terrain = new TerrainMesh(level, DepthMap, relief, blendMap, colorMap, softness);
        if (Switches.Smooth) Terrain.SetSmooth(true);
        if (Switches.SmoothTerrain) Terrain.SetSmoothTerrain(true);
        Decals = TerrainDecals.ForLevel(level, level.Physics);
        if (Decals != null) Terrain.SetDecals(Decals);
        Terrain.FlushDirty(int.MaxValue);
        TerrainView = new TerrainView(Terrain) { Name = "terrain", ReleaseConverted = true };
        AddChild(TerrainView);
        TerrainView.Sync();

        // the backdrop behind the slab: the environment's material
        Backdrop = new MeshInstance3D
        {
            Name = "backdrop", Mesh = BoardMaterials.ToMesh(ReplayMarkers.PlaneGeometry(1, 1)),
            MaterialOverride = inp.BackdropMaterial ?? BasicShader.Material(new BasicKey(false, true, true, Side.Front, Filter.Nearest, false), EnvironmentLayout.ENV_BACKDROP_COLOR),
            Transform = BoardMaterials.Place(level.Width / 2.0, level.Height / 2.0, -2, scaleX: level.Width, scaleY: level.Height),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(Backdrop);

        CpmOverlay = new ClearPhysicsOverlay(level);
        AddChild(CpmOverlay);
        ShadowOverlay = new ShadowOverlay(level);
        AddChild(ShadowOverlay);
        Markers = new MarkersView(level, Game, BoardZ.LEMMING_Z + 2);
        AddChild(Markers);

        // the selection ring: drawn over the slab (no depth test), after the opaque things
        Ring = new MeshInstance3D
        {
            Name = "ring", Mesh = BoardMaterials.ToMesh(ReplayMarkers.RingGeometry(7, 9, 24)), Visible = false,
            MaterialOverride = BasicShader.Material(new BasicKey(true, false, true, Side.Double, Filter.Nearest, false), 0xffd866, renderOrder: BasicShader.OpaquePassLast),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(Ring);

        // the colour blend covers the scenery (water, openings, objects); the lemmings stay crisp
        Cache.SetColorBlend(softness);

        foreach (var portal in Portals)
        {
            var mesh = new MeshInstance3D
            {
                Name = "portal" + portal.Index, Mesh = BoardMaterials.ToMesh(portal.Geometry),
                MaterialOverride = Materials.For(Cache.BlendedMaterialFor(portal.MapObject.Frames[0])),
                Transform = BoardMaterials.Place(portal.OriginX, portal.OriginY, BoardZ.OBJECT_Z),
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            AddChild(mesh);
            var nodes = new PortalNodes { Mesh = mesh };
            if (portal.Hatch is { } h)
            {
                var material = Materials.For(Cache.BlendedMaterialFor(portal.ClosedFrame));
                nodes.Flaps = new();
                foreach (var (sign, x) in new[] { (1, h.LeftX), (-1, h.RightX) })
                {
                    var geom = Render.Portals.BuildFlapGeometry(portal.ClosedFrame, h.DoorRows, h.HalfWidth, h.Depth, sign);
                    var flap = new MeshInstance3D
                    {
                        Name = "flap", Mesh = BoardMaterials.ToMesh(geom), MaterialOverride = material,
                        Transform = BoardMaterials.Place(portal.OriginX + x, portal.OriginY + h.Y, BoardZ.OBJECT_Z),
                        CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                    };
                    AddChild(flap);
                    nodes.Flaps.Add((flap, sign));
                }
            }
            _portalNodes[portal] = nodes;
        }
        // water: a translucent body under the wave slices, shaped to the hollow, as deep as the slab
        _pools = Switches.Doors ? Render.Portals.WaterObjectsFrom(level, ObjectData, byId) : new List<WaterPool>();
        foreach (var pool in _pools)
        {
            var geometry = Render.Portals.BuildPoolGeometry(pool.Runs, pool.Y0, pool.Z0, pool.Z1);
            if (geometry == null) continue;
            var mat = BasicShader.Material(new BasicKey(true, true, false, Side.Front, Filter.Nearest, false), pool.Colour, BoardZ.WATER_OPACITY, renderOrder: 1);
            var mesh = new MeshInstance3D { Name = "water", Mesh = BoardMaterials.ToMesh(geometry), MaterialOverride = mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            AddChild(mesh);
            WaterMeshes.Add((mesh, mat, pool.Colour));
        }
        // the surfaces as a stack of slices, each running the animation at its own offset
        Stacks = Switches.Doors ? Render.Portals.StackedObjectsFrom(level, ObjectData, byId) : new List<WaveStack>();
        _stackIndices = new HashSet<int>(Stacks.Select(s => s.Index));
        foreach (var stack in Stacks)
        {
            var list = new List<MeshInstance3D>();
            var first = WaveFrameCut(stack, stack.MapObject.Frames[0]);
            var entry = first != null ? WaveEntryFor(null, first, null) : null;
            for (int k = 0; k < stack.Phases.Length; k++)
            {
                var mesh = new MeshInstance3D { Name = "wave", CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Visible = first != null };
                _emptySlice[mesh] = first == null;
                if (first != null)
                {
                    mesh.Mesh = Materials.Mesh(entry!.Geometry);
                    mesh.MaterialOverride = Materials.For(Cache.BlendedMaterialFor(first));
                    mesh.Transform = BoardMaterials.Place(stack.MapObject.X + first.OffsetX,
                        stack.MapObject.Y + first.OffsetY + (stack.FlipY ? entry.H : 0), BoardZ.WAVE_FRONT_Z - (k + 1) * BoardZ.SPRITE_DEPTH, stack.FlipY ? -1 : 1);
                }
                AddChild(mesh);
                list.Add(mesh);
            }
            _stackMeshes[stack] = list;
        }

        Lemmings = new SpritePool(Cache, Materials, false, "lemmings");
        Objects = new SpritePool(Cache, Materials, true, "objects");
        AddChild(Lemmings);
        AddChild(Objects);
        Particles = new ParticleCloud(BoardZ.LEMMING_Z + 1);
        AddChild(Particles);
        // every sprite cut to the level's rectangle; the lemmings run on below the edge
        LemCapture.SetBounds(level.Width, level.Height, level.Height + BoardZ.FALL_OUT_REACH);
        ObjCapture.SetBounds(level.Width, level.Height);
        _drawLemming = (f, x, y) => LemCapture.DrawFrame(f, x, y);

        // a saved state carries the depth and relief maps with it
        Game.States.OnSave = s => Terrain.SaveExtra(s);
        Game.States.OnLoad = s => Terrain.LoadExtra(s);
        // the saves of the first minutes made ahead, now (the load), not as large-object garbage in play
        Game.States.Reserve(Game.Sim, ReservedStates, s => Terrain.SaveExtra(s));
    }

    // ------------------------------------------------------------ helpers
    // waveEntryFor: the rounded, blended cut of a slice with edge smoothing on, else its relief
    SpriteEntry WaveEntryFor(Frame? prev, Frame frame, Frame? next) =>
        Switches.SmoothTerrain ? Cache.ForFrameBlended(prev, frame, next) : Cache.ForFrame(frame);

    Frame? WaveFrameCut(WaveStack stack, Frame? frame) => frame == null ? null
        : SpriteBuild.ClipFrameToBounds(frame, stack.MapObject.X, stack.MapObject.Y, stack.FlipY, Level.Width, Level.Height);

    // lemmix/js/game.js ObjectManager.render: every on-map gadget at its current frame
    void RenderObjects(SpriteCapture display)
    {
        foreach (var obj in Level.Objects)
        {
            var g = obj.Gadget;
            if (g.EffectBase == "NONE" && g.Effect == "NONE" && g.Animations.Count == 0) continue;
            display.DrawFrameFlags(g.Render(), obj);
        }
    }

    // clear physics: background gadgets and on-terrain ones left out (the one-way arrows stay)
    bool CpmHides(int i)
    {
        if (i >= Level.Objects.Count) return false;
        var o = Level.Objects[i];
        var g = o.Gadget;
        return !o.OneWay && (g.EffectBase == "BACKGROUND" || g.EffectBase == "PAINT" || g.OnlyOnTerrain);
    }

    // shadowChoice: which lemming casts which skill's shadow now
    public (Lemming Lem, string Skill)? ShadowChoice()
    {
        var sim = Game.Sim;
        if (CursorSim is not { } c || !ShadowsOn) return null;
        string? skill = sim.SelectedSkill;
        if (Game.CursorLemming != null && skill != null && Shadows.ShadowSkills.Contains(skill)) return (Game.CursorLemming, skill);
        var any = sim.GetPriorityLemming(BA.NONE, c.X, c.Y).Lemming;
        if (any != null && any.IsGlider && (any.Action == BA.FALLING || any.Action == BA.GLIDING)) return (any, "GLIDER");
        return null;
    }

    // a lemming's id as the capture's tag, boxed once (not once per lemming per tick)
    static object[] _boxedIds = new object[256];
    static object BoxedId(int id)
    {
        if (id < 0) return id;
        if (id >= _boxedIds.Length) Array.Resize(ref _boxedIds, Math.Max(id + 1, _boxedIds.Length * 2));
        return _boxedIds[id] ??= id;
    }

    static string? ActionName(Lemming L) => L.Removed || L.Action == BA.NONE ? null : L.ActionName;

    // ------------------------------------------------------------ the per-tick bridge
    // syncScene(redrawOnly): the objects, openings, slices, decals, overlays and lemmings of the
    // frame; `redrawOnly` is the same frame again (a hover changed a lemming's look), with no sound
    public void SyncScene(bool redrawOnly)
    {
        SyncCount++;
        using var _ = Perf.Time(Perf.S.Objects);
        if (!redrawOnly) LastTickMs = Now();
        var level = Level;
        var game = Game;

        ObjCapture.Begin();
        RenderObjects(ObjCapture);
        bool cpmOn = game.ClearPhysics;
        bool dropPortals = _portalIndices.Count > 0;
        bool dropStacks = _stackIndices.Count > 0;
        _objectItems.Clear();
        var items = ObjCapture.Items;
        for (int i = 0; i < items.Count; i++)
        {
            if (dropPortals && _portalIndices.Contains(i)) continue;
            if (dropStacks && _stackIndices.Contains(i)) continue;
            if (cpmOn && CpmHides(i)) continue;
            _objectItems.Add(items[i]);
        }
        // the decals go onto the terrain's face, not among the sprites
        if (Decals != null)
        {
            _decalItems.Clear();
            _standing.Clear();
            foreach (var it in _objectItems)
            {
                if (it.Layer == 1) _decalItems.Add(new DecalItem { Frame = it.Frame, X = it.X, Y = it.Y, FlipY = it.FlipY, OneWay = it.OneWay, Off = it.Off });
                else _standing.Add(it);
            }
            Decals.Paint(_decalItems, cpmOn);
            _objectItems.Clear();
            _objectItems.AddRange(_standing);
        }
        // clear physics: the terrain as its physics map, the layer of trigger areas repainted
        Terrain.SetPhysicsPaint(game.ClearPhysics ? level.Physics : null, ClearPhysicsOverlay.HighlightBits(level, CursorSim));
        CpmOverlay.UpdateFor(game);
        var choice = ShadowChoice();
        if (choice is { } ch)
            ShadowOverlay.UpdateFor(game.Sim, ch.Lem, ch.Skill, (ch.Lem.Index, ch.Skill, game.Sim.CurrentIteration, ch.Lem.X, ch.Lem.Y, ch.Lem.Action, ch.Lem.Dx));
        else ShadowOverlay.UpdateFor(game.Sim, null, null, null);

        if (Portals.Count > 0)
        {
            foreach (var portal in Portals)
            {
                var nodes = _portalNodes[portal];
                var frames = portal.MapObject.Frames;
                var frame = portal.MapObject.Gadget.Render();
                // a hatch keeps the open frame on its ceiling square: its doors are geometry
                var shown = portal.Hatch != null ? frames[0] : (frame ?? frames[0]);
                if (shown != null)
                    SetMaterial(nodes.Mesh, Materials.For(game.ClearPhysics ? Cache.FlatMaterialFor(shown) : Cache.BlendedMaterialFor(shown)));
                if (nodes.Flaps != null)
                {
                    var door = Materials.For(game.ClearPhysics ? Cache.FlatMaterialFor(portal.ClosedFrame) : Cache.BlendedMaterialFor(portal.ClosedFrame));
                    foreach (var f in nodes.Flaps) SetMaterial(f.Mesh, door);
                }
                if (nodes.Flaps == null || portal.Openness == null) continue;
                double angle = Render.Portals.FlapAngle(portal, frame!);
                // (the web plays the DOS door effect once here; the Lemmix sim cues its own "door")
                if (angle > 0) _doorSfxPlayed = true;
                foreach (var f in nodes.Flaps)
                {
                    var t = ShownOf(f.Mesh).Transform;
                    SetTransform(f.Mesh, new Transform3D(new Basis(Vector3.Back, (float)(f.Sign * angle)), t.Origin));
                }
            }
        }
        // the wave slices: the same animation at each slice's own offset
        if (Stacks.Count > 0)
        {
            int waveTick = game.GameTimer.GetGameTicks();
            foreach (var stack in Stacks)
            {
                var meshes = _stackMeshes[stack];
                var obj = stack.MapObject;
                var shown = meshes.Count <= _shown.Length ? _shown : new Frame?[meshes.Count];
                for (int k = 0; k < meshes.Count; k++)
                    shown[k] = WaveFrameCut(stack, Render.Portals.FrameAtPhase(obj, waveTick, stack.Phases[k]));
                for (int k = 0; k < meshes.Count; k++)
                {
                    var frame = shown[k];
                    var mesh = meshes[k];
                    _emptySlice[mesh] = frame == null;
                    SetVisible(mesh, frame != null);
                    if (frame == null) continue;
                    var entry = WaveEntryFor(k > 0 ? shown[k - 1] : null, frame, k + 1 < meshes.Count ? shown[k + 1] : null);
                    SetMesh(mesh, Materials.Mesh(entry.Geometry));
                    SetMaterial(mesh, Materials.For(game.ClearPhysics ? Cache.FlatMaterialFor(frame) : Cache.BlendedMaterialFor(frame)));
                    SetTransform(mesh, BoardMaterials.Place(obj.X + frame.OffsetX, obj.Y + frame.OffsetY + (stack.FlipY ? entry.H : 0),
                        BoardZ.WAVE_FRONT_Z - (k + 1) * BoardZ.SPRITE_DEPTH, stack.FlipY ? -1 : 1));
                }
            }
        }
        using (Perf.Time(Perf.S.Pools)) Objects.Sync(_objectItems, _objectZ, false, game.ClearPhysics);

        var lemSection = Perf.Time(Perf.S.Lemmings);
        LemCapture.Begin();
        var lems = game.Sim.Lemmings;
        if (!redrawOnly && OnCue != null)
            foreach (var cue in game.Sounds) OnCue(cue);
        for (int i = 0; i < lems.Count; i++)
        {
            var lem = lems[i];
            // (the web's action and brick memory only feeds the DOS engine's AdLib effects - and
            // the builder's AdLib "ting", which the Lemmix sim cues itself as a file sound)
            if (lem.Removed)
            {
                if (!redrawOnly) NoteFallOut(lem);
                continue;
            }
            if (!redrawOnly) NoteNearBottom(lem, ActionName(lem));
            LemCapture.Tag = BoxedId(lem.Id);
            Sprites.RenderLemming(game, lem, _drawLemming);
        }
        SyncFallers(redrawOnly);
        lemSection.Dispose();
        using (Perf.Time(Perf.S.Pools))
        {
            Lemmings.Sync(LemCapture.Items, _lemmingZ, true, false);
            Particles.Sync(LemCapture.Particles);
        }

        using (Perf.Time(Perf.S.Mesh)) Terrain.FlushDirty();
        Materials.SyncTints();
        TerrainView.Sync();
    }

    // ------------------------------------------------------------ the fallers
    void NoteNearBottom(Lemming lem, string? action)
    {
        if (action == null || !FallActions.Contains(action) || lem.Y + BoardZ.FALL_OUT_MARGIN < Level.Height)
        {
            _nearBottom.Remove(lem.Id);
            return;
        }
        _nearBottom.TryGetValue(lem.Id, out var last);
        _nearBottom[lem.Id] = new Faller
        {
            Lem = lem, Action = action, X = lem.X, Y = lem.Y,
            Dx = last != null ? lem.X - last.X : 0,
            Dy = last != null ? Math.Max(1, lem.Y - last.Y) : BoardZ.FALL_SPEED,
            Frame = lem.Frame,
        };
    }

    void NoteFallOut(Lemming lem)
    {
        if (!_nearBottom.TryGetValue(lem.Id, out var last)) return;
        _nearBottom.Remove(lem.Id);
        _fallers[lem.Id] = last;
    }

    void SyncFallers(bool redrawOnly)
    {
        if (_fallers.Count == 0) return;
        double floorY = FloorYInLevel?.Invoke() ?? double.PositiveInfinity;
        _fallerScratch.Clear();
        _fallerScratch.AddRange(_fallers.Values);
        foreach (var f in _fallerScratch)
        {
            if (!redrawOnly) { f.X += f.Dx; f.Y += f.Dy; f.Frame++; }
            if (f.Y >= floorY) { _fallers.Remove(f.Lem.Id); continue; }
            // a stand-in for the lemming: its pose and place, drawn as it was, under its key
            var L = f.Lem;
            var frame = Sprites.Frame(L.Action, L.Dx, f.Frame, SpriteSet.VariantOf(L, false, Game.ClearPhysics));
            LemCapture.Tag = BoxedId(L.Id);
            if (frame != null) LemCapture.DrawFrame(frame, (int)f.X, (int)f.Y);
        }
    }

    public int FallerCount => _fallers.Count;

    // resetSceneMemory: what the bridge remembers from tick to tick, dropped when the game jumps
    public void ResetSceneMemory()
    {
        _doorSfxPlayed = false;
        _fallers.Clear(); _nearBottom.Clear();
        LastTickMs = Now();
    }

    // ------------------------------------------------------------ per frame
    public void ApplyInterpolation(double alpha) => Lemmings.ApplyInterpolation(alpha);

    // cpmAnimate(now): the gadgets' one colour walks the hues every five seconds in clear physics
    public void CpmAnimate(double now)
    {
        if (Game.ClearPhysics)
        {
            int hex = HslHex(((now % 5000) + 5000) % 5000 / 5000, 1, 0.375);
            Cache.SetFlatColor(hex);
            Materials.SyncTints();
            foreach (var w in WaterMeshes) BasicShader.SetColor(w.Material, hex);
            _waterTinted = true;
        }
        else if (_waterTinted)
        {
            foreach (var w in WaterMeshes) BasicShader.SetColor(w.Material, w.Colour);
            _waterTinted = false;
        }
    }
    bool _waterTinted;

    // THREE.Color.setHSL(h, s, l).getHex()
    public static int HslHex(double h, double s, double l)
    {
        h = ((h % 1) + 1) % 1;
        s = Math.Clamp(s, 0, 1); l = Math.Clamp(l, 0, 1);
        double r, g, b;
        if (s == 0) r = g = b = l;
        else
        {
            double p = l <= 0.5 ? l * (1 + s) : l + s - l * s, q = 2 * l - p;
            static double Hue(double t, double e, double i)
            {
                if (i < 0) i += 1;
                if (i > 1) i -= 1;
                return i < 1.0 / 6 ? t + 6 * (e - t) * i : i < 0.5 ? e : i < 2.0 / 3 ? t + 6 * (e - t) * (2.0 / 3 - i) : t;
            }
            r = Hue(q, p, h + 1.0 / 3); g = Hue(q, p, h); b = Hue(q, p, h - 1.0 / 3);
        }
        static int C(double v) => (int)Math.Clamp(255 * v, 0, 255);
        return C(r) << 16 ^ C(g) << 8 ^ C(b);
    }

    // updateHoverRing's scene part: the ring at the hovered lemming, or hidden
    public void SetRing(Lemming? lem, bool visible)
    {
        Ring.Visible = lem != null && visible;
        if (lem != null) Ring.Transform = BoardMaterials.Place(lem.X, lem.Y - 5, BoardZ.LEMMING_Z + 2);
    }

    // ------------------------------------------------------------ switches
    // rebuildRelief: the colour-keyed relief again (the emboss switch)
    public void RebuildRelief(bool emboss)
    {
        Switches.Emboss = emboss;
        Terrain.SetRelief(Depth.BuildReliefMap(Level, PieceMap, Profile, emboss, GroundData));
        Terrain.FlushDirty(int.MaxValue);
        TerrainView.Sync();
    }

    public void SetSmooth(bool on)
    {
        Switches.Smooth = on;
        Terrain.SetSmooth(on);
        Terrain.FlushDirty(int.MaxValue);
        TerrainView.Sync();
    }

    // the edge smoothing: the terrain, the openings' outlines, the wave slices (drawn again now)
    public void SetSmoothTerrain(bool on)
    {
        Switches.SmoothTerrain = on;
        Terrain.SetSmoothTerrain(on);
        Terrain.FlushDirty(int.MaxValue);
        RebuildPortalEdges();
        SyncScene(true);
    }

    // rebuildPortalEdges: an opening's outline re-meshed for the edge smoothing
    public void RebuildPortalEdges()
    {
        foreach (var portal in Portals)
        {
            if (portal.Rebuild is not { } rb) continue;
            var geom = Render.Portals.BuildPortalGeometry(rb.Frame, rb.Depth, rb.Opening, Switches.SmoothTerrain);
            if (geom == null) continue;
            _portalNodes[portal].Mesh.Mesh = BoardMaterials.ToMesh(geom);
        }
    }

    // rebuildColorBlend: the terrain's colour blend and the scenery's, drawn again now
    public void SetColorBlend(string level)
    {
        Switches.ColorBlend = level;
        double softness = BoardSwitches.Softness(level);
        Terrain.SetColorBlend(Depth.BuildColorBlendMap(Level, PieceMap, Profile, level != "off", GroundData), softness);
        Terrain.FlushDirty(int.MaxValue);
        Cache.SetColorBlend(softness);
        SyncScene(true);
    }

    public bool SliceEmpty(MeshInstance3D m) => _emptySlice.TryGetValue(m, out bool e) && e;
    public IReadOnlyList<MeshInstance3D> StackMeshes(WaveStack s) => _stackMeshes[s];
    public MeshInstance3D PortalMesh(Portal p) => _portalNodes[p].Mesh;
    public IReadOnlyList<(MeshInstance3D Mesh, int Sign)>? PortalFlaps(Portal p) => _portalNodes[p].Flaps;
}
