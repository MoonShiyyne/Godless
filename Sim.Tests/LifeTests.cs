using System.Collections.Generic;
using System.IO;
using Godless.Sim.Annals;
using Godless.Sim.Content;
using Godless.Sim.Core;
using Godless.Sim.Harness;
using Godless.Sim.Life;
using Godless.Sim.Voxels;
using Godless.Sim.World;
using Xunit;

namespace Godless.Sim.Tests
{
    /// <summary>
    /// v2 M1, Life. The tell: herds drift across the meadows and scatter when
    /// wolves come out of the wood; people walk out from a camp and bring deer
    /// back; the god sets creatures down, strikes them, blesses and curses them.
    /// </summary>
    public class LifeTests
    {
        static ContentDatabase Shipped()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Assets", "Content"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return ContentLoader.Load(new DirectoryContentSource(Path.Combine(dir.FullName, "Assets", "Content"))).Database;
        }

        static readonly ContentDatabase Content = Shipped();

        sealed class World
        {
            public SimWorld Sim;
            public ParcelGrid Grid;
            public LifeSystem Life;
            public int X, Z;
        }

        static World Make(ulong seed = 7)
        {
            WorldChoice choice = WorldChoice.Pick(Content, "green-shore");
            var sim = new SimWorld(seed, Content, VoxelTypes.FromContent(Content));
            sim.Island = TestIslands.Generate(sim.Voxels.Store, sim.Streams, choice.Biomes, sim.VoxelTypes, choice.Preset, choice.Features);
            sim.BeginHistory();
            ConstraintFields fields;
            ParcelGrid grid = Survey.Of(sim, Content, choice.Biomes, out fields);
            LifeSystem life = Genesis.AddSystems(sim, Content, grid, fields, choice.Biomes);
            int px, pz;
            Assert.True(Survey.FlattestNearWater(grid, sim.Island, choice.Biomes, Symbol.None, out px, out pz));
            return new World { Sim = sim, Grid = grid, Life = life, X = px * ParcelGrid.Size + 2, Z = pz * ParcelGrid.Size + 2 };
        }

        [Fact]
        public void TheShippedSpeciesLoadAndKnowWhoHuntsWhom()
        {
            SpeciesTable t = SpeciesTable.FromContent(Content);
            Assert.Empty(t.Problems);
            Species wolf = t[t.IndexOf("wolf")], deer = t[t.IndexOf("deer")];
            Assert.True(wolf.IsHunting(t.IndexOf("deer")));
            Assert.True(deer.FleesFrom(t.IndexOf("wolf")));
            Assert.True(t[t.IndexOf("human")].Person);
            Voxels.DetailModelTable models = Voxels.DetailModelTable.FromContent(Content);
            Assert.Empty(models.Problems);
            foreach (Species s in t.All)
            {
                Assert.NotNull(models.Find(s.StandModel));
                foreach (string w in s.WalkModels) Assert.NotNull(models.Find(w));
            }
        }

        [Theory]
        [InlineData("{\"type\":\"species\",\"id\":\"x\",\"tell\":\"t\",\"speed\":9,\"model\":{\"stand\":\"m\"}}", "moves 9")]
        [InlineData("{\"type\":\"species\",\"id\":\"x\",\"tell\":\"t\",\"adultYears\":5,\"lifeYears\":2,\"model\":{\"stand\":\"m\"}}", "dies before it is grown")]
        [InlineData("{\"type\":\"species\",\"id\":\"x\",\"tell\":\"t\",\"hunts\":[\"unicorn\"],\"model\":{\"stand\":\"m\"}}", "'unicorn', which is not loaded")]
        public void ABadSpeciesIsRefusedAndSaysWhy(string doc, string expected)
        {
            var src = new MemoryContentSource().Add("base", "mod.json", "{}").Add("base", "s.json", doc);
            SpeciesTable t = SpeciesTable.FromContent(ContentLoader.Load(src).Database);
            Assert.Contains(t.Problems, p => p.Contains(expected));
        }

