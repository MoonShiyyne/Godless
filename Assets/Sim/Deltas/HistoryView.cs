using System.Collections.Generic;
using Godless.Sim.Core;
using Godless.Sim.Voxels;

namespace Godless.Sim.Deltas
{
    /// <summary>
    /// A copy of the world that can be moved to any past tick without touching
    /// the live one. The timeline slider's model (S07, and S54a's scrub).
    ///
    /// DeltaLog.Reconstruct rebuilds a year from the nearest snapshot, which
    /// is right for a jump but wasteful for a slider: every drag would clone
    /// the island and redraw all of it. This walks from wherever the view is
    /// now — backward by restoring each delta's old type, forward by applying
    /// its new one — and reports exactly which voxels it touched, so the
    /// renderer redraws the hill that changed and nothing else.
    ///
    /// Read-only with respect to history. The past can be watched, not edited;
    /// the god acts only in the present.
    /// </summary>
    public sealed class HistoryView
    {
        readonly DeltaLog _log;
        readonly ChunkStore _store;
        long _tick;

        /// <summary>Starts as a copy of the live world at the present tick.</summary>
        public HistoryView(ChunkStore live, DeltaLog log, long presentTick)
        {
            _store = live.Clone();
            _log = log;
            _tick = presentTick;
        }

        public ChunkStore Store { get { return _store; } }
        public long Tick { get { return _tick; } }

        /// <summary>
        /// Moves the view to the end of the given tick. Every voxel it changes
        /// is appended to <paramref name="changed"/>, possibly more than once.
        /// </summary>
        public void Seek(long tick, List<Int3> changed)
        {
            IReadOnlyList<VoxelDelta> deltas = _log.All();

            if (tick < _tick)
            {
                // Backward: undo, newest first, everything after the target.
                int from = FirstAfter(deltas, tick);
                int to = FirstAfter(deltas, _tick) - 1;
                for (int i = to; i >= from; i--) Apply(deltas[i], deltas[i].OldType, changed);
            }
            else if (tick > _tick)
            {
                // Forward: redo, oldest first, up to and including the target.
                int from = FirstAfter(deltas, _tick);
                int to = FirstAfter(deltas, tick) - 1;
                for (int i = from; i <= to; i++) Apply(deltas[i], deltas[i].NewType, changed);
            }

            _tick = tick;
        }

        void Apply(VoxelDelta d, ushort type, List<Int3> changed)
        {
            _store.SetByIndex(d.ChunkIndex, d.VoxelIndex, type);
            if (changed == null) return;

            int ci = d.ChunkIndex;
            int cy = ci / (ChunkStore.ChunksX * ChunkStore.ChunksZ);
            int rem = ci % (ChunkStore.ChunksX * ChunkStore.ChunksZ);
            int cz = rem / ChunkStore.ChunksX;
            int cx = rem % ChunkStore.ChunksX;
            int vi = d.VoxelIndex;
            changed.Add(new Int3(cx * Chunk.Size + (vi & 31),
                                 cy * Chunk.Size + ((vi >> 10) & 31),
                                 cz * Chunk.Size + ((vi >> 5) & 31)));
        }

        /// <summary>Index of the first delta whose tick is after the given one.</summary>
        static int FirstAfter(IReadOnlyList<VoxelDelta> deltas, long tick)
        {
            int lo = 0, hi = deltas.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (deltas[mid].Tick <= tick) lo = mid + 1; else hi = mid;
            }
            return lo;
        }
    }
}
