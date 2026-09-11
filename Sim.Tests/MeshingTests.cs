using System.Collections.Generic;
using System.Diagnostics;
using Godless.Meshing;
using Godless.Sim.Content;
using Godless.Sim.Core;
using Godless.Sim.Voxels;
using Godless.Sim.World;
using Xunit;

namespace Godless.Sim.Tests
{
    public class MeshingTests
    {
        readonly Xunit.Abstractions.ITestOutputHelper _out;
        public MeshingTests(Xunit.Abstractions.ITestOutputHelper output) { _out = output; }

        const ushort Stone = 1, Soil = 2, Water = 3;

        static VoxelVisuals Visuals()
        {
            return VoxelVisuals.FromTable(new[]
            {
                default(VoxelVisual),                                                  // air
                new VoxelVisual { Opaque = true, R = 100, G = 100, B = 110 },          // stone
                new VoxelVisual { Opaque = true, R = 140, G = 100, B = 70 },           // soil
                new VoxelVisual { Liquid = true, R = 60, G = 120, B = 180 },           // water
            });
        }

        static (MeshData opaque, MeshData liquid) Mesh(ChunkStore store, int cx = 0, int cy = 0, int cz = 0)
        {
            var opaque = new MeshData();
            var liquid = new MeshData();
            ChunkMesher.Build(store, cx, cy, cz, Visuals(), opaque, liquid);
            return (opaque, liquid);
        }

        [Fact]
        public void ASingleVoxelIsACube()
        {
            var store = new ChunkStore();
            store.SetRaw(5, 5, 5, Stone);

            MeshData m = Mesh(store).opaque;
            Assert.Equal(6, m.QuadCount);
            Assert.Equal(24, m.VertexCount);
            Assert.Equal(36, m.Indices.Count);
        }

        /// <summary>
        /// The greedy part. A solid chunk in open air is six faces of 32x32
        /// voxels each, and every one of them must merge to a single quad —
        /// 6 quads, not 6,144.
        /// </summary>
        [Fact]
        public void ASolidChunkInOpenAirIsSixQuads()
        {
            var store = new ChunkStore();
            for (int y = 0; y < 32; y++)
                for (int z = 0; z < 32; z++)
                    for (int x = 0; x < 32; x++)
                        store.SetRaw(x, y, z, Stone);

            Assert.Equal(6, Mesh(store).opaque.QuadCount);
        }

        [Fact]
        public void TouchingVoxelsHideTheFaceBetweenThem()
        {
            var store = new ChunkStore();
            store.SetRaw(5, 5, 5, Stone);
            store.SetRaw(6, 5, 5, Stone);
            // Same material: the shared face disappears and the long sides merge.
            Assert.Equal(6, Mesh(store).opaque.QuadCount);
        }

        [Fact]
        public void DifferentMaterialsDoNotMergeButStillCullTheirSharedFace()
        {
            var store = new ChunkStore();
            store.SetRaw(5, 5, 5, Stone);
            store.SetRaw(6, 5, 5, Soil);
            // Two ends plus four long sides that cannot merge across material.
            Assert.Equal(10, Mesh(store).opaque.QuadCount);
        }

        [Fact]
        public void FacesAgainstANeighbouringChunkAreCulledAgainstWhatIsReallyThere()
        {
            var store = new ChunkStore();
            store.SetRaw(31, 5, 5, Stone);   // last column of chunk 0
            store.SetRaw(32, 5, 5, Stone);   // first column of chunk 1

            // The +x face of (31,5,5) is hidden by the voxel in the next chunk.
            Assert.Equal(5, Mesh(store, 0, 0, 0).opaque.QuadCount);
            Assert.Equal(5, Mesh(store, 1, 0, 0).opaque.QuadCount);
        }

        [Fact]
        public void AnEmptyChunkBuildsNothing()
        {
            var store = new ChunkStore();
            var opaque = new MeshData();
            Assert.False(ChunkMesher.Build(store, 3, 2, 3, Visuals(), opaque, new MeshData()));
            Assert.True(opaque.IsEmpty);
        }

        /// <summary>
        /// Every triangle must face out. A winding error renders as a hole in
        /// the world, and it is invisible here unless something checks it.
        /// </summary>
        [Fact]
        public void EveryTriangleFacesOutward()
        {
            var store = new ChunkStore();
            // An irregular lump, so every direction and both diagonal choices
            // get exercised.
            for (int x = 2; x < 9; x++)
                for (int z = 2; z < 9; z++)
                    for (int y = 0; y < 1 + ((x * 3 + z * 5) % 4); y++)
                        store.SetRaw(x, y, z, (ushort)(((x + z) % 2) + 1));

            MeshData m = Mesh(store).opaque;
            Assert.True(m.TriangleCount > 0);

            for (int t = 0; t < m.Indices.Count; t += 3)
            {
                float[] a = P(m, m.Indices[t]), b = P(m, m.Indices[t + 1]), c = P(m, m.Indices[t + 2]);
                float[] n = N(m, m.Indices[t]);
                float[] cross = Cross(Sub(b, a), Sub(c, a));
                float dot = cross[0] * n[0] + cross[1] * n[1] + cross[2] * n[2];
                Assert.True(dot > 0f, "triangle " + (t / 3) + " winds inward");
            }
        }