        /// <summary>The world is alive the moment it is made: wild herds, and bands of people with camps.</summary>
        [Fact]
        public void AWorldStartsWithHerdsAndBands()
        {
            World w = Make();
            w.Sim.Tick();
            Living life = w.Sim.Life;
            Assert.NotNull(life);
            foreach (Species s in life.Species.All)
                Assert.True(life.CountOf(life.Species.IndexOf(s.Name)) > 0, "no " + s.Plural + " at the start");
            Assert.NotEmpty(life.Bands);
            foreach (Group b in life.Bands)
            {
                Assert.True(b.Members > 0);
                Assert.Equal(LifeSystem.BandFoundedKind, w.Sim.Annals.Get(b.Record).Kind);
            }
        }

        /// <summary>The same seed and the same acts give the same creatures, row for row (L2).</summary>
        [Fact]
        public void TheSameSeedGivesTheSameLife()
        {
            ulong Run()
            {
                World w = Make(11);
                int wolf = w.Life.Life.Species.IndexOf("wolf");
                for (int t = 0; t < 400; t++)
                {
                    if (t == 50) w.Sim.Commands.Submit(new Spawn(w.Life, wolf, new Int3(w.X, 0, w.Z), 4), w.Sim.Clock.Tick);
                    if (t == 120) w.Sim.Commands.Submit(new Smite(w.Life, new Int3(w.X, 0, w.Z), 5), w.Sim.Clock.Tick);
                    w.Sim.Tick();
                }
                return w.Sim.Life.Creatures.Digest() ^ w.Sim.Life.Grazing.Digest() ^ w.Sim.Annals.Digest();
            }
            Assert.Equal(Run(), Run());
        }

        /// <summary>Creatures never walk into water or off the world, and move a little every so often.</summary>
        [Fact]
        public void CreaturesKeepToDryLand()
        {
            World w = Make();
            Creatures c = null;
            double moved = 0.0;
            for (int t = 0; t < 360; t++)
            {
                double[] x0 = null, z0 = null;
                if (c != null) { x0 = (double[])c.X.Clone(); z0 = (double[])c.Z.Clone(); }
                w.Sim.Tick();
                c = w.Sim.Life.Creatures;
                for (int i = 0; i < c.Length; i++)
                {
                    if (!c.Alive[i]) continue;
                    Assert.True(w.Life.Passable((int)c.X[i], (int)c.Z[i]), "a " + w.Sim.Life.Species[c.Species[i]].Name + " stands in water at " + c.X[i] + "," + c.Z[i]);
                    if (x0 != null && i < x0.Length) moved += System.Math.Abs(c.X[i] - x0[i]) + System.Math.Abs(c.Z[i] - z0[i]);
                }
            }
            Assert.True(moved > 1000.0, "nothing moved in a year");
        }

        [Fact]
        public void SetDownPeopleMakeABandWithACampThatSettles()
        {
            World w = Make();
            w.Sim.Tick();
            int human = w.Life.Life.Species.IndexOf("human");
            int before = w.Sim.Life.Groups.Count;
            w.Sim.Commands.Submit(new Spawn(w.Life, human, new Int3(w.X, 0, w.Z), 10), w.Sim.Clock.Tick);
            w.Sim.Tick();
            Group b = w.Sim.Life.Groups.Find(g => g.Number >= before && g.IsBand);
            Assert.NotNull(b);
            Assert.Equal(10, b.Members);
            AnnalRecord founded = w.Sim.Annals.Get(b.Record);
            Assert.Equal(Powers.SpawnedKind, w.Sim.Annals.Get(founded.Cause).Kind);   // on record: the god set them down
            for (int t = 0; t < 2 * 360; t++) w.Sim.Tick();
            Assert.True(b.Settled || b.Gone, "a band neither settled nor died out in two years");
        }

