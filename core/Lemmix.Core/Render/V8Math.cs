namespace Lemmix.Render;

// The transcendental functions as V8 computes them (src/base/ieee754.cc, its fdlibm port, and
// the Math.hypot builtin), so pictures and geometry built from Math.sin / Math.cos / Math.exp /
// Math.hypot come out bit for bit as the web page's. .NET's Math.Sin and friends call the
// platform libm, which rounds the last bit differently in a few percent of cases.
// ieee754.cc is compiled by clang with its default -ffp-contract=on, so on arm64 (the Mac the
// oracles run on, the headsets' browsers) every `a*b + c` inside one expression is a fused
// multiply-add: F() below, placed where clang places them (the left product first). With that,
// 200 000 random arguments agree with node (V8 14.6, arm64) on every bit; an x86 build of V8,
// which has no contraction, would differ from both in the last bit now and then.
// Only the argument ranges the 3D layer uses need the fast paths; |x| >= 2^19 * pi/2 falls back
// to the platform's reduction.
public static class V8Math
{
    // a * b + c rounded once: the fmadd/fmsub clang contracts `a*b + c` within an expression to
    // (-ffp-contract=on, the default) on arm64, where V8's ieee754.cc is built that way
    static double F(double a, double b, double c) => Math.FusedMultiplyAdd(a, b, c);

    static int Hi(double x) => (int)(BitConverter.DoubleToInt64Bits(x) >> 32);
    static uint Lo(double x) => unchecked((uint)BitConverter.DoubleToInt64Bits(x));
    static double FromWords(int hi, uint lo) => BitConverter.Int64BitsToDouble(((long)hi << 32) | lo);

    static readonly int[] Npio2Hw =
    {
        0x3FF921FB, 0x400921FB, 0x4012D97C, 0x401921FB, 0x401F6A7A, 0x4022D97C,
        0x4025FDBB, 0x402921FB, 0x402C463A, 0x402F6A7A, 0x4031475C, 0x4032D97C,
        0x40346B9C, 0x4035FDBB, 0x40378FDB, 0x403921FB, 0x403AB41B, 0x403C463A,
        0x403DD85A, 0x403F6A7A, 0x40407E4C, 0x4041475C, 0x4042106C, 0x4042D97C,
        0x4043A28C, 0x40446B9C, 0x404534AC, 0x4045FDBB, 0x4046C6CB, 0x40478FDB,
        0x404858EB, 0x404921FB,
    };

    const double Half = 0.5;
    const double Invpio2 = 6.36619772367581382433e-01;
    const double Pio2_1 = 1.57079632673412561417e+00;
    const double Pio2_1t = 6.07710050650619224932e-11;
    const double Pio2_2 = 6.07710050630396597660e-11;
    const double Pio2_2t = 2.02226624879595063154e-21;
    const double Pio2_3 = 2.02226624871116645580e-21;
    const double Pio2_3t = 8.47842766036889956997e-32;

