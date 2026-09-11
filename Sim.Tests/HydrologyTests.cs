using System.IO;
using Godless.Sim.Content;
using Godless.Sim.Core;
using Godless.Sim.Voxels;
using Godless.Sim.World;
using Xunit;

namespace Godless.Sim.Tests
{
    public class HydrologyTests
    {
        const int Sea = 10;

        /// <summary>A grid of heights from a function, with rain on every land column.</summary>
        static DrainageMap Drain(int w, int d, System.Func<int, int, int> height, out int[] ground, out int[] rain)
        {
            ground = new int[w * d];
            rain = new int[w * d];
            for (int z = 0; z < d; z++)
                for (int x = 0; x < w; x++)
                {
                    ground[z * w + x] = height(x, z);
                    rain[z * w + x] = ground[z * w + x] > Sea ? 1 : 0;
                }
            return Drainage.Compute(w, d, ground, rain, Sea);
        }

        static int Cone(int x, int z)
        {
            int dx = x - 20, dz = z - 20;
            int r = (int)System.Math.Sqrt(dx * dx + dz * dz);
            return 40 - r;
        }

        /// <summary>Rain is neither lost nor made: everything that falls arrives at the sea.</summary>
        [Fact]
        public void EveryDropThatFallsReachesTheSea()
        {
            DrainageMap map = Drain(41, 41, Cone, out int[] ground, out int[] rain);

            long fallen = 0, arrived = 0;
            for (int i = 0; i < rain.Length; i++) fallen += rain[i];
            for (int z = 0; z < 41; z++)
                for (int x = 0; x < 41; x++)
                    if (map.DownstreamOf(x, z) < 0) arrived += map.FlowAt(x, z);
            Assert.Equal(fallen, arrived);
        }

        [Fact]
        public void WaterRunsToANeighbourNoHigherAndAlwaysEndsAtTheSea()
        {
            const int w = 41;
            DrainageMap map = Drain(w, w, Cone, out int[] ground, out _);
            for (int z = 0; z < w; z++)
                for (int x = 0; x < w; x++)
                {
                    int at = z * w + x, steps = 0;
                    while (map.DownstreamOf(at % w, at / w) >= 0)
                    {
                        int next = map.DownstreamOf(at % w, at / w);
                        Assert.True(System.Math.Abs(next % w - at % w) <= 1 && System.Math.Abs(next / w - at / w) <= 1, "one step at a time");
                        Assert.True(map.FilledLevelAt(next % w, next / w) <= map.FilledLevelAt(at % w, at / w), "never uphill");
                        at = next;
                        Assert.True(++steps < w * w, "a cycle");
                    }
                    Assert.True(ground[at] <= Sea || at % w == 0 || at / w == 0 || at % w == w - 1 || at / w == w - 1);
                }
        }

        /// <summary>A pit fills to where it spills, and that depth is a lake.</summary>
        [Fact]
        public void APitFillsToItsSpillHeight()
        {
            // A plateau at 30 draining to sea on the west, with a 3x3 pit at
            // 24 and a rim that is lowest, at 28, on its east side.
            DrainageMap map = Drain(20, 20, (x, z) =>
            {
                if (x == 0) return Sea;
                if (x >= 9 && x <= 11 && z >= 9 && z <= 11) return 24;
                if (x >= 8 && x <= 12 && z >= 8 && z <= 12) return x == 12 && z == 10 ? 28 : 33;
                return 30;
            }, out _, out _);

            Assert.Equal(30 - 24, map.PondDepthAt(10, 10));
            Assert.Equal(0, map.PondDepthAt(3, 3));
        }

        [Fact]
        public void FlatGroundStillDrainsAndDrainsTheSameWayTwice()
        {
            System.Func<int, int, int> flat = (x, z) => x == 0 || z == 0 ? Sea : 25;
            DrainageMap a = Drain(30, 30, flat, out _, out _);
            DrainageMap b = Drain(30, 30, flat, out _, out _);
            Assert.Equal(a.Digest(), b.Digest());
            // Every interior column finds a way off; the rim of the world is
            // itself an outlet, like the sea.
            for (int z = 1; z < 29; z++)
                for (int x = 1; x < 29; x++)
                    Assert.True(a.DownstreamOf(x, z) >= 0);
        }

        // ── on a real island ───────────────────────────────────────────────

        static IslandMap Island(ulong seed, out ChunkStore store, out VoxelTypes types)
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Assets", "Content"))) dir = dir.Parent;
            ContentDatabase content = ContentLoader.Load(new DirectoryContentSource(Path.Combine(dir.FullName, "Assets", "Content"))).Database;
            store = new ChunkStore();
            types = VoxelTypes.FromContent(content);
            return IslandGenerator.Generate(store, new StreamRegistry(seed), BiomeTable.FromContent(content), types);
        }

        /// <summary>
        /// S0B's tell, in numbers: there are rivers and lakes, and the ground
        /// beside the water is the low ground — the land that floods first
        /// stands near the water, and the far land stands high above it.
        /// </summary>
        [Fact]
        public void TheLandBesideTheWaterIsTheLandThatFloods()
        {
            IslandMap map = Island(7, out ChunkStore store, out VoxelTypes types);
            ushort water = types.IdOf(Symbol.For("voxel.water"));

            int rivers = 0, lakes = 0;
            long besideSum = 0, besideCount = 0, landSum = 0, landCount = 0;
            for (int z = 1; z < ChunkStore.SizeZ - 1; z++)
                for (int x = 1; x < ChunkStore.SizeX - 1; x++)
                {
                    if (!map.IsLand(x, z)) continue;
                    if (map.IsRiver(x, z))
                    {
                        rivers++;
                        Assert.Equal(water, store.Get(x, map.HeightAt(x, z), z));
                        Assert.Equal(0, map.HeightAboveWaterAt(x, z));
                        continue;
                    }
                    if (map.IsLake(x, z)) { lakes++; continue; }

                    int above = map.HeightAboveWaterAt(x, z);
                    landSum += above; landCount++;
                    if (map.IsRiver(x + 1, z) || map.IsRiver(x - 1, z) || map.IsRiver(x, z + 1) || map.IsRiver(x, z - 1))
                    { besideSum += above; besideCount++; }
                }

            Assert.True(rivers > 100, rivers + " river columns");
            Assert.True(lakes > 0, "no lakes");
            double beside = (double)besideSum / besideCount, overall = (double)landSum / landCount;
            Assert.True(beside * 3 < overall, "beside a river " + beside + " voxels up, the island on average " + overall);
        }

        [Fact]
        public void HydrologyIsPartOfTheSeed()
        {
            IslandMap a = Island(11, out ChunkStore sa, out _);
            IslandMap b = Island(11, out ChunkStore sb, out _);
            Assert.Equal(a.Digest(), b.Digest());
            Assert.Equal(sa.Digest(), sb.Digest());
        }
    }
}
