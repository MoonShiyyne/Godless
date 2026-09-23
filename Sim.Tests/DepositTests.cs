using System.Collections.Generic;
using System.IO;
using Godless.Sim.Annals;
using Godless.Sim.Content;
using Godless.Sim.Core;
using Godless.Sim.Deltas;
using Godless.Sim.Harness;
using Godless.Sim.Save;
using Godless.Sim.Economy;
using Godless.Sim.Voxels;
using Godless.Sim.World;
using Xunit;

namespace Godless.Sim.Tests
{
    /// <summary>
    /// Deposits and depletion. S2F.
    ///
    /// The tell: a clearing grows out from a village's fire, pits open in the
    /// rubble above it, and a material worked out of reach stops being
    /// gathered — on record — so what is built next is built of something else.
    /// </summary>
    public class DepositTests
    {
        readonly Xunit.Abstractions.ITestOutputHelper _out;
        public DepositTests(Xunit.Abstractions.ITestOutputHelper output) { _out = output; }

        static ContentDatabase Shipped()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Assets", "Content")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return ContentLoader.Load(new DirectoryContentSource(Path.Combine(dir.FullName, "Assets", "Content"))).Database;
        }

        static readonly ContentDatabase Content = Shipped();

        [Fact]
        public void EveryMaterialABiomePromisesIsGrownOrLaidThere()
        {
            WorldChoice all = WorldChoice.Pick(Content, "");
            Assert.Empty(all.Features.Problems);
            Assert.True(all.Features.Count >= 9);
            foreach (FeatureKind kind in all.Features.All)
                Assert.False(string.IsNullOrEmpty(kind.Tell), kind.Name + " has no tell (L4)");
        }

        [Fact]
        public void TheSameSeedGrowsTheSameWood()
        {
            WorldChoice choice = WorldChoice.Pick(Content, "cold-massif");
            VoxelTypes types = VoxelTypes.FromContent(Content);
            var a = new ChunkStore();
            var b = new ChunkStore();
            // Two real generations: this is the test of generation itself, so it must not share.
            IslandMap ia = IslandGenerator.Generate(a, new StreamRegistry(3), choice.Biomes, types, choice.Preset, choice.Features);
            IslandMap ib = IslandGenerator.Generate(b, new StreamRegistry(3), choice.Biomes, types, choice.Preset, choice.Features);
            Assert.Equal(a.Digest(), b.Digest());
            Assert.Equal(ia.Deposits.Digest(), ib.Deposits.Digest());
            Assert.True(ia.Deposits.Count > 500);
        }

    }
}