        /// <summary>
        /// The completeness check on the greedy merge. Whatever the shape, the
        /// merged quads must cover exactly the exposed faces — the total area
        /// of the output equals the number of voxel faces that touch air. Too
        /// little means a hole; too much means overlapping, z-fighting quads.
        /// </summary>
        [Fact]
        public void MergedAreaEqualsExposedFacesExactly()
        {
            var store = new ChunkStore();
            var rng = new RngStream(StableHash.OfString("test.mesh.lump"));
            for (int i = 0; i < 1500; i++)
                store.SetRaw(rng.NextInt(30) + 1, rng.NextInt(30) + 1, rng.NextInt(30) + 1,
                             (ushort)(rng.NextInt(2) + 1));

            int exposed = 0;
            int[,] dirs = { { 1, 0, 0 }, { -1, 0, 0 }, { 0, 1, 0 }, { 0, -1, 0 }, { 0, 0, 1 }, { 0, 0, -1 } };
            for (int x = 0; x < 32; x++)
                for (int y = 0; y < 32; y++)
                    for (int z = 0; z < 32; z++)
                    {
                        if (store.Get(x, y, z) == VoxelTypes.AirId) continue;
                        for (int d = 0; d < 6; d++)
                            if (store.Get(x + dirs[d, 0], y + dirs[d, 1], z + dirs[d, 2]) == VoxelTypes.AirId)
                                exposed++;
                    }

            MeshData m = Mesh(store).opaque;
            double area = 0;
            for (int q = 0; q < m.VertexCount; q += 4)
            {
                float[] p0 = P(m, q), p1 = P(m, q + 1), p3 = P(m, q + 3);
                float[] c = Cross(Sub(p1, p0), Sub(p3, p0));
                area += System.Math.Sqrt(c[0] * c[0] + c[1] * c[1] + c[2] * c[2]);
            }

            _out.WriteLine(exposed + " exposed faces merged into " + m.QuadCount + " quads");
            Assert.Equal(exposed, (int)System.Math.Round(area));
            Assert.True(m.QuadCount < exposed, "merging should reduce the quad count");
        }

        [Fact]
        public void AmbientOcclusionDarkensCornersNextToAWall()
        {
            var store = new ChunkStore();
            for (int x = 0; x < 5; x++)
                for (int z = 0; z < 5; z++)
                    store.SetRaw(x, 0, z, Stone);   // a floor
            store.SetRaw(2, 1, 1, Stone);           // one block standing on it

            MeshData m = Mesh(store).opaque;

            var shades = new HashSet<int>();
            for (int i = 0; i < m.Colors.Count; i += 4) shades.Add(m.Colors[i]);
            Assert.True(shades.Count > 1, "a block on a floor must occlude some corners of the floor");

            // And an isolated floor with nothing on it is uniformly lit on top.
            var open = new ChunkStore();
            for (int x = 0; x < 5; x++)
                for (int z = 0; z < 5; z++)
                    open.SetRaw(x, 0, z, Stone);
            MeshData flat = Mesh(open).opaque;
            Assert.Equal(6, flat.QuadCount);   // a 5x5x1 slab
        }

        [Fact]
        public void WaterMeshesItsSurfaceSeparatelyAndDoesNotHideTheSeabed()
        {
            var store = new ChunkStore();
            for (int x = 0; x < 4; x++)
                for (int z = 0; z < 4; z++)
                {
                    store.SetRaw(x, 0, z, Stone);   // seabed
                    store.SetRaw(x, 1, z, Water);   // one layer of sea
                }

            var (opaque, liquid) = Mesh(store);

            // The water's top is one merged quad; its sides face air too.
            Assert.True(liquid.QuadCount >= 1);
            bool hasUp = false;
            for (int i = 1; i < liquid.Normals.Count; i += 3) if (liquid.Normals[i] > 0.5f) hasUp = true;
            Assert.True(hasUp, "the sea needs a surface");

            // Water is not opaque, so the stone under it keeps its top face.
            bool seabedTop = false;
            for (int i = 0; i < opaque.VertexCount; i++)
                if (opaque.Normals[i * 3 + 1] > 0.5f && opaque.Positions[i * 3 + 1] == 1f) seabedTop = true;
            Assert.True(seabedTop, "the seabed must still be drawn under the water");
        }

