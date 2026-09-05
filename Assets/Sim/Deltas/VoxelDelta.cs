using Godless.Sim.Annals;

namespace Godless.Sim.Deltas
{
    /// <summary>
    /// One voxel change, and why.
    ///
    /// Part 24's shape exactly: tick, chunk, index, old id, new id, and the
    /// record that caused it. The doc says intentId; a RecordId is the more
    /// general form, since a build intent has an annal record of its own and
    /// so does a flood, a collapse and a god's hand.
    ///
    /// Twenty bytes. At the rate settlements actually build, the whole stream
    /// for a long run is small, and it clusters both spatially and in time
    /// because construction does.
    /// </summary>
    public readonly struct VoxelDelta
    {
        public readonly long Tick;
        public readonly ushort ChunkIndex;   // 1280 chunks
        public readonly ushort VoxelIndex;   // 32768 voxels per chunk
        public readonly ushort OldType;
        public readonly ushort NewType;
        public readonly RecordId Cause;

        public VoxelDelta(long tick, ushort chunkIndex, ushort voxelIndex,
                          ushort oldType, ushort newType, RecordId cause)
        {
            Tick = tick;
            ChunkIndex = chunkIndex;
            VoxelIndex = voxelIndex;
            OldType = oldType;
            NewType = newType;
            Cause = cause;
        }
    }
}
