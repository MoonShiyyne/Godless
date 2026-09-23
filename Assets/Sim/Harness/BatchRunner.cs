using System.Collections.Generic;
using System.Text;
using Godless.Sim.Content;
using Godless.Sim.Voxels;

namespace Godless.Sim.Harness
{
    /// <summary>How a world is built for one seed. Systems are added here.</summary>
    public delegate SimWorld WorldFactory(ulong seed);

    /// <summary>
    /// Runs a batch of seeded simulations and checks invariants over the
    /// results. S08.
    ///
    /// This is the machine, not the assertion library. The vocabulary is the
    /// expensive half and it cannot be written ahead of the systems it
    /// describes — you cannot assert "no motif library reaches zero" before
    /// motifs exist. What can exist now is the thing that runs two hundred
    /// seeds and tells you which ones broke, which is what turns balance bugs
    /// from invisible into obvious.
    /// </summary>
    /// <summary>
    /// Records whatever a run wants asserted later. Systems own their own
    /// metrics: the runner cannot know what "median pop" means until there
    /// are people.
    /// </summary>
    public delegate void MetricCollector(SimWorld world, RunResult into);

    public sealed class BatchRunner
    {
        readonly WorldFactory _factory;
        readonly List<Invariant> _invariants = new List<Invariant>();
        readonly List<MetricCollector> _collectors = new List<MetricCollector>();

        public BatchRunner(WorldFactory factory) { _factory = factory; }

        public BatchRunner Collect(MetricCollector collector)
        {
            _collectors.Add(collector);
            return this;
        }

        public BatchRunner Assert(Invariant invariant)
        {
            _invariants.Add(invariant);
            return this;
        }

        /// <summary>
        /// A world with no systems and no content — the stratum-0 baseline,
        /// which is still enough to prove the clock, the streams, the annals
        /// and the delta log agree with themselves across runs.
        /// </summary>
        public static WorldFactory EmptyWorld(ContentDatabase content = null, VoxelTypes types = null)
        {
            ContentDatabase c = content ?? new ContentDatabase();
            VoxelTypes t = types ?? VoxelTypes.Build(new Core.Symbol[0]);
            return seed => new SimWorld(seed, c, t);
        }

        public RunResult RunOne(ulong seed, int years)
        {
            SimWorld world = _factory(seed);
            world.RunYears(years);
            RunResult result = Standard(world, years);
            foreach (MetricCollector collector in _collectors) collector(world, result);
            return result;
        }

        static RunResult Standard(SimWorld world, int years)
        {
            var result = new RunResult
            {
                Seed = world.Seed,
                Years = years,
                Ticks = world.Clock.Tick,
                AnnalsDigest = world.Annals.Digest(),
                WorldDigest = world.Voxels.Store.Digest(),
                DeltaDigest = world.Voxels.Log.Digest(),
                AnnalCount = world.Annals.Count,
                DeltaCount = world.Voxels.Log.Count,
            };

            result.Record("annals.count", world.Annals.Count);
            result.Record("deltas.count", world.Voxels.Log.Count);
            result.Record("clock.ticks-not-run", world.Clock.Tick - world.TicksRun);
            result.Record("world.chunks", world.Voxels.Store.AllocatedChunks);
            result.Record("world.megabytes", world.Voxels.Store.MemoryBytes / 1048576.0);
            return result;
        }

        public BatchReport Run(ulong firstSeed, int seedCount, int years)
        {
            var runs = new List<RunResult>(seedCount);
            for (int i = 0; i < seedCount; i++) runs.Add(RunOne(firstSeed + (ulong)i, years));

            var outcomes = new List<InvariantOutcome>();
            foreach (Invariant invariant in _invariants)
            {
                if (invariant.IsPerRun)
                {
                    var failing = new List<ulong>();
                    foreach (RunResult run in runs)
                        if (!invariant.CheckRun(run)) failing.Add(run.Seed);

                    outcomes.Add(new InvariantOutcome
                    {
                        Invariant = invariant,
                        Passed = failing.Count == 0,
                        FailingSeeds = failing,
                    });
                }
                else
                {
                    outcomes.Add(new InvariantOutcome
                    {
                        Invariant = invariant,
                        Passed = invariant.CheckBatch(runs),
                        FailingSeeds = new List<ulong>(),
                    });
                }
            }

            return new BatchReport(runs, outcomes);
        }

        /// <summary>
        /// Runs each seed twice and compares digests. The determinism check
        /// the whole project rests on, and the one invariant that needs no
        /// systems to be meaningful.
        /// </summary>
        public IReadOnlyList<ulong> FindNondeterministicSeeds(ulong firstSeed, int seedCount, int years)
        {
            var broken = new List<ulong>();
            for (int i = 0; i < seedCount; i++)
            {
                ulong seed = firstSeed + (ulong)i;
                if (RunOne(seed, years).Digest() != RunOne(seed, years).Digest()) broken.Add(seed);
            }
            return broken;
        }
    }

    public sealed class BatchReport
    {
        public BatchReport(IReadOnlyList<RunResult> runs, IReadOnlyList<InvariantOutcome> outcomes)
        {
            Runs = runs;
            Outcomes = outcomes;
        }

        public IReadOnlyList<RunResult> Runs { get; private set; }
        public IReadOnlyList<InvariantOutcome> Outcomes { get; private set; }

        public bool AllPassed
        {
            get
            {
                foreach (InvariantOutcome o in Outcomes) if (!o.Passed) return false;
                return true;
            }
        }

        public int FailedCount
        {
            get
            {
                int n = 0;
                foreach (InvariantOutcome o in Outcomes) if (!o.Passed) n++;
                return n;
            }
        }

        /// <summary>
        /// Aggregates a metric across the batch. The doc's assertions are
        /// phrased over medians rather than means for a reason: one collapsed
        /// settlement should not drag a whole batch's average somewhere
        /// misleading.
        /// </summary>
        public double Median(string metric)
        {
            var values = new List<double>(Runs.Count);
            foreach (RunResult r in Runs) values.Add(r.Metric(metric));
            if (values.Count == 0) return 0.0;
            values.Sort();
            int mid = values.Count / 2;
            return (values.Count % 2 == 1) ? values[mid] : (values[mid - 1] + values[mid]) * 0.5;
        }

        public string ToText()
        {
            var sb = new StringBuilder();
            var c = System.Globalization.CultureInfo.InvariantCulture;

            sb.Append(Runs.Count.ToString(c)).Append(" seeds");
            if (Runs.Count > 0)
                sb.Append(" x ").Append(Runs[0].Years.ToString(c)).Append(" years");
            sb.Append('\n');

            if (Outcomes.Count == 0)
            {
                sb.Append("  no invariants registered\n");
            }
            foreach (InvariantOutcome o in Outcomes)
            {
                sb.Append(o.Passed ? "  ok   " : "  FAIL ");
                sb.Append('[').Append(o.Invariant.Owner).Append("] ");
                sb.Append(o.Invariant.Name).Append('\n');

                if (!o.Passed && o.FailingSeeds.Count > 0)
                {
                    sb.Append("         failing seeds: ");
                    int show = o.FailingSeeds.Count < 10 ? o.FailingSeeds.Count : 10;
                    for (int i = 0; i < show; i++)
                    {
                        if (i > 0) sb.Append(", ");
                        sb.Append(o.FailingSeeds[i].ToString(c));
                    }
                    if (o.FailingSeeds.Count > show)
                        sb.Append(" (+").Append((o.FailingSeeds.Count - show).ToString(c)).Append(" more)");
                    sb.Append('\n');
                }
            }
            return sb.ToString();
        }
    }
}
