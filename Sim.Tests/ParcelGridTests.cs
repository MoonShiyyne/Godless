using System.Diagnostics;
using System.IO;
using Godless.Sim.Annals;
using Godless.Sim.Content;
using Godless.Sim.Core;
using Godless.Sim.Deltas;
using Godless.Sim.Voxels;
using Godless.Sim.World;
using Xunit;

namespace Godless.Sim.Tests
{
    public class ParcelGridTests
    {
        readonly Xunit.Abstractions.ITestOutputHelper _out;
        public ParcelGridTests(Xunit.Abstractions.ITestOutputHelper output) { _out = output; }

        const ushort Stone = 1, Water = 2;
        static readonly bool[] Solid = { false, true, false };
        static readonly bool[] Wet = { false, false, true };

        static int NaiveTop(ChunkStore s, int x, int z, bool[] matches)
        {
            for (int y = ChunkStore.SizeY - 1; y >= 0; y--) { ushort t = s.Get(x, y, z); if (t < matches.Length && matches[t]) return y; }
            return -1;
        }

        [Fact]
        public void TheGridIsFourColumnsAParcel()
        {
            Assert.Equal(128, ParcelGrid.Width);
            Assert.Equal(128, ParcelGrid.Depth);
            Assert.Equal(512, ParcelGrid.Width * ParcelGrid.Size);
        }

        /// <summary>
        /// The chunk-skipping column scan must agree with a naive one — across
        /// missing chunks, uniform chunks of each kind, and mixed ones.
        /// </summary>
        [Fact]
        public void TopMatchingAgreesWithANaiveScanEverywhere()
        {
            var s = new ChunkStore();
            var rng = new RngStream(StableHash.OfString("test.tops"));
            for (int x = 0; x < 64; x++)
                for (int z = 0; z < 64; z++)
                {
                    int h = rng.NextInt(150);
                    for (int y = 0; y <= h; y++) s.SetRaw(x, y, z, Stone);
                    if (rng.NextInt(4) == 0) s.SetRaw(x, h + 1, z, Water);
                }
            // A fully solid chunk, so the uniform path is exercised too.
            for (int x = 96; x < 128; x++) for (int z = 96; z < 128; z++) for (int y = 0; y < 64; y++) s.SetRaw(x, y, z, Stone);

            for (int x = 0; x < 130; x += 3)
                for (int z = 0; z < 130; z += 3)
                {
                    Assert.Equal(NaiveTop(s, x, z, Solid), s.TopMatching(x, z, Solid));
                    Assert.Equal(NaiveTop(s, x, z, Wet), s.TopMatching(x, z, Wet));
                }
            Assert.Equal(-1, s.TopMatching(400, 400, Solid));   // nothing there
            Assert.Equal(63, s.TopMatching(100, 100, Solid));   // uniform chunk answered whole
        }

        static ChunkStore Terrain(out int seaLevel)
        {
            // A sloping plain rising east, with a sea along the west edge.
            seaLevel = 20;
            var s = new ChunkStore();
            for (int x = 0; x < 200; x++)
                for (int z = 0; z < 200; z++)
                {
                    int h = 10 + x / 5;
                    for (int y = 0; y <= h; y++) s.SetRaw(x, y, z, Stone);
                    for (int y = h + 1; y <= seaLevel; y++) s.SetRaw(x, y, z, Water);
                }
            return s;
        }

        [Fact]
        public void ParcelStatisticsMatchTheColumnsTheyCover()
        {
            ChunkStore s = Terrain(out _);
            ParcelGrid g = ParcelGrid.Build(s, Solid, Wet);

            for (int px = 0; px < 50; px += 7)
                for (int pz = 0; pz < 50; pz += 9)
                {
                    int lo = int.MaxValue, hi = int.MinValue, wet = 0, sum = 0;
                    for (int dx = 0; dx < 4; dx++)
                        for (int dz = 0; dz < 4; dz++)
                        {
                            int x = px * 4 + dx, z = pz * 4 + dz;
                            int top = NaiveTop(s, x, z, Solid);
                            lo = System.Math.Min(lo, top); hi = System.Math.Max(hi, top); sum += top;
                            if (s.Get(x, top + 1, z) == Water) wet++;
                        }
                    Assert.Equal(lo, g.MinGround(px, pz));
                    Assert.Equal(hi, g.MaxGround(px, pz));
                    Assert.Equal(wet, g.WetColumns(px, pz));
                    Assert.Equal(sum / 16.0, g.Height[px, pz]);
                    Assert.Equal(hi - lo, g.Slope[px, pz]);
                }
        }

