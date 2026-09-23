using System.Collections.Generic;
using System.IO;
using Godless.Sim.Annals;
using Godless.Sim.Build;
using Godless.Sim.Content;
using Godless.Sim.Core;
using Godless.Sim.Deltas;
using Godless.Sim.Harness;
using Godless.Sim.Settlements;
using Godless.Sim.Voxels;
using Godless.Sim.World;
using Xunit;

namespace Godless.Sim.Tests
{
    /// <summary>
    /// The god's hand reaching the simulation (S07, S10).
    ///
    /// Two failures this pins down. A raised hill never reached the planning
    /// grid, so a year later the village still sited, farmed and walked the
    /// flat ground under it. And each stroke advanced the clock before it
    /// wrote, so holding the mouse ran a dozen ticks a second in which no
    /// system ran: nobody ate, worked or built.
    /// </summary>
    public class GodHandTests
    {
        readonly Xunit.Abstractions.ITestOutputHelper _out;
        public GodHandTests(Xunit.Abstractions.ITestOutputHelper output) { _out = output; }

        static ContentDatabase Shipped()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Assets", "Content"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return ContentLoader.Load(new DirectoryContentSource(Path.Combine(dir.FullName, "Assets", "Content"))).Database;
        }

        static readonly ContentDatabase Content = Shipped();

        sealed class Village
        {
            public SimWorld World;
            public ParcelGrid Grid;
            public ConstraintFields Fields;
            public BiomeTable Biomes;
            public Settlement Town;
            public GodHand Hand;
        }

        /// <summary>Green shore, seed 7, twenty people at the suggested site, every system running — as the Editor starts.</summary>
        static Village Settle()
        {
            WorldChoice choice = WorldChoice.Pick(Content, "green-shore");
            var world = new SimWorld(7, Content, VoxelTypes.FromContent(Content));
            world.Island = TestIslands.Generate(world.Voxels.Store, world.Streams, choice.Biomes, world.VoxelTypes, choice.Preset, choice.Features);
            world.BeginHistory();
            ConstraintFields fields;
            ParcelGrid grid = Founding.Survey(world, Content, choice.Biomes, out fields);
            int px, pz;
            Assert.True(Founding.StandInSite(grid, world.Island, choice.Biomes, Symbol.For("biome.temperate"), out px, out pz)
                        || Founding.StandInSite(grid, world.Island, choice.Biomes, Symbol.None, out px, out pz));
            Settlement town = Founding.Begin(world, Content, grid, choice.Biomes, "first", 20, px, pz, null);
            Founding.AddSystems(world, Content, grid, fields, choice.Biomes);
            return new Village
            {
                World = world, Grid = grid, Fields = fields, Biomes = choice.Biomes, Town = town,
                Hand = new GodHand(world, grid, GroundPalette.From(world.Island, choice.Biomes, world.VoxelTypes)),
            };
        }

        /// <summary>Land six parcels from the fire, in the first direction that has any.</summary>
        static void BesideTheFire(Village v, out int px, out int pz)
        {
            int[] offsets = { 6, 0, -6, 0, 0, 6, 0, -6 };
            for (int i = 0; i < offsets.Length; i += 2)
            {
                px = v.Town.HearthParcelX + offsets[i];
                pz = v.Town.HearthParcelZ + offsets[i + 1];
                if (v.Grid.IsLand(px, pz) && v.Grid.WetColumns(px, pz) == 0) return;
            }
            px = pz = -1;
            Assert.Fail("no dry land beside the fire");
        }

        static Int3 Column(Village v, int px, int pz)
        {
            int x = px * ParcelGrid.Size + ParcelGrid.Size / 2, z = pz * ParcelGrid.Size + ParcelGrid.Size / 2;
            return new Int3(x, v.Grid.GroundAt(x, z), z);
        }

        static double MeanGround(ChunkStore store, bool[] solid, int px, int pz)
        {
            int sum = 0;
            for (int dz = 0; dz < ParcelGrid.Size; dz++)
                for (int dx = 0; dx < ParcelGrid.Size; dx++)
                    sum += store.TopMatching(px * ParcelGrid.Size + dx, pz * ParcelGrid.Size + dz, solid);
            return sum / (double)(ParcelGrid.Size * ParcelGrid.Size);
        }

