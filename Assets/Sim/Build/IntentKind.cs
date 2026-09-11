using System.Collections.Generic;
using Godless.Sim.Content;
using Godless.Sim.Core;
using Godless.Sim.Drives;

namespace Godless.Sim.Build
{
    /// <summary>One kind of thing a settlement can commission, as content declares it.</summary>
    public sealed class IntentKind
    {
        public Symbol Id { get; internal set; }
        public string Name { get; internal set; }

        /// <summary>What a stranger sees when one of these is raised and built. Required.</summary>
        public string Tell { get; internal set; }

        /// <summary>The need whose pressure raises it. Index into the NeedTable.</summary>
        public int Answers { get; internal set; }

        /// <summary>Accumulated pressure, in need-over-threshold agent-ticks, that raises one.</summary>
        public double Threshold { get; internal set; }

        /// <summary>How many may stand open at once. Pressure past that makes them more urgent, not more numerous.</summary>
        public int MaxOpen { get; internal set; }

        /// <summary>Days for unanswered pressure to halve. What stops one wet night a year adding up to a house.</summary>
        public double HalfLifeDays { get; internal set; }

        /// <summary>
        /// Voxels the settlement is prepared to spend. A volume, not a list of
        /// materials: which material is S19's answer, against what is in stock
        /// when it is built — so the same intent reads differently in timber
        /// country and in stone country.
        /// </summary>
        public int BudgetVoxels { get; internal set; }
    }

    /// <summary>
    /// Every intent kind the loaded content declares, in stable-hash order. S14.
    ///
    /// Stratum 1 ships one, shelter, per the doc's "one building type". The
    /// second intent kind is S2A's, at stratum 2, and it is a monument.
    /// </summary>
    public sealed class IntentKindTable
    {
        readonly IntentKind[] _kinds;
        readonly List<string> _problems;

        IntentKindTable(IntentKind[] kinds, List<string> problems) { _kinds = kinds; _problems = problems; }

        public int Count { get { return _kinds.Length; } }
        public IntentKind this[int index] { get { return _kinds[index]; } }
        public IReadOnlyList<IntentKind> All { get { return _kinds; } }
        public IReadOnlyList<string> Problems { get { return _problems; } }

        public static IntentKindTable FromContent(ContentDatabase content, NeedTable needs)
        {
            var problems = new List<string>();
            var loaded = new List<IntentKind>();

            foreach (string id in content.Ids("intent"))
            {
                JsonValue doc = content.Get("intent", id);
                string tell = doc["tell"].AsString(null);
                string answers = doc["answers"].AsString("");
                int need = needs.IndexOf(answers);
                double threshold = doc["threshold"].AsDouble(0.0);
                int maxOpen = doc["maxOpen"].AsInt32(1);
                double halfLife = doc["halfLifeDays"].AsDouble(30.0);

                string fault = null;
                if (string.IsNullOrEmpty(tell) || tell.Trim().Length == 0) fault = "declares no tell";
                else if (need < 0) fault = "answers need '" + answers + "', which is not loaded";
                else if (!(threshold > 0.0)) fault = "has a threshold that is not above zero";
                else if (maxOpen < 1) fault = "has maxOpen below one";
                else if (!(halfLife > 0.0)) fault = "has a half-life that is not above zero";
                if (fault != null)
                {
                    problems.Add("intent '" + id + "' " + fault + ".");
                    continue;
                }

                loaded.Add(new IntentKind
                {
                    Id = Symbol.For("intent." + id),
                    Name = id,
                    Tell = tell.Trim(),
                    Answers = need,
                    Threshold = threshold,
                    MaxOpen = maxOpen,
                    HalfLifeDays = halfLife,
                    BudgetVoxels = doc["budgetVoxels"].AsInt32(0),
                });
            }

            loaded.Sort((a, b) => a.Id.CompareTo(b.Id));

            // One kind per need, or two kinds would race for the same pressure.
            // The first in stable-hash order keeps it, so which one survives
            // does not depend on load order.
            var kept = new List<IntentKind>();
            foreach (IntentKind k in loaded)
            {
                IntentKind rival = kept.Find(o => o.Answers == k.Answers);
                if (rival != null)
                {
                    problems.Add("intent '" + k.Name + "' answers need '" + needs[k.Answers].Name
                                 + "', which intent '" + rival.Name + "' already answers.");
                    continue;
                }
                kept.Add(k);
            }
            return new IntentKindTable(kept.ToArray(), problems);
        }

        /// <summary>The kind a need's pressure raises, or -1. One per need: two would race.</summary>
        public int KindFor(int need)
        {
            for (int i = 0; i < _kinds.Length; i++) if (_kinds[i].Answers == need) return i;
            return -1;
        }
    }
}
