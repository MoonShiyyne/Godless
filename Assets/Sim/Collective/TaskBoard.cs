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

        /// <summary>The world a harvest takes voxels out of (S2F). Null where gathering is a sum.</summary>
        public Deltas.VoxelWorld Voxels;

        /// <summary>Where rubble's detail lives, so salvage can take it away (S2T).</summary>
        public Voxels.DetailLayer Details;
        public int TicksPerDay = Core.SimClock.DefaultTicksPerDay;

        /// <summary>How loads are carried (S2X). Null where nothing is heaped and everything goes straight to the yard.</summary>
        public HaulRules Hauling;
    }

    public sealed class TaskBoard
    {
        public const string ThresholdStream = "collective.threshold";

        /// <summary>How fast a task's stimulus follows its demand. A day or two of lag.</summary>
        public const double Smoothing = 0.15;

        readonly TaskKind[] _kind;          // per task
        readonly int[] _material;           // per task; -1 = every material, by yield
        readonly Symbol[] _id;              // per task
        readonly double[] _stimulus;        // per task
        readonly double[] _demand;          // per task, as last computed
        double[] _ordered;                  // per material, voxels the standing plans still want
        double[][] _threshold;              // per agent, per task
        long[][] _work;                     // per agent, per task
        int[] _current;                     // per agent; -1 = free
        List<ulong> _rows = new List<ulong>();   // which person each row belongs to
        readonly bool _hauls;               // a haul task exists, so what is cut is heaped for it (S2X)
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
            foreach (TaskKind k in _kind) if (k.Verb == "haul") _hauls = true;
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
            ComputeDemand(s, work);

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

                // Work that means one task (S2I: someone gone to the fields)
                // takes that task while it is wanted, whatever they were doing.
                string verb = _activities[a.Activity].TaskVerb;
                if (verb.Length > 0)
                {
                    int meant = -1;
                    for (int t = 0; t < tasks; t++) if (_kind[t].Verb == verb && _demand[t] > 0.0) { meant = t; break; }
                    j = meant;
                    if (j >= 0 && !Do(s, j, a, work, rng)) j = -1;
                    _current[i] = j;
                    if (j >= 0) { workers[j]++; _work[i][j]++; } else _idle++;
                    Learn(i, j);
                    continue;
                }
                if (j >= 0 && (_demand[j] <= 0.0 || Roll(rng) < _kind[j].QuitChance)) j = -1;
                if (j < 0)
                {
                    // Tasks are considered in the order they press on this
                    // person — the loudest call against their own threshold
                    // first — and they take up the first that fires. Scanning
                    // in a random order instead spreads the hands evenly over
                    // everything that is wanted at all, which is how a
                    // settlement starves beside a full timber yard.
                    for (int k = 0; k < tasks; k++)
                    {
                        int t = Loudest(i, k);
                        if (t < 0) break;
                        double st = _stimulus[t], th = _threshold[i][t];
                        double p = st * st / (st * st + th * th);
                        if (Roll(rng) < p) { j = t; break; }
                    }
                }
                if (j >= 0 && !Do(s, j, a, work, rng)) j = -1;   // nothing to do after all
                _current[i] = j;
                if (j >= 0) { workers[j]++; _work[i][j]++; }
                if (j < 0) _idle++;

                Learn(i, j);
            }

            // Stimulus follows what is wanted now, smoothed, rather than
            // accumulating what was ever wanted. Letting it pile up made it a
            // record of the settlement's history instead of its state: a
            // village that went hungry once kept foraging for years while the
            // houses it needed went unbuilt.
            for (int t = 0; t < tasks; t++)
            {
                double call = _kind[t].StimulusGrowth * _demand[t];
                if (call > _kind[t].StimulusCap) call = _kind[t].StimulusCap;
                double target = call - _kind[t].WorkDone * workers[t];
                if (target < 0.0) target = 0.0;
                _stimulus[t] += (target - _stimulus[t]) * Smoothing;
                if (_stimulus[t] < 0.0) _stimulus[t] = 0.0;
            }
        }

        static double Roll(RngStream rng) { return rng.NextInt(1000000) / 1000000.0; }

        /// <summary>Doing a task lowers a person's threshold for it; not doing the others raises theirs.</summary>
        void Learn(int person, int doing)
        {
            for (int t = 0; t < _kind.Length; t++)
            {
                TaskKind k = _kind[t];
                double th = _threshold[person][t] + (t == doing ? -k.Learn : k.Forget);
                _threshold[person][t] = th < k.ThresholdMin ? k.ThresholdMin : (th > k.ThresholdMax ? k.ThresholdMax : th);
            }
        }

        /// <summary>
        /// The task with the kth loudest call for this person: its stimulus
        /// against their own threshold for it. Tasks nothing wants are silent.
        /// </summary>
        int Loudest(int agent, int rank)
        {
            double ceiling = double.PositiveInfinity;
            int found = -1;
            for (int pass = 0; pass <= rank; pass++)
            {
                int at = -1;
                double best = double.NegativeInfinity;
                for (int t = 0; t < _kind.Length; t++)
                {
                    if (_demand[t] <= 0.0) continue;
                    double loud = _stimulus[t] / _threshold[agent][t];
                    if (loud >= ceiling) continue;      // taken by an earlier pass
                    if (loud > best) { best = loud; at = t; }
                }
                if (at < 0) return -1;
                found = at;
                ceiling = best;
            }
            return found;
        }

        /// <summary>A tick of the task. False when there turned out to be nothing to do.</summary>
        bool Do(Settlement s, int task, Agent agent, WorkSite work, RngStream rng)
        {
            if (_kind[task].Verb == "build")
            {
                if (work == null || work.Builder == null) return false;
                return work.Builder.Work(s, agent, work.Grid, work.Tick, work.Annals, rng);
            }

            if (_kind[task].Verb == "farm")
            {
                if (work == null || s.Farms.Count == 0) return false;
                return Farms.Work(s, agent, work.Tick, work.Annals);
            }

            if (_kind[task].Verb == "haul")
            {
                if (work == null || work.Hauling == null) return false;
                return Hauling.Carry(s, agent, work.Hauling, work.Details, work.Tick);
            }

            if (_kind[task].Verb == "forage")
            {
                if (s.Catchment == null || s.Catchment.FoodPerLabourTick <= 0.0) return false;
                s.Food += s.Catchment.Forage(agent.LabourShare);
                return true;
            }

            int m = _material[task];

            // Rubble first (S2T): what fell is already cut, and lies closer than any wood.
            if (m >= 0 && s.Rubble.Count > 0 && work != null && work.Voxels != null && Collapse.HasRubble(s, m))
            {
                int most = (int)SimMath.Round(s.Stock.Materials[m].PerLabourTick);
                Collapse.Salvage(work.Voxels, work.Details, s, m, most < 1 ? 1 : most, work.Tick);
                agent.WorkingAt = -1;
                return true;
            }

            if (m >= 0)
            {
                if (s.Catchment.HasDeposits && work != null && work.Voxels != null)
                {
                    // S2F: the nearest tree with anything left comes down.
                    int worked = s.Catchment.Harvest(m, agent.LabourShare, s.Stock, work.Voxels, work.Tick, work.TicksPerDay,
                                                     work.Annals, s.Id, s.Hearth, s.Founded,
                                                     _hauls && work.Hauling != null ? s : null);
                    if (worked < 0) return false;
                    agent.WorkingAt = worked;
                    return true;
                }
                s.Stock.Gather(m, agent.LabourShare, s.Catchment);
                return true;
            }

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
        void ComputeDemand(Settlement s, WorkSite work)
        {
            int count = s.Stock.Materials.Count;
            if (_ordered == null || _ordered.Length != count) _ordered = new double[count];
            for (int m = 0; m < count; m++) _ordered[m] = 0.0;

            // What the standing plans actually call for, material by material.
            //
            // This used to split the whole building budget across materials in
            // proportion to how *easily* each could be gathered, which made the
            // yard a mirror of the landscape rather than of the plans. Adding
            // two materials to content was then enough to starve a house that
            // wanted neither: every share shrank, nobody fetched enough oak,
            // and the walls stopped halfway up. A plan that names its cost is
            // the only thing that should decide what people carry.
            long designed = 0;
            foreach (Project p in s.Projects)
            {
                if (p.Complete || p.Built == null) continue;
                long total = 0;
                for (int m = 0; m < p.Built.Cost.Length && m < count; m++) total += p.Built.Cost[m];
                if (total <= 0) continue;

                long[] owed = Construction.Owed(p);
                for (int m = 0; m < owed.Length && m < count; m++) _ordered[m] += owed[m];
                designed += total;
            }

            // Intents nobody has designed yet have a budget but no bill of
            // materials, so those still spread by what the land gives — it is
            // the best guess available before a site is chosen.
            long commissioned = 0;
            if (s.Intents != null)
                foreach (BuildIntent intent in s.Intents.Intents)
                    if (intent.Outstanding) commissioned += intent.BudgetVoxels;
            commissioned -= designed;
            if (commissioned < 0) commissioned = 0;

            double yieldSum = 0.0;
            long held = 0;
            // Held includes what lies in heaps waiting to be carried (S2X): it is cut already.
            var heaped = new double[count];
            foreach (Pile p in s.Piles) if (!p.IsFood && p.Material < count) heaped[p.Material] += p.Amount;
            for (int m = 0; m < count; m++) { yieldSum += s.Catchment.YieldPerLabourTick(m); held += s.Stock.Of(m) + (long)heaped[m]; }

            for (int t = 0; t < _kind.Length; t++)
            {
                // Every demand in the same unit — ticks of work it would take
                // — so a settlement can weigh a house against a meal.
                if (_kind[t].Verb == "build") { _demand[t] = Construction.Remaining(s) / 4.0; continue; }
                if (_kind[t].Verb == "farm")
                {
                    // A tick in a field is worth many ticks foraging, and it is
                    // only a few ticks: while food is short the plots waiting
                    // call as loud as the hunger does, so they get their hands
                    // before the whole village goes back to the woods.
                    double fields = Farms.Wanted(s);
                    _demand[t] = fields > 0.0 ? System.Math.Max(fields, FoodDemand(s) * 1.5) : 0.0;
                    continue;
                }
                if (_kind[t].Verb == "haul")
                {
                    // Loads lying about. Food lying in a field rots by the day, so
                    // while any is out there it calls as loud as the hunger it
                    // would answer — a harvest is fetched before it is lost.
                    double loads = work != null ? Hauling.Loads(s, work.Hauling) : 0.0;
                    if (loads > 0.0 && s.HarvestToFetch >= 1.0) loads = System.Math.Max(loads, FoodDemand(s) * 1.2 + 1.0);
                    _demand[t] = loads;
                    continue;
                }
                if (_kind[t].Verb == "forage")
                {
                    // Demand for food and demand for timber are both numbers,
                    // and they are not the same number: a settlement asking
                    // for three thousand voxels of house will outvote an empty
                    // store and starve beside a full yard, which is what
                    // happened. An emptying store multiplies its own demand,
                    // so hunger takes the hands it needs and gives them back.
                    // Loud as the hunger, but the task's stimulus cap (content)
                    // stops it drowning every other call once enough are at it:
                    // without one, at the food ceiling nothing else ever got a
                    // hand again and no house went up for ten years.
                    double shortfall = Subsistence.Wanted(s) - s.Food - Hauling.Piled(s, -1);
                    double rate = s.Catchment != null && s.Catchment.ForageRateNow > 0.0 ? s.Catchment.ForageRateNow : 1.0;
                    _demand[t] = shortfall <= 0.0 ? 0.0 : shortfall / rate * FoodUrgency(s);
                    continue;
                }

                int m = _material[t];
                double share = m >= 0 && yieldSum > 0.0
                    ? commissioned * s.Catchment.YieldPerLabourTick(m) / yieldSum : 0.0;
                double want = m >= 0
                    ? _kind[t].ReserveVoxels * (yieldSum > 0.0 ? s.Catchment.YieldPerLabourTick(m) / yieldSum : 0.0)
                      + _ordered[m] + share
                    : _kind[t].ReserveVoxels + commissioned + designed;
                double d = m >= 0 ? want - s.Stock.Of(m) - heaped[m] : want - held;
                double per = m >= 0 ? s.Catchment.YieldPerLabourTick(m) : 1.0;

                // Nothing of it left in reach: wanting it does not make it
                // gatherable, and a call nobody can answer only takes hands
                // away from one they can (S2F).
                if (m >= 0 && per <= 0.0 && Collapse.HasRubble(s, m)) per = s.Stock.Materials[m].PerLabourTick;
                if (m >= 0 && per <= 0.0 && s.Catchment.HasDeposits) { _demand[t] = 0.0; continue; }
                if (per <= 0.0) per = 1.0;
                _demand[t] = d > 0.0 ? d / per : 0.0;
            }
        }

        /// <summary>
        /// Food wanted, in ticks of foraging it would take: the shortfall
        /// against a season in the store, louder the emptier the store.
        /// </summary>
        static double FoodDemand(Settlement s)
        {
            double shortfall = Subsistence.Wanted(s) - s.Food - Hauling.Piled(s, -1);
            if (shortfall <= 0.0) return 0.0;
            return shortfall / FoodRate(s) * FoodUrgency(s);
        }

        /// <summary>Meals a tick of foraging brings at the full rate.</summary>
        static double FoodRate(Settlement s)
        {
            return s.Catchment != null && s.Catchment.FoodPerLabourTick > 0.0 ? s.Catchment.FoodPerLabourTick : 1.0;
        }

        /// <summary>How loud an emptying store makes food: once, up to ten times with nothing left.</summary>
        static double FoodUrgency(Settlement s)
        {
            double days = s.People.Count > 0 ? s.Food / (s.People.Count * Subsistence.MealsADay) : 30.0;
            return days >= 30.0 ? 1.0 : 1.0 + 9.0 * (30.0 - days) / 30.0;
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
        readonly HaulRules _hauling;

        /// <param name="builder">What a build task does, or null in a world with nothing to build.</param>
        /// <param name="hauling">How loads are carried (S2X), or null for a world where everything goes straight to the yard.</param>
        public TaskSystem(Construction builder = null, World.ParcelGrid grid = null, HaulRules hauling = null)
        {
            _builder = builder;
            _grid = grid;
            _hauling = hauling;
        }

        public Symbol Id { get { return SystemId; } }

        public void Tick(SimWorld world)
        {
            RngStream rng = world.Streams.Get(StreamId);
            var work = new WorkSite
            {
                Builder = _builder, Grid = _grid, Annals = world.Annals, Tick = world.Clock.Tick,
                Voxels = world.Voxels, TicksPerDay = world.Clock.TicksPerDay, Details = world.Details,
                Hauling = _hauling,
            };
            foreach (Settlement s in world.Settlements)
                if (s.Tasks != null) s.Tasks.Step(s, rng, work);
        }
    }
}