        [Fact]
        public void SmiteKillsWhatIsThereAndSaysSo()
        {
            World w = Make();
            w.Sim.Tick();
            int sheep = w.Life.Life.Species.IndexOf("sheep");
            w.Sim.Commands.Submit(new Spawn(w.Life, sheep, new Int3(w.X, 0, w.Z), 7), w.Sim.Clock.Tick);
            w.Sim.Tick();
            int there = Powers.Within(w.Sim.Life, w.X, w.Z, 3).Count;
            Assert.True(there > 0);
            long smitten = w.Sim.Life.Creatures.Deaths[(int)Death.Smitten];
            w.Sim.Commands.Submit(new Smite(w.Life, new Int3(w.X, 0, w.Z), 3), w.Sim.Clock.Tick);
            w.Sim.Tick();
            Assert.Equal(smitten + there, w.Sim.Life.Creatures.Deaths[(int)Death.Smitten]);
            Assert.Single(w.Sim.Annals.OfKind(Powers.SmoteKind));
        }

        [Fact]
        public void FireBurnsTheTreesOutOfTheWorldCitingTheGod()
        {
            World w = Make();
            w.Sim.Tick();
            DepositMap deposits = w.Sim.Island.Deposits;
            int tree = -1;
            foreach (int f in deposits.Within(w.X, w.Z, 200))
                if (deposits.KindOf(f).Shape == FeatureShape.Tree && deposits.Remaining(f) > 0) { tree = f; break; }
            Assert.True(tree >= 0);
            int deltas = w.Sim.Voxels.Log.Count;
            w.Sim.Commands.Submit(new Fire(w.Life, w.Grid, new Int3(deposits.X(tree), 0, deposits.Z(tree)), 6), w.Sim.Clock.Tick);
            w.Sim.Tick();
            Assert.Equal(0, deposits.Remaining(tree));
            IReadOnlyList<Deltas.VoxelDelta> log = w.Sim.Voxels.Log.All();
            Assert.True(log.Count > deltas);
            for (int i = deltas; i < log.Count; i++)
                if (log[i].Tick == w.Sim.Clock.Tick) Assert.Equal(Powers.FireKind, w.Sim.Annals.Get(log[i].Cause).Kind);
        }

        [Fact]
        public void BlessedAndCursedAreMarkedAndTheCursedTurnOnTheirNeighbours()
        {
            World w = Make();
            w.Sim.Tick();
            int deer = w.Life.Life.Species.IndexOf("deer");
            w.Sim.Commands.Submit(new Spawn(w.Life, deer, new Int3(w.X, 0, w.Z), 8), w.Sim.Clock.Tick);
            w.Sim.Tick();
            w.Sim.Commands.Submit(new Touch(w.Life, new Int3(w.X, 0, w.Z), false, 6), w.Sim.Clock.Tick);
            w.Sim.Tick();
            Creatures c = w.Sim.Life.Creatures;
            int cursed = 0;
            foreach (int i in Powers.Within(w.Sim.Life, w.X, w.Z, 6)) if (c.Has(i, Creatures.Cursed)) cursed++;
            Assert.True(cursed > 0);
            long killed = c.Deaths[(int)Death.Killed];
            for (int t = 0; t < 60; t++) w.Sim.Tick();
            Assert.True(c.Deaths[(int)Death.Killed] > killed, "the cursed harmed nobody");
        }

        /// <summary>Wolves hunt: over a few years deer and sheep are killed, not just old.</summary>
        [Fact]
        public void PredatorsKill()
        {
            World w = Make();
            for (int t = 0; t < 360; t++) w.Sim.Tick();
            Assert.True(w.Sim.Life.Creatures.Deaths[(int)Death.Killed] > 5);
        }

        /// <summary>No two creatures of a herd are the same: each has its own temperament and pace.</summary>
        [Fact]
        public void EachCreatureHasATemperamentOfItsOwn()
        {
            World w = Make();
            w.Sim.Tick();
            Creatures c = w.Sim.Life.Creatures;
            var bold = new HashSet<double>();
            var pace = new HashSet<double>();
            int n = 0;
            for (int i = 0; i < c.Length; i++)
            {
                if (!c.Alive[i]) continue;
                n++; bold.Add(c.Bold[i]); pace.Add(c.Pace[i]);
                Assert.InRange(c.Bold[i], 0.0, 1.0);
                Assert.InRange(c.Social[i], 0.0, 1.0);
                Assert.InRange(c.Restless[i], 0.0, 1.0);
            }
            Assert.True(bold.Count > n / 2, "temperaments repeat: " + bold.Count + " kinds of boldness among " + n);
            Assert.True(pace.Count > n / 2);
        }

