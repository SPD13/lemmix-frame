using Lemmix.Util;

namespace Lemmix.Engine;

// A combiner: `f` (source bytes at fi) onto `b` (destination bytes at bi), RGBA.
public delegate void Combine(byte[] fd, int fi, byte[] bd, int bi);

// web/lemmix/js/pixels.js Pixels: the pixel arithmetic NeoLemmix draws with, ported from
// LemRendering.pas (CombineTerrain*, CombineGadgets*, the physics-prep channels) and
// Graphics32's MergeMem; the nine-slice from LemTypes.pas.
public static class Pixels
{
    public const int AlphaCutoff = 0x80;

    // Graphics32 MergeMem: `f` over `b`. Writes the result into b's slot.
    public static void MergeOver(byte[] fd, int fi, byte[] bd, int bi)
    {
        int fa = fd[fi + 3];
        if (fa == 0) return;
        if (fa == 255)
        {
            bd[bi] = fd[fi]; bd[bi + 1] = fd[fi + 1]; bd[bi + 2] = fd[fi + 2]; bd[bi + 3] = 255;
            return;
        }
        int ba = bd[bi + 3];
        if (ba == 0)
        {
            bd[bi] = fd[fi]; bd[bi + 1] = fd[fi + 1]; bd[bi + 2] = fd[fi + 2]; bd[bi + 3] = (byte)fa;
            return;
        }
        double ra = fa + ba - JsMath.Round(fa * ba / 255.0);
        double wf = fa / ra, wb = (ba * (255 - fa) / 255.0) / ra;
        bd[bi] = JsMath.ClampU8(JsMath.Round(fd[fi] * wf + bd[bi] * wb));
        bd[bi + 1] = JsMath.ClampU8(JsMath.Round(fd[fi + 1] * wf + bd[bi + 1] * wb));
        bd[bi + 2] = JsMath.ClampU8(JsMath.Round(fd[fi + 2] * wf + bd[bi + 2] * wb));
        bd[bi + 3] = JsMath.ClampU8(ra);
    }

    // CombineTerrainDefault: paint where the piece has any alpha.
    public static void CombineTerrainDefault(byte[] fd, int fi, byte[] bd, int bi)
    {
        if (fd[fi + 3] != 0) MergeOver(fd, fi, bd, bi);
    }

    // CombineTerrainNoOverwrite: the existing pixel stays in front.
    [ThreadStatic] static byte[]? _scratch;
    public static void CombineTerrainNoOverwrite(byte[] fd, int fi, byte[] bd, int bi)
    {
        if (fd[fi + 3] == 0) return;
        var scratch = _scratch ??= new byte[4];
        scratch[0] = fd[fi]; scratch[1] = fd[fi + 1]; scratch[2] = fd[fi + 2]; scratch[3] = fd[fi + 3];
        MergeOver(bd, bi, scratch, 0);
        bd[bi] = scratch[0]; bd[bi + 1] = scratch[1]; bd[bi + 2] = scratch[2]; bd[bi + 3] = scratch[3];
    }

    public static int SolidityErase(int f, int b)
    {
        if (f == 0) return b;
        if (f == 255 || b == 0) return 0;
        return (int)JsMath.Round((1 - f / 255.0) * (b / 255.0) * 255);
    }

    public static int Solidity(int f, int b)
    {
        if (f == 0) return b;
        if (b == 0) return f;
        if (f == 255 || b == 255) return 255;
        return (int)JsMath.Round((1 - (1 - f / 255.0) * (1 - b / 255.0)) * 255);
    }

    // CombineTerrainErase: only the alpha changes.
    public static void CombineTerrainErase(byte[] fd, int fi, byte[] bd, int bi)
    {
        bd[bi + 3] = JsMath.ClampU8(SolidityErase(fd[fi + 3], bd[bi + 3]));
    }

    // CombineGadgetsDefault: opaque replaces, translucent merges.
    public static void CombineGadget(byte[] fd, int fi, byte[] bd, int bi)
    {
        int fa = fd[fi + 3];
        if (fa == 255)
        {
            bd[bi] = fd[fi]; bd[bi + 1] = fd[fi + 1]; bd[bi + 2] = fd[fi + 2]; bd[bi + 3] = 255;
        }
        else if (fa != 0)
        {
            MergeOver(fd, fi, bd, bi);
        }
    }

    // The physics-prep info map keeps four channels per pixel, in RGBA order:
    //   R = steel, G = one-way eligible, B = erase (source only), A = solidity
    static int Property(int f, int b, int intensity) => b + (int)JsMath.Round((f - b) * (intensity / 255.0));

    static void PhysicsPrepInternal(int sSol, int sSteel, int sOne, int sErase, byte[] bd, int bi)
    {
        int dSol = bd[bi + 3], dSteel = bd[bi], dOne = bd[bi + 1];
        if (sErase > 0)
        {
            dSol = SolidityErase(sErase, dSol);
            if (dSol == 0) { dSteel = 0; dOne = 0; }
            else { dSteel = Property(0, dSteel, sErase); dOne = Property(0, dOne, sErase); }
        }
        else
        {
            dSol = Solidity(sSol, dSol);
            dSteel = Property(sSteel, dSteel, sSol);
            dOne = Property(sOne, dOne, sSol);
        }
        bd[bi] = JsMath.ClampU8(dSteel); bd[bi + 1] = JsMath.ClampU8(dOne); bd[bi + 3] = JsMath.ClampU8(dSol);
    }

