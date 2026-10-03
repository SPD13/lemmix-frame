using System.Collections.Generic;
using Lemmix.Engine;
using Lemmix.Render;

namespace Lemmix.App.Board;

// The board's half that is plain computation (no Godot): the depth compositing maps, the openings,
// the terrain meshed whole (smoothing, decals) and every chunk compacted into the arrays a mesh is
// made from. A level's load builds it on a worker (GameSession.Prepare) while the headset keeps
// rendering; BoardScene then only turns it into nodes and meshes on the main thread.
public sealed class BoardData
{
    public required GroundData GroundData;
    public required PortalObjectData ObjectData;
    public required byte[] DepthMap;
    public required ushort[] PieceMap;
    public required double Softness;
    public required List<Portal> Portals;
    public required TerrainMesh Terrain;
    public TerrainDecals? Decals;
    public required RawColor.CompactSurface[]?[] ChunkSurfaces;
    public required RawColor.CompactSurface[]?[] DecalSurfaces;

    /** Any thread: everything BoardScene computed before it made nodes. */
    public static BoardData Build(BoardInputs inp)
    {
        var level = inp.Level;
        var sw = inp.Switches;
        var ground = GroundData.FromLevel(level);
        var objects = PortalObjectData.For(level);
        var depth = Depth.BuildDepthMap(level, ground, inp.Profile);
        var pieces = Depth.BuildPieceMap(level, ground);
        var relief = Depth.BuildReliefMap(level, pieces, inp.Profile, sw.Emboss, ground);
        var blendMap = Depth.BuildBlendMap(level, pieces, inp.Profile, ground);
        var colorMap = Depth.BuildColorBlendMap(level, pieces, inp.Profile, sw.ColorBlend != "off", ground);
        double softness = BoardSwitches.Softness(sw.ColorBlend);
        var byId = inp.EnvProfile?.ObjectsById;
        // entrances/exits become openings, carving the terrain behind them (before the mesher reads the depth)
        var portals = sw.Doors ? Lemmix.Render.Portals.BuildPortals(level, objects, byId, depth, BoardZ.OBJECT_Z, sw.SmoothTerrain) : new List<Portal>();
        LoadTimes.Lap("board-maps");
        var terrain = new TerrainMesh(level, depth, relief, blendMap, colorMap, softness);
        if (sw.Smooth) terrain.SetSmooth(true);
        if (sw.SmoothTerrain) terrain.SetSmoothTerrain(true);
        var decals = TerrainDecals.ForLevel(level, level.Physics);
        if (decals != null) terrain.SetDecals(decals);
        terrain.FlushDirty(int.MaxValue);
        LoadTimes.Lap("board-mesh");
        var chunks = new RawColor.CompactSurface[]?[terrain.ChunkMeshes.Length];
        for (int i = 0; i < chunks.Length; i++) if (terrain.ChunkMeshes[i] is { } g) chunks[i] = RawColor.Compact(g);
        var decalSurfaces = new RawColor.CompactSurface[]?[terrain.DecalMeshes.Length];
        for (int i = 0; i < decalSurfaces.Length; i++) if (terrain.DecalMeshes[i] is { } g) decalSurfaces[i] = RawColor.Compact(g);
        LoadTimes.Lap("board-compact");
        return new BoardData
        {
            GroundData = ground, ObjectData = objects, DepthMap = depth, PieceMap = pieces, Softness = softness,
            Portals = portals, Terrain = terrain, Decals = decals, ChunkSurfaces = chunks, DecalSurfaces = decalSurfaces,
        };
    }
}
