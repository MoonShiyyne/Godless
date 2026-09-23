using System.Collections.Generic;
using System.IO;
using Godless.Meshing;
using Godless.Sim.Annals;
using Godless.Sim.Build;
using Godless.Sim.Content;
using Godless.Sim.Core;
using Godless.Sim.Culture;
using Godless.Sim.Harness;
using Godless.Sim.Save;
using Godless.Sim.Economy;
using Godless.Sim.Voxels;
using Godless.Sim.World;
using Xunit;

namespace Godless.Sim.Tests
{
    /// <summary>
    /// Detail cells (S2R), beds (S2S), and buildings coming down (S2T).
    ///
    /// The tells: a house has a bed for everyone it sleeps; when it comes down
    /// every voxel of it falls into a heap of what it was made of, the beds
    /// with it, and that heap is ground to build on and salvage to carry off.
    /// </summary>
    public class DetailAndCollapseTests
    {
        readonly Xunit.Abstractions.ITestOutputHelper _out;
        public DetailAndCollapseTests(Xunit.Abstractions.ITestOutputHelper output) { _out = output; }

        static ContentDatabase Shipped()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Assets", "Content"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return ContentLoader.Load(new DirectoryContentSource(Path.Combine(dir.FullName, "Assets", "Content"))).Database;
        }

        static readonly ContentDatabase Content = Shipped();
        static readonly DetailModelTable Models = DetailModelTable.FromContent(Content);

        // ── S2R ─────────────────────────────────────────────────────────────

        [Fact]
        public void TheShippedModelsLoadAndKnowTheirSlots()
        {
            Assert.Empty(Models.Problems);
            DetailModel bed = Models.Find("bed");
            Assert.NotNull(bed);
            Assert.Equal(16, bed.SizeX);
            Assert.Equal(8, bed.SizeZ);
            Assert.Contains("frame", bed.Slots);
            for (int i = 1; i <= 3; i++) Assert.NotNull(Models.Find("rubble-" + i));
        }

        [Fact]
        public void TurningAModelTurnsItsCellsAndKeepsThemAll()
        {
            DetailModel bed = Models.Find("bed");
            for (int turn = 0; turn < 4; turn++)
            {
                int n = 0;
                for (int y = 0; y < bed.SizeY; y++)
                    for (int z = 0; z < bed.TurnedSizeZ(turn); z++)
                        for (int x = 0; x < bed.TurnedSizeX(turn); x++)
                            if (bed.AtTurned(x, y, z, turn) != 0) n++;
                Assert.Equal(bed.CellCount, n);
            }
            Assert.Equal(bed.SizeZ, bed.TurnedSizeX(1));
        }

        [Fact]
        public void ABadModelIsRefusedAndSaysWhy()
        {
            var src = new MemoryContentSource().Add("base", "mod.json", "{}")
                .Add("base", "models/x.json", "{\"type\":\"model\",\"id\":\"x\",\"tell\":\"t\",\"size\":{\"x\":2,\"y\":1,\"z\":1},\"paint\":{\"a\":{\"value\":10}},\"layers\":[[\"ab\"]]}");
            DetailModelTable t = DetailModelTable.FromContent(ContentLoader.Load(src).Database);
            Assert.Single(t.Problems);
            Assert.Contains("'b'", t.Problems[0]);
        }

        [Fact]
        public void EveryPlacementAndRemovalIsOnRecord()
        {
            var layer = new DetailLayer();
            DetailModel bed = Models.Find("bed");
            var cause = new RecordId(3);
            int id = layer.Place(bed, 400, 200, 400, 1, new ushort[] { 5 }, 10, cause);
            Assert.Equal(1, layer.Count);
            Assert.Contains(layer.Get(id), layer.InChunk(DetailLayer.ChunkOf(100, 50, 100)));
            Assert.True(layer.Remove(id, 11, cause));
            Assert.Equal(0, layer.Count);
            Assert.Equal(2, layer.Log.Count);
            Assert.True(layer.Log[0].Added);
            Assert.False(layer.Log[1].Added);
            Assert.Equal(cause, layer.Log[1].Cause);
        }

        [Fact]
        public void AModelMeshesTheSameWhicheverWayItFaces()
        {
            DetailModel bed = Models.Find("bed");
            MeshData a = DetailMesher.Build(bed, 0, slot => new VoxelVisual { Opaque = true, R = 90, G = 60, B = 30 });
            MeshData b = DetailMesher.Build(bed, 1, slot => new VoxelVisual { Opaque = true, R = 90, G = 60, B = 30 });
            Assert.False(a.IsEmpty);
            Assert.Equal(a.QuadCount, b.QuadCount);
        }

        // ── S2S and S2T on a lived-in village ───────────────────────────────

    }
}
