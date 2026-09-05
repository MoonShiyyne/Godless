using System.Collections.Generic;
using Godless.Sim.Core;
using Godless.Sim.Voxels;

namespace Godless.Sim.Deltas
{
    /// <summary>
    /// Every voxel change the world has ever seen, plus periodic snapshots so
    /// any past year can be rebuilt without replaying from the beginning.
    ///
    /// Part 24 is emphatic that this belongs in M0: cheap before anything
    /// writes voxels, painful once twenty systems do. It is also the best
    /// debugging tool the project will ever have, because it turns "reproduce
    /// the bug" into "replay year 84".
    ///
    /// What it unlocks later is all one mechanism: the timeline scrub, the
    /// timelapse export, per-structure timestamps, and the stratigraphic
    /// probe reading a column of ground as datable layers.
    /// </summary>
    public sealed class DeltaLog
    {
        /// <summary>
        /// Ticks between snapshots. At the default clock that is roughly
        /// twenty-five years — often enough that a seek replays a bounded
        /// number of deltas, rare enough that snapshots stay a rounding error
        /// against a 2 MB world.
        /// </summary>
        public const long DefaultSnapshotInterval = 25L * 360L * 4L;

        readonly List<VoxelDelta> _deltas = new List<VoxelDelta>();
        readonly List<long> _snapshotTicks = new List<long>();
        readonly List<ChunkStore> _snapshots = new List<ChunkStore>();
        readonly long _snapshotInterval;

        long _lastSnapshotTick = long.MinValue;

        public DeltaLog() : this(DefaultSnapshotInterval) { }

        public DeltaLog(long snapshotInterval)
        {
            if (snapshotInterval <= 0)
                throw new System.ArgumentOutOfRangeException(nameof(snapshotInterval));
            _snapshotInterval = snapshotInterval;
        }

        public int Count { get { return _deltas.Count; } }
        public int SnapshotCount { get { return _snapshots.Count; } }
        public IReadOnlyList<VoxelDelta> All() { return _deltas; }

        internal void Append(VoxelDelta delta)
        {
            if (_deltas.Count > 0 && delta.Tick < _deltas[_deltas.Count - 1].Tick)
                throw new System.ArgumentException("deltas are appended in tick order");
            _deltas.Add(delta);
        }

        /// <summary>
        /// Takes a snapshot if enough ticks have passed. Called by VoxelWorld
        /// as the clock advances; the first call always snapshots, so there is
        /// a baseline to replay forward from.
        /// </summary>
        public void MaybeSnapshot(long tick, ChunkStore world)
        {
            if (_lastSnapshotTick != long.MinValue && tick - _lastSnapshotTick < _snapshotInterval) return;
            Snapshot(tick, world);
        }

        public void Snapshot(long tick, ChunkStore world)
        {
            if (_snapshotTicks.Count > 0 && tick < _snapshotTicks[_snapshotTicks.Count - 1])
                throw new System.ArgumentException("snapshots are taken in tick order");
            _snapshotTicks.Add(tick);
            _snapshots.Add(world.Clone());
            _lastSnapshotTick = tick;
        }

        /// <summary>
        /// The world as it stood at the end of the given tick.
        ///
        /// This is the timeline slider: not a video, the actual world state,
        /// navigable from any camera angle. Finds the newest snapshot at or
        /// before the tick and replays deltas forward from it.
        /// </summary>
        public ChunkStore Reconstruct(long tick)
        {
            int snapshot = NewestSnapshotAtOrBefore(tick);

            ChunkStore world;
            long from;
            if (snapshot < 0) { world = new ChunkStore(); from = long.MinValue; }
            else { world = _snapshots[snapshot].Clone(); from = _snapshotTicks[snapshot]; }

            // Replay everything after the snapshot's tick, up to and including
            // the requested one. Deltas at the snapshot tick itself are already
            // baked in, because the snapshot is taken after that tick's writes.
            for (int i = 0; i < _deltas.Count; i++)
            {
                VoxelDelta d = _deltas[i];
                if (from != long.MinValue && d.Tick <= from) continue;
                if (d.Tick > tick) break;
                world.SetByIndex(d.ChunkIndex, d.VoxelIndex, d.NewType);
            }
            return world;
        }

        int NewestSnapshotAtOrBefore(long tick)
        {
            int best = -1;
            for (int i = 0; i < _snapshotTicks.Count; i++)
            {
                if (_snapshotTicks[i] > tick) break;
                best = i;
            }
            return best;
        }

        /// <summary>
        /// Every change to one voxel, oldest first — the per-structure
        /// timestamps the chronicle reads, at voxel granularity.
        /// </summary>
        public IReadOnlyList<VoxelDelta> HistoryOf(Int3 at)
        {
            var hits = new List<VoxelDelta>();
            if (!ChunkStore.InBounds(at)) return hits;

            ushort chunkIndex = (ushort)ChunkStore.ChunkIndex(at.X >> 5, at.Y >> 5, at.Z >> 5);
            ushort voxelIndex = (ushort)Chunk.Index(at.X & 31, at.Y & 31, at.Z & 31);

            foreach (VoxelDelta d in _deltas)
                if (d.ChunkIndex == chunkIndex && d.VoxelIndex == voxelIndex) hits.Add(d);
            return hits;
        }

        public IReadOnlyList<VoxelDelta> InTickRange(long fromInclusive, long toInclusive)
        {
            var hits = new List<VoxelDelta>();
            foreach (VoxelDelta d in _deltas)
                if (d.Tick >= fromInclusive && d.Tick <= toInclusive) hits.Add(d);
            return hits;
        }

        public ulong Digest()
        {
            var digest = new Digest();
            foreach (VoxelDelta d in _deltas)
            {
                digest.Add(d.Tick);
                digest.Add(d.ChunkIndex);
                digest.Add(d.VoxelIndex);
                digest.Add(d.OldType);
                digest.Add(d.NewType);
                digest.Add(d.Cause.Index);
            }
            return digest.Value;
        }
    }
}
