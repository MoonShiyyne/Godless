using System.IO;
using Godless.Sim.Annals;
using Godless.Sim.Build;
using Godless.Sim.Content;
using Godless.Sim.Core;
using Godless.Sim.Drives;
using Godless.Sim.Harness;
using Godless.Sim.Settlements;
using Godless.Sim.Voxels;
using Godless.Sim.World;
using Xunit;

namespace Godless.Sim.Tests
{
    /// <summary>
    /// Town borders, and a town outgrown sending its homeless to found another (S2Y).
    ///
    /// The tell: a line round each town that stops where a neighbour's begins,
    /// and a new fire far off lit by the families who had nowhere to live.
    /// </summary>
    public class TownsTests
    {
        readonly Xunit.Abstractions.ITestOutputHelper _out;
        public TownsTests(Xunit.Abstractions.ITestOutputHelper output) { _out = output; }

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

        static bool SettleableNear(SimWorld world, ParcelGrid grid, BiomeTable biomes, int cx, int cz, out int px, out int pz)
        {
            for (int r = 0; r < 12; r++)
                for (int dz = -r; dz <= r; dz++)
                    for (int dx = -r; dx <= r; dx++)
                        if (Founding.Appraise(world, null, grid, biomes, cx + dx, cz + dz).CanSettle) { px = cx + dx; pz = cz + dz; return true; }
            px = pz = -1;
            return false;
        }

        [Fact]
        public void BordersHoldEachTownsGroundAndStopWhereTheyMeet()
        {
            WorldChoice choice;
            ParcelGrid grid;
            ConstraintFields fields;
            SimWorld world = Unsettled(out choice, out grid, out fields);
            TownRules rules = TownRules.FromContent(Content);
            Assert.NotNull(rules);

            int ax, az;
            Assert.True(Founding.StandInSite(grid, world.Island, choice.Biomes, Symbol.None, out ax, out az));
            Settlement a = Founding.Begin(world, Content, grid, choice.Biomes, "a", 4, ax, az, null);
            int bx, bz;
            Assert.True(SettleableNear(world, grid, choice.Biomes, ax + 12, az, out bx, out bz), "no second site near the first");
            Settlement b = Founding.Begin(world, Content, grid, choice.Biomes, "b", 4, bx, bz, null);

            var borders = new Territory();
            borders.Redraw(world.Settlements, grid, rules.BorderParcels);
            Assert.Equal(a, borders.Owner(ax, az));
            Assert.Equal(b, borders.Owner(bx, bz));
            foreach (int parcel in a.Claims) Assert.Equal(a, borders.Owner(parcel % ParcelGrid.Width, parcel / ParcelGrid.Width));

            // Nearer to a is a's, nearer to b is b's, and past the reach of both is nobody's.
            Assert.True(borders.Area(a) > 0 && borders.Area(b) > 0);
            if (grid.IsLand(ax + 2, az)) Assert.Equal(a, borders.Owner(ax + 2, az));
            if (grid.IsLand(bx - 2, bz) && System.Math.Abs(bx - 2 - ax) > 2) Assert.Equal(b, borders.Owner(bx - 2, bz));
            Assert.Null(borders.Owner(ax, az - rules.BorderParcels - 30 < 0 ? 0 : az - rules.BorderParcels - 30));
            _out.WriteLine("a holds " + borders.Area(a) + " parcels, b " + borders.Area(b));

            // A town builds nowhere inside another's border.
            for (int dz = -rules.BorderParcels; dz <= rules.BorderParcels; dz++)
                for (int dx = -rules.BorderParcels; dx <= rules.BorderParcels; dx++)
                    if (borders.Owner(bx + dx, bz + dz) == b) Assert.True(borders.BelongsToAnother(a, bx + dx, bz + dz));
        }

        [Fact]
        public void AnOutgrownTownSendsItsHomelessOffToFoundAnother()
        {
            WorldChoice choice;
            ParcelGrid grid;
            ConstraintFields fields;
            SimWorld world = Unsettled(out choice, out grid, out fields);
            TownRules rules = TownRules.FromContent(Content);
            int px, pz;
            Assert.True(Founding.StandInSite(grid, world.Island, choice.Biomes, Symbol.None, out px, out pz));
            Settlement home = Founding.Begin(world, Content, grid, choice.Biomes, "first", 20, px, pz, null);
            Founding.AddSystems(world, Content, grid, fields, choice.Biomes);
            for (int t = 0; t < world.Clock.TicksPerDay; t++) world.Tick();

            // A family of twenty with nowhere to live.
            Household roofless = Households.FormIn(home, world.Clock.Tick, world.Annals, home.Founded);
            for (int i = 0; i < 20; i++) Households.Join(home, home.Add(world.Streams), roofless);
            Assert.True(Towns.ManyHomeless(home, rules));
            int before = home.People.Count;
            double food = home.Food;

            var borders = new Territory();
            borders.Redraw(world.Settlements, grid, rules.BorderParcels);
            int sx, sz;
            Assert.True(Towns.FindSite(world, Content, grid, choice.Biomes, borders, home, rules, out sx, out sz), "nowhere on the island for a new town");
            double apart = System.Math.Sqrt((sx - px) * (sx - px) + (sz - pz) * (sz - pz));
            Assert.True(apart >= rules.NearestTownParcels, "the new fire is " + apart.ToString("0") + " parcels from the old");
            Assert.True(apart <= rules.FarthestParcels * 1.5);
            Assert.Null(borders.Owner(sx, sz));

            Settlement town = Towns.Emigrate(world, Content, grid, choice.Biomes, home, sx, sz, rules);
            Assert.NotNull(town);
            _out.WriteLine("town at " + sx + "," + sz + ", " + apart.ToString("0") + " parcels off, " + town.People.Count + " people");
            Assert.Equal(2, world.Settlements.Count);
            Assert.Equal(20, town.People.Count);
            Assert.Equal(before - 20, home.People.Count);
            Assert.True(town.Food > 0.0 && home.Food < food);
            Assert.Equal(sx, town.HearthParcelX);
            Assert.Single(town.Households);
            foreach (Agent a in town.People) Assert.Equal(town.Households[0].Number, a.Household);
            foreach (Household h in home.Households) Assert.True(h.Size > 0);
            Assert.Equal(home.Genome.Digest(), town.Genome.Digest());

            // On record: the new town was founded because the old one was outgrown.
            AnnalRecord founded = world.Annals.Get(town.Founded);
            Assert.Equal(Towns.OutgrownKind, world.Annals.Get(founded.Cause).Kind);

            // And both go on living.
            for (int t = 0; t < 30 * world.Clock.TicksPerDay; t++) world.Tick();
            Assert.True(town.People.Count >= 18, "the new town lost its people: " + town.People.Count);
            foreach (Agent a in town.People)
                Assert.True(System.Math.Abs(a.ParcelX - town.HearthParcelX) < 60 && System.Math.Abs(a.ParcelZ - town.HearthParcelZ) < 60,
                            "someone of the new town is still at the old one");
        }
    }
}
