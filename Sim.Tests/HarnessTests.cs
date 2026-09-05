using System.Collections.Generic;
using Godless.Sim.Annals;
using Godless.Sim.Content;
using Godless.Sim.Core;
using Godless.Sim.Harness;
using Godless.Sim.Voxels;
using Xunit;

namespace Godless.Sim.Tests
{
    /// <summary>A well-behaved system: everything it does comes from its seeded stream.</summary>
    sealed class Builder : ISimSystem
    {
        public Symbol Id { get { return Symbol.For("test.builder"); } }

        public void Tick(SimWorld world)
        {
            if (!world.Clock.IsFirstTickOfYear) return;

            RngStream rng = world.Streams.Get("test.builder");
            var at = new Int3(rng.NextInt(ChunkStore.SizeX), 40, rng.NextInt(ChunkStore.SizeZ));

            RecordId cause = world.Annals.Write(world.Clock.Tick, Symbol.For("test.built"),
                                                Symbol.None, at, RecordId.None);
            world.Voxels.Set(at, 1, world.Clock.Tick, cause);
        }
    }

    /// <summary>
    /// A system that breaks L2 on purpose, to prove the harness notices.
    /// Sim.Tests is outside Assets/Sim, so the law guard permits this here
    /// and would reject it anywhere it mattered.
    /// </summary>
    sealed class WallClockDriven : ISimSystem
    {
        public Symbol Id { get { return Symbol.For("test.broken"); } }

        public void Tick(SimWorld world)
        {
            if (!world.Clock.IsFirstTickOfYear) return;
            long ticks = System.DateTime.UtcNow.Ticks;
            world.Annals.Write(world.Clock.Tick, Symbol.For("test.whenever"),
                               Symbol.None, Int3.Nowhere, RecordId.None, ticks & 0xFF);
        }
    }

    public class HarnessTests
    {
        static WorldFactory WorldWith(params ISimSystem[] systems)
        {
            var content = new ContentDatabase();
            VoxelTypes types = VoxelTypes.Build(new[] { Symbol.For("voxel.stone") });
            return seed =>
            {
                var world = new SimWorld(seed, content, types);
                foreach (ISimSystem s in systems) world.Add(s);
                return world;
            };
        }

        [Fact]
        public void RunsEverySeedAndReachesTheRequestedYear()
        {
            BatchReport report = new BatchRunner(WorldWith(new Builder())).Run(0, 8, 25);

            Assert.Equal(8, report.Runs.Count);
            foreach (RunResult r in report.Runs)
            {
                Assert.Equal(25L * 360L * 4L, r.Ticks);
                Assert.Equal(25, r.AnnalCount);   // one record a year
                Assert.True(r.DeltaCount > 0);
            }
        }

        [Fact]
        public void DifferentSeedsProduceDifferentHistories()
        {
            BatchReport report = new BatchRunner(WorldWith(new Builder())).Run(0, 5, 20);

            var digests = new HashSet<ulong>();
            foreach (RunResult r in report.Runs) digests.Add(r.Digest());
            Assert.True(digests.Count > 1, "seeds should diverge");
        }

        /// <summary>
        /// The check the whole project rests on, proved to have teeth: a
        /// system that reads the wall clock must be caught.
        /// </summary>
        [Fact]
        public void VerifyCatchesASystemThatBreaksDeterminism()
        {
            var clean = new BatchRunner(WorldWith(new Builder()));
            Assert.Empty(clean.FindNondeterministicSeeds(0, 5, 20));

            var broken = new BatchRunner(WorldWith(new WallClockDriven()));
            Assert.NotEmpty(broken.FindNondeterministicSeeds(0, 5, 20));
        }

        [Fact]
        public void AFailingPerRunInvariantNamesTheSeedsThatFailed()
        {
            BatchReport report = new BatchRunner(WorldWith(new Builder()))
                .Assert(Invariant.PerRun("S99", "impossible: no year ever happens",
                                         run => run.AnnalCount == 0))
                .Run(100, 4, 10);

            Assert.False(report.AllPassed);
            Assert.Equal(1, report.FailedCount);

            InvariantOutcome outcome = report.Outcomes[0];
            Assert.Equal(new ulong[] { 100, 101, 102, 103 }, outcome.FailingSeeds);
            Assert.Contains("S99", report.ToText());
            Assert.Contains("FAIL", report.ToText());
        }

        [Fact]
        public void ABatchInvariantSeesEveryRunAtOnce()
        {
            // The shape Part 27 needs for "in at least 30% of runs".
            BatchReport report = new BatchRunner(WorldWith(new Builder()))
                .Assert(Invariant.PerBatch("S99", "most runs build something",
                    runs =>
                    {
                        int built = 0;
                        foreach (RunResult r in runs) if (r.DeltaCount > 0) built++;
                        return built * 10 >= runs.Count * 3;
                    }))
                .Run(0, 10, 15);

            Assert.True(report.AllPassed);
        }

        [Fact]
        public void SystemsTickInRegistrationOrder()
        {
            var order = new List<string>();
            var first = new Recorder("first", order);
            var second = new Recorder("second", order);

            SimWorld world = new SimWorld(1, new ContentDatabase(),
                                          VoxelTypes.Build(new Symbol[0]));
            world.Add(first).Add(second);
            world.Tick();

            Assert.Equal(new[] { "first", "second" }, order);
        }

        sealed class Recorder : ISimSystem
        {
            readonly string _name; readonly List<string> _log;
            public Recorder(string name, List<string> log) { _name = name; _log = log; }
            public Symbol Id { get { return Symbol.For("test." + _name); } }
            public void Tick(SimWorld world) { _log.Add(_name); }
        }

        [Fact]
        public void MedianIsTheMiddleValueNotTheMean()
        {
            BatchReport report = new BatchRunner(WorldWith(new Builder())).Run(0, 5, 12);
            Assert.Equal(12.0, report.Median("annals.count"));
        }

        [Fact]
        public void AnEmptyWorldStillRunsAndCostsAlmostNothing()
        {
            BatchReport report = new BatchRunner(BatchRunner.EmptyWorld()).Run(0, 3, 50);

            foreach (RunResult r in report.Runs)
            {
                Assert.Equal(0, r.AnnalCount);
                Assert.Equal(0, r.DeltaCount);
                Assert.True(r.Metric("world.megabytes") < 0.05);
            }
        }

        [Fact]
        public void EveryStandardInvariantHoldsOnAWorldThatBuilds()
        {
            var runner = new BatchRunner(WorldWith(new Builder()));
            foreach (Invariant i in StandardInvariants.All()) runner.Assert(i);

            BatchReport report = runner.Run(0, 20, 100);
            Assert.True(report.AllPassed, report.ToText());
        }

        /// <summary>
        /// L7's audit: the rule is one assertion per system from stratum 2 on,
        /// and it can only be enforced if every invariant names its owner.
        /// </summary>
        [Fact]
        public void EveryInvariantNamesTheSystemThatOwnsIt()
        {
            foreach (Invariant i in StandardInvariants.All())
            {
                Assert.False(string.IsNullOrEmpty(i.Owner));
                Assert.StartsWith("S", i.Owner);
                Assert.False(string.IsNullOrEmpty(i.Name));
            }
            Assert.NotEmpty(StandardInvariants.Owners());
        }
    }
}
