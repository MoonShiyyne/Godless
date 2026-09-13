using System.Collections.Generic;
using System.IO;
using Godless.Sim.Annals;
using Godless.Sim.Content;
using Godless.Sim.Core;
using Godless.Sim.Deltas;
using Godless.Sim.Harness;
using Godless.Sim.Save;
using Godless.Sim.Settlements;
using Godless.Sim.Voxels;
using Godless.Sim.World;
using Xunit;

namespace Godless.Sim.Tests
{
    /// <summary>
    /// Deposits and depletion. S2F.
    ///
    /// The tell: a clearing grows out from a village's fire, pits open in the
    /// rubble above it, and a material worked out of reach stops being
    /// gathered — on record — so what is built next is built of something else.
    /// </summary>
    public class DepositTests
    {
        readonly Xunit.Abstractions.ITestOutputHelper _out;
        public DepositTests(Xunit.Abstractions.ITestOutputHelper output) { _out = output; }

        static ContentDatabase Shipped()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Assets", "Content")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return ContentLoader.Load(new DirectoryContentSource(Path.Combine(dir.FullName, "Assets", "Content"))).Database;
        }

        static readonly ContentDatabase Content = Shipped();

        /// <summary>A planted green shore with one settlement founded on it, nothing yet ticking.</summary>
        sealed class Village
        {
            public SimWorld World;
            public Settlement Town;
            public MaterialTable Materials;
            public WorldChoice Choice;
            public ParcelGrid Grid;

            public Village(ulong seed = 7)
            {
                Choice = WorldChoice.Pick(Content, "green-shore");
                VoxelTypes types = VoxelTypes.FromContent(Content);
                World = new SimWorld(seed, Content, types);
                World.Island = TestIslands.Generate(World.Voxels.Store, World.Streams, Choice.Biomes, types,
                                                        Choice.Preset, Choice.Features);
                World.BeginHistory();

                ConstraintFields fields;
                Grid = Founding.Survey(World, Content, Choice.Biomes, out fields);
                int px, pz;
                Assert.True(Founding.StandInSite(Grid, World.Island, Choice.Biomes, Symbol.For("biome.temperate"), out px, out pz));
                Town = Founding.Begin(World, Content, Grid, Choice.Biomes, "first", 20, px, pz, null);
                Materials = Town.Stock.Materials;
                World.Clock.Advance();
            }

            public DepositMap Deposits { get { return World.Island.Deposits; } }

            public int Harvest(string material, int ticks)
            {
                int m = Materials.IndexOf(material);
                int last = -1;
                for (int i = 0; i < ticks; i++)
                {
                    World.Clock.Advance();
                    int f = Town.Catchment.Harvest(m, 1.0, Town.Stock, World.Voxels, World.Clock.Tick, World.Clock.TicksPerDay,
                                                   World.Annals, Town.Id, Town.Hearth, Town.Founded);
                    World.Voxels.EndTick(World.Clock.Tick);
                    if (f >= 0) last = f;
                }
                return last;
            }

            public double Distance(int feature)
            {
                double dx = Deposits.X(feature) - Town.Hearth.X, dz = Deposits.Z(feature) - Town.Hearth.Z;
                return System.Math.Sqrt(dx * dx + dz * dz);
            }
        }

        [Fact]
        public void EveryMaterialABiomePromisesIsGrownOrLaidThere()
        {
            WorldChoice all = WorldChoice.Pick(Content, "");
            Assert.Empty(all.Features.Problems);
            Assert.True(all.Features.Count >= 9);
            foreach (FeatureKind kind in all.Features.All)
                Assert.False(string.IsNullOrEmpty(kind.Tell), kind.Name + " has no tell (L4)");
        }

        [Fact]
        public void TheSameSeedGrowsTheSameWood()
        {
            WorldChoice choice = WorldChoice.Pick(Content, "cold-massif");
            VoxelTypes types = VoxelTypes.FromContent(Content);
            var a = new ChunkStore();
            var b = new ChunkStore();
            // Two real generations: this is the test of generation itself, so it must not share.
            IslandMap ia = IslandGenerator.Generate(a, new StreamRegistry(3), choice.Biomes, types, choice.Preset, choice.Features);
            IslandMap ib = IslandGenerator.Generate(b, new StreamRegistry(3), choice.Biomes, types, choice.Preset, choice.Features);
            Assert.Equal(a.Digest(), b.Digest());
            Assert.Equal(ia.Deposits.Digest(), ib.Deposits.Digest());
            Assert.True(ia.Deposits.Count > 500);
        }

