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
        public const int SizeX = 1024;
        public const int SizeY = 160;
        public const int SizeZ = 1024;

        public const int ChunksX = SizeX / Chunk.Size; // 32
        public const int ChunksY = SizeY / Chunk.Size; // 5
        public const int ChunksZ = SizeZ / Chunk.Size; // 32
        public const int ChunkCount = ChunksX * ChunksY * ChunksZ; // 5120

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

        /// <summary>
        /// Where a delta's chunk and voxel indices sit in the world. The
        /// renderer needs it to know which chunk a simulated change dirtied.
        /// </summary>
        public static Int3 PositionOf(int chunkIndex, int voxelIndex)
        {
            int cx = chunkIndex % ChunksX;
            int cz = (chunkIndex / ChunksX) % ChunksZ;
            int cy = chunkIndex / (ChunksX * ChunksZ);
            int x = voxelIndex % Chunk.Size;
            int z = (voxelIndex / Chunk.Size) % Chunk.Size;
            int y = voxelIndex / (Chunk.Size * Chunk.Size);
            return new Int3(cx * Chunk.Size + x, cy * Chunk.Size + y, cz * Chunk.Size + z);
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
            ReleaseIfEmpty(ci, chunk);
            return true;
        }

        /// <summary>
        /// A chunk emptied back to nothing but air is freed. That returns its
        /// memory, and it keeps "the same world" meaning the same contents: a
        /// region that was dug out and filled back in is indistinguishable
        /// from one that was never touched, which is what a digest, a save
        /// check and a scrubbed timeline all assume.
        /// </summary>
        void ReleaseIfEmpty(int chunkIndex, Chunk chunk)
        {
            if (chunk.IsUniform && chunk.UniformType == VoxelTypes.AirId) _chunks[chunkIndex] = null;
        }

        public bool SetRaw(Int3 at, ushort type) { return SetRaw(at.X, at.Y, at.Z, type); }

        /// <summary>
        /// The highest y in a column whose voxel type is marked in
        /// <paramref name="matches"/>, or -1 if none is.
        ///
        /// Walks the column a chunk at a time from the top: a missing chunk is
        /// 32 voxels of air skipped in one step, and a uniform chunk is
        /// answered from its single type. Most of an island's sky is missing
        /// chunks, so this is what makes a whole-island height pass cheap.
        /// </summary>
        public int TopMatching(int x, int z, bool[] matches)
        {
            if (x < 0 || x >= SizeX || z < 0 || z >= SizeZ) return -1;
            int cx = x >> 5, cz = z >> 5, lx = x & 31, lz = z & 31;

            for (int cy = ChunksY - 1; cy >= 0; cy--)
            {
                Chunk chunk = _chunks[ChunkIndex(cx, cy, cz)];
                if (chunk == null) continue;

                if (chunk.IsUniform)
                {
                    ushort t = chunk.UniformType;
                    if (t < matches.Length && matches[t]) return cy * Chunk.Size + Chunk.Size - 1;
                    continue;
                }

                for (int ly = Chunk.Size - 1; ly >= 0; ly--)
                {
                    ushort t = chunk.Get(lx, ly, lz);
                    if (t < matches.Length && matches[t]) return cy * Chunk.Size + ly;
                }
            }
            return -1;
        }

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

        /// <summary>A deep copy, for snapshots.</summary>
        public ChunkStore Clone()
        {
            var copy = new ChunkStore();
            for (int i = 0; i < _chunks.Length; i++)
                if (_chunks[i] != null) copy._chunks[i] = _chunks[i].Clone();
            return copy;
        }

        /// <summary>Becomes a deep copy of another store. For tools and tests that start many worlds from one island.</summary>
        public void CopyFrom(ChunkStore other)
        {
            for (int i = 0; i < _chunks.Length; i++)
                _chunks[i] = other._chunks[i] != null ? other._chunks[i].Clone() : null;
        }

        /// <summary>Writes by raw chunk and voxel index. Used by delta replay.</summary>
        public void SetByIndex(int chunkIndex, int voxelIndex, ushort type)
        {
            Chunk chunk = _chunks[chunkIndex];
            if (chunk == null)
            {
                if (type == VoxelTypes.AirId) return;
                chunk = new Chunk(VoxelTypes.AirId);
                _chunks[chunkIndex] = chunk;
            }
            chunk.Set(voxelIndex & 31, (voxelIndex >> 10) & 31, (voxelIndex >> 5) & 31, type);
            ReleaseIfEmpty(chunkIndex, chunk);
        }

        /// <summary>
        /// A digest of the whole world. Chunk order is fixed by index, so two
        /// runs that generated the same island agree on this exactly.
        /// </summary>
        public ulong Digest()
        {
            var digest = new Digest();
            ushort[] buffer = null;
            for (int ci = 0; ci < _chunks.Length; ci++)
            {
                Chunk chunk = _chunks[ci];

                // Content, not allocation: an all-air chunk and a missing one
                // are the same world and must digest the same.
                if (chunk == null || (chunk.IsUniform && chunk.UniformType == VoxelTypes.AirId)) continue;

                digest.Add(ci);
                if (chunk.IsUniform) { digest.AddShort(chunk.UniformType); continue; }

                if (buffer == null) buffer = new ushort[Chunk.Volume];
                chunk.CopyTypesTo(buffer);
                for (int i = 0; i < buffer.Length; i++) digest.AddShort(buffer[i]);
            }
            return digest.Value;
        }
    }
}
