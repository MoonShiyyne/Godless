using System.IO;
using Godless.Sim.Annals;
using Godless.Sim.Content;
using Godless.Sim.Core;
using Godless.Sim.Economy;
using Godless.Sim.Voxels;
using Godless.Sim.World;
using Xunit;

namespace Godless.Sim.Tests
{
    public class StockTests
    {
        static ContentDatabase Shipped()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Assets", "Content"))) dir = dir.Parent;
            return ContentLoader.Load(new DirectoryContentSource(Path.Combine(dir.FullName, "Assets", "Content"))).Database;
        }

        static ContentDatabase With(params string[] docs)
        {
            var src = new MemoryContentSource().Add("base", "mod.json", "{}");
            for (int i = 0; i < docs.Length; i++) src.Add("base", "d" + i + ".json", docs[i]);
            return ContentLoader.Load(src).Database;
        }

        [Fact]
        public void EveryMaterialABiomeOffersCanBeGatheredAndThePaletteStillHolds()
        {
            ContentDatabase content = Shipped();
            MaterialTable materials = MaterialTable.FromContent(content, BiomeTable.FromContent(content));
            Assert.Empty(materials.Problems);
            // Every gatherable voxel some biome offers, and nothing else:
            // soil and water are gatherable but no biome builds with them.
            Assert.True(materials.Count >= 7);
            Assert.True(materials.IndexOf("soil") < 0 && materials.IndexOf("water") < 0);
            Assert.Empty(Palette.FromContent(content).Violations());
        }

        [Fact]
        public void ABiomePromisingAMaterialNobodyCanGatherIsNamed()
        {
            ContentDatabase content = With(
                "{\"type\":\"biome\",\"id\":\"marsh\",\"materials\":[\"reed\",\"peat\"]}",
                "{\"type\":\"voxel\",\"id\":\"reed\",\"gather\":{\"perLabourTick\":0.5}}",
                "{\"type\":\"voxel\",\"id\":\"clay\",\"gather\":{\"perLabourTick\":0}}");
            MaterialTable materials = MaterialTable.FromContent(content, BiomeTable.FromContent(content));
            Assert.Equal(1, materials.Count);
            Assert.Contains(materials.Problems, p => p.Contains("'marsh'") && p.Contains("'peat'"));
            Assert.Contains(materials.Problems, p => p.Contains("'clay'"));
        }

        sealed class Isle
        {
            public ContentDatabase Content;
            public BiomeTable Biomes;
            public MaterialTable Materials;
            public IslandMap Map;
        }

        static Isle MakeIsle(ulong seed)
        {
            var isle = new Isle { Content = Shipped() };
            isle.Biomes = BiomeTable.FromContent(isle.Content);
            isle.Materials = MaterialTable.FromContent(isle.Content, isle.Biomes);
            isle.Map = IslandGenerator.Generate(new ChunkStore(), new StreamRegistry(seed), isle.Biomes,
                                                VoxelTypes.FromContent(isle.Content));
            return isle;
        }

        /// <summary>The land column deepest inside a biome: the most of its own kind within 32.</summary>
        static void HeartOf(Isle isle, string biome, out int bx, out int bz)
        {
            int want = isle.Biomes.IndexOf(Symbol.For("biome." + biome));
            int best = -1; bx = bz = -1;
            for (int z = 32; z < ChunkStore.SizeZ - 32; z += 8)
                for (int x = 32; x < ChunkStore.SizeX - 32; x += 8)
                {
                    if (!isle.Map.IsLand(x, z) || isle.Map.BiomeAt(x, z) != want) continue;
                    int n = 0;
                    for (int dz = -32; dz <= 32; dz += 4)
                        for (int dx = -32; dx <= 32; dx += 4)
                            if (isle.Map.IsLand(x + dx, z + dz) && isle.Map.BiomeAt(x + dx, z + dz) == want) n++;
                    if (n > best) { best = n; bx = x; bz = z; }
                }
        }

