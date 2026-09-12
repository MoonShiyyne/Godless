using System.IO;
using Godless.Sim.Annals;
using Godless.Sim.Build;
using Godless.Sim.Content;
using Godless.Sim.Core;
using Godless.Sim.Drives;
using Godless.Sim.Harness;
using Godless.Sim.Settlements;
using Godless.Sim.Voxels;
using Godless.Sim.World;
using Xunit;

namespace Godless.Sim.Tests
{
    public class SubsistenceTests
    {
        static ContentDatabase Load()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Assets", "Content"))) dir = dir.Parent;
            return ContentLoader.Load(new DirectoryContentSource(Path.Combine(dir.FullName, "Assets", "Content"))).Database;
        }

        static readonly ContentDatabase Content = Load();

        sealed class Village
        {
            public SimWorld World;
            public Settlement Town;

            /// <param name="food">Meals a forager-tick, in place of what the land would give.</param>
            public Village(double food, int stocked = 6000, int people = 20)
            {
                BiomeTable biomes = BiomeTable.FromContent(Content);
                VoxelTypes types = VoxelTypes.FromContent(Content);
                World = new SimWorld(7, Content, types);
                World.Island = IslandGenerator.Generate(World.Voxels.Store, World.Streams, biomes, types);

                ConstraintFields fields;
                ParcelGrid grid = Founding.Survey(World, Content, biomes, out fields);
                int px, pz;
                Assert.True(Founding.StandInSite(grid, World.Island, biomes, Symbol.None, out px, out pz));
                Town = Founding.Begin(World, Content, grid, biomes, "test", people, px, pz, null);

                MaterialTable materials = MaterialTable.FromContent(Content, biomes);
                var sources = new long[materials.Count];
                for (int m = 0; m < materials.Count; m++)
                {
                    sources[m] = Town.Catchment.Sources(m);
                    if (sources[m] > 0) Town.Stock.Add(m, stocked);
                }
                Town.Catchment = Catchment.FromSources(materials, sources, food);
                Founding.AddSystems(World, Content, grid, fields, biomes);
                World.BeginHistory();
            }

            public void Live(int days) { for (int i = 0; i < days * 4; i++) World.Tick(); }

            public int Houses
            {
                get { int n = 0; foreach (Project p in Town.Projects) if (p.Complete) n++; return n; }
            }
        }

        /// <summary>What the land gives is what the biome is: wooded and rainy feeds people, bare highland does not.</summary>
        [Fact]
        public void TheLandItselfDecidesHowWellPeopleEat()
        {
            BiomeTable biomes = BiomeTable.FromContent(Content);
            VoxelTypes types = VoxelTypes.FromContent(Content);
            var store = new ChunkStore();
            IslandMap island = IslandGenerator.Generate(store, new StreamRegistry(7), biomes, types);
            MaterialTable materials = MaterialTable.FromContent(Content, biomes);

            double best = 0.0, worst = double.MaxValue;
            for (int x = 64; x < 448; x += 64)
                for (int z = 64; z < 448; z += 64)
                {
                    if (!island.IsLand(x, z)) continue;
                    double food = Catchment.Survey(island, biomes, materials, x, z).FoodPerLabourTick;
                    if (food > best) best = food;
                    if (food < worst) worst = food;
                }

            Assert.True(best > worst * 1.5, "the island feeds people the same everywhere: " + worst + " to " + best);
            Assert.True(best > 0.3, "nowhere on the island feeds anyone");
        }

        /// <summary>
        /// S1E's tell: a settlement on good ground grows, and one that cannot
        /// feed itself loses people and stops finishing its houses.
        /// </summary>
        [Fact]
        public void GoodGroundGrowsAndBadGroundEmpties()
        {
            // Both start with an empty yard, so the labour that goes to
            // finding food is labour that does not go to building.
            var fed = new Village(1.2, stocked: 0);
            var starving = new Village(0.05, stocked: 0);
            fed.Live(1400);
            starving.Live(1400);

            Assert.True(fed.Town.Born > 0, "nobody was born on good ground");
            Assert.True(fed.Town.People.Count > 20, "the fed village did not grow: " + fed.Town.People.Count);

            Assert.True(starving.Town.Died > 0, "nobody was lost on bad ground");
            Assert.True(starving.Town.People.Count < 20, "the starving village did not empty: " + starving.Town.People.Count);
            Assert.True(starving.Town.People.Count < fed.Town.People.Count,
                        starving.Town.People.Count + " on bad ground against " + fed.Town.People.Count + " on good");

            // And the hands never leave the food: on thin ground the houses
            // do not go up at all, which is the stage plan's tell arriving
            // from the other direction.
            Assert.True(fed.Houses > starving.Houses,
                        "good ground built " + fed.Houses + " houses, thin ground " + starving.Houses);
            Assert.True(starving.Town.ShelterCapacity < 6,
                        starving.Town.ShelterCapacity + " sleeping places on ground that cannot feed anyone");

            // And the going hungry is on record, so the chronicle can say when.
            Assert.NotEmpty(starving.World.Annals.OfKind(Subsistence.HungerKind));
            Assert.NotEmpty(starving.World.Annals.OfKind(Subsistence.DiedKind));
            Assert.NotEmpty(fed.World.Annals.OfKind(Subsistence.BornKind));
        }

        /// <summary>Growth asks for the next house: a child makes the settlement a bed short.</summary>
        [Fact]
        public void GrowingAsksForAnotherHouse()
        {
            var village = new Village(1.2);
            village.Live(1400);
            Assert.True(village.Houses >= 3, village.Houses + " houses");
            Assert.True(village.Town.ShelterCapacity >= 20, village.Town.ShelterCapacity + " sleeping places");
        }

        /// <summary>
        /// The task board follows the population: whoever stays keeps their
        /// temperament and their history, and a newcomer draws their own.
        /// </summary>
        [Fact]
        public void TheBoardFollowsWhoIsAlive()
        {
            var village = new Village(1.2);
            village.Live(120);

            Agent keeps = village.Town.People[3];
            int task = 0;
            double threshold = village.Town.Tasks.Threshold(3, task);
            long work = village.Town.Tasks.WorkBy(3, task);

            village.Town.Remove(0, village.World.Streams);
            int now = -1;
            for (int i = 0; i < village.Town.People.Count; i++) if (village.Town.People[i] == keeps) now = i;
            Assert.Equal(2, now);
            Assert.Equal(threshold, village.Town.Tasks.Threshold(now, task));
            Assert.Equal(work, village.Town.Tasks.WorkBy(now, task));

            Agent child = village.Town.Add(village.World.Streams);
            int last = village.Town.People.Count - 1;
            Assert.Equal(child, village.Town.People[last]);
            Assert.InRange(village.Town.Tasks.Threshold(last, task), 0.0, 1.0);
            Assert.Equal(0, village.Town.Tasks.WorkBy(last, task));
        }

        [Fact]
        public void TheSameSeedFeedsTheSamePeople()
        {
            var a = new Village(0.8);
            var b = new Village(0.8);
            a.Live(200);
            b.Live(200);
            Assert.Equal(a.World.Annals.Digest(), b.World.Annals.Digest());
            Assert.Equal(a.Town.Digest(), b.Town.Digest());
        }
    }
}
