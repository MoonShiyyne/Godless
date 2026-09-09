using Godless.Sim.Annals;
using Godless.Sim.Core;
using Godless.Sim.Voxels;

namespace Godless.Sim.Deltas
{
    /// <summary>
    /// The only sanctioned way to change the world once history has started.
    ///
    /// This is L3 made structural. Set requires a cause, so a voxel cannot be
    /// changed without naming the record that produced it, and the delta log
    /// gets its provenance for free rather than by convention. ChunkStore's
    /// own SetRaw records nothing and is named to be awkward for exactly this
    /// reason: worldgen and save restore use it, and nothing else should.
    ///
    /// The chronicle, the Silence scoring, the stratigraphic probe and the
    /// timelapse all read what this writes.
    /// </summary>
    public sealed class VoxelWorld
    {
        readonly ChunkStore _store;
        readonly DeltaLog _log;

        public VoxelWorld(ChunkStore store, DeltaLog log)
        {
            _store = store;
            _log = log;
        }

        public ChunkStore Store { get { return _store; } }
        public DeltaLog Log { get { return _log; } }

        public ushort Get(Int3 at) { return _store.Get(at); }
        public ushort Get(int x, int y, int z) { return _store.Get(x, y, z); }

        /// <summary>
        /// Changes one voxel and records why. Returns false if the write was
        /// out of bounds or changed nothing — an unchanged voxel writes no
        /// delta, so the log stays a record of history rather than of effort.
        /// </summary>
        public bool Set(Int3 at, ushort type, long tick, RecordId cause)
        {
            if (!ChunkStore.InBounds(at)) return false;

            ushort previous = _store.Get(at);
            if (previous == type) return false;

            _store.SetRaw(at, type);

            _log.Append(new VoxelDelta(
                tick,
                (ushort)ChunkStore.ChunkIndex(at.X >> 5, at.Y >> 5, at.Z >> 5),
                (ushort)Chunk.Index(at.X & 31, at.Y & 31, at.Z & 31),
                previous, type, cause));

            return true;
        }

        public bool Set(int x, int y, int z, ushort type, long tick, RecordId cause)
        {
            return Set(new Int3(x, y, z), type, tick, cause);
        }

        /// <summary>
        /// Restores one delta from a save: applies the voxel and re-appends
        /// the record unchanged, without consulting the current state.
        ///
        /// Separate from Set because Set derives OldType from what is there
        /// now, which during a replay is whatever the previous delta left —
        /// correct, but it would silently paper over a corrupt log instead of
        /// letting the digest check catch it.
        /// </summary>
        public void ReplaySaved(long tick, ushort chunkIndex, ushort voxelIndex,
                                ushort oldType, ushort newType, RecordId cause)
        {
            _store.SetByIndex(chunkIndex, voxelIndex, newType);
            _log.Append(new VoxelDelta(tick, chunkIndex, voxelIndex, oldType, newType, cause));
        }

        /// <summary>
        /// Snapshots if the interval has elapsed. Call once per tick from the
        /// simulation loop, after that tick's writes.
        /// </summary>
        public void EndTick(long tick) { _log.MaybeSnapshot(tick, _store); }

        /// <summary>The world as it stood at a past tick. Nothing here is mutated.</summary>
        public ChunkStore AsOf(long tick) { return _log.Reconstruct(tick); }
    }
}
