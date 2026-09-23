using System.Collections.Generic;
using System.IO;
using Godless.Sim.Annals;
using Godless.Sim.Build;
using Godless.Sim.Collective;
using Godless.Sim.Content;
using Godless.Sim.Core;
using Godless.Sim.Culture;
using Godless.Sim.Deltas;
using Godless.Sim.Drives;
using Godless.Sim.Harness;
using Godless.Sim.Settlements;
using Godless.Sim.Voxels;
using Godless.Sim.World;
using Xunit;

namespace Godless.Sim.Tests
{
    public class ConstructionTests
    {
        static ContentDatabase Load()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Assets", "Content"))) dir = dir.Parent;
            return ContentLoader.Load(new DirectoryContentSource(Path.Combine(dir.FullName, "Assets", "Content"))).Database;
        }

        static readonly ContentDatabase Content = Load();

        /// <summary>An island with one settlement on it, living its days in full.</summary>
        sealed class Village
        {
            public SimWorld World;
            public Settlement Town;
            public ParcelGrid Grid;

            public Village(ulong seed = 7, int stocked = 4000)
            {
                BiomeTable biomes = BiomeTable.FromContent(Content);
                VoxelTypes types = VoxelTypes.FromContent(Content);
                World = new SimWorld(seed, Content, types);
                World.Island = TestIslands.Generate(World.Voxels.Store, World.Streams, biomes, types);

                bool[] solid = TerrainBrush.SolidTable(Content, types);
                var wet = new bool[types.Count];
                wet[types.IdOf(Symbol.For("voxel.water"))] = true;
                Grid = ParcelGrid.Build(World.Voxels.Store, solid, wet);
                ConstraintFields fields = ConstraintFields.Compute(World.Island, Grid, biomes);

                DriveRules rules = DriveRules.FromContent(Content);
                MaterialTable materials = MaterialTable.FromContent(Content, biomes);
                var genes = GeneTable.FromContent(Content);
                int px, pz;
                Assert.True(Founding.StandInSite(Grid, World.Island, biomes, Symbol.None, out px, out pz));
                int hx = px * ParcelGrid.Size + 2, hz = pz * ParcelGrid.Size + 2;
                var hearth = new Int3(hx, Grid.GroundAt(hx, hz) + 1, hz);
                Town = Settlement.Found("test", hearth, biomes.At(World.Island.BiomeAt(hx, hz)), 20, rules, 0, World.Annals, RecordId.None);
                Town.Catchment = Catchment.Survey(World.Island, biomes, materials, hx, hz);
                Town.Stock = new MaterialStock(materials);
                for (int m = 0; m < materials.Count; m++) if (Town.Catchment.Offers(m)) Town.Stock.Add(m, stocked);
                Town.Genome = new Genome(genes);

                IntentKindTable kinds = IntentKindTable.FromContent(Content, rules.Needs);
                Town.AttachIntents(new IntentBus(kinds, rules.Needs.Count));
                Town.Tasks = new TaskBoard(TaskKindTable.FromContent(Content), Town, rules, World.Streams);
                World.Settlements.Add(Town);

                TileSet tiles = TileSet.FromContent(Content, materials);
                Palette palette = Palette.FromContent(Content);
                var construction = new Construction(World.Voxels, materials, types, tiles, palette);
                World.Add(new DriveSystem(rules))
                     .Add(new IntentSystem())
                     .Add(new SiteSystem(GrammarTable.FromContent(Content, genes),
                                         SitingTable.FromContent(Content, genes, kinds),
                                         tiles, materials, palette, Grid, fields))
                     .Add(new TaskSystem(construction, Grid));
                World.BeginHistory();
            }

            public void Live(int days) { for (int i = 0; i < days * 4; i++) World.Tick(); }

            public Project First { get { return Town.Projects.Count > 0 ? Town.Projects[0] : null; } }

            /// <summary>Runs until the test's question can be asked, or gives up.</summary>
            public bool LiveUntil(System.Func<bool> until, int days = 400)
            {
                for (int i = 0; i < days * 4; i++)
                {
                    World.Tick();
                    if (until()) return true;
                }
                return false;
            }
        }

