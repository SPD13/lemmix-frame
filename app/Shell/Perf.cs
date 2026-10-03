using System;
using System.Diagnostics;

namespace Lemmix.App;

// The benchmark's per-subsystem clock (Shell/Benchmark): time and managed allocations charged to
// the innermost open section, so the sections of a frame add up without double counting (a tick's
// "sim" is the tick minus the scene sync inside it). Off unless the benchmark turns it on; a closed
// section costs two timestamp reads and two allocation-counter reads, and allocates nothing.
public static class Perf
{
    public enum S
    {
        Sim,          // the game's tick: Sim.Update, the saved states, the panel (minus what follows)
        Objects,      // SyncScene: the gadgets captured, decals painted, overlays, portals, wave slices
        Lemmings,     // SyncScene: the lemmings and fallers captured
        Pools,        // SyncScene: the sprite pools and particles placed
        Mesh,         // TerrainMesh.FlushDirty: dirty chunks re-meshed (core)
        TexUpload,    // TerrainView.Sync: the level and decal textures uploaded
        MeshSwap,     // TerrainView.Sync: rebuilt chunks converted to ArrayMeshes
        Step,         // GameSession.Step outside the ticks: interpolation, hover, markers
        Env,          // EnvironmentView.Update: the room placed, its pictures put up
        Bar,          // the skill bar: SkillBar.Update + SkillBarView.Sync
        Shell,        // the rest of App.Frame: windows, pointers, level end
        Rewind,       // Game.BackFrames: the state loaded and the frames simulated again
        Resync,       // TerrainMesh.Resync after a jump (texture refill, changed chunks re-meshed)
        Restore,      // the rest of the jump's refresh (scene memory, SyncScene)
        Bench,        // the benchmark's own work (assignments)
        Count,
    }

    public static bool On;
    public static readonly long[] Ticks = new long[(int)S.Count];
    public static readonly long[] Bytes = new long[(int)S.Count];
    static readonly int[] _stack = new int[32];
    static int _depth;
    static long _mark, _markBytes;

    public static string Name(int s) => ((S)s).ToString();

    static void Charge(long now, long bytes)
    {
        if (_depth > 0)
        {
            int top = _stack[_depth - 1];
            Ticks[top] += now - _mark;
            Bytes[top] += bytes - _markBytes;
        }
        _mark = now; _markBytes = bytes;
    }

    public static Section Time(S s)
    {
        if (!On || _depth >= _stack.Length) return default;
        Charge(Stopwatch.GetTimestamp(), GC.GetAllocatedBytesForCurrentThread());
        _stack[_depth++] = (int)s;
        return new Section(true);
    }

    static void End()
    {
        Charge(Stopwatch.GetTimestamp(), GC.GetAllocatedBytesForCurrentThread());
        _depth--;
    }

    // the accumulators back to zero (the benchmark reads them once a frame)
    public static void Reset()
    {
        Array.Clear(Ticks);
        Array.Clear(Bytes);
    }

    public static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    public readonly struct Section : IDisposable
    {
        readonly bool _open;
        public Section(bool open) { _open = open; }
        public void Dispose() { if (_open) End(); }
    }
}
