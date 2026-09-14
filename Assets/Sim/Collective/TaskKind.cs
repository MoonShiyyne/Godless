using System.Collections.Generic;
using Godless.Sim.Content;
using Godless.Sim.Core;

namespace Godless.Sim.Collective
{
    /// <summary>
    /// One kind of work, as content declares it, with the numbers of the
    /// response-threshold model that allocates it. S1C.
    /// </summary>
    public sealed class TaskKind
    {
        public Symbol Id { get; internal set; }
        public string Name { get; internal set; }
        public string Tell { get; internal set; }

        /// <summary>What doing it does. Stratum 1 knows one verb: "gather".</summary>
        public string Verb { get; internal set; }

        /// <summary>One task per building material the catchment offers, rather than one task.</summary>
        public bool PerMaterial { get; internal set; }

        /// <summary>Individual thresholds are drawn uniformly from this range at birth.</summary>
        public double ThresholdMin { get; internal set; }
        public double ThresholdMax { get; internal set; }

        /// <summary>Threshold lost for each tick spent on the task: doing it makes you readier to do it.</summary>
        public double Learn { get; internal set; }

        /// <summary>Threshold regained for each working tick spent on something else.</summary>
        public double Forget { get; internal set; }

        /// <summary>Chance per tick of putting it down and looking round again.</summary>
        public double QuitChance { get; internal set; }

        /// <summary>Stimulus added per tick per unit of unmet demand.</summary>
        public double StimulusGrowth { get; internal set; }

        /// <summary>
        /// The loudest demand can call, before work done quiets it. A shortfall
        /// of thousands of meals wants so many foragers and no more: past this,
        /// the people already at it are enough to quiet the call, and the rest
        /// are free for other work. Unbounded unless content says.
        /// </summary>
        public double StimulusCap { get; internal set; } = double.PositiveInfinity;

        /// <summary>Stimulus removed per worker-tick. Work done is what quiets the call for more.</summary>
        public double WorkDone { get; internal set; }

        /// <summary>For gathering: voxels a settlement keeps in hand even with nothing commissioned.</summary>
        public int ReserveVoxels { get; internal set; }
    }

    /// <summary>
    /// Every task kind the loaded content declares, in stable-hash order.
    /// </summary>
    public sealed class TaskKindTable
    {
        readonly TaskKind[] _kinds;
        readonly List<string> _problems;

        TaskKindTable(TaskKind[] kinds, List<string> problems) { _kinds = kinds; _problems = problems; }

        public int Count { get { return _kinds.Length; } }
        public TaskKind this[int index] { get { return _kinds[index]; } }
        public IReadOnlyList<TaskKind> All { get { return _kinds; } }
        public IReadOnlyList<string> Problems { get { return _problems; } }

        /// <summary>Verbs the simulation knows how to carry out.</summary>
        public static readonly string[] KnownVerbs = { "gather", "build", "forage", "haul", "farm" };

        public static TaskKindTable FromContent(ContentDatabase content)
        {
            var problems = new List<string>();
            var loaded = new List<TaskKind>();

            foreach (string id in content.Ids("task"))
            {
                JsonValue doc = content.Get("task", id);
                string tell = doc["tell"].AsString(null);
                string verb = doc["verb"].AsString("");
                JsonValue th = doc["threshold"];
                double lo = th["min"].AsDouble(0.1), hi = th["max"].AsDouble(1.0);

                string fault = null;
                if (string.IsNullOrEmpty(tell) || tell.Trim().Length == 0) fault = "declares no tell";
                else if (System.Array.IndexOf(KnownVerbs, verb) < 0) fault = "has verb '" + verb + "', which the simulation cannot carry out";
                else if (!(lo > 0.0) || hi < lo) fault = "has a threshold range that is not 0 < min <= max";
                if (fault != null) { problems.Add("task '" + id + "' " + fault + "."); continue; }

                loaded.Add(new TaskKind
                {
                    Id = Symbol.For("task." + id),
                    Name = id,
                    Tell = tell.Trim(),
                    Verb = verb,
                    PerMaterial = doc["perMaterial"].AsBool(false),
                    ThresholdMin = lo,
                    ThresholdMax = hi,
                    Learn = doc["learn"].AsDouble(0.0),
                    Forget = doc["forget"].AsDouble(0.0),
                    QuitChance = doc["quitChance"].AsDouble(0.1),
                    StimulusGrowth = doc["stimulusGrowth"].AsDouble(0.001),
                    StimulusCap = doc["stimulusCap"].AsDouble(double.PositiveInfinity),
                    WorkDone = doc["workDone"].AsDouble(0.05),
                    ReserveVoxels = doc["reserveVoxels"].AsInt32(0),
                });
            }

            loaded.Sort((a, b) => a.Id.CompareTo(b.Id));
            return new TaskKindTable(loaded.ToArray(), problems);
        }
    }
}
