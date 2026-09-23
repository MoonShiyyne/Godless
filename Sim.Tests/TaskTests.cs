using System.Collections.Generic;
using System.IO;
using Godless.Sim.Annals;
using Godless.Sim.Collective;
using Godless.Sim.Content;
using Godless.Sim.Core;
using Godless.Sim.Drives;
using Godless.Sim.Settlements;
using Godless.Sim.World;
using Xunit;

namespace Godless.Sim.Tests
{
    public class TaskTests
    {
        static ContentDatabase Shipped()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Assets", "Content"))) dir = dir.Parent;
            return ContentLoader.Load(new DirectoryContentSource(Path.Combine(dir.FullName, "Assets", "Content"))).Database;
        }

        static ContentDatabase With(params string[] docs)
        {
            var src = new MemoryContentSource().Add("base", "mod.json", "{}");
            for (int i = 0; i < docs.Length; i++) src.Add("base", "d" + i + ".json", docs[i]);
            return ContentLoader.Load(src).Database;
        }

        /// <summary>
        /// A task with a standing demand — a reserve bigger than anyone will
        /// ever gather — so allocation can be watched without intents or roofs
        /// getting in the way. The growth is set so a task's stimulus settles
        /// where about one and a half hands answer it: the model makes
        /// specialists when demand is moderate against the labour available,
        /// and a demand that swamps everyone puts everyone on everything.
        /// </summary>
        static string Gather(double min, double max, double learn = 0.03, double forget = 0.004, int reserve = 1000000)
        {
            var c = System.Globalization.CultureInfo.InvariantCulture;
            return "{\"type\":\"task\",\"id\":\"gather\",\"tell\":\"x\",\"verb\":\"gather\",\"perMaterial\":true,"
                 + "\"threshold\":{\"min\":" + min.ToString(c) + ",\"max\":" + max.ToString(c) + "},"
                 + "\"learn\":" + learn.ToString(c) + ",\"forget\":" + forget.ToString(c) + ",\"quitChance\":0.05,"
                 + "\"stimulusGrowth\":0.00000015,\"workDone\":0.03,\"reserveVoxels\":" + reserve.ToString(c) + "}";
        }

        sealed class Town
        {
            public DriveRules Rules;
            public Settlement S;
            public TaskBoard Board;
            public readonly Annalist Annals = new Annalist();
            public readonly RngStream Rng = new RngStream(99);
            long _tick;

            public Town(string taskJson, int people = 20)
            {
                ContentDatabase content = Shipped();
                Rules = DriveRules.FromContent(content);
                BiomeTable biomes = BiomeTable.FromContent(content);
                MaterialTable materials = MaterialTable.FromContent(content, biomes);
                S = Settlement.Found("test", new Int3(100, 50, 100), biomes.At(0), people, Rules, 0, Annals, RecordId.None);
                S.ShelterCapacity = people;   // roofed, so the days go to work

                // Three materials on offer, one of them richly.
                var sources = new long[materials.Count];
                sources[materials.IndexOf("oak")] = 3000;
                sources[materials.IndexOf("granite")] = 1500;
                sources[materials.IndexOf("thatch")] = 1500;
                S.Catchment = Catchment.FromSources(materials, sources);
                S.Stock = new MaterialStock(materials);
                Board = new TaskBoard(TaskKindTable.FromContent(With(taskJson)), S, Rules, new StreamRegistry(5));
                S.Tasks = Board;
            }

            public void Live(int days, System.Func<int, bool> absent = null)
            {
                for (int d = 0; d < days; d++)
                    for (int t = 0; t < 4; t++, _tick++)
                    {
                        DriveSystem.Step(S, Rules, _tick, t == 3, Sky.Fair, Annals);
                        Board.Step(S, Rng, null, absent);
                    }
            }

            /// <summary>
            /// Over everyone who gathered at all, the share of their gathering
            /// spent on their own main task. One means everyone has one job;
            /// work dealt out by lot would give about the biggest task's share.
            /// </summary>
            public double OneJobIndex(out int workers)
            {
                double sum = 0.0;
                workers = 0;
                for (int i = 0; i < S.People.Count; i++)
                {
                    long total = 0, main = 0;
                    for (int j = 0; j < Board.Count; j++) { total += Board.WorkBy(i, j); if (Board.WorkBy(i, j) > main) main = Board.WorkBy(i, j); }
                    if (total < 20) continue;
                    sum += (double)main / total;
                    workers++;
                }
                return workers == 0 ? 0.0 : sum / workers;
            }

