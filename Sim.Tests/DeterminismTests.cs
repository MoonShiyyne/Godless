using System;
using System.Collections.Generic;
using Godless.Sim.Core;
using Xunit;

namespace Godless.Sim.Tests
{
    /// <summary>
    /// L2. The determinism test the setup runbook says to write first, and
    /// that the doc says "will fail usefully for the next year".
    ///
    /// The registry's S01 tell is "same seed, 300 years, byte-identical
    /// annals. Two players can trade a world seed." The annalist (S05) does
    /// not exist yet, so the strongest honest version of that today is a
    /// digest over every value every stream produces across 300 simulated
    /// years. When S05 lands, digest the annals instead and delete nothing.
    /// </summary>
    public class DeterminismTests
    {
        const int Years = 300;

        /// <summary>
        /// Runs a fixed workload and returns a digest of everything drawn.
        /// registrationOrder lets a caller register the same streams in a
        /// different order, which is the whole point of test three.
        /// </summary>
        static ulong RunWorld(ulong worldSeed, IReadOnlyList<string> registrationOrder)
        {
            var clock = SimClock.Default();
            var streams = new StreamRegistry(worldSeed);

            // Touch every stream once up front, in the given order, so that
            // "registration order" is a real variable and not a no-op.
            foreach (var id in registrationOrder) streams.Get(id);

            var digest = new Digest();
            var weather = streams.Get("world.weather");
            var mutation = streams.Get("culture.mutation");
            var siting = streams.Get("build.siting");

            long lastTick = clock.TicksInYears(Years);
            for (long tick = 0; tick < lastTick; tick++)
            {
                clock.Advance();

                // Something every tick, something every day, something every
                // year — three cadences, so a bug in the clock shows up as a
                // digest change rather than as nothing at all.
                digest.Add(weather.NextUInt64());
                if (clock.IsFirstTickOfDay) digest.Add(siting.NextUInt64());
                if (clock.IsFirstTickOfYear) digest.Add(mutation.NextUInt64());
            }

            digest.Add((ulong)clock.Year);
            return digest.Value;
        }

        static readonly string[] OrderA = { "world.weather", "culture.mutation", "build.siting" };
        static readonly string[] OrderB = { "build.siting", "world.weather", "culture.mutation" };

        [Fact]
        public void SameSeed_ThreeHundredYears_IsByteIdentical()
        {
            Assert.Equal(RunWorld(20260904UL, OrderA), RunWorld(20260904UL, OrderA));
        }

        [Fact]
        public void DifferentSeed_DivergesEntirely()
        {
            Assert.NotEqual(RunWorld(1UL, OrderA), RunWorld(2UL, OrderA));
        }

        /// <summary>
        /// The one that matters for modding. Streams resolve by hashed string
        /// id, never by load order — so registering the same set in a
        /// different sequence must produce an identical world. If this ever
        /// fails, installing any mod reshuffles every existing save.
        /// </summary>
        [Fact]
        public void RegistrationOrder_DoesNotChangeTheWorld()
        {
            Assert.Equal(RunWorld(20260904UL, OrderA), RunWorld(20260904UL, OrderB));
        }

        [Fact]
        public void Streams_AreIndependentOfOneAnother()
        {
            var a = new StreamRegistry(7UL);
            var b = new StreamRegistry(7UL);

            // Draw heavily from an unrelated stream in `b` only.
            var noise = b.Get("unrelated.noise");
            for (int i = 0; i < 10_000; i++) noise.NextUInt64();

            Assert.Equal(a.Get("culture.mutation").NextUInt64(),
                         b.Get("culture.mutation").NextUInt64());
        }

        /// <summary>
        /// Golden values. The hash and the generator are part of the save
        /// format: changing either silently reshuffles every world anyone has
        /// ever generated. If this test fails, you did not fix a bug — you
        /// broke every seed that has ever been shared. Bump a format version
        /// deliberately, then update these numbers in the same commit.
        /// </summary>
        [Fact]
        public void HashAndGenerator_AreFrozen()
        {
            Assert.Equal(0xAF63DC4C8601EC8CUL, StableHash.OfString("a"));
            Assert.Equal(0x852CBBDE271D42AAUL, StableHash.OfString("culture.mutation"));
            Assert.Equal(StableHash.OfString(""), StableHash.OfString(""));

            var s = new RngStream(0UL);
            Assert.Equal(0x99EC5F36CB75F2B4UL, s.NextUInt64());
            Assert.Equal(0xBF6E1F784956452AUL, s.NextUInt64());
        }

        [Fact]
        public void Clock_ConvertsYearsToTicksAndBack()
        {
            var clock = SimClock.Default();
            long ticks = clock.TicksInYears(Years);
            for (long i = 0; i < ticks; i++) clock.Advance();

            Assert.Equal(Years, clock.Year);
            Assert.Equal(0, clock.DayOfYear);
            Assert.True(clock.IsFirstTickOfYear);
        }

        [Fact]
        public void NextInt_IsUniformAndInRange_WithoutModuloBias()
        {
            var s = new RngStream(42UL);
            var counts = new int[7];
            for (int i = 0; i < 70_000; i++)
            {
                int v = s.NextInt(7);
                Assert.InRange(v, 0, 6);
                counts[v]++;
            }
            foreach (int c in counts) Assert.InRange(c, 9_000, 11_000);
        }
    }
}
