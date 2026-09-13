using Godless.Sim.Core;
using Godless.Sim.Voxels;
using Xunit;

namespace Godless.Sim.Tests
{
    public class ChunkTests
    {
        [Fact]
        public void StartsUniformAndCostsNoIndexArray()
        {
            var chunk = new Chunk(VoxelTypes.AirId);
            Assert.True(chunk.IsUniform);
            Assert.Equal(0, chunk.BitsPerIndex);
            Assert.Equal(VoxelTypes.AirId, chunk.Get(5, 5, 5));
            Assert.True(chunk.MemoryBytes < 128, "a uniform chunk must not allocate voxel storage");
        }

        [Fact]
        public void WidensTheIndexOnlyAsThePaletteGrows()
        {
            var chunk = new Chunk(0);

            chunk.Set(0, 0, 0, 1);
            Assert.Equal(1, chunk.BitsPerIndex);   // 2 types

            chunk.Set(1, 0, 0, 2);
            chunk.Set(2, 0, 0, 3);
            Assert.Equal(2, chunk.BitsPerIndex);   // 4 types

            for (ushort t = 4; t <= 8; t++) chunk.Set(t, 0, 0, t);
            Assert.Equal(4, chunk.BitsPerIndex);   // up to 16 types

            for (ushort t = 9; t <= 20; t++) chunk.Set(t, 1, 0, t);
            Assert.Equal(8, chunk.BitsPerIndex);
        }

        [Fact]
        public void RepackingPreservesEveryVoxelAlreadyWritten()
        {
            var chunk = new Chunk(0);

            // Write a pattern that forces three widenings, then verify all of it.
            for (int i = 0; i < 40; i++)
                chunk.Set(i & 31, (i >> 5) & 31, 0, (ushort)(i + 1));

            for (int i = 0; i < 40; i++)
                Assert.Equal((ushort)(i + 1), chunk.Get(i & 31, (i >> 5) & 31, 0));
        }

        [Fact]
        public void CollapsesBackToUniformWhenOnlyOneTypeRemains()
        {
            var chunk = new Chunk(0);
            chunk.Set(4, 4, 4, 7);
            Assert.False(chunk.IsUniform);

            chunk.Set(4, 4, 4, 0);
            Assert.True(chunk.IsUniform);
            Assert.Equal(0, chunk.UniformType);
            Assert.True(chunk.MemoryBytes < 128, "collapsing must release the index array");
        }

        [Fact]
        public void EveryCellIsAddressableAndIndependent()
        {
            var chunk = new Chunk(0);
            // A deterministic pattern over the whole volume.
            for (int y = 0; y < Chunk.Size; y++)
                for (int z = 0; z < Chunk.Size; z++)
                    for (int x = 0; x < Chunk.Size; x++)
                        chunk.Set(x, y, z, (ushort)(((x + y * 3 + z * 7) % 5) + 1));

            for (int y = 0; y < Chunk.Size; y++)
                for (int z = 0; z < Chunk.Size; z++)
                    for (int x = 0; x < Chunk.Size; x++)
                        Assert.Equal((ushort)(((x + y * 3 + z * 7) % 5) + 1), chunk.Get(x, y, z));
        }

        [Fact]
        public void IndicesAreDistinctForEveryCell()
        {
            var seen = new System.Collections.Generic.HashSet<int>();
            for (int y = 0; y < Chunk.Size; y++)
                for (int z = 0; z < Chunk.Size; z++)
                    for (int x = 0; x < Chunk.Size; x++)
                        Assert.True(seen.Add(Chunk.Index(x, y, z)), "index collision at " + x + "," + y + "," + z);
            Assert.Equal(Chunk.Volume, seen.Count);
        }
    }

    public class ChunkStoreTests
    {
        /// <summary>Thirty-two bytes a column: about six times what an island surface costs.</summary>
        static readonly long Budget = ChunkStore.SizeX * (long)ChunkStore.SizeZ * 32L;

        readonly Xunit.Abstractions.ITestOutputHelper _out;
        public ChunkStoreTests(Xunit.Abstractions.ITestOutputHelper output) { _out = output; }