            public void Flatten(double threshold)
            {
                for (int i = 0; i < S.People.Count; i++)
                    for (int j = 0; j < Board.Count; j++) Board.SetThreshold(i, j, threshold);
            }

            public int[] Busiest(int task, int n)
            {
                var order = new List<int>();
                for (int i = 0; i < S.People.Count; i++) order.Add(i);
                order.Sort((x, y) => { int c = Board.WorkBy(y, task).CompareTo(Board.WorkBy(x, task)); return c != 0 ? c : x.CompareTo(y); });
                return order.GetRange(0, n).ToArray();
            }
        }

        [Fact]
        public void TheShippedTaskLoadsAndNonsenseIsRefused()
        {
            Assert.Empty(TaskKindTable.FromContent(Shipped()).Problems);
            TaskKindTable kinds = TaskKindTable.FromContent(With(
                "{\"type\":\"task\",\"id\":\"pray\",\"tell\":\"x\",\"verb\":\"pray\"}",
                "{\"type\":\"task\",\"id\":\"haul\",\"verb\":\"gather\"}"));
            Assert.Equal(0, kinds.Count);
            Assert.Contains(kinds.Problems, p => p.Contains("'pray'"));
            Assert.Contains(kinds.Problems, p => p.Contains("'haul'") && p.Contains("tell"));
        }

        [Fact]
        public void OneGatheringTaskPerMaterialTheLandOffers()
        {
            var town = new Town(Gather(0.1, 1.0));
            Assert.Equal(3, town.Board.Count);
            Assert.True(town.Board.IndexOf("task.gather.oak") >= 0);
            Assert.Equal(-1, town.Board.IndexOf("task.gather.slate"));
        }

        /// <summary>
        /// S1C's tell: nobody is told what to do, yet the same few are always
        /// in the woods and the same few at the quarry. After two months
        /// nearly everyone gathers, and each of them nearly always the same
        /// material.
        /// </summary>
        [Fact]
        public void NobodyIsAssignedAnythingYetEachPersonHasAJob()
        {
            var town = new Town(Gather(0.05, 1.0));
            town.Live(60);
            for (int j = 0; j < town.Board.Count; j++)
                Assert.True(town.Board.TotalWork(j) > 100, town.Board.TaskId(j) + " barely worked");

            double index = town.OneJobIndex(out int workers);
            Assert.True(workers >= 8, workers + " people gathered");
            Assert.True(index > 0.85, "each gatherer spent " + index + " of their time on their main task");
        }

        /// <summary>
        /// Reinforcement alone differentiates an identical population. Every
        /// threshold starts the same; with learning, whoever happens to start
        /// a job gets readier for it and keeps it. Without learning the same
        /// population stays generalist. Part 16: validated on paper wasps.
        /// </summary>
        [Fact]
        public void AnIdenticalPopulationSpecialisesBecauseDoingAJobMakesYouReadierForIt()
        {
            var learning = new Town(Gather(0.05, 1.0, learn: 0.03, forget: 0.004));
            var fixedMinds = new Town(Gather(0.05, 1.0, learn: 0.0, forget: 0.0));
            learning.Flatten(0.5);
            fixedMinds.Flatten(0.5);
            learning.Live(60);
            fixedMinds.Live(60);

            double withLearning = learning.OneJobIndex(out _), without = fixedMinds.OneJobIndex(out _);
            Assert.True(withLearning > 0.85, "with learning " + withLearning);
            Assert.True(withLearning > without + 0.1, "with learning " + withLearning + ", without " + without);
        }

        /// <summary>
        /// Kill the specialists and the work does not stop: the stimulus climbs
        /// until others cross their own thresholds. Nobody reassigns anyone.
        /// </summary>
        [Fact]
        public void TakeTheSpecialistsAwayAndOthersPickTheWorkUp()
        {
            var town = new Town(Gather(0.05, 1.0));
            town.Live(60);
            int oak = town.Board.IndexOf("task.gather.oak");
            int[] specialists = town.Busiest(oak, 4);
            var gone = new HashSet<int>(specialists);

            long before = town.Board.TotalWork(oak);
            town.Live(60, i => gone.Contains(i));
            long after = town.Board.TotalWork(oak) - before;

            long bySpecialistsSince = 0;
            foreach (int i in specialists) bySpecialistsSince += town.Board.WorkBy(i, oak);
            // The stimulus has to climb before anyone crosses their own
            // threshold for a job that was never theirs, so this is slower
            // than it was with the specialists there — but it happens.
            Assert.True(after > 0, "the woods stayed empty");
            Assert.True(after > before * 0.15, "work fell from " + before + " in 60 days to " + after + " in the next 60");
            int replacements = 0;
            for (int i = 0; i < town.S.People.Count; i++)
                if (!gone.Contains(i) && town.Board.WorkBy(i, oak) > 20) replacements++;
            Assert.True(replacements > 0, "nobody stepped in");
        }

