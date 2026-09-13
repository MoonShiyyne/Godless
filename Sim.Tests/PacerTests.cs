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
    public class PacerTests
    {
        static TickPacer Pacer(int level = 1) { return new TickPacer(daysPerSecondAt1x: 1.0, ticksPerDay: 4, level: level); }

        static int Run(TickPacer pacer, double seconds, int budget = 1000)
        {
            pacer.Advance(seconds);
            return pacer.Take(budget);
        }

        [Fact]
        public void ASecondAtOneTimesIsADay()
        {
            TickPacer pacer = Pacer();
            Assert.Equal("1x", pacer.Label);
            Assert.Equal(4, Run(pacer, 1.0));
        }

        [Fact]
        public void SpeedMultipliesTheTicksAndNothingElse()
        {
            Assert.Equal(4, Run(Pacer(1), 1.0));
            Assert.Equal(8, Run(Pacer(2), 1.0));
            Assert.Equal(16, Run(Pacer(3), 1.0));
            Assert.Equal(32, Run(Pacer(4), 1.0));
            Assert.Equal(64, Run(Pacer(5), 1.0));
        }

        /// <summary>Part of a tick is not a thing that can happen; it waits.</summary>
        [Fact]
        public void PartOfATickIsCarriedNotLost()
        {
            TickPacer pacer = Pacer();
            Assert.Equal(0, Run(pacer, 0.1));
            Assert.Equal(0, Run(pacer, 0.1));
            Assert.Equal(1, Run(pacer, 0.1));   // three tenths of a day is one tick and a fifth
            Assert.Equal(0, Run(pacer, 0.0));
        }

        [Fact]
        public void PausedIsPausedAndComesBackWhereItWas()
        {
            TickPacer pacer = Pacer(3);
            pacer.TogglePause();
            Assert.True(pacer.IsPaused);
            Assert.Equal("paused", pacer.Label);
            Assert.Equal(0, Run(pacer, 5.0));

            pacer.TogglePause();
            Assert.Equal(4, pacer.Multiplier);
            Assert.Equal(16, Run(pacer, 1.0));
        }

        [Fact]
        public void SpeedStopsAtBothEnds()
        {
            TickPacer pacer = Pacer(0);
            pacer.Slower();
            Assert.Equal(0, pacer.Level);
            for (int i = 0; i < 20; i++) pacer.Faster();
            Assert.Equal(TickPacer.Multipliers.Length - 1, pacer.Level);
        }

        /// <summary>
        /// A frame that took a second — a breakpoint, a load — must not be
        /// repaid as a burst nobody sees. The debt is capped and the world
        /// simply runs behind for a moment.
        /// </summary>
        [Fact]
        public void AStallIsNotRepaidAsABurst()
        {
            TickPacer pacer = Pacer(5);
            pacer.Advance(30.0);
            Assert.True(pacer.Behind <= TickPacer.MaxDebtTicks);
            Assert.Equal((int)TickPacer.MaxDebtTicks, pacer.Take(10000));
        }

        [Fact]
        public void TicksAFrameCannotAffordStayOwed()
        {
            TickPacer pacer = Pacer(4);   // 32 ticks a second
            pacer.Advance(1.0);
            Assert.Equal(4, pacer.Take(4));
            Assert.Equal(4, pacer.Take(4));
            Assert.Equal(24, pacer.Take(100));
            Assert.Equal(0, pacer.Take(100));
        }

        [Fact]
        public void AStepIsTicksByAnotherRoad()
        {
            TickPacer pacer = Pacer(0);
            pacer.Request(4);
            Assert.Equal(4, pacer.Take(100));
        }

        // ── the invariant the whole thing rests on ──────────────────────────

        static ContentDatabase Load()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Assets", "Content"))) dir = dir.Parent;
            return ContentLoader.Load(new DirectoryContentSource(Path.Combine(dir.FullName, "Assets", "Content"))).Database;
        }

        static readonly ContentDatabase Content = Load();

        static SimWorld Village(ulong seed)
        {
            BiomeTable biomes = BiomeTable.FromContent(Content);
            VoxelTypes types = VoxelTypes.FromContent(Content);
            var world = new SimWorld(seed, Content, types);
            world.Island = TestIslands.Generate(world.Voxels.Store, world.Streams, biomes, types);

            ConstraintFields fields;
            ParcelGrid grid = Founding.Survey(world, Content, biomes, out fields);
            int px, pz;
            Assert.True(Founding.StandInSite(grid, world.Island, biomes, Symbol.None, out px, out pz));
            Settlement town = Founding.Begin(world, Content, grid, biomes, "test", 20, px, pz, null);

            MaterialTable materials = MaterialTable.FromContent(Content, biomes);
            for (int m = 0; m < materials.Count; m++) if (town.Catchment.Offers(m)) town.Stock.Add(m, 3000);
            Founding.AddSystems(world, Content, grid, fields, biomes);
            world.BeginHistory();
            return world;
        }

        /// <summary>
        /// The guarantee every later speed rests on: the world after a number
        /// of ticks is the same world however those ticks were spread. One
        /// long run, against the same run chopped into the ragged little
        /// batches a real frame rate produces at changing speeds.
        /// </summary>
        [Fact]
        public void HowTheTicksAreSpreadCannotChangeTheWorld()
        {
            const int ticks = 1200;
            SimWorld whole = Village(7);
            for (int i = 0; i < ticks; i++) whole.Tick();

            SimWorld ragged = Village(7);
            var pacer = new TickPacer(daysPerSecondAt1x: 1.0, ticksPerDay: ragged.Clock.TicksPerDay, level: 1);
            var rng = new RngStream(StableHash.OfString("test.frames"));
            while (ragged.Clock.Tick < ticks)
            {
                // A frame of somewhere between 8 and 120 ms, at a speed the
                // watcher keeps changing, with the renderer sometimes behind.
                pacer.Level = 1 + rng.NextInt(TickPacer.Multipliers.Length - 1);
                pacer.Advance(rng.NextInt(8, 120) / 1000.0);
                int take = pacer.Take(1 + rng.NextInt(16));
                long room = ticks - ragged.Clock.Tick;
                if (take > room) take = (int)room;
                for (int i = 0; i < take; i++) ragged.Tick();
            }

            Assert.Equal(ticks, (int)ragged.Clock.Tick);
            Assert.Equal(whole.Annals.Digest(), ragged.Annals.Digest());
            Assert.Equal(whole.Voxels.Store.Digest(), ragged.Voxels.Store.Digest());
            Assert.Equal(whole.Settlements[0].Digest(), ragged.Settlements[0].Digest());
        }

        /// <summary>And a pause is not a slower world, it is a stopped one: nothing moves.</summary>
        [Fact]
        public void PausingChangesNothingAtAll()
        {
            SimWorld world = Village(7);
            for (int i = 0; i < 200; i++) world.Tick();
            ulong annals = world.Annals.Digest(), voxels = world.Voxels.Store.Digest();

            var pacer = new TickPacer(1.0, world.Clock.TicksPerDay, 3);
            pacer.TogglePause();
            for (int frame = 0; frame < 600; frame++)
            {
                pacer.Advance(1.0 / 60.0);
                Assert.Equal(0, pacer.Take(64));
            }

            Assert.Equal(annals, world.Annals.Digest());
            Assert.Equal(voxels, world.Voxels.Store.Digest());
        }
    }
}