        /// <summary>
        /// A group at ease is not one body: its members stand apart, and do
        /// different things — some graze, some rest, some wander.
        /// </summary>
        [Fact]
        public void AGroupIsManyCreaturesNotOne()
        {
            World w = Make();
            int deer = w.Life.Life.Species.IndexOf("deer");
            w.Sim.Tick();
            w.Sim.Commands.Submit(new Spawn(w.Life, deer, new Int3(w.X, 0, w.Z), 8), w.Sim.Clock.Tick);
            int varied = 0, looked = 0;
            double apart = 0.0;
            int pairs = 0;
            Creatures c = w.Sim.Life.Creatures;
            for (int t = 0; t < 240; t++)
            {
                w.Sim.Tick();
                if (t % 20 != 19) continue;
                foreach (Group g in w.Sim.Life.Groups)
                {
                    if (g.Gone || g.Members < 3) continue;
                    var doing = new HashSet<Doing>();
                    for (int i = 0; i < c.Length; i++)
                    {
                        if (!c.Alive[i] || c.Group[i] != g.Number) continue;
                        doing.Add(c.Doing[i]);
                        double near = double.MaxValue;
                        for (int j = 0; j < c.Length; j++)
                        {
                            if (j == i || !c.Alive[j] || c.Group[j] != g.Number) continue;
                            double dx = c.X[i] - c.X[j], dz = c.Z[i] - c.Z[j];
                            near = System.Math.Min(near, System.Math.Sqrt(dx * dx + dz * dz));
                        }
                        apart += near; pairs++;
                    }
                    looked++;
                    if (doing.Count >= 2) varied++;
                }
            }
            Assert.True(looked > 0);
            Assert.True(varied * 2 >= looked, varied + " of " + looked + " groups had members doing different things");
            Assert.True(apart / pairs >= 1.5, "members stand " + (apart / pairs) + " voxels from the nearest of their group");
        }

        /// <summary>One alone joins the first of its kind it meets; a group grown past its size loses its restless.</summary>
        [Fact]
        public void CreaturesJoinAndLeaveGroupsOfTheirOwnAccord()
        {
            World w = Make();
            int sheep = w.Life.Life.Species.IndexOf("sheep");
            w.Sim.Tick();
            Living life = w.Sim.Life;
            long joined = life.Joined;
            w.Sim.Commands.Submit(new Spawn(w.Life, sheep, new Int3(w.X, 0, w.Z), 5), w.Sim.Clock.Tick);
            w.Sim.Commands.Submit(new Spawn(w.Life, sheep, new Int3(w.X + 8, 0, w.Z), 1), w.Sim.Clock.Tick);
            for (int t = 0; t < 120; t++) w.Sim.Tick();
            Assert.True(life.Joined > joined, "the lone sheep never joined anyone");

            long left = life.Left;
            w.Sim.Commands.Submit(new Spawn(w.Life, sheep, new Int3(w.X, 0, w.Z + 20), 30), w.Sim.Clock.Tick);
            for (int t = 0; t < 360; t++) w.Sim.Tick();
            Assert.True(life.Left > left, "a flock of 30, twice its kind's size, kept every one for a year");
        }

        [Fact]
        public void LifeInvariantsHoldOverABatch()
        {
            WorldChoice choice = WorldChoice.Pick(Content, "green-shore");
            var runner = new BatchRunner(LifeInvariants.Living(Content, choice));
            runner.Collect(LifeInvariants.Collector());
            foreach (Invariant i in LifeInvariants.All()) runner.Assert(i);
            BatchReport report = runner.Run(0, 2, 1);
            Assert.True(report.AllPassed, report.ToText());
        }
    }
}
