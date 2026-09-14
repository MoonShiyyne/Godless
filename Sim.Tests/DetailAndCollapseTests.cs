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
using Godless.Sim.Settlements;
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

        sealed class Village
        {
            public SimWorld World;
            public Settlement Town;
            public bool[] Ground;
            public MaterialTable Materials;

            public Village(ulong seed = 7)
            {
                WorldChoice choice = WorldChoice.Pick(Content, "green-shore");
                World = SettlementInvariants.Settled(Content, choice, 20, TestIslands.Generate)(seed);
                Town = World.Settlements[0];
                Ground = TerrainBrush.SolidTable(Content, World.VoxelTypes);
                Materials = Town.Stock.Materials;
            }

            public Project FirstHouse(int days = 700, bool onGround = false)
            {
                for (int d = 0; d < days; d++)
                {
                    foreach (Project p in Town.Projects)
                        if (p.Complete && p.Host == null && (!onGround || p.Ground == null || p.Ground.Strategy != GroundStrategy.Stilt)) return p;
                    for (int t = 0; t < World.Clock.TicksPerDay; t++) World.Tick();
                }
                return null;
            }
        }

        [Fact]
        public void AFinishedHouseHasABedForEveryoneItSleeps()
        {
            var v = new Village();
            Project house = v.FirstHouse();
            Assert.NotNull(house);
            Assert.Equal(house.Plan.Capacity, house.Beds.Count);

            Int3 lo = Construction.World(house, 0, 0, 0);
            Int3 hi = Construction.World(house, house.Plan.Width - 1, house.Plan.Height - 1, house.Plan.Depth - 1);
            foreach (Furnishing.Bed bed in house.Beds)
            {
                DetailInstance inst = v.World.Details.Get(bed.Instance);
                Assert.NotNull(inst);
                Assert.Equal(Furnishing.BedModel, inst.Model);
                Assert.InRange(bed.Centre.X, lo.X, hi.X);
                Assert.InRange(bed.Centre.Z, lo.Z, hi.Z);
                Assert.True(v.World.Voxels.Get(bed.Centre.X, bed.Centre.Y - 1, bed.Centre.Z) != VoxelTypes.AirId, "a bed with no floor under it");
            }
        }

        [Fact]
        public void ABuildingBroughtDownFallsIntoAHeapOfWhatItWas()
        {
            var v = new Village();
            Project house = v.FirstHouse();
            Assert.NotNull(house);
            int beds = house.Beds.Count;
            int bedsBefore = 0;
            foreach (DetailInstance d in v.World.Details.All) if (d.Model == Furnishing.BedModel) bedsBefore++;

            v.World.Clock.Advance();
            RecordId god = v.World.Annals.Write(v.World.Clock.Tick, Symbol.For("god.brought-down"), Symbol.None, new Int3(0, 0, 0), RecordId.None);
            int fell = Collapse.BringDown(v.World, v.Town, house, god, v.Ground, v.Materials, Models);
            v.World.Voxels.EndTick(v.World.Clock.Tick);
            _out.WriteLine(fell + " voxels fell into " + v.Town.Rubble.Count + " rubble");

            Assert.True(fell > 100);
            Assert.True(house.Destroyed);
            Assert.DoesNotContain(house, v.Town.Projects);
            Assert.Contains(house, v.Town.Ruins);

            // Nothing of it stands where it stood.
            for (int y = 0; y < house.Plan.Height; y++)
                for (int z = 0; z < house.Plan.Depth; z++)
                    for (int x = 0; x < house.Plan.Width; x++)
                    {
                        ushort t = house.Built.At(x, y, z);
                        if (t == VoxelTypes.AirId) continue;
                        Assert.NotEqual(t, v.World.Voxels.Get(Construction.World(house, x, y, z)));
                    }

            // The beds went with it.
            int bedsAfter = 0;
            foreach (DetailInstance d in v.World.Details.All) if (d.Model == Furnishing.BedModel) bedsAfter++;
            Assert.Equal(bedsBefore - beds, bedsAfter);

            // Every heap of rubble rests on something, is rubble, and remembers its material.
            ushort rubble = v.World.VoxelTypes.IdOf(Collapse.RubbleVoxel);
            Assert.True(v.Town.Rubble.Count > fell / 2);
            foreach (RubbleCell c in v.Town.Rubble)
            {
                Assert.Equal(rubble, v.World.Voxels.Get(c.At));
                Assert.NotEqual(VoxelTypes.AirId, v.World.Voxels.Get(c.At.X, c.At.Y - 1, c.At.Z));
                Assert.NotNull(v.World.Details.Get(c.Detail));
            }

            // Nobody lives there, nobody owns the ground, and the record says why.
            foreach (Household h in v.Town.Households) Assert.DoesNotContain(house, h.Home);
            Assert.False(v.Town.IsClaimed(house.Site.ParcelX, house.Site.ParcelZ));
            IReadOnlyList<AnnalRecord> collapsed = v.World.Annals.OfKind(Collapse.CollapsedKind);
            Assert.Single(collapsed);
            Assert.Equal(god, collapsed[0].Cause);
        }

        [Fact]
        public void RubbleIsSalvagedBackIntoTheMaterialItWas()
        {
            var v = new Village(7);
            Project house = v.FirstHouse();
            Assert.NotNull(house);
            v.World.Clock.Advance();
            Collapse.BringDown(v.World, v.Town, house, RecordId.None, v.Ground, v.Materials, Models);

            int material = v.Town.Rubble[0].Material;
            Assert.True(material >= 0);
            long before = v.Town.Stock.Of(material);
            int heap = v.Town.Rubble.Count;
            v.World.Clock.Advance();
            int taken = Collapse.Salvage(v.World.Voxels, v.World.Details, v.Town, material, 5, v.World.Clock.Tick);
            Assert.True(taken > 0);
            Assert.Equal(before + taken, v.Town.Stock.Of(material));
            Assert.Equal(heap - taken, v.Town.Rubble.Count);
        }

        [Fact]
        public void AHouseWithItsGroundDugAwayFallsAndCitesTheDigging()
        {
            var v = new Village(7);
            Project house = v.FirstHouse(onGround: true);
            Assert.NotNull(house);

            // Dig out the ground under most of its floor, as a god's brush would.
            v.World.Clock.Advance();
            long tick = v.World.Clock.Tick;
            RecordId dig = v.World.Annals.Write(tick, Symbol.For("god.lowered-ground"), Symbol.None, new Int3(0, 0, 0), RecordId.None);
            for (int z = Grammar.Margin; z < house.Plan.Depth - Grammar.Margin; z++)
                for (int x = Grammar.Margin; x < house.Plan.Width - Grammar.Margin; x++)
                {
                    Int3 floor = Construction.World(house, x, 0, z);
                    for (int dy = 1; dy <= 4; dy++) v.World.Voxels.Set(floor.X, floor.Y - dy, floor.Z, VoxelTypes.AirId, tick, dig);
                }
            v.World.Voxels.EndTick(tick);
            Assert.True(Collapse.Support(v.World, house) < SupportSystem.Fails);

            for (int t = 0; t < 2 * v.World.Clock.TicksPerDay && !house.Destroyed; t++) v.World.Tick();
            Assert.True(house.Destroyed, "a house with no ground under it still stands");

            IReadOnlyList<AnnalRecord> undermined = v.World.Annals.OfKind(Collapse.UnderminedKind);
            Assert.NotEmpty(undermined);
            Assert.Equal(dig, undermined[0].Cause);
        }

        [Fact]
        public void DetailsSaveAndLoadBack()
        {
            var v = new Village(7);
            Project house = v.FirstHouse();
            Assert.NotNull(house);

            using var buffer = new MemoryStream();
            SaveGame.Write(buffer, v.World);
            buffer.Position = 0;
            SimWorld loaded = SaveGame.Read(buffer, Content, BiomeTable.FromContent(Content), v.World.VoxelTypes);
            Assert.Equal(v.World.Details.Count, loaded.Details.Count);
            Assert.Equal(v.World.Details.Digest(), loaded.Details.Digest());
        }

        [Fact]
        public void AWingOnATallHouseCanRiseItsFullHeight()
        {
            GrammarTable grammars = GrammarTable.FromContent(Content, GeneTable.FromContent(Content));
            Grammar wing = grammars.PartFor("shelter", "wing");
            var genome = new Genome(GeneTable.FromContent(Content));
            Palette palette = Palette.FromContent(Content);
            Blueprint low = wing.Build(genome, palette, 64, 64, 1150, new Dictionary<string, double> { { "capacity", 3 } });
            Blueprint tall = wing.Build(genome, palette, 64, 64, 1150, new Dictionary<string, double> { { "capacity", 3 }, { "storeys", 3 } });
            Assert.Equal(1, Construction.Storeys(low));
            Assert.Equal(3, Construction.Storeys(tall));
            Blueprint leanTo = wing.Build(genome, palette, 64, 64, 1150, new Dictionary<string, double> { { "capacity", 3 }, { "pitch", 0.25 } });
            Assert.True(leanTo.Height < low.Height || leanTo.Height == low.Height);
        }
    }
}
