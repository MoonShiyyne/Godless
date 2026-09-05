using Godless.Sim.Core;

namespace Godless.Sim.Voxels
{
    /// <summary>
    /// The island: 512 x 160 x 512 voxels as a fixed grid of 32^3 chunks.
    ///
    /// Bounded on purpose. Part 23 is explicit that there is no infinite
    /// streaming, and "one island, one civilization, one history" is a
    /// stronger frame than endless terrain — it is also what lets everything
    /// be simulated rather than sampled.
    ///
    /// Chunks are allocated on first write. An unallocated chunk reads as
    /// air, so a fresh world costs one reference per chunk and nothing else.
    /// </summary>
    public sealed class ChunkStore
    {
        public const int SizeX = 512;
        public const int SizeY = 160;
        public const int SizeZ = 512;

        public const int ChunksX = SizeX / Chunk.Size; // 16
        public const int ChunksY = SizeY / Chunk.Size; // 5
        public const int ChunksZ = SizeZ / Chunk.Size; // 16
        public const int ChunkCount = ChunksX * ChunksY * ChunksZ; // 1280

        readonly Chunk[] _chunks = new Chunk[ChunkCount];

        public static bool InBounds(int x, int y, int z)
        {
            return x >= 0 && x < SizeX && y >= 0 && y < SizeY && z >= 0 && z < SizeZ;
        }

        public static bool InBounds(Int3 at) { return InBounds(at.X, at.Y, at.Z); }

        public static int ChunkIndex(int cx, int cy, int cz)
        {
            return (cy * ChunksZ + cz) * ChunksX + cx;
        }

        /// <summary>Reads out of bounds as air, so callers need not guard every edge.</summary>
        public ushort Get(int x, int y, int z)
        {
            if (!InBounds(x, y, z)) return VoxelTypes.AirId;

            Chunk chunk = _chunks[ChunkIndex(x >> 5, y >> 5, z >> 5)];
            if (chunk == null) return VoxelTypes.AirId;
            return chunk.Get(x & 31, y & 31, z & 31);
        }

        public ushort Get(Int3 at) { return Get(at.X, at.Y, at.Z); }

        /// <summary>
        /// Writes a voxel WITHOUT recording why.
        ///
        /// Named to be awkward on purpose. L3 requires every voxel change to
        /// carry the event that caused it, and S04 provides the sanctioned
        /// path that records one. This entry point exists for the two cases
        /// with no cause to record — generating the island before history
        /// starts, and restoring a save — and using it anywhere else is how
        /// provenance quietly stops being true.
        /// </summary>
        public bool SetRaw(int x, int y, int z, ushort type)
        {
            if (!InBounds(x, y, z)) return false;

            int ci = ChunkIndex(x >> 5, y >> 5, z >> 5);
            Chunk chunk = _chunks[ci];

            if (chunk == null)
            {
                if (type == VoxelTypes.AirId) return false; // already air
                chunk = new Chunk(VoxelTypes.AirId);
                _chunks[ci] = chunk;
            }

            chunk.Set(x & 31, y & 31, z & 31, type);
            return true;
        }

        public bool SetRaw(Int3 at, ushort type) { return SetRaw(at.X, at.Y, at.Z, type); }

        public Chunk ChunkAt(int cx, int cy, int cz)
        {
            if (cx < 0 || cx >= ChunksX || cy < 0 || cy >= ChunksY || cz < 0 || cz >= ChunksZ) return null;
            return _chunks[ChunkIndex(cx, cy, cz)];
        }

        public int AllocatedChunks
        {
            get
            {
                int n = 0;
                for (int i = 0; i < _chunks.Length; i++) if (_chunks[i] != null) n++;
                return n;
            }
        }

        /// <summary>
        /// S03's tell: the whole island is resident and editable inside the
        /// memory budget. This is the number that claim is measured against.
        /// </summary>
        public long MemoryBytes
        {
            get
            {
                long bytes = _chunks.Length * 8; // the reference array
                for (int i = 0; i < _chunks.Length; i++)
                    if (_chunks[i] != null) bytes += _chunks[i].MemoryBytes;
                return bytes;
            }
        }

        /// <summary>
        /// A digest of the whole world. Chunk order is fixed by index, so two
        /// runs that generated the same island agree on this exactly.
        /// </summary>
        public ulong Digest()
        {
            var digest = new Digest();
            for (int ci = 0; ci < _chunks.Length; ci++)
            {
                Chunk chunk = _chunks[ci];
                if (chunk == null) continue;

                digest.Add(ci);
                if (chunk.IsUniform) { digest.Add(chunk.UniformType); continue; }

                for (int y = 0; y < Chunk.Size; y++)
                    for (int z = 0; z < Chunk.Size; z++)
                        for (int x = 0; x < Chunk.Size; x++)
                            digest.Add(chunk.Get(x, y, z));
            }
            return digest.Value;
        }
    }
}
