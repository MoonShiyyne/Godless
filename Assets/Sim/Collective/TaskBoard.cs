using System.Collections.Generic;
using Godless.Sim.Annals;
using Godless.Sim.Build;
using Godless.Sim.Core;
using Godless.Sim.Drives;
using Godless.Sim.Harness;
using Godless.Sim.Settlements;

namespace Godless.Sim.Collective
{
    /// <summary>
    /// Who does what, with nobody deciding. One per settlement. S1C.
    ///
    /// Part 16's response-threshold model: each task carries a stimulus that
    /// grows with unmet demand and falls with work done, and each person has
    /// their own threshold for each task. Someone free to work takes a task up
    /// with probability s² / (s² + θ²). Low-threshold people answer first,
    /// their work quiets the stimulus, and everyone else stays out of it — so
    /// variance in thresholds alone makes specialists. Doing a task lowers
    /// your threshold for it and not doing it raises it, which is how an
    /// identical population becomes woodcutters and quarriers within days.
    /// Take the specialists away and the stimulus climbs until someone else
    /// crosses their own threshold. No manager, and no job-assignment system
    /// to write — this is the thing that deletes it.
    ///
    /// Stratum 1's only verb is gathering. The demand for each material is
    /// what the settlement has commissioned but does not yet hold, split by
    /// how readily the land gives each material up, so the stock comes to
    /// look like the land around it.
    /// </summary>
    /// <summary>What a builder needs to do a tick of building: the site, the ground and the record.</summary>
    public sealed class WorkSite
    {
        public Construction Builder;
        public World.ParcelGrid Grid;
        public Annalist Annals;
        public long Tick;
    }

    public sealed class TaskBoard
    {
        public const string ThresholdStream = "collective.threshold";

        readonly TaskKind[] _kind;          // per task
        readonly int[] _material;           // per task; -1 = every material, by yield
        readonly Symbol[] _id;              // per task
        readonly double[] _stimulus;        // per task
        readonly double[] _demand;          // per task, as last computed
        double[][] _threshold;              // per agent, per task
        long[][] _work;                     // per agent, per task
        int[] _current;                     // per agent; -1 = free
        List<ulong> _rows = new List<ulong>();   // which person each row belongs to
        readonly ActivityTable _activities;
        long _idle;

        public TaskBoard(TaskKindTable kinds, Settlement s, DriveRules rules, StreamRegistry streams)
        {
            _activities = rules.Activities;
            var kind = new List<TaskKind>();
            var material = new List<int>();
            var id = new List<Symbol>();

            MaterialTable materials = s.Stock != null ? s.Stock.Materials : null;
            foreach (TaskKind k in kinds.All)
            {
                if (k.Verb == "gather" && (materials == null || s.Catchment == null)) continue;
                if (k.Verb == "build" || k.Verb == "forage") { kind.Add(k); material.Add(-1); id.Add(k.Id); continue; }
                if (k.Verb == "gather" && k.PerMaterial)
                {
                    for (int m = 0; m < materials.Count; m++)
                    {
                        if (!s.Catchment.Offers(m)) continue;
                        kind.Add(k); material.Add(m);
                        id.Add(Symbol.For("task." + k.Name + "." + materials[m].Name));
                    }
                }
                else
                {
                    kind.Add(k); material.Add(-1); id.Add(k.Id);
                }
            }

            _kind = kind.ToArray();
            _material = material.ToArray();
            _id = id.ToArray();
            int tasks = _kind.Length, people = s.People.Count;
            _stimulus = new double[tasks];
            _demand = new double[tasks];
            _threshold = new double[people][];
            _work = new long[people][];
            _current = new int[people];

            // Each threshold is drawn from the pair of stable ids — the person
            // and the task — so adding a task or a person never reshuffles
            // anyone else's temperament (L2).
            for (int i = 0; i < people; i++)
            {
                _threshold[i] = new double[tasks];
                _work[i] = new long[tasks];
                _current[i] = -1;
                _rows.Add(s.People[i].Id.Hash);
                for (int j = 0; j < tasks; j++) _threshold[i][j] = Draw(s.People[i].Id, j, streams);
            }
        }

        double Draw(Symbol person, int task, StreamRegistry streams)
        {
            RngStream r = streams.Derive(ThresholdStream, StableHash.Combine(person.Hash, _id[task].Hash));
            double u = r.NextInt(1000001) / 1000000.0;
            return _kind[task].ThresholdMin + u * (_kind[task].ThresholdMax - _kind[task].ThresholdMin);
        }

