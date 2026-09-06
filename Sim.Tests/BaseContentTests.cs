using System.IO;
using Godless.Sim.Content;
using Xunit;

namespace Godless.Sim.Tests
{
    /// <summary>
    /// Loads the real Assets/Content folder from disk. The in-memory tests
    /// prove the loader; this proves the shipped content actually parses,
    /// which is the failure that would otherwise surface as a black screen.
    /// </summary>
    public class BaseContentTests
    {
        static string ContentRoot()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Assets", "Content")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return Path.Combine(dir.FullName, "Assets", "Content");
        }

        [Fact]
        public void ShippedContentLoadsAndParses()
        {
            LoadResult r = ContentLoader.Load(new DirectoryContentSource(ContentRoot()));

            Assert.Empty(r.Warnings);
            Assert.False(r.ContainsCodeMod);
            Assert.Single(r.LoadOrder);
            Assert.Equal("base", r.LoadOrder[0].Id);

            Assert.True(r.Database.Contains("biome", "temperate"));
            Assert.True(r.Database.Contains("biome", "flood-plain"));
            // The flood plain has no timber — that constraint is the whole
            // reason it exists as a test of the parameter space (Part 19).
            Assert.InRange(r.Database.Get("biome", "flood-plain")["treeCoverPercent"].AsInt32(-1), 0, 10);

            // Deliberately not an exact count: content grows, and a test that
            // has to be edited every time a biome is added trains people to
            // edit it without reading it.
            Assert.True(r.Database.Ids("biome").Count >= 2);
            Assert.True(r.Database.Ids("voxel").Count >= 1);
        }

        /// <summary>
        /// L5's tell and half of G0's exit condition, against the real
        /// directory layout: point the loader at a content root with no base
        /// mod in it and the game still boots, with nothing to build.
        /// </summary>
        [Fact]
        public void WithoutTheBaseMod_ItStillBoots()
        {
            string empty = Path.Combine(Path.GetTempPath(), "godless-empty-content-" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(empty);
            try
            {
                LoadResult r = ContentLoader.Load(new DirectoryContentSource(empty));
                Assert.Empty(r.LoadOrder);
                Assert.Equal(0, r.Database.DocumentCount);
            }
            finally { Directory.Delete(empty, true); }
        }
    }
}
