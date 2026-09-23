using System.IO;
using Godless.Sim.Build;
using Godless.Sim.Content;
using Godless.Sim.Core;
using Godless.Sim.Culture;
using Godless.Sim.Economy;
using Godless.Sim.World;
using Xunit;

namespace Godless.Sim.Tests
{
    public class TileSetTests
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

        static MaterialTable Materials(ContentDatabase content)
        {
            return MaterialTable.FromContent(content, BiomeTable.FromContent(content));
        }

        /// <summary>
        /// The content-integrity check S1D exists for: the tileset can answer
        /// for every role the grammar writes, so no house is built with holes
        /// in it where a role had no materials.
        /// </summary>
        [Fact]
        public void TheShippedTilesetAnswersForEveryRoleTheGrammarWrites()
        {
            ContentDatabase content = Shipped();
            TileSet tiles = TileSet.FromContent(content, Materials(content));
            Assert.Empty(tiles.Problems);
            Assert.False(string.IsNullOrWhiteSpace(tiles.Tell));

            GrammarTable grammars = GrammarTable.FromContent(content, GeneTable.FromContent(content));
            foreach (Grammar g in grammars.All) Assert.Empty(tiles.Answers(g));
        }

        [Fact]
        public void NothingRestsOnThatch()
        {
            ContentDatabase content = Shipped();
            TileSet tiles = TileSet.FromContent(content, Materials(content));
            Assert.False(tiles.Carries(Symbol.For("class.thatch")));
            Assert.True(tiles.Carries(Symbol.For("class.stone")));
            Assert.True(tiles.Carries(Symbol.For("class.timber")));
            Assert.True(tiles.NeedsFooting(Symbol.For("class.earth")));
            Assert.False(tiles.NeedsFooting(Symbol.For("class.stone")));

            // A class nobody declared rules on carries, rather than vanishing.
            Assert.True(tiles.Carries(Symbol.For("class.bone")));
        }

        [Fact]
        public void TheSilhouetteRulesAreRead()
        {
            ContentDatabase content = Shipped();
            TileSet tiles = TileSet.FromContent(content, Materials(content));
            Assert.Equal(3, tiles.MaxMaterials);
            Assert.True(tiles.MinRoofWallContrast >= 8);
            Assert.True(tiles.BandTheBase);

            MaterialTable materials = Materials(content);
            Palette palette = Palette.FromContent(content);
            int oak = materials.IndexOf("oak"), thatch = materials.IndexOf("thatch");
            Assert.True(TileSet.Contrast(palette, materials, oak, thatch) >= tiles.MinRoofWallContrast);
        }

        [Fact]
        public void ATilesetThatCannotBuildSomethingSaysSo()
        {
            ContentDatabase content = With(
                "{\"type\":\"voxel\",\"id\":\"oak\",\"class\":\"timber\",\"value\":12,\"gather\":{\"perLabourTick\":1}}",
                "{\"type\":\"tileset\",\"id\":\"base\",\"roles\":{"
                + "\"wall\":{\"classes\":[\"timber\"]},"
                + "\"roof\":{\"classes\":[\"bronze\"]},"
                + "\"floor\":{\"classes\":[]}},"
                + "\"carries\":{\"glass\":false}}");
            TileSet tiles = TileSet.FromContent(content, Materials(content));

            Assert.Contains(tiles.Problems, p => p.Contains("'bronze'"));
            Assert.Contains(tiles.Problems, p => p.Contains("'floor'") && p.Contains("nothing that can build it"));
            Assert.Contains(tiles.Problems, p => p.Contains("'glass'"));
            Assert.Contains(tiles.Problems, p => p.Contains("no tell"));
        }

        [Fact]
        public void AGrammarRoleWithNoTilesIsNamed()
        {
            ContentDatabase content = Shipped();
            ContentDatabase thin = With(
                "{\"type\":\"voxel\",\"id\":\"granite\",\"class\":\"stone\",\"value\":34,\"gather\":{\"perLabourTick\":1}}",
                "{\"type\":\"tileset\",\"id\":\"base\",\"tell\":\"x\",\"roles\":{\"wall\":{\"classes\":[\"stone\"]}}}");
            TileSet tiles = TileSet.FromContent(thin, Materials(thin));

            Grammar dwelling = GrammarTable.FromContent(content, GeneTable.FromContent(content)).For("shelter");
            var gaps = tiles.Answers(dwelling);
            Assert.NotEmpty(gaps);
            Assert.Contains(gaps, g => g.Contains("role.roof"));
        }
    }
}