        /// <summary>
        /// The report's own case: a hill raised about 36 voxels beside the
        /// fire. The next tick the grid reads it — height, slope, exposure —
        /// and siting, which reads the grid, sees ground too steep to build on
        /// where it was flat.
        /// </summary>
        [Fact]
        public void ARaisedHillIsOnThePlanningGridByTheNextTickAndSitingSeesIt()
        {
            Village v = Settle();
            for (int t = 0; t < v.World.Clock.TicksPerDay; t++) v.World.Tick();

            int px, pz;
            BesideTheFire(v, out px, out pz);
            double height = v.Grid.Height[px, pz];
            double exposure = v.Fields.Exposure[px, pz];
            IExprScope facts = SiteScorer.Facts(v.Town, v.Grid, v.Fields, v.Town.Genome, px, pz);
            Assert.Equal(height, facts.Resolve("parcel.height"));

            // Parcels on the hill's flank that a fire could have been lit on.
            const int radius = 10;
            var flatBefore = new List<int>();
            for (int z = pz - 3; z <= pz + 3; z++)
                for (int x = px - 3; x <= px + 3; x++)
                    if (Founding.Appraise(v.World, null, v.Grid, v.Biomes, x, z).CanSettle) flatBefore.Add(z * ParcelGrid.Width + x);
            Assert.NotEmpty(flatBefore);

            long tick = v.World.Clock.Tick;
            for (int stroke = 0; stroke < 3; stroke++) v.Hand.Raise(Column(v, px, pz), radius, 12);
            Assert.Equal(tick, v.World.Clock.Tick);
            Assert.True(v.Grid.HasPending);
            Assert.Equal(height, v.Grid.Height[px, pz]);   // read at the start of the next tick, not per stroke

            // What the grid should read: the ground as the brush left it.
            var expected = new Dictionary<int, double>();
            for (int z = pz - 3; z <= pz + 3; z++)
                for (int x = px - 3; x <= px + 3; x++)
                    expected[z * ParcelGrid.Width + x] = MeanGround(v.World.Voxels.Store, v.Hand.Solid, x, z);

            v.World.Tick();

            Assert.False(v.Grid.HasPending);
            foreach (KeyValuePair<int, double> e in expected)
                Assert.Equal(e.Value, v.Grid.Height[e.Key % ParcelGrid.Width, e.Key / ParcelGrid.Width]);
            double raised = v.Grid.Height[px, pz] - height;
            _out.WriteLine("parcel (" + px + ", " + pz + ") rose " + raised.ToString("0.0") + " voxels; exposure "
                           + exposure.ToString("0.00") + " -> " + v.Fields.Exposure[px, pz].ToString("0.00"));
            Assert.True(raised >= 30.0, "the hill is " + raised + " voxels on the grid");
            Assert.True(v.Fields.Exposure[px, pz] > exposure, "a hill stands above the land round it");

            facts = SiteScorer.Facts(v.Town, v.Grid, v.Fields, v.Town.Genome, px, pz);
            Assert.Equal(v.Grid.Height[px, pz], facts.Resolve("parcel.height"));

            int steep = 0;
            foreach (int p in flatBefore)
            {
                SiteReport r = Founding.Appraise(v.World, null, v.Grid, v.Biomes, p % ParcelGrid.Width, p / ParcelGrid.Width);
                if (!r.CanSettle && r.Why == "too steep for a fire") steep++;
            }
            _out.WriteLine(steep + " of " + flatBefore.Count + " settleable parcels are now too steep");
            Assert.True(steep > 0, "no parcel on the hill's flanks turned steep");
        }