        /// <summary>
        /// On open ground a 3-4 chamfer transform is exactly the octile
        /// distance, so brute force — the nearest water parcel by octile
        /// distance — is the oracle.
        /// </summary>
        [Fact]
        public void WaterDistanceIsTheOctileDistanceToTheNearestWater()
        {
            var s = new ChunkStore();
            for (int x = 0; x < 512; x += 1)
                for (int z = 0; z < 512; z += 1)
                    s.SetRaw(x, 0, z, Stone);
            // Three ponds.
            int[,] ponds = { { 10, 12 }, { 90, 40 }, { 60, 110 } };
            for (int i = 0; i < 3; i++) s.SetRaw(ponds[i, 0] * 4 + 1, 1, ponds[i, 1] * 4 + 2, Water);

            ParcelGrid g = ParcelGrid.Build(s, Solid, Wet);
            for (int px = 0; px < 128; px += 5)
                for (int pz = 0; pz < 128; pz += 5)
                {
                    double best = double.MaxValue;
                    for (int i = 0; i < 3; i++)
                    {
                        int dx = System.Math.Abs(px - ponds[i, 0]), dz = System.Math.Abs(pz - ponds[i, 1]);
                        double octile = (3.0 * System.Math.Max(dx, dz) + (4.0 - 3.0) * System.Math.Min(dx, dz)) / 3.0;
                        if (octile < best) best = octile;
                    }
                    Assert.Equal(best, g.WaterDistance[px, pz], 10);
                }
        }

        [Fact]
        public void LandAndWaterAreTellApart()
        {
            ChunkStore s = Terrain(out _);
            ParcelGrid g = ParcelGrid.Build(s, Solid, Wet);

            // Ground rises past sea level at x = 50, parcel 12.
            Assert.False(g.IsLand(2, 10));
            Assert.Equal(0.0, g.WaterDistance[2, 10]);
            Assert.True(g.IsLand(30, 10));
            Assert.True(g.WaterDistance[30, 10] > g.WaterDistance[16, 10], "inland is farther from water");
        }

        /// <summary>
        /// The grid follows the god's edits: refreshing only the footprint of a
        /// brush stroke gives the same grid as rebuilding the whole island.
        /// </summary>
        [Fact]
        public void RefreshingAnEditsFootprintEqualsAFullRebuild()
        {
            ChunkStore s = Terrain(out int sea);
            var world = new VoxelWorld(s, new DeltaLog());
            world.EndTick(0);
            ParcelGrid g = ParcelGrid.Build(s, Solid, Wet);

            TerrainBrush.Raise(world, Solid, 100, 100, 8, 12, Stone, 1, RecordId.None, null);
            TerrainBrush.Lower(world, Solid, 60, 140, 6, 9, Water, sea, 2, RecordId.None, null);
            g.Refresh(s, 100 - 8, 100 - 8, 100 + 8, 100 + 8);
            g.Refresh(s, 60 - 6, 140 - 6, 60 + 6, 140 + 6);

            Assert.Equal(ParcelGrid.Build(s, Solid, Wet).Digest(), g.Digest());
        }

        [Fact]
        public void TheSameWorldGivesTheSameGrid()
        {
            ChunkStore s = Terrain(out _);
            Assert.Equal(ParcelGrid.Build(s, Solid, Wet).Digest(), ParcelGrid.Build(s, Solid, Wet).Digest());
        }

        [Fact]
        public void AddingWeightedFieldsIsHowScoresCombine()
        {
            var a = new InfluenceMap(Symbol.For("field.a"));
            var b = new InfluenceMap(Symbol.For("field.b"));
            a.Fill(2.0); b.Fill(3.0);
            a.Add(b, -0.5);
            Assert.Equal(0.5, a[7, 7]);
            Assert.Equal(0.0, a[-1, 3]);   // out of bounds reads as nothing
        }

        /// <summary>A real island: the fields must say something sensible, and cheaply.</summary>
        [Fact]
        public void OnARealIslandTheFieldsReadTheLand()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Assets", "Content"))) dir = dir.Parent;
            ContentDatabase content = ContentLoader.Load(new DirectoryContentSource(Path.Combine(dir.FullName, "Assets", "Content"))).Database;

            VoxelTypes types = VoxelTypes.FromContent(content);
            var store = new ChunkStore();
            IslandMap island = IslandGenerator.Generate(store, new StreamRegistry(7), BiomeTable.FromContent(content), types);
            bool[] solid = TerrainBrush.SolidTable(content, types);
            var wet = new bool[types.Count];
            wet[types.IdOf(Symbol.For("voxel.water"))] = true;

            var watch = Stopwatch.StartNew();
            ParcelGrid g = ParcelGrid.Build(store, solid, wet);
            watch.Stop();

            int land = 0; double farthest = 0; double steepest = 0;
            for (int px = 0; px < 128; px++)
                for (int pz = 0; pz < 128; pz++)
                    if (g.IsLand(px, pz))
                    {
                        land++;
                        farthest = System.Math.Max(farthest, g.WaterDistance[px, pz]);
                        steepest = System.Math.Max(steepest, g.Slope[px, pz]);
                    }

            _out.WriteLine("built in " + watch.Elapsed.TotalMilliseconds.ToString("0") + " ms; " + land
                           + " land parcels; farthest from water " + farthest.ToString("0.0")
                           + " parcels; steepest parcel rises " + steepest + " voxels");

            // The generator's own land count, at parcel resolution.
            int islandLand = 0;
            for (int x = 0; x < 512; x++) for (int z = 0; z < 512; z++) if (island.IsLand(x, z)) islandLand++;
            Assert.InRange(land, islandLand / 16 * 0.85, islandLand / 16 * 1.15);

            Assert.True(farthest > 3, "an island has an interior");
            Assert.True(steepest > 4, "an island has slopes");
            Assert.True(watch.Elapsed.TotalMilliseconds < 2000, "a whole-island build must stay cheap");
        }
    }
}