        /// <summary>
        /// Matches the board to who is alive (S1E). Everyone who stays keeps
        /// their thresholds and their history; a newcomer draws their own from
        /// their id, so who they turn out to be does not depend on when they
        /// were born.
        /// </summary>
        public void Sync(Settlement s, StreamRegistry streams)
        {
            int people = s.People.Count, tasks = _kind.Length;
            var threshold = new double[people][];
            var work = new long[people][];
            var current = new int[people];
            var rows = new List<ulong>(people);

            for (int i = 0; i < people; i++)
            {
                Symbol id = s.People[i].Id;
                int old = _rows.IndexOf(id.Hash);
                rows.Add(id.Hash);
                if (old >= 0)
                {
                    threshold[i] = _threshold[old];
                    work[i] = _work[old];
                    current[i] = _current[old];
                    continue;
                }
                threshold[i] = new double[tasks];
                work[i] = new long[tasks];
                current[i] = -1;
                for (int j = 0; j < tasks; j++) threshold[i][j] = Draw(id, j, streams);
            }

            _threshold = threshold;
            _work = work;
            _current = current;
            _rows = rows;
        }

        public int Count { get { return _kind.Length; } }
        public Symbol TaskId(int task) { return _id[task]; }
        public TaskKind KindOf(int task) { return _kind[task]; }

        /// <summary>The material a gathering task brings in, or -1 for a mixed one.</summary>
        public int MaterialOf(int task) { return _material[task]; }

        public double Stimulus(int task) { return _stimulus[task]; }
        public double Demand(int task) { return _demand[task]; }
        public double Threshold(int agent, int task) { return _threshold[agent][task]; }
        public int CurrentTask(int agent) { return _current[agent]; }
        public long WorkBy(int agent, int task) { return _work[agent][task]; }

        /// <summary>Productive ticks that found no task worth taking up.</summary>
        public long IdleTicks { get { return _idle; } }

        public long TotalWork(int task)
        {
            long t = 0;
            for (int i = 0; i < _work.Length; i++) t += _work[i][task];
            return t;
        }

        /// <summary>Test and tooling seam: set a person's threshold directly.</summary>
        public void SetThreshold(int agent, int task, double value) { _threshold[agent][task] = value; }

        public int IndexOf(string taskName)
        {
            Symbol want = Symbol.For(taskName);
            for (int j = 0; j < _id.Length; j++) if (_id[j] == want) return j;
            return -1;
        }

        /// <summary>
        /// One tick. Everyone whose drives chose productive work this tick is
        /// available; <paramref name="absent"/> removes people from that pool,
        /// which is what a death or a departure will do.
        /// </summary>
        public void Step(Settlement s, RngStream rng, WorkSite work = null, System.Func<int, bool> absent = null)
        {
            int tasks = _kind.Length;
            if (tasks == 0) return;
            ComputeDemand(s);

            var workers = new int[tasks];
            IReadOnlyList<Agent> people = s.People;
            for (int i = 0; i < people.Count; i++)
            {
                Agent a = people[i];
                if (a.Activity < 0 || !_activities[a.Activity].Productive) continue;
                if (absent != null && absent(i)) { _current[i] = -1; continue; }

                // Put a task down when nothing more of it is wanted, and
                // otherwise now and then, to look round again.
                int j = _current[i];
                if (j >= 0 && (_demand[j] <= 0.0 || Roll(rng) < _kind[j].QuitChance)) j = -1;
                if (j < 0)
                {
                    int start = rng.NextInt(tasks);
                    for (int k = 0; k < tasks; k++)
                    {
                        int t = (start + k) % tasks;
                        if (_demand[t] <= 0.0) continue;   // nothing of it is wanted
                        double st = _stimulus[t], th = _threshold[i][t];
                        double p = st * st / (st * st + th * th);
                        if (Roll(rng) < p) { j = t; break; }
                    }
                }
                if (j >= 0 && !Do(s, j, a, work, rng)) j = -1;   // nothing to do after all
                _current[i] = j;
                if (j >= 0) { workers[j]++; _work[i][j]++; }
                if (j < 0) _idle++;

                for (int t = 0; t < tasks; t++)
                {
                    TaskKind k = _kind[t];
                    double th = _threshold[i][t] + (t == j ? -k.Learn : k.Forget);
                    _threshold[i][t] = th < k.ThresholdMin ? k.ThresholdMin : (th > k.ThresholdMax ? k.ThresholdMax : th);
                }
            }

            for (int t = 0; t < tasks; t++)
            {
                double st = _stimulus[t] + _kind[t].StimulusGrowth * _demand[t] - _kind[t].WorkDone * workers[t];
                _stimulus[t] = st > 0.0 ? st : 0.0;
            }
        }

