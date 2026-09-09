using System.Collections.Generic;
using Godless.Sim.Content;
using Godless.Sim.Core;
using Godless.Sim.World;
using Xunit;

namespace Godless.Sim.Tests
{
    public class PaletteTests
    {
        readonly Xunit.Abstractions.ITestOutputHelper _out;
        public PaletteTests(Xunit.Abstractions.ITestOutputHelper output) { _out = output; }

        static ContentDatabase Shipped()
        {
            var dir = new System.IO.DirectoryInfo(System.IO.Directory.GetCurrentDirectory());
            while (dir != null && !System.IO.Directory.Exists(System.IO.Path.Combine(dir.FullName, "Assets", "Content")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return ContentLoader.Load(new DirectoryContentSource(
                System.IO.Path.Combine(dir.FullName, "Assets", "Content"))).Database;
        }

        /// <summary>
        /// S0A's tell: two material classes never read as the same value at
        /// camera distance. Checked against the content that actually ships,
        /// because a rule nothing is held to is a comment.
        /// </summary>
        [Fact]
        public void TheShippedPaletteObeysItsOwnContrastRule()
        {
            Palette palette = Palette.FromContent(Shipped());
            IReadOnlyList<string> problems = palette.Violations();

            foreach (string p in problems) _out.WriteLine(p);
            Assert.Empty(problems);
        }

        [Fact]
        public void TheModuleGridIsWhatTheGrammarWillDivideAgainst()
        {
            Palette palette = Palette.FromContent(Shipped());

            Assert.Equal(0.5, palette.VoxelSizeMetres);
            Assert.Equal(6, palette.FloorHeightVoxels);
            Assert.Equal(3.0, palette.FloorHeightMetres);   // a believable storey
            Assert.True(palette.DoorHeightVoxels < palette.FloorHeightVoxels,
                        "a door has to fit inside a storey");
            Assert.True(palette.BayWidthVoxels % palette.DoorWidthVoxels == 0,
                        "a bay should divide into whole doors, or the grammar cannot centre one");
        }

        static ContentDatabase Custom(string paletteJson, params string[] voxels)
        {
            var src = new MemoryContentSource().Add("base", "mod.json", "{}");
            src.Add("base", "palette.json", paletteJson);
            for (int i = 0; i < voxels.Length; i++) src.Add("base", "v" + i + ".json", voxels[i]);
            return ContentLoader.Load(src).Database;
        }

        const string StrictPalette = "{\"type\":\"palette\",\"id\":\"base\",\"maxMaterials\":3,\"minValueSeparation\":10}";

        static string Voxel(string id, int value)
        {
            return "{\"type\":\"voxel\",\"id\":\"" + id + "\",\"value\":" + value + ",\"class\":\"stone\"}";
        }

        [Fact]
        public void TwoMaterialsTooCloseInValueAreCaughtAndNamed()
        {
            Palette palette = Palette.FromContent(Custom(StrictPalette,
                Voxel("granite", 30), Voxel("basalt", 36)));

            IReadOnlyList<string> problems = palette.Violations();
            Assert.Single(problems);
            Assert.Contains("granite", problems[0]);
            Assert.Contains("basalt", problems[0]);
            Assert.Contains("read as one material", problems[0]);
        }

        [Fact]
        public void MaterialsFarEnoughApartPass()
        {
            Assert.Empty(Palette.FromContent(Custom(StrictPalette,
                Voxel("granite", 20), Voxel("chalk", 80))).Violations());
        }

        [Fact]
        public void ExceedingTheMaterialCapIsCaught()
        {
            Palette palette = Palette.FromContent(Custom(StrictPalette,
                Voxel("a", 10), Voxel("b", 30), Voxel("c", 50), Voxel("d", 70)));

            bool capped = false;
            foreach (string p in palette.Violations()) if (p.Contains("cap is")) capped = true;
            Assert.True(capped, "four materials against a cap of three should be caught");
        }

        [Fact]
        public void AMaterialWithNoDeclaredValueIsCaught()
        {
            Palette palette = Palette.FromContent(Custom(StrictPalette,
                "{\"type\":\"voxel\",\"id\":\"mystery\",\"class\":\"stone\"}"));

            Assert.Contains(palette.Violations(), p => p.Contains("no usable value"));
        }

        /// <summary>
        /// L5 again: no content, no crash. A world with no palette document
        /// still boots on the defaults rather than dividing by a zero storey.
        /// </summary>
        [Fact]
        public void NoPaletteDocumentStillYieldsAUsableGrid()
        {
            Palette palette = Palette.FromContent(new ContentDatabase());

            Assert.True(palette.FloorHeightVoxels > 0);
            Assert.True(palette.VoxelSizeMetres > 0.0);
            Assert.True(palette.MaxMaterials > 0);
            Assert.Empty(palette.Materials);
            Assert.Empty(palette.Violations());
        }
    }
}
