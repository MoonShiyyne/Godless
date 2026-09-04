namespace Godless.Sim.Core
{
    /// <summary>
    /// xoshiro256**, seeded through splitmix64. Integer operations only, so
    /// it produces the same sequence on every platform, in Mono, in IL2CPP
    /// and in CoreCLR — which is what lets two players trade a world seed.
    ///
    /// One stream per system, never one generator shared between systems:
    /// a shared generator couples every system's draw count to every other
    /// system's, so adding one call anywhere reshuffles the whole world.
    ///
    /// This generator is part of the save format. See the frozen-values test.
    ///
    /// Deliberately absent: NextDouble / NextFloat. The sim core's numeric
    /// representation — fixed-point, or float under strict discipline — is an
    /// open stratum-0 decision, and handing out floats before it is made is
    /// how the decision gets taken by accident. See claude/build-order.md.
    /// </summary>
    public sealed class RngStream
    {
        ulong _s0, _s1, _s2, _s3;

        public RngStream(ulong seed)
        {
            // splitmix64 spreads a small seed across the whole state. Seeding
            // xoshiro directly from a counter gives correlated early output.
            ulong z = seed;
            _s0 = SplitMix64(ref z);
            _s1 = SplitMix64(ref z);
            _s2 = SplitMix64(ref z);
            _s3 = SplitMix64(ref z);
        }

        public ulong NextUInt64()
        {
            unchecked
            {
                ulong result = Rotl(_s1 * 5UL, 7) * 9UL;
                ulong t = _s1 << 17;

                _s2 ^= _s0;
                _s3 ^= _s1;
                _s1 ^= _s2;
                _s0 ^= _s3;
                _s2 ^= t;
                _s3 = Rotl(_s3, 45);

                return result;
            }
        }

        /// <summary>
        /// Uniform in [0, exclusiveBound). Rejection sampled, because
        /// <c>NextUInt64() % bound</c> is biased toward low values whenever
        /// the bound does not divide 2^64 — invisible in play, and exactly
        /// the kind of thing that shows up as "settlements prefer the first
        /// site in the list" three strata later.
        /// </summary>
        public int NextInt(int exclusiveBound)
        {
            if (exclusiveBound <= 0)
                throw new System.ArgumentOutOfRangeException(
                    nameof(exclusiveBound), "bound must be positive");

            unchecked
            {
                ulong bound = (ulong)exclusiveBound;
                ulong threshold = (0UL - bound) % bound; // 2^64 mod bound
                ulong r;
                do { r = NextUInt64(); } while (r < threshold);
                return (int)(r % bound);
            }
        }

        /// <summary>Uniform in [inclusiveMin, exclusiveMax).</summary>
        public int NextInt(int inclusiveMin, int exclusiveMax)
        {
            if (exclusiveMax <= inclusiveMin)
                throw new System.ArgumentOutOfRangeException(
                    nameof(exclusiveMax), "max must exceed min");
            return inclusiveMin + NextInt(exclusiveMax - inclusiveMin);
        }

        public bool NextBool() { return (NextUInt64() >> 63) != 0UL; }

        static ulong Rotl(ulong x, int k)
        {
            unchecked { return (x << k) | (x >> (64 - k)); }
        }

        static ulong SplitMix64(ref ulong state)
        {
            unchecked
            {
                state += 0x9E3779B97F4A7C15UL;
                ulong z = state;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                return z ^ (z >> 31);
            }
        }
    }
}
