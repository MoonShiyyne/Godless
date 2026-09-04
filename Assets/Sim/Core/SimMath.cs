namespace Godless.Sim.Core
{
    /// <summary>
    /// Deterministic replacements for the transcendental functions, and the
    /// reason Assets/Sim is allowed to use double at all.
    ///
    /// The decision, recorded: the sim core uses double under discipline
    /// rather than fixed-point. In .NET the four basic operations and Sqrt
    /// are IEEE-754 correctly rounded and give bit-identical results in
    /// CoreCLR, Mono and IL2CPP. What varies between runtimes is the
    /// transcendentals, because System.Math forwards those to the platform's
    /// libm, and two machines can legitimately return values one ulp apart.
    /// One ulp, compounded over 300 simulated years, is a different history.
    ///
    /// So the discipline is narrow and greppable: Tools/check-laws.sh bans
    /// System.Math's transcendentals inside Assets/Sim, and everything the
    /// sim needs is reimplemented here from +, -, *, / and Sqrt alone.
    ///
    /// Math.Sqrt, Abs, Floor, Ceiling, Round, Min, Max, Truncate and Sign
    /// stay legal — all are exactly specified by IEEE-754.
    /// </summary>
    public static class SimMath
    {
        public const double Pi = 3.14159265358979323846;
        public const double TwoPi = 6.28318530717958647693;
        public const double HalfPi = 1.57079632679489661923;
        public const double QuarterPi = 0.78539816339744830961;
        public const double Ln2 = 0.69314718055994530942;
        public const double Log2E = 1.44269504088896340736;

        // pi/2 split in two, so argument reduction keeps its low bits: the
        // subtraction x - k*HI is exact for moderate k, and the residual LO
        // is applied afterwards instead of being rounded away.
        const double HalfPiHi = 1.5707963267948966;
        const double HalfPiLo = 6.123233995736766e-17;

        // ── exact, delegated ────────────────────────────────────────────────
        public static double Sqrt(double x) { return System.Math.Sqrt(x); }
        public static double Abs(double x) { return x < 0.0 ? -x : x; }
        public static double Min(double a, double b) { return a < b ? a : b; }
        public static double Max(double a, double b) { return a > b ? a : b; }
        public static int Sign(double x) { return x > 0.0 ? 1 : (x < 0.0 ? -1 : 0); }

        public static double Clamp(double x, double lo, double hi)
        {
            return x < lo ? lo : (x > hi ? hi : x);
        }

        /// <summary>Clamped to [0,1]. The form every utility curve wants.</summary>
        public static double Clamp01(double x) { return x < 0.0 ? 0.0 : (x > 1.0 ? 1.0 : x); }

        public static double Lerp(double a, double b, double t) { return a + (b - a) * t; }

        public static double Floor(double x)
        {
            if (x >= 9.2233720368547758e18 || x <= -9.2233720368547758e18) return x;
            double t = (double)(long)x;
            return (x < 0.0 && t != x) ? t - 1.0 : t;
        }

        public static double Ceiling(double x) { return -Floor(-x); }
        public static double Round(double x) { return Floor(x + 0.5); }

        // ── exponential ─────────────────────────────────────────────────────

        /// <summary>
        /// 2^x. Everything exponential is built on this: split x into an
        /// integer part, which becomes an exponent field directly, and a
        /// fraction in [-0.5, 0.5], where the series below converges in ten
        /// terms to well under one ulp.
        /// </summary>
        public static double Exp2(double x)
        {
            if (double.IsNaN(x)) return x;
            if (x >= 1024.0) return double.PositiveInfinity;
            if (x <= -1075.0) return 0.0;

            double n = Round(x);
            double f = x - n;

            // Taylor of 2^f about 0: coefficients are (ln 2)^k / k!,
            // evaluated by Horner from the highest degree down.
            double p = 7.0545550001155e-9;         // (ln2)^10 / 10!
            p = p * f + 1.0177530874436e-7;        // (ln2)^9  / 9!
            p = p * f + 1.3215486790144e-6;        // (ln2)^8  / 8!
            p = p * f + 1.5252733804060e-5;        // (ln2)^7  / 7!
            p = p * f + 1.5403530393381e-4;        // (ln2)^6  / 6!
            p = p * f + 1.3333558146428e-3;        // (ln2)^5  / 5!
            p = p * f + 9.6181291076285e-3;        // (ln2)^4  / 4!
            p = p * f + 5.5504108664822e-2;        // (ln2)^3  / 3!
            p = p * f + 2.4022650695910e-1;        // (ln2)^2  / 2!
            p = p * f + 6.9314718055995e-1;        //  ln2
            p = p * f + 1.0;

            return p * Pow2Int((int)n);
        }

        /// <summary>2^n for integral n, built straight into the exponent field.</summary>
        static double Pow2Int(int n)
        {
            if (n >= 1024) return double.PositiveInfinity;
            if (n <= -1075) return 0.0;

            if (n >= -1022)
                return System.BitConverter.Int64BitsToDouble((long)(n + 1023) << 52);

            // Subnormal: halve twice from the smallest normal.
            double d = System.BitConverter.Int64BitsToDouble(1L << 52); // 2^-1022
            for (int i = -1022; i > n; i--) d *= 0.5;
            return d;
        }

        public static double Exp(double x) { return Exp2(x * Log2E); }

        /// <summary>
        /// log2(x). Decompose into mantissa and exponent, put the mantissa in
        /// [sqrt(1/2), sqrt(2)) so the atanh series sees |s| &lt; 0.1716, then
        /// six terms are already past 1e-12.
        /// </summary>
        public static double Log2(double x)
        {
            if (double.IsNaN(x) || x < 0.0) return double.NaN;
            if (x == 0.0) return double.NegativeInfinity;
            if (double.IsPositiveInfinity(x)) return x;

            long bits = System.BitConverter.DoubleToInt64Bits(x);
            int exponent = (int)((bits >> 52) & 0x7FF);

            if (exponent == 0) // subnormal: scale up by 2^64 and correct after
            {
                x *= 18446744073709551616.0;
                bits = System.BitConverter.DoubleToInt64Bits(x);
                exponent = (int)((bits >> 52) & 0x7FF);
                exponent -= 64;
            }

            int e = exponent - 1023;
            // Mantissa in [1,2) by forcing the exponent field to 1023.
            double m = System.BitConverter.Int64BitsToDouble((bits & 0x000FFFFFFFFFFFFFL) | (1023L << 52));

            // Recentre to [sqrt(1/2), sqrt(2)) so the series argument is small.
            if (m > 1.4142135623730951) { m *= 0.5; e += 1; }

            double s = (m - 1.0) / (m + 1.0);
            double s2 = s * s;

            // ln(m) = 2s(1 + s^2/3 + s^4/5 + ...)
            double series = 1.0 / 13.0;
            series = series * s2 + 1.0 / 11.0;
            series = series * s2 + 1.0 / 9.0;
            series = series * s2 + 1.0 / 7.0;
            series = series * s2 + 1.0 / 5.0;
            series = series * s2 + 1.0 / 3.0;
            series = series * s2 + 1.0;

            return e + (2.0 * s * series) * Log2E;
        }

        public static double Log(double x) { return Log2(x) * Ln2; }

        public static double Pow(double b, double e)
        {
            if (e == 0.0) return 1.0;
            if (b == 0.0) return e > 0.0 ? 0.0 : double.PositiveInfinity;

            if (b < 0.0)
            {
                // Only integral exponents are meaningful for a negative base.
                double r = Round(e);
                if (r != e) return double.NaN;
                double magnitude = Exp2(Log2(-b) * e);
                bool odd = (Abs(r) % 2.0) == 1.0;
                return odd ? -magnitude : magnitude;
            }
            return Exp2(Log2(b) * e);
        }

        /// <summary>
        /// Half-life decay, the shape scars and salience both use: the value
        /// remaining after `elapsed`, halving every `halfLife`.
        /// </summary>
        public static double Decay(double value, double elapsed, double halfLife)
        {
            if (halfLife <= 0.0) return 0.0;
            return value * Exp2(-elapsed / halfLife);
        }

        // ── trigonometric ───────────────────────────────────────────────────

        public static double Sin(double x)
        {
            if (double.IsNaN(x) || double.IsInfinity(x)) return double.NaN;
            int quadrant;
            double r = ReduceQuarter(x, out quadrant);
            switch (quadrant & 3)
            {
                case 0: return SinCore(r);
                case 1: return CosCore(r);
                case 2: return -SinCore(r);
                default: return -CosCore(r);
            }
        }

        public static double Cos(double x)
        {
            if (double.IsNaN(x) || double.IsInfinity(x)) return double.NaN;
            int quadrant;
            double r = ReduceQuarter(x, out quadrant);
            switch (quadrant & 3)
            {
                case 0: return CosCore(r);
                case 1: return -SinCore(r);
                case 2: return -CosCore(r);
                default: return SinCore(r);
            }
        }

        public static double Tan(double x)
        {
            double c = Cos(x);
            return c == 0.0 ? double.NaN : Sin(x) / c;
        }

        /// <summary>x reduced into [-pi/4, pi/4], with the quadrant it came from.</summary>
        static double ReduceQuarter(double x, out int quadrant)
        {
            double k = Round(x / HalfPi);
            quadrant = (int)(((long)k) & 3L);
            if (k < 0.0) quadrant = (quadrant + 4) & 3;
            return (x - k * HalfPiHi) - k * HalfPiLo;
        }

        static double SinCore(double r)
        {
            double r2 = r * r;
            double p = -7.6471637318198164e-13;   // -1/15!
            p = p * r2 + 1.6059043836821613e-10;  //  1/13!
            p = p * r2 + -2.5052108385441720e-8;  // -1/11!
            p = p * r2 + 2.7557319223985893e-6;   //  1/9!
            p = p * r2 + -1.9841269841269841e-4;  // -1/7!
            p = p * r2 + 8.3333333333333333e-3;   //  1/5!
            p = p * r2 + -1.6666666666666667e-1;  // -1/3!
            return r + r * r2 * p;
        }

        static double CosCore(double r)
        {
            double r2 = r * r;
            double p = -1.1470745597729725e-11;   // -1/14!
            p = p * r2 + 2.0876756987868099e-9;   //  1/12!
            p = p * r2 + -2.7557319223985891e-7;  // -1/10!
            p = p * r2 + 2.4801587301587302e-5;   //  1/8!
            p = p * r2 + -1.3888888888888889e-3;  // -1/6!
            p = p * r2 + 4.1666666666666667e-2;   //  1/4!
            return 1.0 - 0.5 * r2 + r2 * r2 * p;
        }

        /// <summary>
        /// atan, via the identity atan(t) = pi/4 + atan((t-1)/(t+1)) to pull
        /// the argument under tan(pi/8) before the series runs.
        /// </summary>
        public static double Atan(double t)
        {
            if (double.IsNaN(t)) return t;
            if (double.IsPositiveInfinity(t)) return HalfPi;
            if (double.IsNegativeInfinity(t)) return -HalfPi;

            bool negative = t < 0.0;
            if (negative) t = -t;

            bool inverted = t > 1.0;
            if (inverted) t = 1.0 / t;

            bool shifted = t > 0.41421356237309505; // tan(pi/8)
            if (shifted) t = (t - 1.0) / (t + 1.0);

            double result = AtanCore(t);
            if (shifted) result += QuarterPi;
            if (inverted) result = HalfPi - result;
            return negative ? -result : result;
        }

        /// <summary>
        /// atan(t) = t - t^3/3 + t^5/5 - ... for |t| &lt;= tan(pi/8), where
        /// twelve terms land under 1e-13.
        /// </summary>
        static double AtanCore(double t)
        {
            double t2 = t * t;
            double p = -1.0 / 23.0;
            p = p * t2 + 1.0 / 21.0;
            p = p * t2 - 1.0 / 19.0;
            p = p * t2 + 1.0 / 17.0;
            p = p * t2 - 1.0 / 15.0;
            p = p * t2 + 1.0 / 13.0;
            p = p * t2 - 1.0 / 11.0;
            p = p * t2 + 1.0 / 9.0;
            p = p * t2 - 1.0 / 7.0;
            p = p * t2 + 1.0 / 5.0;
            p = p * t2 - 1.0 / 3.0;
            p = p * t2 + 1.0;
            return t * p;
        }

        public static double Atan2(double y, double x)
        {
            if (double.IsNaN(y) || double.IsNaN(x)) return double.NaN;

            if (x > 0.0) return Atan(y / x);
            if (x < 0.0) return y >= 0.0 ? Atan(y / x) + Pi : Atan(y / x) - Pi;

            // x == 0
            if (y > 0.0) return HalfPi;
            if (y < 0.0) return -HalfPi;
            return 0.0;
        }
    }
}
