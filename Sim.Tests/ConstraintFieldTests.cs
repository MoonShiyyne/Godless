using System.IO;
using Godless.Sim.Content;
using Godless.Sim.Core;
using Godless.Sim.Voxels;
using Godless.Sim.World;
using Xunit;

namespace Godless.Sim.Tests
{
    public class ConstraintFieldTests
    {
        sealed class Isle
        {
            public IslandMap Map;
            public ParcelGrid Grid;
            public BiomeTable Biomes;
            public ConstraintFields Fields;
        }

        static Isle Make(ulong seed)
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Assets", "Content"))) dir = dir.Parent;
            ContentDatabase content = ContentLoader.Load(new DirectoryContentSource(Path.Combine(dir.FullName, "Assets", "Content"))).Database;

            var isle = new Isle { Biomes = BiomeTable.FromContent(content) };
            VoxelTypes types = VoxelTypes.FromContent(content);
            var store = new ChunkStore();
            isle.Map = TestIslands.Generate(store, new StreamRegistry(seed), isle.Biomes, types);

            bool[] solid = TerrainBrush.SolidTable(content, types);
            var wet = new bool[types.Count];
            wet[types.IdOf(Symbol.For("voxel.water"))] = true;
            isle.Grid = ParcelGrid.Build(store, solid, wet);
            isle.Fields = ConstraintFields.Compute(isle.Map, isle.Grid, isle.Biomes);
            return isle;
        }

        static readonly Isle Seven = Make(7);

        static double MeanOverBiome(Isle isle, InfluenceMap field, string biome)
        {
            int want = isle.Biomes.IndexOf(Symbol.For("biome." + biome));
            double sum = 0.0;
            int n = 0;
            for (int pz = 0; pz < ParcelGrid.Depth; pz++)
                for (int px = 0; px < ParcelGrid.Width; px++)
                {
                    if (!isle.Grid.IsLand(px, pz)) continue;
                    if (isle.Map.BiomeAt(px * 4 + 2, pz * 4 + 2) != want) continue;
                    sum += field[px, pz];
                    n++;
                }
            return n == 0 ? 0.0 : sum / n;
        }

        [Fact]
        public void EveryFieldRunsZeroToOne()
        {
            foreach (InfluenceMap f in new[] { Seven.Fields.Sun, Seven.Fields.SnowLoad, Seven.Fields.Damp, Seven.Fields.Exposure, Seven.Fields.FloodRisk })
            {
                Assert.InRange(f.Min(), 0.0, 1.0);
                Assert.InRange(f.Max(), 0.0, 1.0);
            }
        }

        /// <summary>
        /// The fields say what the island is like: winter bites in the
        /// highlands, the delta is damp, and the low ground floods.
        /// </summary>
        [Fact]
        public void TheFieldsReadTheIslandTheWayTheBiomesDo()
        {
            Assert.True(MeanOverBiome(Seven, Seven.Fields.SnowLoad, "highland")
                      > MeanOverBiome(Seven, Seven.Fields.SnowLoad, "temperate"), "snow is a highland problem");
            Assert.True(MeanOverBiome(Seven, Seven.Fields.SnowLoad, "temperate")
                      > MeanOverBiome(Seven, Seven.Fields.SnowLoad, "shore"), "and worse than at the shore");
            Assert.True(MeanOverBiome(Seven, Seven.Fields.Damp, "flood-plain")
                      > MeanOverBiome(Seven, Seven.Fields.Damp, "highland"), "the delta is the damp one");
            Assert.True(MeanOverBiome(Seven, Seven.Fields.FloodRisk, "shore")
                      > MeanOverBiome(Seven, Seven.Fields.FloodRisk, "highland"), "the shore is what floods");
        }

        [Fact]
        public void SunFallsOnTheSlopesThatTurnTowardIt()
        {
            double toward = 0.0, away = 0.0;
            int nToward = 0, nAway = 0;
            for (int pz = 1; pz < ParcelGrid.Depth - 1; pz++)
                for (int px = 0; px < ParcelGrid.Width; px++)
                {
                    if (!Seven.Grid.IsLand(px, pz)) continue;
                    double fall = Seven.Grid.Height[px, pz + 1] - Seven.Grid.Height[px, pz - 1];
                    if (fall < -2.0) { toward += Seven.Fields.Sun[px, pz]; nToward++; }
                    else if (fall > 2.0) { away += Seven.Fields.Sun[px, pz]; nAway++; }
                }

            Assert.True(nToward > 50 && nAway > 50, "the island has slopes both ways");
            Assert.True(toward / nToward > 0.6, "sunward slopes: " + toward / nToward);
            Assert.True(away / nAway < 0.4, "shaded slopes: " + away / nAway);
        }

        [Fact]
        public void ThePeakIsExposedAndTheHollowsAreNot()
        {
            int hx = 0, hz = 0;
            double highest = double.MinValue;
            for (int pz = 0; pz < ParcelGrid.Depth; pz++)
                for (int px = 0; px < ParcelGrid.Width; px++)
                    if (Seven.Grid.IsLand(px, pz) && Seven.Grid.Height[px, pz] > highest)
                    { highest = Seven.Grid.Height[px, pz]; hx = px; hz = pz; }

            Assert.True(Seven.Fields.Exposure[hx, hz] > 0.7, "the summit stands above what is around it");
            Assert.True(Seven.Fields.Exposure.Min() < 0.3, "somewhere on the island is sheltered");
        }

        [Fact]
        public void WaterIsWhereTheFloodRiskIs()
        {
            double besideWater = 0.0, inland = 0.0;
            int nBeside = 0, nInland = 0;
            for (int pz = 0; pz < ParcelGrid.Depth; pz++)
                for (int px = 0; px < ParcelGrid.Width; px++)
                {
                    if (!Seven.Grid.IsLand(px, pz)) continue;
                    double distance = Seven.Grid.WaterDistance[px, pz];
                    if (distance <= 1.0) { besideWater += Seven.Fields.FloodRisk[px, pz]; nBeside++; }
                    else if (distance > 5.0) { inland += Seven.Fields.FloodRisk[px, pz]; nInland++; }
                }

            Assert.True(nBeside > 20, nBeside + " parcels beside water");
            Assert.True(nInland > 20, nInland + " parcels well inland");
            Assert.True(besideWater / nBeside > 0.6, "beside the water: " + besideWater / nBeside);
            Assert.True(inland / nInland < 0.4, "well inland: " + inland / nInland);
        }

        [Fact]
        public void TheSameIslandGivesTheSameFields()
        {
            Assert.Equal(Seven.Fields.Digest(), Make(7).Fields.Digest());
            Assert.NotEqual(Seven.Fields.Digest(), Make(8).Fields.Digest());
        }
    }
}
