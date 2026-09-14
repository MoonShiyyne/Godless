using System.Collections.Generic;
using Godless.Sim.Content;
using Godless.Sim.Core;

namespace Godless.Sim.Drives
{
    /// <summary>One need, as content declares it. Levels run 0 (met) to 1 (desperate).</summary>
    public sealed class Need
    {
        public Symbol Id { get; internal set; }
        public string Name { get; internal set; }

        /// <summary>What a stranger sees when this need goes unmet. Required.</summary>
        public string Tell { get; internal set; }

        /// <summary>Level a new agent starts at.</summary>
        public double Start { get; internal set; }

        /// <summary>Above this level an agent generates pressure for the need.</summary>
        public double Threshold { get; internal set; }

        /// <summary>
        /// Urgency is level raised to this power. Two means a need at half
        /// feels like a quarter and a need at nine tenths like four fifths —
        /// desperate needs dominate, mild ones barely register.
        /// </summary>
        public int UrgencyPower { get; internal set; }

        /// <summary>Added every tick regardless. Negative means it eases on its own.</summary>
        public double Drift { get; internal set; }

        /// <summary>
        /// Relief a tick from being near others while out and about (S2V): per
        /// person within <see cref="NearWithin"/> voxels, counting at most
        /// <see cref="NearMost"/>. Zero for a need company does nothing for.
        /// </summary>
        public double NearRelief { get; internal set; }
        public int NearWithin { get; internal set; } = 4;
        public int NearMost { get; internal set; } = 3;

        /// <summary>Per condition bit: added each tick the condition holds. Signed.</summary>
        internal double[] Rise;

        public double RiseFor(Symbol condition)
        {
            int bit = Conditions.BitOf(condition);
            return bit < 0 ? 0.0 : Rise[bit];
        }

        public double Urgency(double level)
        {
            double u = 1.0;
            for (int i = 0; i < UrgencyPower; i++) u *= level;
            return u;
        }
    }

    /// <summary>
    /// Every need the loaded content declares. S12.
    ///
    /// Part 03's first layer: "cheap utility AI over needs". The needs are
    /// content, ordered by stable hash like genes, so a mod that adds one
    /// never moves another. Stratum 1 ships shelter, warmth, hunger and
    /// safety; grief, reverence and status wait for the systems that produce
    /// and consume them.
    /// </summary>
    public sealed class NeedTable
    {
        readonly Need[] _needs;
        readonly List<string> _problems;

        NeedTable(Need[] needs, List<string> problems) { _needs = needs; _problems = problems; }

        public int Count { get { return _needs.Length; } }
        public Need this[int index] { get { return _needs[index]; } }
        public IReadOnlyList<Need> All { get { return _needs; } }

        /// <summary>Needs that were refused, and why.</summary>
        public IReadOnlyList<string> Problems { get { return _problems; } }

        public static NeedTable FromContent(ContentDatabase content)
        {
            var problems = new List<string>();
            var loaded = new List<Need>();

            foreach (string id in content.Ids("need"))
            {
                JsonValue doc = content.Get("need", id);
                string tell = doc["tell"].AsString(null);
                if (string.IsNullOrEmpty(tell) || tell.Trim().Length == 0)
                {
                    problems.Add("need '" + id + "' declares no tell. A need nobody can see go unmet is "
                                 + "simulation nobody is buying; it is not loaded.");
                    continue;
                }

                double threshold = doc["threshold"].AsDouble(0.5);
                double start = doc["start"].AsDouble(0.0);
                int power = doc["urgencyPower"].AsInt32(2);
                if (threshold < 0.0 || threshold > 1.0 || start < 0.0 || start > 1.0)
                {
                    problems.Add("need '" + id + "' has a threshold or start outside 0..1.");
                    continue;
                }
                if (power < 1 || power > 4)
                {
                    problems.Add("need '" + id + "' has urgencyPower " + power + "; it must be 1 to 4.");
                    continue;
                }

                var rise = new double[Conditions.Known.Count];
                JsonValue rises = doc["rises"];
                string unknown = null;
                for (int i = 0; i < rises.Keys.Count; i++)
                {
                    string name = rises.Keys[i];
                    int bit = Conditions.BitOf(Symbol.For("condition." + name));
                    if (bit < 0) { unknown = name; break; }
                    rise[bit] = rises[name].AsDouble(0.0);
                }
                if (unknown != null)
                {
                    problems.Add("need '" + id + "' reacts to condition '" + unknown + "', which nothing "
                                 + "produces. Known: " + KnownNames() + ".");
                    continue;
                }

                loaded.Add(new Need
                {
                    Id = Symbol.For("need." + id),
                    Name = id,
                    Tell = tell.Trim(),
                    Start = start,
                    Threshold = threshold,
                    UrgencyPower = power,
                    Drift = doc["drift"].AsDouble(0.0),
                    Rise = rise,
                    NearRelief = doc["nearOthers"]["relief"].AsDouble(0.0),
                    NearWithin = doc["nearOthers"]["within"].AsInt32(4),
                    NearMost = doc["nearOthers"]["most"].AsInt32(3),
                });
            }

            loaded.Sort((a, b) => a.Id.CompareTo(b.Id));
            return new NeedTable(loaded.ToArray(), problems);
        }

        static string KnownNames()
        {
            var names = new List<string>();
            foreach (Symbol s in Conditions.Known) names.Add(s.ToString().Substring("condition.".Length));
            return string.Join(", ", names);
        }

        public int IndexOf(Symbol need)
        {
            for (int i = 0; i < _needs.Length; i++) if (_needs[i].Id == need) return i;
            return -1;
        }

        public int IndexOf(string name) { return IndexOf(Symbol.For("need." + name)); }
    }
}
