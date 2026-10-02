using System.Runtime.InteropServices;
using Lemmix.Engine;
using Lemmix.Oracle;

namespace Lemmix.Tests.Ui;

// oracle/lib/ui-env.js hashFrameInto / hashBitmapInto.
public static class UiHash
{
    public static void Frame(StateHash h, Frame? f)
    {
        if (f == null) { h.Null(); return; }
        h.Word(f.Width); h.Word(f.Height); h.Word(f.OffsetX); h.Word(f.OffsetY);
        h.Bytes(MemoryMarshal.AsBytes(f.Data.AsSpan()));
        h.Bytes(f.Mask);
    }

    public static void Bitmap(StateHash h, Bitmap? b)
    {
        if (b == null) { h.Null(); return; }
        h.Word(b.Width); h.Word(b.Height); h.Bytes(b.Data);
    }
}
