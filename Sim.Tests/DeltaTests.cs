using System.Collections.Generic;
using Godless.Sim.Annals;
using Godless.Sim.Core;
using Godless.Sim.Deltas;
using Godless.Sim.Voxels;
using Xunit;

namespace Godless.Sim.Tests
{
    public class DeltaLogTests
    {
        const ushort Stone = 1;
        const ushort Soil = 2;

        static VoxelWorld World(long snapshotInterval = DeltaLog.DefaultSnapshotInterval)
        {
            return new VoxelWorld(new ChunkStore(), new DeltaLog(snapshotInterval));
        }

        [Fact]
        public void EveryChangeIsRecordedWithItsBeforeAfterAndCause()
        {
            var annals = new Annalist();
            RecordId hand = annals.Write(10, Symbol.For("god.raised-ridge"), RecordId.None);

            VoxelWorld world = World();
            Assert.True(world.Set(new Int3(100, 40, 100), Stone, 10, hand));

            Assert.Equal(1, world.Log.Count);
            VoxelDelta d = world.Log.All()[0];
            Assert.Equal(10, d.Tick);
            Assert.Equal(VoxelTypes.AirId, d.OldType);
            Assert.Equal(Stone, d.NewType);
            Assert.Equal(hand, d.Cause);
        }

        [Fact]
        public void AWriteThatChangesNothingRecordsNothing()
        {
            VoxelWorld world = World();
            world.Set(new Int3(5, 5, 5), Stone, 1, RecordId.None);
            Assert.False(world.Set(new Int3(5, 5, 5), Stone, 2, RecordId.None));
            Assert.Equal(1, world.Log.Count);
        }

        [Fact]
        public void OutOfBoundsWritesAreRefusedAndUnlogged()
        {
            VoxelWorld world = World();
            Assert.False(world.Set(new Int3(-1, 0, 0), Stone, 1, RecordId.None));
            Assert.False(world.Set(new Int3(0, 160, 0), Stone, 1, RecordId.None));
            Assert.Equal(0, world.Log.Count);
        }

        /// <summary>
        /// M0's exit condition, without the renderer: raise a hill, then scrub
        /// back to before you raised it and find the ground as it was.
        /// </summary>
        [Fact]
        public void RaiseAHill_ThenScrubBackToBeforeYouRaisedIt()
        {
            var annals = new Annalist();
            VoxelWorld world = World();

            // A flat plain at y = 40, laid down before history starts.
            for (int x = 90; x < 110; x++)
                for (int z = 90; z < 110; z++)
                    world.Store.SetRaw(x, 40, z, Soil);

            world.EndTick(0);
            ChunkStore before = world.AsOf(0);

            // Year 12: the god raises a hill.
            RecordId raised = annals.Write(12, Symbol.For("god.raised-ridge"), Symbol.None,
                                           new Int3(100, 40, 100), RecordId.None);
            for (int x = 96; x < 104; x++)
                for (int z = 96; z < 104; z++)
                    for (int y = 41; y <= 46; y++)
                        world.Set(new Int3(x, y, z), Stone, 12, raised);
            world.EndTick(12);

            // The hill is there now.
            Assert.Equal(Stone, world.Get(100, 45, 100));

            // Scrub back: the world of tick 0 has flat ground and no hill.
            ChunkStore then = world.AsOf(0);
            Assert.Equal(VoxelTypes.AirId, then.Get(100, 45, 100));
            Assert.Equal(Soil, then.Get(100, 40, 100));
            Assert.Equal(before.Digest(), then.Digest());

            // And forward again to the year it was raised.
            Assert.Equal(Stone, world.AsOf(12).Get(100, 45, 100));

            // Every one of those voxels can say why it is there.
            IReadOnlyList<VoxelDelta> history = world.Log.HistoryOf(new Int3(100, 45, 100));
            Assert.Single(history);
            Assert.Equal(raised, history[0].Cause);
            Assert.Equal("god.raised-ridge", annals.Get(history[0].Cause).Kind.ToString());
        }