    // __ieee754_rem_pio2: x - n*pi/2 as y0 + y1
    static int RemPio2(double x, out double y0, out double y1)
    {
        int hx = Hi(x);
        int ix = hx & 0x7fffffff;
        double z, w, t, r, fn;
        if (ix <= 0x3fe921fb) { y0 = x; y1 = 0; return 0; }
        if (ix < 0x4002d97c)
        {
            if (hx > 0)
            {
                z = x - Pio2_1;
                if (ix != 0x3ff921fb) { y0 = z - Pio2_1t; y1 = (z - y0) - Pio2_1t; }
                else { z -= Pio2_2; y0 = z - Pio2_2t; y1 = (z - y0) - Pio2_2t; }
                return 1;
            }
            else
            {
                z = x + Pio2_1;
                if (ix != 0x3ff921fb) { y0 = z + Pio2_1t; y1 = (z - y0) + Pio2_1t; }
                else { z += Pio2_2; y0 = z + Pio2_2t; y1 = (z - y0) + Pio2_2t; }
                return -1;
            }
        }
        if (ix <= 0x413921fb)
        {
            t = Math.Abs(x);
            int n = (int)F(t, Invpio2, Half);
            fn = n;
            r = F(-fn, Pio2_1, t);
            w = fn * Pio2_1t;
            if (n < 32 && ix != Npio2Hw[n - 1])
            {
                y0 = r - w;
            }
            else
            {
                int j = ix >> 20;
                y0 = r - w;
                int high = Hi(y0);
                int i = j - ((high >> 20) & 0x7ff);
                if (i > 16)
                {
                    t = r;
                    w = fn * Pio2_2;
                    r = t - w;
                    w = F(fn, Pio2_2t, -((t - r) - w));
                    y0 = r - w;
                    high = Hi(y0);
                    i = j - ((high >> 20) & 0x7ff);
                    if (i > 49)
                    {
                        t = r;
                        w = fn * Pio2_3;
                        r = t - w;
                        w = F(fn, Pio2_3t, -((t - r) - w));
                        y0 = r - w;
                    }
                }
            }
            y1 = (r - y0) - w;
            if (hx < 0) { y0 = -y0; y1 = -y1; return -n; }
            return n;
        }
        // never reached by the 3D layer's arguments: the platform's reduction
        double q = Math.IEEERemainder(x, Math.PI / 2);
        y0 = q; y1 = 0;
        return (int)Math.Round((x - q) / (Math.PI / 2));
    }

    const double S1 = -1.66666666666666324348e-01, S2 = 8.33333333332248946124e-03, S3 = -1.98412698298579493134e-04,
        S4 = 2.75573137070700676789e-06, S5 = -2.50507602534068634195e-08, S6 = 1.58969099521155010221e-10;

    static double KernelSin(double x, double y, int iy)
    {
        int ix = Hi(x) & 0x7fffffff;
        if (ix < 0x3e400000) { if ((int)x == 0) return x; }
        double z = x * x;
        double v = z * x;
        double r = F(z, F(z, F(z, F(z, S6, S5), S4), S3), S2);
        if (iy == 0) return F(v, F(z, r, S1), x);
        return x - F(-v, S1, F(z, F(Half, y, -(v * r)), -y));
    }

    const double C1 = 4.16666666666666019037e-02, C2 = -1.38888888888741095749e-03, C3 = 2.48015872894767294178e-05,
        C4 = -2.75573143513906633035e-07, C5 = 2.08757232129817482790e-09, C6 = -1.13596475577881948265e-11;

    static double KernelCos(double x, double y)
    {
        int ix = Hi(x) & 0x7fffffff;
        if (ix < 0x3e400000) { if ((int)x == 0) return 1.0; }
        double z = x * x;
        double r = z * F(z, F(z, F(z, F(z, F(z, C6, C5), C4), C3), C2), C1);
        if (ix < 0x3FD33333) return 1.0 - F(0.5, z, -F(z, r, -(x * y)));
        double qx = ix > 0x3fe90000 ? 0.28125 : FromWords(ix - 0x00200000, 0);
        double hz = F(0.5, z, -qx);
        double a = 1.0 - qx;
        return a - (hz - F(z, r, -(x * y)));
    }

    public static double Sin(double x)
    {
        int ix = Hi(x) & 0x7fffffff;
        if (ix <= 0x3fe921fb) return KernelSin(x, 0, 0);
        if (ix >= 0x7ff00000) return x - x;
        int n = RemPio2(x, out double y0, out double y1);
        return (n & 3) switch
        {
            0 => KernelSin(y0, y1, 1),
            1 => KernelCos(y0, y1),
            2 => -KernelSin(y0, y1, 1),
            _ => -KernelCos(y0, y1),
        };
    }

    public static double Cos(double x)
    {
        int ix = Hi(x) & 0x7fffffff;
        if (ix <= 0x3fe921fb) return KernelCos(x, 0);
        if (ix >= 0x7ff00000) return x - x;
        int n = RemPio2(x, out double y0, out double y1);
        return (n & 3) switch
        {
            0 => KernelCos(y0, y1),
            1 => -KernelSin(y0, y1, 1),
            2 => -KernelCos(y0, y1),
            _ => KernelSin(y0, y1, 1),
        };
    }

