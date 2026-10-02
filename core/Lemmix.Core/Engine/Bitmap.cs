using System.Runtime.InteropServices;
using Lemmix.Util;

namespace Lemmix.Engine;

// web/lemmix/js/pixels.js Bitmap: {width, height, data} with RGBA bytes, the layout a canvas
// ImageData uses, so a uint view is ABGR on a little-endian machine (every target is one).
// The JS data is a Uint8ClampedArray: every store goes through JsMath.ClampU8.
public sealed class Bitmap
{
    public Bitmap(int width, int height, byte[]? data = null)
    {
        Width = width;
        Height = height;
        Data = data ?? new byte[width * height * 4];
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] Data { get; }

    public Bitmap Clone() => new(Width, Height, (byte[])Data.Clone());

    // A uint view (ABGR words), sharing the bytes.
    public Span<uint> Words() => MemoryMarshal.Cast<byte, uint>(Data.AsSpan(0, Width * Height * 4));

    // Rotate 90° clockwise, like TBitmap32.Rotate90.
    public Bitmap Rotate90()
    {
        int w = Width, h = Height;
        var output = new Bitmap(h, w);
        var s = Words();
        var d = output.Words();
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                d[x * h + (h - 1 - y)] = s[y * w + x]; // (x, y) -> (h - 1 - y, x)
        return output;
    }

    public Bitmap FlipHorizontal()
    {
        int w = Width, h = Height;
        var output = new Bitmap(w, h);
        var s = Words();
        var d = output.Words();
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++) d[y * w + (w - 1 - x)] = s[y * w + x];
        return output;
    }

    public Bitmap FlipVertical()
    {
        int w = Width, h = Height;
        var output = new Bitmap(w, h);
        var s = Words();
        var d = output.Words();
        for (int y = 0; y < h; y++) s.Slice(y * w, w).CopyTo(d.Slice((h - 1 - y) * w, w));
        return output;
    }

    // A sub-rectangle as its own bitmap.
    public Bitmap Crop(int x0, int y0, int w, int h)
    {
        var output = new Bitmap(w, h);
        var s = Words();
        var d = output.Words();
        for (int y = 0; y < h; y++) s.Slice((y0 + y) * Width + x0, w).CopyTo(d.Slice(y * w, w));
        return output;
    }

    // The frames of a strip (vertical unless `horizontal`).
    public List<Bitmap> Frames(int count, bool horizontal = false)
    {
        var output = new List<Bitmap>();
        if (horizontal)
        {
            int fw = JsMath.Floor(Width / (double)count);
            for (int i = 0; i < count; i++) output.Add(Crop(i * fw, 0, fw, Height));
        }
        else
        {
            int fh = JsMath.Floor(Height / (double)count);
            for (int i = 0; i < count; i++) output.Add(Crop(0, i * fh, Width, fh));
        }
        return output;
    }

    // Multiply the colour channels by a colour (MaskImageFromImage's first step).
    public Bitmap Tinted(int rgb)
    {
        var output = Clone();
        var d = output.Data;
        int mr = (rgb >> 16) & 255, mg = (rgb >> 8) & 255, mb = rgb & 255;
        for (int i = 0; i < d.Length; i += 4)
        {
            d[i] = JsMath.ClampU8(JsMath.ToInt32(d[i] * mr / 255.0));
            d[i + 1] = JsMath.ClampU8(JsMath.ToInt32(d[i + 1] * mg / 255.0));
            d[i + 2] = JsMath.ClampU8(JsMath.ToInt32(d[i + 2] * mb / 255.0));
        }
        return output;
    }
}