        [Fact]
        public void WithNothingWantedNobodyWorks()
        {
            var town = new Town(Gather(0.05, 1.0, reserve: 0));
            town.Live(20);
            for (int j = 0; j < town.Board.Count; j++) Assert.Equal(0, town.Board.TotalWork(j));
            Assert.True(town.Board.IdleTicks > 0);
        }

        [Fact]
        public void GatheringFillsTheStockInTheShapeOfTheLand()
        {
            var town = new Town(Gather(0.05, 1.0));
            town.Live(60);
            MaterialTable m = town.S.Stock.Materials;
            long oak = town.S.Stock.Of(m.IndexOf("oak")), granite = town.S.Stock.Of(m.IndexOf("granite"));
            Assert.True(oak > 0 && granite > 0);
            Assert.True(oak > granite, "the land gives oak up most readily, so there is most of it: " + oak + " oak, " + granite + " granite");
        }

        /// <summary>
        /// The land comes to offer a material it did not when the town was
        /// founded — the reach widened out to a pine wood, or what was cut grew
        /// back — and a task goes on the board for it. The board was fixed at
        /// founding, so green shore planned a wing in pine that nobody had a
        /// task to fetch, and it stood unfinished from year 8 on.
        /// </summary>
        [Fact]
        public void AMaterialThatComesIntoReachGetsAGatheringTaskAndNobodyElseChanges()
        {
            var town = new Town(Gather(0.05, 1.0));
            town.Live(30);
            MaterialTable materials = town.S.Stock.Materials;
            int slate = materials.IndexOf("slate");
            Assert.Equal(-1, town.Board.IndexOf("task.gather.slate"));
            Assert.False(town.Board.Gathers(slate));

            // What everyone had on the tasks they had, before slate is found.
            int people = town.S.People.Count, before = town.Board.Count;
            var ids = new Symbol[before];
            var threshold = new double[people, before];
            var work = new long[people, before];
            for (int j = 0; j < before; j++) ids[j] = town.Board.TaskId(j);
            for (int i = 0; i < people; i++)
                for (int j = 0; j < before; j++) { threshold[i, j] = town.Board.Threshold(i, j); work[i, j] = town.Board.WorkBy(i, j); }

            var sources = new long[materials.Count];
            for (int m = 0; m < materials.Count; m++) sources[m] = town.S.Catchment.Sources(m);
            sources[slate] = 1500;
            town.S.Catchment = Catchment.FromSources(materials, sources);
            town.Board.Step(town.S, town.Rng);

            Assert.Equal(before + 1, town.Board.Count);
            Assert.True(town.Board.Gathers(slate));

            // In material order, whenever it came (L2).
            int last = -1;
            for (int j = 0; j < town.Board.Count; j++)
            {
                int m = town.Board.MaterialOf(j);
                if (m < 0) continue;
                Assert.True(m > last, "gathering tasks out of material order at " + town.Board.TaskId(j));
                last = m;
            }

            // Everyone keeps their temperament and their history: one tick of
            // learning at most, and at most one tick's work.
            for (int k = 0; k < before; k++)
            {
                int j = town.Board.IndexOf(ids[k].ToString());
                Assert.True(j >= 0, ids[k] + " left the board");
                for (int i = 0; i < people; i++)
                {
                    Assert.InRange(town.Board.Threshold(i, j), threshold[i, k] - 0.031, threshold[i, k] + 0.031);
                    Assert.InRange(town.Board.WorkBy(i, j), work[i, k], work[i, k] + 1);
                }
            }

            town.Live(60);
            Assert.True(town.Board.TotalWork(town.Board.IndexOf("task.gather.slate")) > 0, "nobody went for the slate");
            Assert.True(town.S.Stock.Of(slate) > 0);
        }

        [Fact]
        public void TheSameSeedDividesTheWorkTheSameWay()
        {
            var a = new Town(Gather(0.05, 1.0));
            var b = new Town(Gather(0.05, 1.0));
            a.Live(30); b.Live(30);
            Assert.Equal(a.Board.Digest(), b.Board.Digest());
            Assert.Equal(a.S.Digest(), b.S.Digest());
        }
    }
}