        static double Roll(RngStream rng) { return rng.NextInt(1000000) / 1000000.0; }

        /// <summary>A tick of the task. False when there turned out to be nothing to do.</summary>
        bool Do(Settlement s, int task, Agent agent, WorkSite work, RngStream rng)
        {
            if (_kind[task].Verb == "build")
            {
                if (work == null || work.Builder == null) return false;
                return work.Builder.Work(s, agent, work.Grid, work.Tick, work.Annals, rng);
            }

            if (_kind[task].Verb == "forage")
            {
                if (s.Catchment == null || s.Catchment.FoodPerLabourTick <= 0.0) return false;
                s.Food += s.Catchment.FoodPerLabourTick;
                return true;
            }

            int m = _material[task];
            if (m >= 0) { s.Stock.Gather(m, 1.0, s.Catchment); return true; }

            // A mixed gathering task spends the tick on whatever the land gives most readily.
            int best = -1;
            for (int k = 0; k < s.Stock.Materials.Count; k++)
                if (best < 0 || s.Catchment.YieldPerLabourTick(k) > s.Catchment.YieldPerLabourTick(best)) best = k;
            if (best >= 0) s.Stock.Gather(best, 1.0, s.Catchment);
            return true;
        }

        /// <summary>
        /// Gathering demand: what is commissioned plus a reserve, less what is
        /// held, split across materials by how readily the land gives each up.
        /// </summary>
        void ComputeDemand(Settlement s)
        {
            long commissioned = 0;
            if (s.Intents != null)
                foreach (BuildIntent intent in s.Intents.Intents)
                    if (intent.Outstanding) commissioned += intent.BudgetVoxels;

            double yieldSum = 0.0;
            long held = 0;
            for (int m = 0; m < s.Stock.Materials.Count; m++) { yieldSum += s.Catchment.YieldPerLabourTick(m); held += s.Stock.Of(m); }

            for (int t = 0; t < _kind.Length; t++)
            {
                if (_kind[t].Verb == "build") { _demand[t] = Construction.Remaining(s); continue; }
                if (_kind[t].Verb == "forage")
                {
                    double shortfall = Subsistence.Wanted(s) - s.Food;
                    _demand[t] = shortfall > 0.0 ? shortfall : 0.0;
                    continue;
                }

                double want = _kind[t].ReserveVoxels + commissioned;
                int m = _material[t];
                double d = m >= 0
                    ? (yieldSum > 0.0 ? want * s.Catchment.YieldPerLabourTick(m) / yieldSum : 0.0) - s.Stock.Of(m)
                    : want - held;
                _demand[t] = d > 0.0 ? d : 0.0;
            }
        }

        public ulong Digest()
        {
            var d = new Digest();
            for (int t = 0; t < _kind.Length; t++) { d.Add(_id[t].Hash); d.Add(System.BitConverter.DoubleToInt64Bits(_stimulus[t])); }
            for (int i = 0; i < _threshold.Length; i++)
            {
                d.Add(_current[i]);
                for (int t = 0; t < _kind.Length; t++)
                {
                    d.Add(System.BitConverter.DoubleToInt64Bits(_threshold[i][t]));
                    d.Add(_work[i][t]);
                }
            }
            d.Add(_idle);
            return d.Value;
        }
    }

    /// <summary>Runs every settlement's task board each tick, after drives have chosen who is working. S1C.</summary>
    public sealed class TaskSystem : ISimSystem
    {
        public static readonly Symbol SystemId = Symbol.For("system.tasks");
        public const string StreamId = "collective.tasks";

        readonly Construction _builder;
        readonly World.ParcelGrid _grid;

        /// <param name="builder">What a build task does, or null in a world with nothing to build.</param>
        public TaskSystem(Construction builder = null, World.ParcelGrid grid = null)
        {
            _builder = builder;
            _grid = grid;
        }

        public Symbol Id { get { return SystemId; } }

        public void Tick(SimWorld world)
        {
            RngStream rng = world.Streams.Get(StreamId);
            var work = new WorkSite { Builder = _builder, Grid = _grid, Annals = world.Annals, Tick = world.Clock.Tick };
            foreach (Settlement s in world.Settlements)
                if (s.Tasks != null) s.Tasks.Step(s, rng, work);
        }
    }
}
