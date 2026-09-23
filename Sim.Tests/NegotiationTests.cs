using System.Collections.Generic;
using System.IO;
using Godless.Sim.Annals;
using Godless.Sim.Build;
using Godless.Sim.Content;
using Godless.Sim.Core;
using Godless.Sim.Culture;
using Godless.Sim.Harness;
using Godless.Sim.Economy;
using Godless.Sim.Voxels;
using Godless.Sim.World;
using Xunit;

namespace Godless.Sim.Tests
{
    public class NegotiationTests
    {
        static ContentDatabase Load()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Assets", "Content"))) dir = dir.Parent;
            return ContentLoader.Load(new DirectoryContentSource(Path.Combine(dir.FullName, "Assets", "Content"))).Database;
        }

        static readonly ContentDatabase Content = Load();
        static readonly GeneTable Genes = GeneTable.FromContent(Content);

        static ContentDatabase With(string doc)
        {
            var src = new MemoryContentSource().Add("base", "mod.json", "{}").Add("base", "n.json", doc);
            return ContentLoader.Load(src).Database;
        }

        [Fact]
        public void TheShippedStrategiesLoad()
        {
            NegotiationTable table = NegotiationTable.FromContent(Content, Genes);
            Assert.Empty(table.Problems);
            Assert.Equal(3, table.Count);
            foreach (GroundRule rule in table.All) Assert.False(string.IsNullOrWhiteSpace(rule.Tell), rule.Name);
        }

        [Theory]
        [InlineData("{\"type\":\"negotiation\",\"id\":\"ground\",\"strategies\":{\"burrow\":{\"tell\":\"x\",\"score\":\"1\"}}}", "'burrow'")]
        [InlineData("{\"type\":\"negotiation\",\"id\":\"ground\",\"strategies\":{\"terrace\":{\"score\":\"1\"}}}", "no tell")]
        [InlineData("{\"type\":\"negotiation\",\"id\":\"ground\",\"strategies\":{\"terrace\":{\"tell\":\"x\",\"score\":\"1 +\"}}}", "broken score")]
        [InlineData("{\"type\":\"negotiation\",\"id\":\"ground\",\"strategies\":{\"stilt\":{\"tell\":\"x\",\"score\":\"site.mood\"}}}", "'site.mood'")]
        public void NonsenseIsRefused(string doc, string expected)
        {
            NegotiationTable table = NegotiationTable.FromContent(With(doc), Genes);
            Assert.Equal(0, table.Count);
            Assert.Contains(expected, Assert.Single(table.Problems));
        }

        sealed class Isle
        {
            public IslandMap Map;
            public ParcelGrid Grid;
            public ConstraintFields Fields;
            public NegotiationTable Rules = NegotiationTable.FromContent(Content, Genes);

            public Isle(ulong seed = 7)
            {
                BiomeTable biomes = BiomeTable.FromContent(Content);
                VoxelTypes types = VoxelTypes.FromContent(Content);
                var store = new ChunkStore();
                Map = TestIslands.Generate(store, new StreamRegistry(seed), biomes, types);

                bool[] solid = TerrainBrush.SolidTable(Content, types);
                var wet = new bool[types.Count];
                wet[types.IdOf(Symbol.For("voxel.water"))] = true;
                Grid = ParcelGrid.Build(store, solid, wet);
                Fields = ConstraintFields.Compute(Map, Grid, biomes);
            }

            public GroundPlan At(int px, int pz, Genome genome) { return Rules.Choose(px, pz, Grid, Fields, genome, 8, 6); }
        }

        static readonly Isle Island = new Isle();

        /// <summary>
        /// S16's tell: the ground decides how a building meets it. Flat dry
        /// land is levelled, a slope is stepped, and land that floods is built
        /// over on posts.
        /// </summary>
        [Fact]
        public void TheGroundDecidesHowABuildingMeetsIt()
        {
            var plain = new Genome(Genes);
            int flatX = -1, flatZ = -1, steepX = -1, steepZ = -1, wetX = -1, wetZ = -1;
            for (int pz = 2; pz < ParcelGrid.Depth - 2 && (flatX < 0 || steepX < 0 || wetX < 0); pz++)
                for (int px = 2; px < ParcelGrid.Width - 2; px++)
                {
                    if (!Island.Grid.IsLand(px, pz) || Island.Grid.WetColumns(px, pz) > 0) continue;
                    double slope = Island.Grid.Slope[px, pz], flood = Island.Fields.FloodRisk[px, pz];
                    if (flatX < 0 && slope == 0 && flood < 0.2) { flatX = px; flatZ = pz; }
                    if (steepX < 0 && slope >= 7 && flood < 0.3) { steepX = px; steepZ = pz; }
                    if (wetX < 0 && flood > 0.9 && slope <= 1) { wetX = px; wetZ = pz; }
                }

            Assert.True(flatX >= 0 && steepX >= 0 && wetX >= 0, "the island has flat, steep and flooding ground");
            Assert.Equal(GroundStrategy.CutAndFill, Island.At(flatX, flatZ, plain).Strategy);
            Assert.Equal(GroundStrategy.Terrace, Island.At(steepX, steepZ, plain).Strategy);
            Assert.Equal(GroundStrategy.Stilt, Island.At(wetX, wetZ, plain).Strategy);
        }

        /// <summary>And the culture has a say: one that builds high stilts where another would level.</summary>
        [Fact]
        public void ACultureThatBuildsHighPutsItOnPosts()
        {
            int flatX = -1, flatZ = -1;
            for (int pz = 2; pz < ParcelGrid.Depth - 2 && flatX < 0; pz++)
                for (int px = 2; px < ParcelGrid.Width - 2 && flatX < 0; px++)
                    if (Island.Grid.IsLand(px, pz) && Island.Grid.WetColumns(px, pz) == 0
                        && Island.Grid.Slope[px, pz] == 0 && Island.Fields.FloodRisk[px, pz] < 0.2) { flatX = px; flatZ = pz; }

            var high = new Genome(Genes);
            high.Mutate(Symbol.For("gene.elevation_bias"), 0.95, 0, Symbol.None, RecordId.None, new Annalist());
            Assert.Equal(GroundStrategy.CutAndFill, Island.At(flatX, flatZ, new Genome(Genes)).Strategy);
            Assert.Equal(GroundStrategy.Stilt, Island.At(flatX, flatZ, high).Strategy);
        }

        [Fact]
        public void TheSameGroundIsMetTheSameWay()
        {
            var plain = new Genome(Genes);
            GroundPlan a = Island.At(40, 40, plain), b = Island.At(40, 40, plain);
            Assert.Equal(a.Strategy, b.Strategy);
            Assert.Equal(a.Floor, b.Floor);
        }
    }
}