        [Fact]
        public void TheSameChunkMeshesIdenticallyEveryTime()
        {
            var store = new ChunkStore();
            for (int i = 0; i < 400; i++) store.SetRaw(i % 32, (i / 32) % 32, (i * 7) % 32, (ushort)((i % 2) + 1));
            Assert.Equal(Mesh(store).opaque.Digest(), Mesh(store).opaque.Digest());
        }

        [Fact]
        public void TrailingZerosIsExact()
        {
            Assert.Equal(32, ChunkMesher.TrailingZeros(0));
            for (int bit = 0; bit < 32; bit++)
            {
                Assert.Equal(bit, ChunkMesher.TrailingZeros(1u << bit));
                Assert.Equal(bit, ChunkMesher.TrailingZeros(0xFFFFFFFFu << bit));
            }
        }

        [Fact]
        public void HslProducesTheExpectedColours()
        {
            VoxelVisuals.HslToRgb(0, 100, 50, out byte r, out byte g, out byte b);
            Assert.Equal((255, 0, 0), (r, g, b));
            VoxelVisuals.HslToRgb(120, 100, 50, out r, out g, out b);
            Assert.Equal((0, 255, 0), (r, g, b));
            VoxelVisuals.HslToRgb(0, 0, 34, out r, out g, out b);
            Assert.True(r == g && g == b && r > 80 && r < 95, "zero saturation is grey at the given lightness");
        }

        /// <summary>
        /// S06's tell, measured on a real island: how long a chunk takes to
        /// build off the main thread. The budget is loose on purpose — this
        /// is a regression tripwire, not a benchmark.
        /// </summary>
        [Fact]
        public void AWholeIslandMeshesAndEachChunkFitsTheBudget()
        {
            var dir = new System.IO.DirectoryInfo(System.IO.Directory.GetCurrentDirectory());
            while (dir != null && !System.IO.Directory.Exists(System.IO.Path.Combine(dir.FullName, "Assets", "Content")))
                dir = dir.Parent;
            ContentDatabase content = ContentLoader.Load(new DirectoryContentSource(
                System.IO.Path.Combine(dir.FullName, "Assets", "Content"))).Database;

            VoxelTypes types = VoxelTypes.FromContent(content);
            var store = new ChunkStore();
            IslandGenerator.Generate(store, new StreamRegistry(7), BiomeTable.FromContent(content), types);
            VoxelVisuals visuals = VoxelVisuals.FromContent(content, types);

            var pad = new ushort[ChunkMesher.PaddedVolume];
            var opaque = new MeshData();
            var liquid = new MeshData();
            long quads = 0, liquidQuads = 0, verts = 0;
            int meshed = 0;
            double copyMs = 0, buildMs = 0, worstBuildMs = 0;

            for (int cy = 0; cy < ChunkStore.ChunksY; cy++)
                for (int cz = 0; cz < ChunkStore.ChunksZ; cz++)
                    for (int cx = 0; cx < ChunkStore.ChunksX; cx++)
                    {
                        var watch = Stopwatch.StartNew();
                        bool any = ChunkMesher.CopyPadded(store, cx, cy, cz, pad);
                        copyMs += watch.Elapsed.TotalMilliseconds;
                        if (!any) continue;

                        opaque.Clear(); liquid.Clear();
                        watch.Restart();
                        ChunkMesher.BuildFromPadded(pad, cx * 32, cy * 32, cz * 32, visuals, opaque, liquid);
                        double ms = watch.Elapsed.TotalMilliseconds;
                        buildMs += ms;
                        if (ms > worstBuildMs) worstBuildMs = ms;

                        meshed++;
                        quads += opaque.QuadCount;
                        liquidQuads += liquid.QuadCount;
                        verts += opaque.VertexCount + liquid.VertexCount;
                    }

            _out.WriteLine(meshed + " chunks, " + quads + " opaque + " + liquidQuads + " water quads, "
                           + verts + " vertices");
            _out.WriteLine("copy " + (copyMs / meshed).ToString("0.00") + " ms/chunk (main thread), build "
                           + (buildMs / meshed).ToString("0.00") + " ms/chunk avg, "
                           + worstBuildMs.ToString("0.00") + " ms worst (worker thread)");

            Assert.True(meshed > 0 && quads > 0);
            // Under 65k vertices per chunk keeps every chunk inside a 16-bit index buffer.
            Assert.True(verts / meshed < 65000);
            Assert.True(worstBuildMs < 250, "a chunk took " + worstBuildMs + " ms to build");
        }

        static float[] P(MeshData m, int i) { return new[] { m.Positions[i * 3], m.Positions[i * 3 + 1], m.Positions[i * 3 + 2] }; }
        static float[] N(MeshData m, int i) { return new[] { m.Normals[i * 3], m.Normals[i * 3 + 1], m.Normals[i * 3 + 2] }; }
        static float[] Sub(float[] a, float[] b) { return new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] }; }
        static float[] Cross(float[] a, float[] b)
        {
            return new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] };
        }
    }
}