        [Fact]
        public void ReconstructionIsExactAtEveryTickAcrossSnapshotBoundaries()
        {
            // A short interval so the run crosses several snapshots.
            VoxelWorld world = World(snapshotInterval: 10);
            var expected = new Dictionary<long, ulong>();

            for (long tick = 0; tick <= 60; tick++)
            {
                world.Set(new Int3((int)tick, 40, 100), (ushort)((tick % 3) + 1), tick, RecordId.None);
                if (tick % 7 == 0) world.Set(new Int3(200, 40, 200), (ushort)((tick % 2) + 1), tick, RecordId.None);
                world.EndTick(tick);
                expected[tick] = world.Store.Digest();
            }

            Assert.True(world.Log.SnapshotCount > 3, "the run should have crossed several snapshots");

            for (long tick = 0; tick <= 60; tick++)
                Assert.Equal(expected[tick], world.AsOf(tick).Digest());
        }

        [Fact]
        public void ReconstructingBeforeTheFirstSnapshotStartsFromAnEmptyWorld()
        {
            VoxelWorld world = World(snapshotInterval: 1000);
            world.Set(new Int3(10, 10, 10), Stone, 50, RecordId.None);
            world.EndTick(50);

            Assert.Equal(VoxelTypes.AirId, world.AsOf(49).Get(10, 10, 10));
            Assert.Equal(Stone, world.AsOf(50).Get(10, 10, 10));
        }

        [Fact]
        public void HistoryOfAVoxelReadsAsDatableLayers()
        {
            VoxelWorld world = World();
            var at = new Int3(50, 40, 50);

            world.Set(at, Stone, 10, RecordId.None);
            world.Set(at, Soil, 120, RecordId.None);
            world.Set(at, VoxelTypes.AirId, 400, RecordId.None);

            IReadOnlyList<VoxelDelta> history = world.Log.HistoryOf(at);
            Assert.Equal(3, history.Count);
            Assert.Equal(10, history[0].Tick);
            Assert.Equal(Stone, history[0].NewType);
            Assert.Equal(Soil, history[1].NewType);
            Assert.Equal(VoxelTypes.AirId, history[2].NewType);
            Assert.Equal(Soil, history[2].OldType);   // what was removed
        }

        [Fact]
        public void DeltasAreAppendedInTickOrder()
        {
            VoxelWorld world = World();
            world.Set(new Int3(1, 1, 1), Stone, 10, RecordId.None);
            Assert.Throws<System.ArgumentException>(() =>
                world.Set(new Int3(2, 2, 2), Stone, 9, RecordId.None));
        }

        [Fact]
        public void Digest_IsIdenticalForIdenticalHistories()
        {
            Assert.Equal(BuildHistory().Log.Digest(), BuildHistory().Log.Digest());

            VoxelWorld diverged = BuildHistory();
            diverged.Set(new Int3(9, 9, 9), Stone, 100, RecordId.None);
            Assert.NotEqual(BuildHistory().Log.Digest(), diverged.Log.Digest());
        }

        static VoxelWorld BuildHistory()
        {
            VoxelWorld world = World();
            for (int i = 0; i < 50; i++)
                world.Set(new Int3(i, 40, i), (ushort)((i % 3) + 1), i, RecordId.None);
            return world;
        }

        /// <summary>
        /// The delta stream should stay small. Part 24 says it is small at the
        /// rate settlements actually build; this pins a number to that so a
        /// system that starts churning voxels every tick is visible.
        /// </summary>
        [Fact]
        public void TheStreamStaysSmallForAPlausibleAmountOfBuilding()
        {
            VoxelWorld world = World();
            var rng = new RngStream(StableHash.OfString("test.building"));

            // Roughly a settlement's worth of construction: 200 structures,
            // each a few hundred voxels, spread over 300 years.
            long tick = 0;
            for (int structure = 0; structure < 200; structure++)
            {
                tick += 500;
                int ox = 100 + rng.NextInt(300), oz = 100 + rng.NextInt(300);
                for (int dx = 0; dx < 7; dx++)
                    for (int dz = 0; dz < 7; dz++)
                        for (int dy = 0; dy < 5; dy++)
                            world.Set(new Int3(ox + dx, 41 + dy, oz + dz), Stone, tick, RecordId.None);
            }

            long bytes = (long)world.Log.Count * 20; // sizeof VoxelDelta, near enough
            Assert.True(bytes < 2L * 1024 * 1024,
                "delta stream was " + bytes / 1024 + " KB for 200 structures");
        }
    }
}