        [Fact]
        public void AStandingTreeIsNotGround()
        {
            var v = new Village();
            int tree = -1;
            for (int f = 0; f < v.Deposits.Count && tree < 0; f++)
                if (v.Deposits.KindOf(f).Shape == FeatureShape.Tree) tree = f;
            Assert.True(tree >= 0);

            int x = v.Deposits.X(tree), z = v.Deposits.Z(tree);
            Assert.NotEqual(VoxelTypes.AirId, v.World.Voxels.Get(x, v.Deposits.Y(tree), z));

            // The planning grid sees the ground under the trunk, not the crown.
            Assert.Equal(v.Deposits.Y(tree) - 1, v.Grid.GroundAt(x, z));
        }

        [Fact]
        public void FellingTakesTheTreeOutOfTheWorldAndSaysWhy()
        {
            var v = new Village();
            int oak = v.Materials.IndexOf("oak");
            int first = v.Town.Catchment.NearestSource(oak);
            Assert.True(first >= 0, "no oak in reach of a temperate fire");
            int x = v.Deposits.X(first), y = v.Deposits.Y(first), z = v.Deposits.Z(first);
            int logBefore = v.World.Voxels.Log.Count;

            v.Harvest("oak", 3);

            Assert.False(v.Deposits.Standing(first));
            Assert.Equal(VoxelTypes.AirId, v.World.Voxels.Get(x, y, z));
            Assert.True(v.Town.Stock.Of(oak) > 0);

            RecordId work = v.Town.Catchment.WorkRecord(oak);
            Assert.True(work.Exists);
            IReadOnlyList<VoxelDelta> all = v.World.Voxels.Log.All();
            Assert.True(all.Count > logBefore);
            for (int i = logBefore; i < all.Count; i++) Assert.Equal(work, all[i].Cause);

            // And the chain ends at the founding.
            var chain = v.World.Annals.CausalChain(work);
            Assert.Equal(Settlement.FoundedKind, chain[chain.Count - 1].Kind);
        }

        [Fact]
        public void TheClearingGrowsOutwardFromTheFire()
        {
            var v = new Village();
            v.Harvest("oak", 400);

            double farthestFelled = 0.0, nearestStanding = double.MaxValue;
            int felled = 0;
            foreach (int f in v.Deposits.Within(v.Town.Hearth.X, v.Town.Hearth.Z, v.Materials.DepositRangeVoxels))
            {
                if (v.Deposits.KindOf(f).Yields != Symbol.For("voxel.oak")) continue;
                if (!v.Deposits.Standing(f)) { felled++; if (v.Distance(f) > farthestFelled) farthestFelled = v.Distance(f); }
                else if (v.Distance(f) < nearestStanding) nearestStanding = v.Distance(f);
            }
            _out.WriteLine(felled + " oaks down, the farthest " + farthestFelled.ToString("0") + " voxels out; the nearest standing "
                           + nearestStanding.ToString("0"));
            Assert.True(felled >= 3);
            Assert.True(farthestFelled <= nearestStanding + 0.001, "a far tree came down while a nearer one stood");
        }

        [Fact]
        public void AMaterialWorkedOutOfReachStopsBeingGatheredAndIsRecorded()
        {
            var v = new Village();
            int sand = v.Materials.IndexOf("sand");
            if (!v.Town.Catchment.Offers(sand)) return;   // no beach in reach on this seed: nothing to work out

            long before = v.Town.Stock.Of(sand);
            v.Harvest("sand", 5000);

            Assert.Equal(-1, v.Town.Catchment.NearestSource(sand));
            Assert.Equal(0.0, v.Town.Catchment.YieldPerLabourTick(sand));
            Assert.True(v.Town.Stock.Of(sand) > before);

            IReadOnlyList<AnnalRecord> exhausted = v.World.Annals.OfKind(Catchment.ExhaustedKind);
            Assert.Single(exhausted);
            Assert.Equal(Symbol.For("voxel.sand"), exhausted[0].Participants[0]);
        }

        [Fact]
        public void WhatWasCutGrowsBackInItsOwnTime()
        {
            var v = new Village();
            int thatch = v.Materials.IndexOf("thatch");
            int tuft = v.Town.Catchment.NearestSource(thatch);
            Assert.True(tuft >= 0);
            FeatureKind grass = v.Deposits.KindOf(tuft);
            int x = v.Deposits.X(tuft), y = v.Deposits.Y(tuft), z = v.Deposits.Z(tuft);
            int initial = v.Deposits.Initial(tuft);

            v.World.Clock.Advance();
            long cut = v.World.Clock.Tick;
            v.Deposits.Take(tuft, initial, v.World.Voxels, cut, v.Town.Founded, v.World.Clock.TicksPerDay);
            v.World.Voxels.EndTick(cut);
            Assert.Equal(0, v.Deposits.Remaining(tuft));
            Assert.Equal(VoxelTypes.AirId, v.World.Voxels.Get(x, y, z));

            long due = cut + (long)grass.RegrowDays * v.World.Clock.TicksPerDay;
            Assert.Equal(0, v.Deposits.Regrow(v.World.Voxels, due - 1));
            Assert.Equal(VoxelTypes.AirId, v.World.Voxels.Get(x, y, z));

            while (v.World.Clock.Tick < due) v.World.Clock.Advance();
            Assert.Equal(1, v.Deposits.Regrow(v.World.Voxels, due));
            Assert.Equal(grass.Voxel, v.World.Voxels.Get(x, y, z));
            Assert.Equal(initial, v.Deposits.Remaining(tuft));
        }

