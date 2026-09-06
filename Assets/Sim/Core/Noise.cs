namespace Godless.Sim.Core
{
    /// <summary>
    /// Deterministic value noise over an integer lattice.
    ///
    /// Built from StableHash and a polynomial fade, so it uses nothing but
    /// integer hashing and the four basic operations — no transcendentals,
    /// no table of gradients that a mod could reorder, and identical output
    /// on every runtime. Two players who trade a seed get the same island.
    ///
    /// Value noise rather than gradient noise on purpose: it is cheaper, it
    /// needs no permutation table to keep in sync, and at the scale an island
    /// is viewed the difference is invisible under a fractal sum.
    /// </summary>
    public static class Noise
    {
        /// <summary>A stable value in [0,1) for a lattice point.</summary>
        public static double Lattice(ulong seed, int x, int y)
        {
            ulong h = StableHash.Combine(seed, (ulong)(uint)x);
            h = StableHash.Combine(h, (ulong)(uint)y);
            // Top 53 bits into a double's mantissa: exact, and never 1.0.
            return (h >> 11) * (1.0 / 9007199254740992.0);
        }

        /// <summary>Quintic fade, 6t^5 - 15t^4 + 10t^3. Smooth in the first two derivatives.</summary>
        static double Fade(double t) { return t * t * t * (t * (t * 6.0 - 15.0) + 10.0); }

        /// <summary>Bilinear value noise at a continuous point, in [0,1).</summary>
        public static double At(ulong seed, double x, double y)
        {
            double fx = SimMath.Floor(x), fy = SimMath.Floor(y);
            int ix = (int)fx, iy = (int)fy;
            double tx = Fade(x - fx), ty = Fade(y - fy);

            double v00 = Lattice(seed, ix, iy);
            double v10 = Lattice(seed, ix + 1, iy);
            double v01 = Lattice(seed, ix, iy + 1);
            double v11 = Lattice(seed, ix + 1, iy + 1);

            double a = v00 + (v10 - v00) * tx;
            double b = v01 + (v11 - v01) * tx;
            return a + (b - a) * ty;
        }

        /// <summary>
        /// Fractal sum. Each octave doubles the frequency and halves the
        /// contribution, which is what turns smooth blobs into terrain that
        /// reads as eroded at more than one scale.
        /// </summary>
        public static double Fractal(ulong seed, double x, double y, int octaves,
                                     double frequency = 1.0, double gain = 0.5, double lacunarity = 2.0)
        {
            double sum = 0.0, amplitude = 1.0, total = 0.0;
            for (int i = 0; i < octaves; i++)
            {
                // A distinct seed per octave, so octaves cannot align.
                ulong octaveSeed = StableHash.Combine(seed, (ulong)i);
                sum += At(octaveSeed, x * frequency, y * frequency) * amplitude;
                total += amplitude;
                amplitude *= gain;
                frequency *= lacunarity;
            }
            return total > 0.0 ? sum / total : 0.0;
        }
    }
}
