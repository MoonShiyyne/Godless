using System.Collections.Generic;
using Godless.Sim.Content;
using Godless.Sim.Core;
using Godless.Sim.Voxels;
using Godless.Sim.World;
using Xunit;

namespace Godless.Sim.Tests
{
    public class IslandTests
    {
        readonly Xunit.Abstractions.ITestOutputHelper _out;
        public IslandTests(Xunit.Abstractions.ITestOutputHelper output) { _out = output; }

        sealed class Generated
        {
            public ChunkStore Store;
            public IslandMap Map;
            public BiomeTable Biomes;
            public VoxelTypes Types;
        }

        static ContentDatabase RealContent()
        {
            var dir = new System.IO.DirectoryInfo(System.IO.Directory.GetCurrentDirectory());
            while (dir != null && !System.IO.Directory.Exists(System.IO.Path.Combine(dir.FullName, "Assets", "Content")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return ContentLoader.Load(new DirectoryContentSource(
                System.IO.Path.Combine(dir.FullName, "Assets", "Content"))).Database;
        }

        // Generation is deterministic and costs a few hundred ms, so one per
        // seed for the whole test run is both correct and much faster.
        static readonly Dictionary<ulong, Generated> Cache = new Dictionary<ulong, Generated>();
        static readonly object CacheGate = new object();

        static Generated Generate(ulong seed)
        {
            lock (CacheGate)
            {
                if (Cache.TryGetValue(seed, out Generated hit)) return hit;
                Generated made = GenerateUncached(seed);
                Cache[seed] = made;
                return made;
            }
        }

        static Generated GenerateUncached(ulong seed)
        {
            ContentDatabase content = RealContent();
            var g = new Generated
            {
                Store = new ChunkStore(),
                Biomes = BiomeTable.FromContent(content),
                Types = VoxelTypes.FromContent(content),
            };
            g.Map = IslandGenerator.Generate(g.Store, new StreamRegistry(seed), g.Biomes, g.Types);
            return g;
        }

        [Fact]
        public void ContentDeclaresTheBiomesAndVoxels_NotCode()
        {
            ContentDatabase content = RealContent();
            Assert.Equal(4, content.Ids("biome").Count);
            Assert.Equal(5, content.Ids("voxel").Count);

            BiomeTable biomes = BiomeTable.FromContent(content);
            Assert.Equal(4, biomes.Count);
            // Sorted by id, which is what makes overlapping windows resolve
            // the same way on every machine.
            Assert.Equal(Symbol.For("biome.flood-plain"), biomes.At(0).Id);
        }

        [Fact]
        public void SameSeedGivesTheSameIsland()
        {
            // Two genuinely separate generations, bypassing the cache.
            Assert.Equal(GenerateUncached(20260906UL).Map.Digest(), GenerateUncached(20260906UL).Map.Digest());
            Assert.Equal(GenerateUncached(20260906UL).Store.Digest(), GenerateUncached(20260906UL).Store.Digest());
        }

        [Fact]
        public void DifferentSeedsGiveDifferentIslands()
        {
            Assert.NotEqual(Generate(1UL).Map.Digest(), Generate(2UL).Map.Digest());
        }

        /// <summary>
        /// It has to be an island. The edge of the world must be sea, not a
        /// cliff where the array stopped.
        /// </summary>
        [Fact]
        public void TheEdgeOfTheWorldIsSea()
        {
            Generated g = Generate(7UL);
            for (int i = 0; i < ChunkStore.SizeX; i += 8)
            {
                Assert.False(g.Map.IsLand(i, 0), "north edge at x=" + i);
                Assert.False(g.Map.IsLand(i, ChunkStore.SizeZ - 1), "south edge at x=" + i);
                Assert.False(g.Map.IsLand(0, i), "west edge at z=" + i);
                Assert.False(g.Map.IsLand(ChunkStore.SizeX - 1, i), "east edge at z=" + i);
            }
        }

        [Fact]
        public void ThereIsActuallyAnIslandInTheMiddle()
        {
            Generated g = Generate(7UL);
            int land = 0;
            for (int z = 0; z < ChunkStore.SizeZ; z += 4)
                for (int x = 0; x < ChunkStore.SizeX; x += 4)
                    if (g.Map.IsLand(x, z)) land++;

            int sampled = (ChunkStore.SizeZ / 4) * (ChunkStore.SizeX / 4);
            double fraction = (double)land / sampled;
            _out.WriteLine("land cover: " + (fraction * 100.0).ToString("0.0") + "%");
            Assert.InRange(fraction, 0.10, 0.75);
        }

        /// <summary>
        /// G1's first test needs biomes that differ. If one biome swallows
        /// the island, "change the biome and see whether the architecture
        /// changes" has nothing to change.
        /// </summary>
        [Fact]
        public void SeveralBiomesAppearAndNoneSwallowsTheIsland()
        {
            Generated g = Generate(7UL);
            var counts = new Dictionary<int, int>();
            int landColumns = 0;

            for (int z = 0; z < ChunkStore.SizeZ; z += 2)
                for (int x = 0; x < ChunkStore.SizeX; x += 2)
                {
                    if (!g.Map.IsLand(x, z)) continue;
                    landColumns++;
                    int b = g.Map.BiomeAt(x, z);
                    counts.TryGetValue(b, out int n);
                    counts[b] = n + 1;
                }

            foreach (KeyValuePair<int, int> kv in counts)
                _out.WriteLine((kv.Key < 0 ? "<none>" : g.Biomes.At(kv.Key).Id.ToString())
                               + ": " + (100.0 * kv.Value / landColumns).ToString("0.0") + "%");

            Assert.True(counts.Count >= 3, "expected at least three biomes on land, got " + counts.Count);
            foreach (KeyValuePair<int, int> kv in counts)
                Assert.True(kv.Value < landColumns * 0.92, "one biome covers almost everything");
        }

        [Fact]
        public void ColumnsAreSolidBelowTheSurfaceAndEmptyAboveIt()
        {
            Generated g = Generate(7UL);
            ushort air = VoxelTypes.AirId;

            for (int z = 64; z < ChunkStore.SizeZ; z += 97)
                for (int x = 64; x < ChunkStore.SizeX; x += 97)
                {
                    int h = g.Map.HeightAt(x, z);
                    Assert.NotEqual(air, g.Store.Get(x, h - 1, z));
                    if (h > IslandMap.SeaLevel)
                        Assert.Equal(air, g.Store.Get(x, h + 1, z));
                }
        }

        [Fact]
        public void SeaFillsEverythingBelowTheWaterline()
        {
            Generated g = Generate(7UL);
            ushort water = g.Types.IdOf(Symbol.For("voxel.water"));
            Assert.Equal(water, g.Store.Get(2, IslandMap.SeaLevel, 2));
            Assert.Equal(VoxelTypes.AirId, g.Store.Get(2, IslandMap.SeaLevel + 4, 2));
        }

        [Fact]
        public void AGeneratedIslandStillFitsTheMemoryBudget()
        {
            Generated g = Generate(7UL);
            long bytes = g.Store.MemoryBytes;
            _out.WriteLine("generated island: " + bytes / 1024 + " KB across "
                           + g.Store.AllocatedChunks + "/" + ChunkStore.ChunkCount + " chunks");
            Assert.True(bytes < 8L * 1024 * 1024, "island cost " + bytes / 1024 + " KB");
        }
    }
}