        /// <summary>
        /// S11's precondition for its tell: the same people, founding in two
        /// places on one island, have two different stocks to build from —
        /// because what they can gather is what the land in reach offers.
        /// </summary>
        [Fact]
        public void TwoHearthsOnOneIslandHaveDifferentLandToBuildFrom()
        {
            Isle isle = MakeIsle(7);
            HeartOf(isle, "temperate", out int tx, out int tz);
            HeartOf(isle, "highland", out int hx, out int hz);
            Catchment lowland = Catchment.Survey(isle.Map, isle.Biomes, isle.Materials, tx, tz);
            Catchment upland = Catchment.Survey(isle.Map, isle.Biomes, isle.Materials, hx, hz);

            int oak = isle.Materials.IndexOf("oak"), slate = isle.Materials.IndexOf("slate");
            Assert.True(lowland.Sources(oak) > upland.Sources(oak), "timber is a lowland material");
            Assert.True(upland.Sources(slate) > lowland.Sources(slate), "slate is a highland one");
            Assert.True(lowland.YieldPerLabourTick(oak) > upland.YieldPerLabourTick(oak));

            // Nothing in reach is nothing to gather.
            int reed = isle.Materials.IndexOf("reed");
            if (!upland.Offers(reed)) Assert.Equal(0.0, upland.YieldPerLabourTick(reed));
        }

        [Fact]
        public void GatheringIsLabourTimesWhatTheLandOffersAndFractionsCarry()
        {
            Isle isle = MakeIsle(7);
            HeartOf(isle, "temperate", out int tx, out int tz);
            Catchment c = Catchment.Survey(isle.Map, isle.Biomes, isle.Materials, tx, tz);
            int oak = isle.Materials.IndexOf("oak");
            var stock = new MaterialStock(isle.Materials);

            double rate = c.YieldPerLabourTick(oak);
            Assert.True(rate > 0.0 && rate <= isle.Materials[oak].PerLabourTick);

            long total = 0;
            for (int t = 0; t < 100; t++) total += stock.Gather(oak, 1.0, c);
            Assert.Equal(total, stock.Of(oak));
            Assert.InRange(total, (long)(100 * rate) - 1, (long)(100 * rate));
        }

        /// <summary>
        /// Taking is all-or-nothing, and running short is on record exactly
        /// once per run of shortages, caused by whatever wanted the material.
        /// </summary>
        [Fact]
        public void RunningShortIsRecordedOnceWithWhatWantedIt()
        {
            ContentDatabase content = Shipped();
            MaterialTable materials = MaterialTable.FromContent(content, BiomeTable.FromContent(content));
            var stock = new MaterialStock(materials);
            var annals = new Annalist();
            Symbol town = Symbol.For("settlement.test");
            var place = new Int3(10, 50, 10);
            RecordId wanted = annals.Write(1, Symbol.For("intent.raised"), town, place, RecordId.None);
            int oak = materials.IndexOf("oak");

            stock.Add(oak, 30);
            Assert.True(stock.TryTake(oak, 20, 2, town, place, annals, wanted));
            Assert.False(stock.TryTake(oak, 20, 3, town, place, annals, wanted));
            Assert.Equal(10, stock.Of(oak));   // nothing taken in part
            Assert.False(stock.TryTake(oak, 20, 4, town, place, annals, wanted));

            var shortages = annals.OfKind(MaterialStock.ShortKind);
            AnnalRecord shortage = Assert.Single(shortages);
            Assert.Equal(wanted, shortage.Cause);
            Assert.Equal(20, shortage.ValueA);
            Assert.Equal(10, shortage.ValueB);
            Assert.Equal(materials[oak].Voxel, shortage.Participants[0]);
            Assert.True(stock.IsShort(oak));

            // A successful take ends the run; the next shortage is news again.
            Assert.True(stock.TryTake(oak, 5, 5, town, place, annals, wanted));
            Assert.False(stock.IsShort(oak));
            Assert.False(stock.TryTake(oak, 50, 6, town, place, annals, wanted));
            Assert.Equal(2, annals.OfKind(MaterialStock.ShortKind).Count);
        }

        [Fact]
        public void WithoutBaseContentThereIsNothingToGather()
        {
            var content = new ContentDatabase();
            MaterialTable materials = MaterialTable.FromContent(content, BiomeTable.FromContent(content));
            Assert.Equal(0, materials.Count);
            Assert.Empty(materials.Problems);
        }
    }
}
