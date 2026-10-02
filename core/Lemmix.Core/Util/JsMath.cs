namespace Lemmix.Util;

// The JavaScript number semantics the Lemmix sources rely on, in one place,
// so a port reads like the JS it comes from (core/README.md, "porting rules").
public static class JsMath
{
    // Math.round: halves go up (towards +infinity), unlike Math.Round's banker's rounding.
    public static double Round(double x) => Math.Floor(x + 0.5);

    public static int RoundInt(double x) => ToInt32(Math.Floor(x + 0.5));

    // x | 0, and the ToInt32 conversion every JS bitwise operator applies:
    // truncate towards zero, then wrap modulo 2^32. NaN and infinities give 0.
    public static int ToInt32(double x)
    {
        if (double.IsNaN(x) || double.IsInfinity(x)) return 0;
        double t = Math.Truncate(x);
        if (t >= int.MinValue && t <= int.MaxValue) return (int)t;
        double m = t % 4294967296.0;
        if (m < 0) m += 4294967296.0;
        return unchecked((int)(uint)m);
    }

    // x >>> 0
    public static uint ToUint32(double x) => unchecked((uint)ToInt32(x));

    // Math.trunc / Math.floor on a double, as int (callers keep the JS operation order)
    public static int Trunc(double x) => ToInt32(Math.Truncate(x));
    public static int Floor(double x) => ToInt32(Math.Floor(x));

    // A store into a Uint8ClampedArray (ToUint8Clamp): clamp to 0..255, round half to even.
    public static byte ClampU8(double v)
    {
        if (double.IsNaN(v) || v <= 0) return 0;
        if (v >= 255) return 255;
        return (byte)Math.Round(v, MidpointRounding.ToEven);
    }

    public static byte ClampU8(int v) => v <= 0 ? (byte)0 : v >= 255 ? (byte)255 : (byte)v;

    // JS % on numbers keeps the sign of the dividend, like C# % on ints and doubles.
    public static int Mod(int a, int b) => a % b;
}
