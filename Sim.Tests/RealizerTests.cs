using System.Collections.Generic;
using System.IO;
using Godless.Sim.Annals;
using Godless.Sim.Build;
using Godless.Sim.Content;
using Godless.Sim.Core;
using Godless.Sim.Culture;
using Godless.Sim.Economy;
using Godless.Sim.Voxels;
using Godless.Sim.World;
using Xunit;

namespace Godless.Sim.Tests
{
    public class RealizerTests
    {
        static ContentDatabase Load()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Assets", "Content"))) dir = dir.Parent;
            return ContentLoader.Load(new DirectoryContentSource(Path.Combine(dir.FullName, "Assets", "Content"))).Database;
        }

        static readonly ContentDatabase Content = Load();
        static readonly GeneTable Genes = GeneTable.FromContent(Content);
        static readonly Palette Pal = Palette.FromContent(Content);
        static readonly MaterialTable Materials = MaterialTable.FromContent(Content, BiomeTable.FromContent(Content));
        static readonly TileSet Tiles = TileSet.FromContent(Content, Materials);
        static readonly VoxelTypes Types = VoxelTypes.FromContent(Content);

        static Blueprint House(params (string gene, double value)[] set)
        {
            var genome = new Genome(Genes);
            foreach (var (gene, value) in set) genome.Mutate(Symbol.For("gene." + gene), value, 0, Symbol.None, RecordId.None, new Annalist());
            Grammar g = GrammarTable.FromContent(Content, Genes).For("shelter");
            return g.Build(genome, Pal, 60, 60, 650);
        }

        static MaterialStock Holding(params string[] materials)
        {
            var stock = new MaterialStock(Materials);
            foreach (string m in materials) stock.Add(Materials.IndexOf(m), 100000);
            return stock;
        }

        static Structure Build(Blueprint plan, MaterialStock stock, ulong seed = 1)
        {
            return Realizer.Realize(plan, Tiles, Materials, stock, Pal, Types, new RngStream(seed));
        }

        static Symbol R(string role) { return Symbol.For("role." + role); }

        /// <summary>
        /// S19's tell: the same grammar and the same genome, against two
        /// stocks, is two visibly different buildings — and each one keeps the
        /// silhouette rules.
        /// </summary>
        [Fact]
        public void SameHouseDifferentStockDifferentBuilding()
        {
            Blueprint plan = House(("roof_pitch", 0.8));
            Structure woods = Build(plan, Holding("oak", "granite", "thatch"));
            Structure uplands = Build(plan, Holding("slate", "granite"));

            Assert.NotEqual(woods.MaterialFor(R("roof")), uplands.MaterialFor(R("roof")));
            Assert.NotEqual(woods.Digest(), uplands.Digest());

            int differing = 0, solid = 0;
            for (int y = 0; y < plan.Height; y++)
                for (int z = 0; z < plan.Depth; z++)
                    for (int x = 0; x < plan.Width; x++)
                    {
                        ushort a = woods.At(x, y, z), b = uplands.At(x, y, z);
                        if (a == VoxelTypes.AirId && b == VoxelTypes.AirId) continue;
                        solid++;
                        if (a != b) differing++;
                    }
            Assert.True(differing > solid / 2, differing + " of " + solid + " cells differ");

            foreach (Structure s in new[] { woods, uplands })
            {
                Assert.Empty(s.Missing);
                Assert.True(s.MaterialCount <= Tiles.MaxMaterials, s.MaterialCount + " materials");
                int wall = s.MaterialFor(R("wall")), roof = s.MaterialFor(R("roof"));
                Assert.True(TileSet.Contrast(Pal, Materials, wall, roof) >= Tiles.MinRoofWallContrast,
                            "roof " + Materials[roof].Name + " on walls of " + Materials[wall].Name);
            }
        }

        [Fact]
        public void NothingRestsOnThatch()
        {
            Blueprint plan = House(("verticality", 1.0), ("roof_pitch", 0.6));
            Structure s = Build(plan, Holding("oak", "granite", "thatch"));

            for (int y = 0; y < plan.Height - 1; y++)
                for (int z = 0; z < plan.Depth; z++)
                    for (int x = 0; x < plan.Width; x++)
                    {
                        ushort id = s.At(x, y, z);
                        if (id == VoxelTypes.AirId) continue;
                        int m = Materials.IndexOf(Types.SymbolOf(id));
                        if (m < 0 || Tiles.Carries(Materials[m].Class)) continue;
                        Assert.Equal(VoxelTypes.AirId, s.At(x, y + 1, z));
                    }
        }

        /// <summary>
        /// Earth walls get a course of something harder where they meet the
        /// ground. The case is a settlement with mud to spare and only a
        /// little stone: too little to wall with, enough to stand on.
        /// </summary>
        [Fact]
        public void MudWallsStandOnStone()
        {
            Blueprint plan = House(("elevation_bias", 0.0));
            var stock = new MaterialStock(Materials);
            stock.Add(Materials.IndexOf("mud"), 100000);
            stock.Add(Materials.IndexOf("reed"), 100000);
            stock.Add(Materials.IndexOf("granite"), 40);
            Structure s = Build(plan, stock);
            int wall = s.MaterialFor(R("wall"));
            Assert.Equal("mud", Materials[wall].Name);

            // Whatever the earth building rests on, it is not earth: the
            // lowest course of every column is the harder material.
            ushort mud = Types.IdOf(Materials[Materials.IndexOf("mud")].Voxel);
            int columns = 0;
            for (int z = 0; z < plan.Depth; z++)
                for (int x = 0; x < plan.Width; x++)
                {
                    int bottom = -1;
                    for (int y = 0; y < plan.Height && bottom < 0; y++) if (s.At(x, y, z) != VoxelTypes.AirId) bottom = y;
                    if (bottom < 0) continue;
                    columns++;
                    Assert.NotEqual(mud, s.At(x, bottom, z));
                }
            Assert.True(columns > 0, "nothing met the ground");
        }

        [Fact]
        public void WithAnEmptyStockItIsStillPlannedAndSaysWhatIsMissing()
        {
            Blueprint plan = House();
            Structure s = Build(plan, new MaterialStock(Materials));
            Assert.NotEmpty(s.Compromises);
            Assert.True(s.TotalVoxels > 0, "a plan is still a plan");
        }

        [Fact]
        public void ItCostsWhatItIsMadeOf()
        {
            Blueprint plan = House();
            Structure s = Build(plan, Holding("oak", "granite", "thatch"));
            int roles = 0;
            foreach (Symbol role in plan.Roles)
            {
                RoleTile tile = Tiles.Find(role);
                if (role.IsNone || tile == null || tile.Open) continue;
                roles += plan.Count(role);
            }
            Assert.Equal(roles, s.TotalVoxels);
        }

        [Fact]
        public void TheSameStockAndSeedBuildTheSameHouse()
        {
            Blueprint plan = House();
            Assert.Equal(Build(plan, Holding("oak", "granite", "thatch")).Digest(),
                         Build(plan, Holding("oak", "granite", "thatch")).Digest());
            Assert.NotEqual(Build(plan, Holding("oak", "granite", "thatch"), 1).Digest(),
                            Build(plan, Holding("oak", "granite", "thatch"), 9).Digest());
        }
    }
}