        [Fact]
        public void TheIslandDividesIntoChunksExactly()
        {
            Assert.Equal(ChunkStore.SizeX / Chunk.Size, ChunkStore.ChunksX);
            Assert.Equal(5, ChunkStore.ChunksY);   // 160 / 32 — the reason chunks are not 64^3
            Assert.Equal(ChunkStore.SizeZ / Chunk.Size, ChunkStore.ChunksZ);
            Assert.Equal(ChunkStore.ChunksX * ChunkStore.ChunksY * ChunkStore.ChunksZ, ChunkStore.ChunkCount);
        }

        [Fact]
        public void AFreshWorldIsAirAndAllocatesNothing()
        {
            var store = new ChunkStore();
            Assert.Equal(VoxelTypes.AirId, store.Get(256, 80, 256));
            Assert.Equal(0, store.AllocatedChunks);
        }

        [Fact]
        public void WritingAirToEmptySpaceStillAllocatesNothing()
        {
            var store = new ChunkStore();
            Assert.False(store.SetRaw(10, 10, 10, VoxelTypes.AirId));
            Assert.Equal(0, store.AllocatedChunks);
        }

        [Fact]
        public void ReadsAndWritesRoundTripAcrossChunkBoundaries()
        {
            var store = new ChunkStore();
            Int3[] places =
            {
                new Int3(0, 0, 0), new Int3(31, 31, 31), new Int3(32, 32, 32),
                new Int3(511, 159, 511), new Int3(256, 80, 256), new Int3(63, 0, 64),
            };

            for (int i = 0; i < places.Length; i++)
                Assert.True(store.SetRaw(places[i], (ushort)(i + 1)));

            for (int i = 0; i < places.Length; i++)
                Assert.Equal((ushort)(i + 1), store.Get(places[i]));
        }

        [Fact]
        public void OutOfBoundsReadsAsAirAndRefusesToWrite()
        {
            var store = new ChunkStore();
            Assert.Equal(VoxelTypes.AirId, store.Get(-1, 0, 0));
            Assert.Equal(VoxelTypes.AirId, store.Get(ChunkStore.SizeX, 0, 0));
            Assert.Equal(VoxelTypes.AirId, store.Get(0, 160, 0));

            Assert.False(store.SetRaw(-1, 0, 0, 1));
            Assert.False(store.SetRaw(0, 0, ChunkStore.SizeZ, 1));
            Assert.Equal(0, store.AllocatedChunks);
        }

        /// <summary>
        /// S03's tell: the whole island is resident and editable inside the
        /// memory budget. A realistic island is a terrain surface — mostly
        /// solid below, mostly air above, a few materials — and the chunks
        /// that are entirely one or the other must cost almost nothing.
        /// </summary>
        [Fact]
        public void AFullIslandFitsTheMemoryBudget()
        {
            var store = new ChunkStore();
            var rng = new RngStream(StableHash.OfString("test.island"));

            // A coarse height field, then fill below it: stone deep, soil near
            // the surface, with a little variation so chunks are not trivially
            // uniform.
            for (int x = 0; x < ChunkStore.SizeX; x++)
                for (int z = 0; z < ChunkStore.SizeZ; z++)
                {
                    int height = 40 + ((x / 37 + z / 41) % 9) + (int)(rng.NextInt(3));
                    for (int y = 0; y < height; y++)
                        store.SetRaw(x, y, z, (ushort)(y > height - 4 ? 2 : 1));
                }

            long bytes = store.MemoryBytes;
            long naive = (long)ChunkStore.SizeX * ChunkStore.SizeY * ChunkStore.SizeZ * 4; // int per voxel

            _out.WriteLine("island: " + bytes / 1024 + " KB across " + store.AllocatedChunks
                           + "/" + ChunkStore.ChunkCount + " chunks; naive int-per-voxel would be "
                           + naive / 1024 / 1024 + " MB (" + (naive / bytes) + "x)");

            // Measured at about 2 MB when this landed at 512 columns square,
            // and 8 MB at 1024. The budget scales with the map for the same
            // reason it existed: loose enough for more materials and a rougher
            // surface, tight enough that losing uniform-chunk elision fails
            // here rather than in a profiler six months from now.
            Assert.True(bytes < Budget,
                "island cost " + (bytes / 1024) + " KB, budget is " + Budget / 1024 + " KB");
            Assert.True(bytes * 16 < naive,
                "palette compression should beat an int per voxel by well over 16x; got "
                + bytes + " against " + naive);

            // And it is still correct after all that.
            Assert.Equal(1, store.Get(0, 0, 0));
            Assert.Equal(VoxelTypes.AirId, store.Get(0, 159, 0));
        }

