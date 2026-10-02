using Lemmix.Engine;
using Lemmix.Io;

namespace Lemmix.Ui;

// web/3d/js/cursor.js GameCursor, its pictures: NeoLemmix's cursor - a cross over the level, a
// square once a lemming is under it, and a small arrow beside either while the direction filter
// is on - six pictures composed from gfx/cursor's standard.png, focused.png, direction_left.png
// and direction_right.png (GameWindow.pas LoadCursors / SetCurrentCursor), 16 px with a 32 px
// twin from gfx/cursor-hr. The hot spot is pixel (8, 8) of the 16 px picture; the sprite over
// the board spans 16 level pixels. The CSS cursor strings and the Three.js textures and sprites
// are the host's; this keeps the composed RGBA pictures.
//
// The JS composes on a canvas (drawImage scaled to the size, smoothing off, source-over). The
// pictures are 16x16 and 32x32 with opaque or transparent pixels only, so composing is copying
// the opaque pixels; a picture of another size is scaled nearest-neighbour, and a translucent
// pixel is blended with Pixels.MergeOver - neither is what a browser canvas is known to do
// bit for bit, and neither occurs with NeoLemmix's files.
public sealed class CursorImages
{
    static readonly string[] CursorFiles = { "standard", "focused", "direction_left", "direction_right" };
    public const int Hotspot = 8;   // of 16
    public const int LevelPx = 16;  // how many level pixels the picture spans

    public bool Ok;
    public readonly Dictionary<string, Bitmap> Small = new(StringComparer.Ordinal);  // key -> the 16 px picture
    public readonly Dictionary<string, Bitmap> Large = new(StringComparer.Ordinal);  // key -> the 32 px one, when cursor-hr has all four
    // key -> the largest picture (what the JS makes textures from)
    public Bitmap? Picture(string key) => Large.TryGetValue(key, out var b) ? b : Small.TryGetValue(key, out var s) ? s : null;

    // Load from the asset root; the result may not be Ok (no NeoLemmix cursor files).
    public static CursorImages Load(IFileSource io)
    {
        var cursor = new CursorImages();
        string dir = StyleManager.AssetDir;
        var lo = new Dictionary<string, Bitmap?>();
        var hi = new Dictionary<string, Bitmap?>();
        foreach (string n in CursorFiles)
        {
            lo[n] = io.Image(dir + "gfx/cursor/" + n + ".png");
            hi[n] = io.Image(dir + "gfx/cursor-hr/" + n + ".png");
        }
        if (CursorFiles.Any(n => lo[n] == null)) return cursor;
        bool hasHi = CursorFiles.All(n => hi[n] != null);
        foreach (string kind in new[] { "standard", "focused" })
            foreach (string side in new[] { "", "left", "right" })
            {
                string key = kind + (side != "" ? "-" + side : "");
                cursor.Small[key] = Compose(lo, kind, side, 16);
                if (hasHi) cursor.Large[key] = Compose(hi, kind, side, 32);
            }
        cursor.Ok = true;
        return cursor;
    }

    static Bitmap Compose(Dictionary<string, Bitmap?> set, string kind, string side, int size)
    {
        var canvas = new Bitmap(size, size);
        DrawImage(canvas, set[kind]!, size);
        if (side != "") DrawImage(canvas, set["direction_" + side]!, size);
        return canvas;
    }

    // ctx.drawImage(img, 0, 0, size, size) with imageSmoothingEnabled = false
    static void DrawImage(Bitmap dst, Bitmap src, int size)
    {
        int sw = src.Width, sh = src.Height;
        for (int y = 0; y < size; y++)
        {
            int sy = (int)Math.Floor((y + 0.5) * sh / size);
            for (int x = 0; x < size; x++)
            {
                int sx = (int)Math.Floor((x + 0.5) * sw / size);
                Pixels.MergeOver(src.Data, (sy * sw + sx) * 4, dst.Data, (y * size + x) * 4);
            }
        }
    }

    // Which of the six: a lemming under the pointer, and the direction filter.
    public static string Key(bool focused, int selectDx) =>
        (focused ? "focused" : "standard") + (selectDx < 0 ? "-left" : selectDx > 0 ? "-right" : "");
}
