using Lemmix.Engine;

namespace Lemmix.Ui;

// A press on the panel, as DisplayImage's mouse events carry it: panel pixels (fractional from a
// ray hit) and the button (0 left, 1 middle, 2 right).
public readonly record struct PanelPointer(double X, double Y, int Button = 0);

// web/js/lemmings.js DisplayImage, the parts web/lemmix/js/panel.js draws into and listens to:
// initSize, clear, drawFrame, redraw and the mouse events. The pixels are an RGBA byte array
// (the ImageData layout) with an ABGR word view, as the JS draws through a Uint32Array.
public sealed class PixelCanvas
{
    readonly Action? _onRedraw;

    // `onRedraw` is the stage's redraw (the 3D page's HeadlessStage marks its texture dirty)
    public PixelCanvas(Action? onRedraw = null) { _onRedraw = onRedraw; }

    public readonly Engine.EventHandler<PanelPointer> OnMouseUp = new();
    public readonly Engine.EventHandler<PanelPointer> OnMouseDown = new();
    public readonly Engine.EventHandler<PanelPointer> OnMouseMove = new();
    public readonly Engine.EventHandler<PanelPointer> OnDoubleClick = new();

    public byte[]? Data { get; private set; }   // imgData.data
    public int Width { get; private set; }
    public int Height { get; private set; }
    public int Version { get; private set; }     // counts redraws

    public int GetWidth() => Data == null ? 0 : Width;
    public int GetHeight() => Data == null ? 0 : Height;

    public void InitSize(int width, int height)
    {
        // create image data (the stage's createImage: a new ImageData, transparent black)
        if (Data == null || Width != width || Height != height)
        {
            Data = new byte[width * height * 4];
            Width = width;
            Height = height;
            Clear();
        }
    }

    // The JS fills `new Uint32Array(this.imgData.data)` - a copy of the bytes, not a view - with
    // 0xFF00FF00, so the display is left as it was: transparent black. Kept as a no-op.
    public void Clear() { }

    // copy a frame to the display: the frame's words where its mask is set, clipped
    public void DrawFrame(Frame frame, int posX, int posY)
    {
        if (Data == null) return;
        int srcW = frame.Width, srcH = frame.Height;
        var srcBuffer = frame.Data;
        var srcMask = frame.Mask;
        int destW = Width, destH = Height;
        var destData = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(Data.AsSpan());
        int destX = posX + frame.OffsetX, destY = posY + frame.OffsetY;
        for (int y = 0; y < srcH; y++)
        {
            int outY = y + destY;
            if (outY < 0 || outY >= destH) continue;
            for (int x = 0; x < srcW; x++)
            {
                int srcIndex = srcW * y + x;
                if (srcMask[srcIndex] == 0) continue; // ignore transparent pixels
                int outX = x + destX;
                if (outX < 0 || outX >= destW) continue;
                destData[destW * outY + outX] = srcBuffer[srcIndex];
            }
        }
    }

    public void Redraw() { Version++; _onRedraw?.Invoke(); }
}