        /// <summary>
        /// S1A's tell: a house goes up over days, and a half-built one is a
        /// legitimate world state — the walls stand to the height they have
        /// reached, from the ground up, and nothing floats.
        /// </summary>
        [Fact]
        public void AHouseGoesUpFromTheGroundAndAHalfBuiltOneStands()
        {
            var village = new Village();
            Assert.True(village.LiveUntil(() => village.First != null && village.First.Placed > 40 && !village.First.Complete),
                        "no house was under way");

            Project p = village.First;
            List<int> order = new List<int>();
            int highestPlaced = -1, lowestUnplaced = int.MaxValue;
            for (int i = 0; i < p.Placed; i++) order.Add(i);

            // Every cell laid stands in the world, and every cell not yet laid
            // is still air: the house is a house up to where the work reached.
            for (int y = 0; y < p.Plan.Height; y++)
                for (int z = 0; z < p.Plan.Depth; z++)
                    for (int x = 0; x < p.Plan.Width; x++)
                    {
                        ushort want = p.Built.At(x, y, z);
                        if (want == VoxelTypes.AirId) continue;
                        Int3 at = Construction.World(p, x, y, z);
                        bool standing = village.World.Voxels.Store.Get(at.X, at.Y, at.Z) == want;
                        if (standing) highestPlaced = System.Math.Max(highestPlaced, y);
                        else lowestUnplaced = System.Math.Min(lowestUnplaced, y);
                    }

            Assert.True(highestPlaced >= 0, "nothing was laid");
            Assert.True(lowestUnplaced < int.MaxValue, "the house was finished after all");
            Assert.True(highestPlaced <= lowestUnplaced, "built out of order: up to " + highestPlaced + ", missing from " + lowestUnplaced);
        }

        /// <summary>Every voxel of the house names the house, and the house names the nights that asked for it.</summary>
        [Fact]
        public void EveryVoxelOfAHouseCarriesTheReasonItIsThere()
        {
            var village = new Village();
            Assert.True(village.LiveUntil(() => village.First != null && village.First.Placed > 20));
            Project p = village.First;

            int mine = 0;
            foreach (VoxelDelta delta in village.World.Voxels.Log.All())
                if (delta.Cause == p.Begun) mine++;
            Assert.Equal(p.Placed, mine);

            var chain = village.World.Annals.CausalChain(p.Begun);
            var kinds = new List<string>();
            foreach (AnnalRecord r in chain) kinds.Add(r.Kind.ToString());
            string trail = string.Join(" <- ", kinds);
            Assert.True(kinds.Contains("structure.begun"), trail);
            Assert.Contains("site.chosen", kinds);
            Assert.Contains("intent.raised", kinds);
            Assert.True(kinds.Contains("drive.exposed"), trail);
            Assert.Equal("settlement.founded", kinds[kinds.Count - 1]);
        }

        /// <summary>A finished house is roofs, and the intent that asked for it is answered.</summary>
        [Fact]
        public void AFinishedHouseIsSomewhereToSleep()
        {
            var village = new Village();
            // The failure this guards against is a house that stands half
            // built forever because one of its materials cannot be gathered
            // here at all, so the message says what the yard was holding.
            bool done = village.LiveUntil(() => village.First != null && village.First.Complete);
            var why = new System.Text.StringBuilder("nothing was finished");
            if (!done && village.First != null && village.First.Built != null)
            {
                why.Append("; placed ").Append(village.First.Placed);
                for (int m = 0; m < village.First.Built.Cost.Length; m++)
                    if (village.First.Built.Cost[m] > 0)
                        why.Append("; ").Append(village.Town.Stock.Materials[m].Name)
                           .Append(" cost ").Append(village.First.Built.Cost[m])
                           .Append(" held ").Append(village.Town.Stock.Of(m))
                           .Append(" yield ").Append(village.Town.Catchment.YieldPerLabourTick(m).ToString("0.0000"));
            }
            Assert.True(done, why.ToString());
            village.Live(2);   // the roofs are taken up at the next night

            Project p = village.First;
            Assert.True(village.Town.ShelterCapacity >= p.Plan.Capacity);
            Assert.Equal(IntentStatus.Resolved, p.Intent.Status);

            var completed = village.World.Annals.OfKind(Construction.CompletedKind);
            Assert.NotEmpty(completed);
            Assert.Equal(p.Begun, completed[0].Cause);

            // And the people stop sleeping in the open, which is what all of
            // this was for.
            int inOpen = 0;
            foreach (Agent a in village.Town.People) if (!a.ShelteredLastNight) inOpen++;
            Assert.True(inOpen < village.Town.People.Count, "everyone is still out in the rain");
        }

        [Fact]
        public void BuildingSpendsTheStockItTakes()
        {
            var village = new Village();
            MaterialTable materials = village.Town.Stock.Materials;
            var before = new long[materials.Count];
            for (int m = 0; m < materials.Count; m++) before[m] = village.Town.Stock.Of(m);

            Assert.True(village.LiveUntil(() => village.First != null && village.First.Placed > 50));
            Project p = village.First;

            long spent = 0;
            for (int m = 0; m < materials.Count; m++)
            {
                long gathered = village.Town.Stock.Of(m) - before[m];
                if (gathered < 0) spent += -gathered;
            }
            Assert.True(spent > 0, "the house was built out of nothing");
        }

