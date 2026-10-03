using System.IO.Compression;
using Lemmix.Engine;

namespace Lemmix.SceneryGen;

// A minimal PNG encoder (RGBA 8-bit, filter 0 rows, zlib): core only decodes.
public static class PngWriter
{
    static readonly uint[] Crc = Enumerable.Range(0, 256).Select(n =>
    {
        uint c = (uint)n;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xedb88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    static void Chunk(Stream s, string type, byte[] data)
    {
        var t = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(BigEndian((uint)data.Length));
        s.Write(t); s.Write(data);
        uint c = 0xffffffffu;
        foreach (byte b in t.Concat(data)) c = Crc[(c ^ b) & 0xff] ^ (c >> 8);
        s.Write(BigEndian(c ^ 0xffffffffu));
    }

    static byte[] BigEndian(uint v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };

    public static void Write(string path, Bitmap bmp)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var f = File.Create(path);
        f.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var ihdr = BigEndian((uint)bmp.Width).Concat(BigEndian((uint)bmp.Height)).Concat(new byte[] { 8, 6, 0, 0, 0 }).ToArray();
        Chunk(f, "IHDR", ihdr);
        using var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.SmallestSize, true))
        {
            int stride = bmp.Width * 4;
            for (int y = 0; y < bmp.Height; y++) { z.WriteByte(0); z.Write(bmp.Data, y * stride, stride); }
        }
        Chunk(f, "IDAT", raw.ToArray());
        Chunk(f, "IEND", Array.Empty<byte>());
    }
}