        /// <summary>
        /// A held button is a dozen strokes a second. None of them moves the
        /// clock; the next tick is a whole one, systems and all; and history
        /// agrees with itself about where the strokes landed.
        /// </summary>
        [Fact]
        public void AStrokeTakesNoTickOfItsOwn()
        {
            Village v = Settle();
            for (int t = 0; t < v.World.Clock.TicksPerDay * 3; t++) v.World.Tick();
            long tick = v.World.Clock.Tick;
            ulong before = v.World.Voxels.Store.Digest();
            int deltas = v.World.Voxels.Log.Count;

            int px, pz;
            BesideTheFire(v, out px, out pz);
            var strokes = new HashSet<int>();
            for (int i = 0; i < 12; i++)
            {
                RecordId r = i % 3 == 2 ? v.Hand.Lower(Column(v, px, pz), 5, 2) : v.Hand.Raise(Column(v, px + (i % 2), pz), 6, 3);
                strokes.Add(r.Index);
            }

            Assert.Equal(tick, v.World.Clock.Tick);
            Assert.Equal(v.World.Clock.Tick, v.World.TicksRun);

            // L3: every voxel the strokes changed cites a stroke, at the present tick.
            IReadOnlyList<VoxelDelta> log = v.World.Voxels.Log.All();
            Assert.True(log.Count > deltas);
            for (int i = deltas; i < log.Count; i++)
            {
                Assert.Equal(tick, log[i].Tick);
                Assert.Contains(log[i].Cause.Index, strokes);
                Assert.StartsWith("god.", v.World.Annals.Get(log[i].Cause).Kind.ToString());
            }

            // The timeline: the present includes the strokes, the tick before does not,
            // and walking back and forth over them gives what a rebuild gives.
            Assert.Equal(v.World.Voxels.Store.Digest(), v.World.Voxels.AsOf(tick).Digest());
            var view = new HistoryView(v.World.Voxels.Store, v.World.Voxels.Log, tick);
            view.Seek(tick - 1, null);
            Assert.Equal(v.World.Voxels.AsOf(tick - 1).Digest(), view.Store.Digest());
            Assert.NotEqual(view.Store.Digest(), v.World.Voxels.Store.Digest());
            view.Seek(tick, null);
            Assert.Equal(v.World.Voxels.Store.Digest(), view.Store.Digest());
            Assert.NotEqual(before, v.World.Voxels.Store.Digest());

            // And the next tick is a real one: needs move, the clock moves by one.
            var person = v.Town.People[0];
            var levels = new double[4];
            for (int n = 0; n < levels.Length; n++) levels[n] = person.Level(n);
            v.World.Tick();
            Assert.Equal(tick + 1, v.World.Clock.Tick);
            Assert.Equal(v.World.Clock.Tick, v.World.TicksRun);
            bool moved = false;
            for (int n = 0; n < levels.Length; n++) if (person.Level(n) != levels[n]) moved = true;
            Assert.True(moved, "nobody's needs moved in the tick after the strokes");
        }

        /// <summary>
        /// Before the first tick the present is the untouched island, already
        /// history's baseline, and cannot take a change. The hand waits for the
        /// world's next tick — run whole — and lands there, so scrubbing back
        /// to tick 0 still finds the island as it was made.
        /// </summary>
        [Fact]
        public void OnAClosedPresentTheHandLandsInTheNextWholeTick()
        {
            Village v = Settle();
            Assert.Equal(0, v.World.Clock.Tick);
            ulong untouched = v.World.Voxels.Store.Digest();

            int px, pz;
            BesideTheFire(v, out px, out pz);
            RecordId stroke = v.Hand.Raise(Column(v, px, pz), 6, 8);

            Assert.Equal(1, v.World.Clock.Tick);
            Assert.Equal(1, v.World.TicksRun);
            Assert.Equal(1, v.World.Annals.Get(stroke).Tick);
            Assert.Equal(untouched, v.World.Voxels.AsOf(0).Digest());
            Assert.Equal(v.World.Voxels.Store.Digest(), v.World.Voxels.AsOf(1).Digest());

            // The next stroke lands in the same tick: it is open now.
            v.Hand.Raise(Column(v, px, pz), 6, 8);
            Assert.Equal(1, v.World.Clock.Tick);
        }

        /// <summary>A wood the god buries under a hill is gone from what the village can fell.</summary>
        [Fact]
        public void ATreeBuriedUnderAHillIsNoLongerThereToFell()
        {
            Village v = Settle();
            v.World.Tick();
            DepositMap deposits = v.World.Island.Deposits;
            Assert.NotNull(deposits);

            // The nearest standing tree to the fire.
            int tree = -1;
            foreach (int f in deposits.Within(v.Town.Hearth.X, v.Town.Hearth.Z, 120))
                if (deposits.KindOf(f).Shape == FeatureShape.Tree && deposits.Standing(f) && deposits.Remaining(f) > 0) { tree = f; break; }
            Assert.True(tree >= 0, "no tree near the fire");
            int remaining = deposits.Remaining(tree);

            var at = new Int3(deposits.X(tree), deposits.Y(tree) - 1, deposits.Z(tree));
            v.Hand.Raise(at, 4, 16);
            Assert.Equal(remaining, deposits.Remaining(tree));   // until the next tick reads the ground
            v.World.Tick();

            Assert.Equal(0, deposits.Remaining(tree));
            Assert.False(deposits.Standing(tree));
        }
    }
}