        [Fact]
        public void Digest_IsIdenticalForIdenticalWorlds()
        {
            Assert.Equal(BuildWorld().Digest(), BuildWorld().Digest());

            ChunkStore changed = BuildWorld();
            changed.SetRaw(100, 50, 100, 9);
            Assert.NotEqual(BuildWorld().Digest(), changed.Digest());
        }

        /// <summary>
        /// Found through S07's history view: two worlds with identical
        /// contents digested differently, because one had allocated a chunk,
        /// emptied it and kept it. "Byte-identical" is G0's promise and the
        /// save loader's integrity check, so it has to mean the contents.
        /// </summary>
        [Fact]
        public void TheSameContentsDigestTheSameWhateverTheirHistory()
        {
            var untouched = new ChunkStore();
            untouched.SetRaw(1, 1, 1, 3);

            var dugAndRefilled = new ChunkStore();
            dugAndRefilled.SetRaw(1, 1, 1, 3);
            dugAndRefilled.SetRaw(300, 100, 300, 5);      // allocate a far chunk...
            dugAndRefilled.SetRaw(300, 100, 300, VoxelTypes.AirId); // ...and empty it

            Assert.Equal(untouched.Digest(), dugAndRefilled.Digest());
            Assert.Equal(untouched.AllocatedChunks, dugAndRefilled.AllocatedChunks);
        }

        [Fact]
        public void AChunkEmptiedBackToAirReturnsItsMemory()
        {
            var store = new ChunkStore();
            store.SetRaw(40, 40, 40, 2);
            Assert.Equal(1, store.AllocatedChunks);
            store.SetRaw(40, 40, 40, VoxelTypes.AirId);
            Assert.Equal(0, store.AllocatedChunks);
            Assert.Equal(VoxelTypes.AirId, store.Get(40, 40, 40));
        }

        static ChunkStore BuildWorld()
        {
            var store = new ChunkStore();
            for (int i = 0; i < 200; i++)
                store.SetRaw(i % ChunkStore.SizeX, i % 160, (i * 7) % ChunkStore.SizeZ, (ushort)((i % 4) + 1));
            return store;
        }
    }

    public class VoxelTypesTests
    {
        [Fact]
        public void AirIsAlwaysZeroSoAnEmptyChunkIsFree()
        {
            VoxelTypes types = VoxelTypes.Build(new[] { Symbol.For("voxel.granite"), Symbol.For("voxel.oak") });
            Assert.Equal(VoxelTypes.AirId, types.IdOf(VoxelTypes.Air));
            Assert.Equal(3, types.Count);
        }

        [Fact]
        public void IdsDependOnlyOnTheSetOfTypes_NotOnDeclarationOrder()
        {
            var a = VoxelTypes.Build(new[] { Symbol.For("voxel.granite"), Symbol.For("voxel.oak"), Symbol.For("voxel.mud") });
            var b = VoxelTypes.Build(new[] { Symbol.For("voxel.mud"), Symbol.For("voxel.granite"), Symbol.For("voxel.oak") });

            Assert.Equal(a.IdOf(Symbol.For("voxel.granite")), b.IdOf(Symbol.For("voxel.granite")));
            Assert.Equal(a.IdOf(Symbol.For("voxel.mud")), b.IdOf(Symbol.For("voxel.mud")));
        }

        [Fact]
        public void IdsRoundTripThroughSymbols_WhichIsWhatSavesStore()
        {
            VoxelTypes types = VoxelTypes.Build(new[] { Symbol.For("voxel.granite"), Symbol.For("voxel.oak") });
            ushort id = types.IdOf(Symbol.For("voxel.oak"));
            Assert.Equal(Symbol.For("voxel.oak"), types.SymbolOf(id));
        }

        [Fact]
        public void AnUndeclaredTypeIsAnError()
        {
            VoxelTypes types = VoxelTypes.Build(new[] { Symbol.For("voxel.granite") });
            Assert.Throws<System.ArgumentException>(() => types.IdOf(Symbol.For("voxel.never-declared")));
        }
    }
}
