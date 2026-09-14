using System.IO;
using Godless.Sim.Content;
using Godless.Sim.Core;
using Godless.Sim.Harness;
using Godless.Sim.Settlements;
using Godless.Sim.Voxels;
using Godless.Sim.World;
using Xunit;

namespace Godless.Sim.Tests
{
    /// <summary>
    /// Choosing where the first fire is lit, before the world starts. The
    /// tell: a village that begins where the player put it, on land the game
    /// agreed people could live on.
    /// </summary>
    public class SiteChoiceTests
    {
        readonly Xunit.Abstractions.ITestOutputHelper _out;
        public SiteChoiceTests(Xunit.Abstractions.ITestOutputHelper output) { _out = output; }

        static ContentDatabase Shipped()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Assets", "Content"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return ContentLoader.Load(new DirectoryContentSource(Path.Combine(dir.FullName, "Assets", "Content"))).Database;
        }

        static readonly ContentDatabase Content = Shipped();

        static SimWorld Unsettled(out WorldChoice choice, out ParcelGrid grid, out ConstraintFields fields)
        {
            choice = WorldChoice.Pick(Content, "green-shore");
            var world = new SimWorld(7, Content, VoxelTypes.FromContent(Content));
            world.Island = TestIslands.Generate(world.Voxels.Store, world.Streams, choice.Biomes, world.VoxelTypes, choice.Preset, choice.Features);
            world.BeginHistory();
            grid = Founding.Survey(world, Content, choice.Biomes, out fields);
            return world;
        }

        [Fact]
        public void TheSuggestedSiteCanBeSettledAndSaysWhatItOffers()
        {
            WorldChoice choice;
            ParcelGrid grid;
            ConstraintFields fields;
            SimWorld world = Unsettled(out choice, out grid, out fields);
            int px, pz;
            Assert.True(Founding.StandInSite(grid, world.Island, choice.Biomes, Symbol.None, out px, out pz));

            SiteReport report = Founding.Appraise(world, Content, grid, choice.Biomes, px, pz);
            _out.WriteLine(report.Biome + ", slope " + report.Slope + ", water " + report.WaterParcels + ", forage " + report.ForagePerDay.ToString("0")
                           + ", " + report.Materials.Count + " materials, room " + report.RoomNearby);
            Assert.True(report.CanSettle, report.Why);
            Assert.Equal("", report.Why);
            Assert.NotEmpty(report.Materials);
            Assert.True(report.ForagePerDay > 0.0);
            Assert.NotEqual("", report.Biome);
        }

        [Fact]
        public void WaterSteepGroundAndTheEdgeOfTheWorldAreRefusedWithAReason()
        {
            WorldChoice choice;
            ParcelGrid grid;
            ConstraintFields fields;
            SimWorld world = Unsettled(out choice, out grid, out fields);

            SiteReport edge = Founding.Appraise(world, Content, grid, choice.Biomes, 2, 2);
            Assert.False(edge.CanSettle);
            Assert.Contains("edge", edge.Why);

            int seaX = -1, seaZ = -1, steepX = -1, steepZ = -1;
            for (int pz = Founding.FromTheEdge; pz < ParcelGrid.Depth - Founding.FromTheEdge; pz++)
                for (int px = Founding.FromTheEdge; px < ParcelGrid.Width - Founding.FromTheEdge; px++)
                {
                    if (seaX < 0 && !grid.IsLand(px, pz)) { seaX = px; seaZ = pz; }
                    if (steepX < 0 && grid.IsLand(px, pz) && grid.WetColumns(px, pz) == 0 && grid.Slope[px, pz] >= Founding.MostSlopeForAFire)
                    { steepX = px; steepZ = pz; }
                }
            Assert.True(seaX >= 0);
            SiteReport sea = Founding.Appraise(world, Content, grid, choice.Biomes, seaX, seaZ);
            Assert.False(sea.CanSettle);
            Assert.Contains("water", sea.Why);

            if (steepX >= 0)
            {
                SiteReport steep = Founding.Appraise(world, Content, grid, choice.Biomes, steepX, steepZ);
                Assert.False(steep.CanSettle);
            }
        }

        [Fact]
        public void AVillageBeginsWhereTheFireWasChosen()
        {
            WorldChoice choice;
            ParcelGrid grid;
            ConstraintFields fields;
            SimWorld world = Unsettled(out choice, out grid, out fields);

            // Some settleable parcel that is not the suggested one.
            int sx, sz;
            Assert.True(Founding.StandInSite(grid, world.Island, choice.Biomes, Symbol.None, out sx, out sz));
            int px = -1, pz = -1;
            for (int z = sz + 6; z < ParcelGrid.Depth - Founding.FromTheEdge && px < 0; z++)
                for (int x = sx - 20; x < sx + 20 && px < 0; x++)
                    if (ParcelGrid.InBounds(x, z) && Founding.Appraise(world, null, grid, choice.Biomes, x, z).CanSettle) { px = x; pz = z; }
            Assert.True(px >= 0, "no other settleable parcel near the suggested one");

            Settlement s = Founding.Begin(world, Content, grid, choice.Biomes, "first", 20, px, pz, null);
            Founding.AddSystems(world, Content, grid, fields, choice.Biomes);
            Assert.Equal(px, s.HearthParcelX);
            Assert.Equal(pz, s.HearthParcelZ);
            for (int t = 0; t < 40; t++) world.Tick();
            Assert.Equal(20, s.People.Count);
        }
    }
}