    // A physics-prep combiner for one piece: `kind` is "standard", "steel", "oneway" or
    // "erase"; `noOverwrite` swaps the roles (the existing pixel is drawn over the new one),
    // except for erasers.
    public static Combine PhysicsCombiner(string kind, bool noOverwrite)
    {
        int steel = kind == "steel" ? 255 : 0;
        int oneWay = kind == "oneway" ? 255 : 0;
        if (kind == "erase")
            return (fd, fi, bd, bi) => PhysicsPrepInternal(0, 0, 0, fd[fi + 3], bd, bi);
        if (!noOverwrite)
            return (fd, fi, bd, bi) => PhysicsPrepInternal(fd[fi + 3], steel, oneWay, 0, bd, bi);
        var tmp = new byte[4];
        return (fd, fi, bd, bi) =>
        {
            // Internal(B, F) then B := F : the new piece is the base, the old pixel is drawn onto it
            tmp[0] = (byte)steel; tmp[1] = (byte)oneWay; tmp[2] = 0; tmp[3] = fd[fi + 3];
            PhysicsPrepInternal(bd[bi + 3], bd[bi], bd[bi + 1], 0, tmp, 0);
            bd[bi] = tmp[0]; bd[bi + 1] = tmp[1]; bd[bi + 2] = 0; bd[bi + 3] = tmp[3];
        };
    }

    // Draw src's rect (sx, sy, w, h) at (dx, dy) on dst through `combine`, clipped.
    public static void Blit(Bitmap dst, int dx, int dy, Bitmap src, int sx, int sy, int w, int h, Combine combine)
    {
        int x0 = Math.Max(0, -dx), y0 = Math.Max(0, -dy);
        int x1 = Math.Min(w, dst.Width - dx), y1 = Math.Min(h, dst.Height - dy);
        if (x1 <= x0 || y1 <= y0) return;
        byte[] sd = src.Data, dd = dst.Data;
        for (int y = y0; y < y1; y++)
        {
            int si = ((sy + y) * src.Width + sx + x0) * 4;
            int di = ((dy + y) * dst.Width + dx + x0) * 4;
            for (int x = x0; x < x1; x++, si += 4, di += 4) combine(sd, si, dd, di);
        }
    }

    public readonly record struct Margins(int Left, int Top, int Right, int Bottom);

    // LemTypes.pas DrawNineSlice: fill (dx, dy, dw, dh) on dst from src, with the margins kept
    // at the edges and everything else tiled. With zero margins the whole image simply tiles.
    public static void DrawNineSlice(Bitmap dst, int dx, int dy, int dw, int dh, Bitmap src, Margins margins, Combine combine)
    {
        int sw = src.Width, sh = src.Height;
        if (dw == sw && dh == sh) { Blit(dst, dx, dy, src, 0, 0, sw, sh, combine); return; }
        int ml = margins.Left, mt = margins.Top, mr = margins.Right, mb = margins.Bottom;
        static (int, int) Trim(int a, int b, int size)
        {
            int overlap = (a + b) - size;
            if (overlap <= 0) return (a, b);
            a -= overlap >> 1; b -= overlap >> 1;
            if (overlap % 2 == 1) { if (a >= b) a--; else b--; }
            if (a < 0) { b += a; a = 0; }
            if (b < 0) { a += b; b = 0; }
            return (a, b);
        }
        (ml, mr) = Trim(ml, mr, dw);
        (mt, mb) = Trim(mt, mb, dh);
        (int, int, int, int)[] Rects(int x, int y, int w, int h)
        {
            int vw = w - (ml + mr), vh = h - (mt + mb);
            return new[]
            {
                (x, y, ml, mt), (x + ml, y, vw, mt), (x + ml + vw, y, mr, mt),
                (x, y + mt, ml, vh), (x + ml, y + mt, vw, vh), (x + ml + vw, y + mt, mr, vh),
                (x, y + mt + vh, ml, mb), (x + ml, y + mt + vh, vw, mb), (x + ml + vw, y + mt + vh, mr, mb),
            };
        }
        var srcRects = Rects(0, 0, sw, sh);
        var dstRects = Rects(dx, dy, dw, dh);
        for (int i = 0; i < 9; i++)
        {
            var (sx, sy, sww, shh) = srcRects[i];
            var (tx, ty, tw, th) = dstRects[i];
            if (sww <= 0 || shh <= 0 || tw <= 0 || th <= 0) continue;
            // DrawTiled: whole tiles, then the remainder
            int countX = JsMath.Floor((tw - 1) / (double)sww), countY = JsMath.Floor((th - 1) / (double)shh);
            for (int iy = 0; iy <= countY; iy++)
            {
                int h = iy == countY ? ((th - 1) % shh) + 1 : shh;
                for (int ix = 0; ix <= countX; ix++)
                {
                    int w = ix == countX ? ((tw - 1) % sww) + 1 : sww;
                    Blit(dst, tx + ix * sww, ty + iy * shh, src, sx, sy, w, h, combine);
                }
            }
        }
    }
}
