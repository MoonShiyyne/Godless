using System.Collections.Generic;
using Godless.Sim.Content;
using Godless.Sim.Core;
using Godless.Sim.Drives;

namespace Godless.Sim.Build
{
    /// <summary>What a commissioned thing is for, which decides what finishing it does.</summary>
    public enum IntentPurpose
    {
        /// <summary>A home: sleeping places, beds, a family moves in (S1A, S2N, S2S).</summary>
        Home,
        /// <summary>A store: food kept in it keeps (S2H).</summary>
        Store,
        /// <summary>Fields rather than a building, laid out by the farm system (S2I).</summary>
        Farm,
        /// <summary>A place the whole town gathers, beside its first fire (S2Z): a hall or a colonnade.</summary>
        Commons,
    }

    /// <summary>One kind of thing a settlement can commission, as content declares it.</summary>
    public sealed class IntentKind
    {
        public Symbol Id { get; internal set; }
        public string Name { get; internal set; }

        /// <summary>What a stranger sees when one of these is raised and built. Required.</summary>
        public string Tell { get; internal set; }

        /// <summary>The need whose pressure raises it. Index into the NeedTable; -1 when something other than a need presses for it.</summary>
        public int Answers { get; internal set; }

        /// <summary>
        /// For a kind no need raises: the name of what presses for it instead
        /// (S2H: "spoilage", the food that rots for want of somewhere to keep it).
        /// </summary>
        public string PressedBy { get; internal set; } = "";

        /// <summary>What finishing one does. Content's "purpose"; a home unless it says otherwise.</summary>
        public IntentPurpose Purpose { get; internal set; }

        /// <summary>Meals one finished store keeps (S2H). Zero for anything else.</summary>
        public int StoresMeals { get; internal set; }

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
                string pressedBy = doc["pressedBy"].AsString("");
                int need = answers.Length == 0 ? -1 : needs.IndexOf(answers);
                string purposeName = doc["purpose"].AsString("home");
                double threshold = doc["threshold"].AsDouble(0.0);
                int maxOpen = doc["maxOpen"].AsInt32(1);
                double halfLife = doc["halfLifeDays"].AsDouble(30.0);

                string fault = null;
                if (string.IsNullOrEmpty(tell) || tell.Trim().Length == 0) fault = "declares no tell";
                else if (need < 0 && (answers.Length > 0 || pressedBy.Length == 0)) fault = "answers need '" + answers + "', which is not loaded";
                else if (purposeName != "home" && purposeName != "store" && purposeName != "farm" && purposeName != "commons")
                    fault = "has purpose '" + purposeName + "'; a purpose is home, store, farm or commons";
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
                    PressedBy = pressedBy,
                    Purpose = purposeName == "store" ? IntentPurpose.Store : purposeName == "farm" ? IntentPurpose.Farm
                            : purposeName == "commons" ? IntentPurpose.Commons : IntentPurpose.Home,
                    StoresMeals = doc["storesMeals"].AsInt32(0),
                });
            }

            loaded.Sort((a, b) => a.Id.CompareTo(b.Id));

            // One kind per need, or two kinds would race for the same pressure.
            // The first in stable-hash order keeps it, so which one survives
            // does not depend on load order.
            var kept = new List<IntentKind>();
            foreach (IntentKind k in loaded)
            {
                IntentKind rival = k.Answers < 0 ? null : kept.Find(o => o.Answers == k.Answers);
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
