using System.Runtime.InteropServices;
using System.Text.Json;
using Lemmix.Engine;
using Lemmix.Oracle;
using Lemmix.Render;
using Lemmix.Tests.Oracle;

namespace Lemmix.Tests.Render;

// oracle/lib/scene-env.js hashF64 / hashGeometry / hashFrame, word for word.
public static class RenderHash
{
    public static void F64(StateHash h, double v)
    {
        long bits = BitConverter.DoubleToInt64Bits(v);
        h.Word(unchecked((uint)bits)); h.Word(unchecked((uint)(bits >> 32)));
    }

    public static void F64(StateHash h, double? v) { if (v == null) h.Null(); else F64(h, v.Value); }

    public static void Floats(StateHash h, float[]? a)
    {
        if (a == null) { h.Null(); return; }
        h.Bytes(MemoryMarshal.AsBytes(a.AsSpan()));
    }

    public static void Geometry(StateHash h, GeometryBuffers? g)
    {
        if (g == null) { h.Null(); return; }
        Floats(h, g.Position); Floats(h, g.Color); Floats(h, g.Uv);
        h.Word(g.Index32 ? 4 : 2);
        h.Bytes(g.IndexBytes());
    }

    public static void Frame(StateHash h, Frame? f)
    {
        if (f == null) { h.Null(); return; }
        h.Word(f.Width); h.Word(f.Height); h.Word(f.OffsetX); h.Word(f.OffsetY);
        h.Bytes(MemoryMarshal.AsBytes(f.Data.AsSpan()));
        h.Bytes(f.Mask);
    }

    public static void Bytes(StateHash h, byte[]? b) { if (b == null) h.Null(); else h.Bytes(b); }

    // The levels an oracle sampled, built by the port (the countdown digits loaded first, as the oracle's loadMasks does).
    static readonly object Gate = new();
    static Masks? _masks;
    public static Masks Masks { get { lock (Gate) return _masks ??= Masks.Load(OracleData.Io); } }

    public static Level Build(JsonElement row, StyleManager styles)
    {
        _ = Masks;
        var data = LevelBuilder.ParseLevel(OracleData.Io.Text(row.GetProperty("url").GetString()!)!);
        return LevelBuilder.Build(data, styles, row.GetProperty("id").GetString()!);
    }

    public static string Hex(Action<StateHash> fill) { var h = new StateHash(); fill(h); return h.Hex(); }
}
