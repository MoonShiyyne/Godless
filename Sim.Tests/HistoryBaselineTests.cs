using System.IO;
using Godless.Sim.Annals;
using Godless.Sim.Content;
using Godless.Sim.Core;
using Godless.Sim.Harness;
using Godless.Sim.Save;
using Godless.Sim.Voxels;
using Godless.Sim.World;
using Xunit;

namespace Godless.Sim.Tests
{
    /// <summary>
    /// Two holes in history found while designing S07's scrub-back. Snapshots
    /// are taken at the end of a tick, so reconstruction treats every delta at
    /// or before a snapshot's tick as already baked in. A write that lands on a
    /// tick that has already been snapshotted is therefore silently lost; and a
    /// loaded world that never takes a baseline cannot be scrubbed back at all.
    /// </summary>
    public class HistoryBaselineTests
    {
        static ContentDatabase Shipped()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Assets", "Content"))) dir = dir.Parent;
            return ContentLoader.Load(new DirectoryContentSource(Path.Combine(dir.FullName, "Assets", "Content"))).Database;
        }

        [Fact]
        public void AWriteOnAnAlreadySnapshottedTickIsRefusedRatherThanLost()
        {
            var world = new Godless.Sim.Deltas.VoxelWorld(new ChunkStore(), new Godless.Sim.Deltas.DeltaLog(10));
            world.Set(new Int3(1, 1, 1), 1, 5, RecordId.None);
            world.EndTick(5);   // first EndTick always snapshots

            var e = Assert.Throws<System.ArgumentException>(() =>
                world.Set(new Int3(2, 2, 2), 1, 5, RecordId.None));
            Assert.Contains("snapshot", e.Message);

            // Refused means untouched: not in the world, not in the log.
            Assert.Equal(VoxelTypes.AirId, world.Get(new Int3(2, 2, 2)));
            Assert.Equal(1, world.Log.Count);

            Assert.True(world.Set(new Int3(2, 2, 2), 1, 6, RecordId.None));
        }

        static SimWorld Played(ContentDatabase content)
        {
            VoxelTypes types = VoxelTypes.FromContent(content);
            var world = new SimWorld(11, content, types);
            world.Island = TestIslands.Generate(world.Voxels.Store, world.Streams, BiomeTable.FromContent(content), types);
            world.BeginHistory();

            ushort granite = types.IdOf(Symbol.For("voxel.granite"));
            for (int year = 1; year <= 4; year++)
            {
                world.RunYears(year);
                RecordId cause = world.Annals.Write(world.Clock.Tick, Symbol.For("god.raised-ground"), RecordId.None);
                world.Clock.Advance();
                for (int dy = 0; dy < 4; dy++)
                    world.Voxels.Set(new Int3(210 + year, 140 + dy, 200), granite, world.Clock.Tick, cause);
                world.Voxels.EndTick(world.Clock.Tick);
            }
            return world;
        }

        /// <summary>
        /// S07's tell, through a save: raise things, save, load, and the
        /// loaded world can still be scrubbed back to the untouched island.
        /// </summary>
        [Fact]
        public void ALoadedWorldCanStillBeScrubbedBackToItsIsland()
        {
            ContentDatabase content = Shipped();
            SimWorld original = Played(content);

            using var buffer = new MemoryStream();
            SaveGame.Write(buffer, original);
            buffer.Position = 0;
            SimWorld restored = SaveGame.Read(buffer, content, BiomeTable.FromContent(content), VoxelTypes.FromContent(content));

            ulong untouched = original.Voxels.AsOf(0).Digest();
            Assert.Equal(untouched, restored.Voxels.AsOf(0).Digest());

            // And the history in between agrees: before the first stroke, the
            // middle, and the present. (Every recorded tick proved the same
            // thing at the cost of two whole-island reconstructions each — two
            // minutes of the suite for no extra coverage.)
            var ticks = new System.Collections.Generic.List<long>();
            foreach (var d in original.Voxels.Log.All()) ticks.Add(d.Tick);
            long[] probes = { ticks[0] - 1, ticks[ticks.Count / 2], ticks[ticks.Count - 1] };
            foreach (long t in probes)
                Assert.Equal(original.Voxels.AsOf(t).Digest(), restored.Voxels.AsOf(t).Digest());
        }
    }
}