        [Fact]
        public void RockComesOutFromTheTopDown()
        {
            var v = new Village();
            int boulder = -1;
            for (int f = 0; f < v.Deposits.Count && boulder < 0; f++)
                if (v.Deposits.KindOf(f).Shape == FeatureShape.Boulder && v.Deposits.Remaining(f) >= 6) boulder = f;
            Assert.True(boulder >= 0);

            FeatureKind kind = v.Deposits.KindOf(boulder);
            int x = v.Deposits.X(boulder), y = v.Deposits.Y(boulder), z = v.Deposits.Z(boulder);
            int[] before = Layers(v, kind, x, y, z);

            v.World.Clock.Advance();
            v.Deposits.Take(boulder, 1, v.World.Voxels, v.World.Clock.Tick, v.Town.Founded, v.World.Clock.TicksPerDay);
            int[] after = Layers(v, kind, x, y, z);

            // Exactly one voxel gone, and from the highest layer it had.
            int top = before.Length - 1;
            while (top > 0 && before[top] == 0) top--;
            for (int layer = 0; layer < before.Length; layer++)
                Assert.Equal(layer == top ? before[layer] - 1 : before[layer], after[layer]);

            int x0, z0, x1, z1;
            Assert.True(v.Deposits.TakeDirty(out x0, out z0, out x1, out z1));
            Assert.True(x0 <= x && x <= x1 && z0 <= z && z <= z1);
        }

        static int[] Layers(Village v, FeatureKind kind, int x, int y, int z)
        {
            var layers = new int[kind.SizeMost + 2];
            for (int dy = 0; dy < layers.Length; dy++)
                for (int dz = -kind.SizeMost; dz <= kind.SizeMost; dz++)
                    for (int dx = -kind.SizeMost; dx <= kind.SizeMost; dx++)
                        if (v.World.Voxels.Get(x + dx, y + dy, z + dz) == kind.Voxel) layers[dy]++;
            return layers;
        }

        [Fact]
        public void AWoodBuiltOverIsTimberInTheYardAndDoesNotComeBack()
        {
            var v = new Village();
            int oak = v.Materials.IndexOf("oak");
            int tree = v.Town.Catchment.NearestSource(oak);
            int x = v.Deposits.X(tree), z = v.Deposits.Z(tree);

            v.World.Clock.Advance();
            int[] got = v.Deposits.Clear(x - 1, z - 1, x + 1, z + 1, v.World.Voxels, v.World.Clock.Tick, v.Town.Founded,
                                         v.World.Clock.TicksPerDay);
            int k = v.Deposits.Kinds.IndexOf(Symbol.For("feature.oak"));
            Assert.True(got[k] > 0);
            Assert.Equal(0, v.Deposits.Remaining(tree));

            long years = v.World.Clock.TicksInYears(20);
            Assert.Equal(0, v.Deposits.Regrow(v.World.Voxels, v.World.Clock.Tick + years));
            Assert.Equal(0, v.Deposits.Remaining(tree));
        }

        [Fact]
        public void ForagingThinsAsTheWoodComesDown()
        {
            var v = new Village();
            double before = v.Town.Catchment.FoodPerLabourTick;
            v.Harvest("oak", 3000);
            v.Harvest("thatch", 1500);
            v.Town.Catchment.Refresh();
            double after = v.Town.Catchment.FoodPerLabourTick;
            _out.WriteLine("a forager-tick fed " + before.ToString("0.00") + ", now " + after.ToString("0.00"));
            Assert.True(after < before * 0.9);
            Assert.True(after >= before * 0.25 - 1e-9, "cleared land still feeds a quarter of what it did");
        }

        [Fact]
        public void APlantedWorldSavesWithItsClearingsAndLoadsBackTheSame()
        {
            var v = new Village(11);
            v.Harvest("oak", 200);

            using var buffer = new MemoryStream();
            SaveGame.Write(buffer, v.World);
            buffer.Position = 0;
            SimWorld loaded = SaveGame.Read(buffer, Content, BiomeTable.FromContent(Content), v.World.VoxelTypes);

            Assert.NotNull(loaded.Island.Deposits);
            Assert.Equal(v.World.Voxels.Store.Digest(), loaded.Voxels.Store.Digest());
        }
    }
}