        [Fact]
        public void BuildersWalkToTheSite()
        {
            var village = new Village();
            Assert.True(village.LiveUntil(() => village.First != null && village.First.Placed > 0));
            Project p = village.First;

            bool someoneThere = false;
            foreach (Agent a in village.Town.People)
                if (a.ParcelX == p.Site.ParcelX && a.ParcelZ == p.Site.ParcelZ) someoneThere = true;
            Assert.True(someoneThere, "nobody is at the building site");
        }

        /// <summary>
        /// A begun building waiting on a material that is not in the yard does
        /// not hold every builder at its foot: they go to one that has what it
        /// needs. And once it has waited long enough, the rest goes up in the
        /// same kind of thing from the yard. Green shore had forty-seven people
        /// standing at a pine wing for years while two houses with everything
        /// they needed were never begun.
        /// </summary>
        [Fact]
        public void ABuildingStalledOnOneMaterialIsPassedOverAndThenFinishedInTheSameKind()
        {
            var village = new Village();
            Settlement s = village.Town;
            MaterialTable materials = s.Stock.Materials;
            Project stalled = null, other = null;
            Assert.True(village.LiveUntil(() =>
            {
                if (village.First == null || !village.First.Begun.Exists || village.First.Placed < 20 || village.First.Complete) return false;
                int m = Construction.NextMaterial(village.First);
                if (m < 0) return false;
                foreach (Project p in s.Projects)
                {
                    if (p == village.First || p.Complete || !Construction.Workable(s, p)) continue;
                    int n = Construction.NextMaterial(p);
                    if (n >= 0 && materials[n].Class != materials[m].Class) { stalled = village.First; other = p; return true; }
                }
                return false;
            }), "never had a begun building and another ready one made of something else");

            // Everything of the stalled one's next material's kind leaves the yard,
            // and the land stops offering it, so nobody can fetch more.
            int wanted = Construction.NextMaterial(stalled);
            var sources = new long[materials.Count];
            for (int m = 0; m < materials.Count; m++)
            {
                sources[m] = s.Catchment.Sources(m);
                if (materials[m].Class != materials[wanted].Class) continue;
                sources[m] = 0;
                Assert.True(s.Stock.TryTake(m, s.Stock.Of(m), village.World.Clock.Tick, s.Id, s.Hearth, village.World.Annals, RecordId.None));
            }
            // While the land still gives it, a builder with nothing to lay goes for it.
            if (s.Catchment.YieldPerLabourTick(wanted) > 0.0) Assert.Equal(wanted, Construction.Fetch(s));
            s.Catchment = Catchment.FromSources(materials, sources);
            Assert.NotEqual(wanted, Construction.Fetch(s));

            Assert.False(Construction.Workable(s, stalled));
            int stalledAt = stalled.Placed, otherAt = other.Placed;
            village.Live(5);
            Assert.Equal(stalledAt, stalled.Placed);
            Assert.True(other.Placed > otherAt || other.Complete, "the builders stood at the stalled building");

            // Something of the same kind turns up in the yard, and after the wait it is used.
            int instead = -1;
            for (int m = 0; m < materials.Count; m++)
                if (m != wanted && materials[m].Class == materials[wanted].Class) { instead = m; break; }
            Assert.True(instead >= 0, "no other material of the kind " + materials[wanted].Class);
            s.Stock.Add(instead, 2000);
            village.Live(3);
            Assert.Equal(stalledAt, stalled.Placed);
            village.Live(Construction.StandInAfterDays + 5);
            Assert.True(stalled.Placed > stalledAt, "the stalled building never took up the " + materials[instead].Name);
            Assert.Contains("finished in " + materials[instead].Name + " after waiting on " + materials[wanted].Name, stalled.Built.Compromises);

            List<int> order = new List<int>();
            Blueprint plan = stalled.Plan;
            for (int y = 0; y < plan.Height; y++)
                for (int z = 0; z < plan.Depth; z++)
                    for (int x = 0; x < plan.Width; x++)
                        if (stalled.Built.At(x, y, z) != VoxelTypes.AirId) order.Add((y * plan.Depth + z) * plan.Width + x);
            int cell = order[stalledAt];
            Int3 at = Construction.World(stalled, cell % plan.Width, cell / (plan.Width * plan.Depth), (cell / plan.Width) % plan.Depth);
            Assert.Equal(village.World.VoxelTypes.IdOf(materials[instead].Voxel), village.World.Voxels.Get(at));
        }

        [Fact]
        public void TheSameSeedBuildsTheSameVillage()
        {
            var a = new Village();
            var b = new Village();
            a.Live(60);
            b.Live(60);
            Assert.Equal(a.World.Annals.Digest(), b.World.Annals.Digest());
            Assert.Equal(a.World.Voxels.Store.Digest(), b.World.Voxels.Store.Digest());
            Assert.Equal(a.Town.Digest(), b.Town.Digest());
        }
    }
}
