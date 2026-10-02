using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Lemmix.Engine;

namespace Lemmix.Oracle;

// oracle/lib/hash.js StateHash, word for word: everything is fed as 32-bit words; two lanes,
// FNV-1a style with different primes, the second with a xorshift.
public sealed class StateHash
{
    public const uint NullWord = 0x7fffffff;
    public uint H1 = 0x811c9dc5, H2 = 0x9747b28c;

    public void Word(uint w)
    {
        H1 = unchecked((H1 ^ w) * 0x01000193u);
        uint h2 = unchecked((H2 ^ w) * 0x5bd1e995u);
        H2 = h2 ^ (h2 >> 13);
    }

    public void Word(int w) => Word(unchecked((uint)w));
    public void Bool(bool b) => Word(b ? 1u : 0u);
    public void Null() => Word(NullWord);
    public void Int(int? v) { if (v == null) Null(); else Word(v.Value); }

    public void Str(string? s)
    {
        if (s == null) { Null(); return; }
        Word(s.Length);
        foreach (char c in s) Word((uint)c);
    }

    public void Bytes(ReadOnlySpan<byte> u8)
    {
        Word(u8.Length);
        int n4 = u8.Length >> 2;
        for (int i = 0; i < n4; i++) Word(BinaryPrimitives.ReadUInt32LittleEndian(u8.Slice(i * 4, 4)));
        int rest = u8.Length & 3;
        if (rest != 0)
        {
            uint w = 0;
            for (int j = 0; j < rest; j++) w |= (uint)u8[n4 * 4 + j] << (8 * j);
            Word(w);
        }
    }

    public void Bytes(ushort[] a) => Bytes(MemoryMarshal.AsBytes(a.AsSpan()));
    public void Bytes(sbyte[] a) => Bytes(MemoryMarshal.AsBytes(a.AsSpan()));

    public string Hex() => H1.ToString("x8") + H2.ToString("x8");

    // state.js hashTerrainInto
    public void Terrain(Level level)
    {
        Bytes(level.Physics);
        Bytes(level.GroundImage);
        Bytes(level.GroundMask.GroundMask);
    }

    public static string TerrainHash(Level level) { var h = new StateHash(); h.Terrain(level); return h.Hex(); }
}