    const double Huge = 1.0e+300, Twom1000 = 9.33263618503218878990e-302;
    const double OThreshold = 7.09782712893383973096e+02, UThreshold = -7.45133219101941108420e+02;
    static readonly double[] HalF = { 0.5, -0.5 };
    static readonly double[] Ln2HI = { 6.93147180369123816490e-01, -6.93147180369123816490e-01 };
    static readonly double[] Ln2LO = { 1.90821492927058770002e-10, -1.90821492927058770002e-10 };
    const double Invln2 = 1.44269504088896338700e+00;
    const double P1 = 1.66666666666666019037e-01, P2 = -2.77777777770155933842e-03, P3 = 6.61375632143793436117e-05,
        P4 = -1.65339022054652515390e-06, P5 = 4.13813679705723846039e-08;

    public static double Exp(double x)
    {
        double y, hi = 0, lo = 0, c, t, twopk;
        int k = 0;
        uint hx = unchecked((uint)Hi(x));
        int xsb = (int)((hx >> 31) & 1);
        hx &= 0x7fffffff;
        if (hx >= 0x40862E42)
        {
            if (hx >= 0x7ff00000)
            {
                if (((hx & 0xfffff) | Lo(x)) != 0) return x + x;
                return xsb == 0 ? x : 0.0;
            }
            if (x > OThreshold) return double.PositiveInfinity;
            if (x < UThreshold) return 0.0;
        }
        if (hx > 0x3fd62e42)
        {
            if (hx < 0x3FF0A2B2)
            {
                hi = x - Ln2HI[xsb]; lo = Ln2LO[xsb]; k = 1 - xsb - xsb;
            }
            else
            {
                k = (int)F(Invln2, x, HalF[xsb]);
                t = k;
                hi = F(-t, Ln2HI[0], x);
                lo = t * Ln2LO[0];
            }
            x = hi - lo;
        }
        else if (hx < 0x3e300000)
        {
            if (Huge + x > 1.0) return 1.0 + x;
        }
        else k = 0;

        t = x * x;
        twopk = k >= -1021 ? FromWords(0x3ff00000 + (k << 20), 0) : FromWords(0x3ff00000 + ((k + 1000) << 20), 0);
        c = F(-t, F(t, F(t, F(t, F(t, P5, P4), P3), P2), P1), x);
        if (k == 0) return 1.0 - ((x * c) / (c - 2.0) - x);
        y = 1.0 - ((lo - (x * c) / (2.0 - c)) - hi);
        if (k >= -1021)
        {
            if (k == 1024) return y * 2.0 * 8.98846567431158e307;
            return y * twopk;
        }
        return y * twopk * Twom1000;
    }

    // Math.hypot as the V8 builtin computes it: scaled by the largest, Kahan-summed squares.
    public static double Hypot(params double[] args)
    {
        if (args.Length == 0) return 0;
        var abs = new double[args.Length];
        bool nan = false;
        double max = 0;
        for (int i = 0; i < args.Length; i++)
        {
            double v = args[i];
            if (double.IsNaN(v)) nan = true;
            else
            {
                double a = Math.Abs(v);
                abs[i] = a;
                if (a > max) max = a;
            }
        }
        if (max == double.PositiveInfinity) return double.PositiveInfinity;
        if (nan) return double.NaN;
        if (max == 0) return 0;
        double sum = 0, compensation = 0;
        for (int i = 0; i < abs.Length; i++)
        {
            double n = abs[i] / max;
            double summand = n * n - compensation;
            double preliminary = sum + summand;
            compensation = (preliminary - sum) - summand;
            sum = preliminary;
        }
        return Math.Sqrt(sum) * max;
    }

    // Math.pow(x, 2), the only power the 3D layer takes: V8 gives exactly x * x.
    public static double Square(double x) => x * x;
}
