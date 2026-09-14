using System.Collections.Generic;
using Godless.Sim.Content;
using Godless.Sim.Core;

namespace Godless.Sim.Drives
{
    /// <summary>Where an activity is done (S2V). A person walks there, and it counts once they arrive.</summary>
    public enum ActionPlace { Anywhere, Fire, Store, Home, Bed, Water, People, Task, Wild }

    /// <summary>One thing an agent can spend a tick doing, as content declares it.</summary>
    public sealed class Activity
    {
        /// <summary>Where it is done (S2V). Anywhere means here and now, as before.</summary>
        public ActionPlace At { get; internal set; }

        /// <summary>Meals it takes from the store each time it is done (S2V). Impossible while the store has fewer.</summary>
        public double UsesFood { get; internal set; }

        /// <summary>
        /// The share of a tick it takes (S2V). Below one it is an errand: done
        /// when the need presses, on the side, and the rest of the tick goes to
        /// whatever the person does next.
        /// </summary>
        public double Takes { get; internal set; } = 1.0;

        public bool IsErrand { get { return Takes < 1.0 && !Productive; } }

        /// <summary>Forager-ticks of food it brings to the store each tick it is done (S2V), at what the land gives.</summary>
        public double GathersFood { get; internal set; }

        /// <summary>What a stranger would say someone doing it is doing.</summary>
        public string Doing { get; internal set; }

        /// <summary>How a body looks doing it: stand, sit, kneel, lie, work. Presentation reads it; the sim does not.</summary>
        public string Pose { get; internal set; }
        public Symbol Id { get; internal set; }
        public string Name { get; internal set; }

        /// <summary>Utility with every need met. The floor an urgent need has to beat.</summary>
        public double Base { get; internal set; }

        /// <summary>Conditions that must all hold for this to be possible at all.</summary>
        public ulong Requires { get; internal set; }

        /// <summary>
        /// True for the one kind of activity that is not about the agent:
        /// labour the settlement can spend. S1C splits it into tasks.
        /// </summary>
        public bool Productive { get; internal set; }

        /// <summary>Per need index: how much a tick of this takes off the level.</summary>
        internal double[] Relieves;

        public double ReliefFor(int need) { return Relieves[need]; }

        public bool PossibleUnder(ulong conditions) { return (conditions & Requires) == Requires; }
    }

    /// <summary>
    /// Every activity the loaded content declares, in stable-hash order. S12.
    ///
    /// An activity's utility is its base plus the urgency of every need it
    /// relieves, and an agent does whichever scores highest. That is the
    /// whole of the utility AI, and the part worth noticing is what is not
    /// in the list: building. Agents never decide to build (Part 03). They
    /// only fail to have their needs met, and the failure is pressure.
    /// </summary>
    public sealed class ActivityTable
    {
        readonly Activity[] _activities;
        readonly List<string> _problems;

        ActivityTable(Activity[] activities, List<string> problems) { _activities = activities; _problems = problems; }

        public int Count { get { return _activities.Length; } }
        public Activity this[int index] { get { return _activities[index]; } }
        public IReadOnlyList<Activity> All { get { return _activities; } }
        public IReadOnlyList<string> Problems { get { return _problems; } }

        public static ActivityTable FromContent(ContentDatabase content, NeedTable needs)
        {
            var problems = new List<string>();
            var loaded = new List<Activity>();

            foreach (string id in content.Ids("activity"))
            {
                JsonValue doc = content.Get("activity", id);
                string fault = null;

                ulong requires = 0UL;
                JsonValue req = doc["requires"];
                for (int i = 0; i < req.Count && fault == null; i++)
                {
                    string name = req[i].AsString("");
                    ulong bit = Conditions.Mask(Symbol.For("condition." + name));
                    if (bit == 0UL) fault = "requires condition '" + name + "', which nothing produces";
                    requires |= bit;
                }

                var relieves = new double[needs.Count];
                JsonValue rel = doc["relieves"];
                for (int i = 0; i < rel.Keys.Count && fault == null; i++)
                {
                    string name = rel.Keys[i];
                    int n = needs.IndexOf(name);
                    if (n < 0) fault = "relieves need '" + name + "', which is not loaded";
                    else relieves[n] = rel[name].AsDouble(0.0);
                }

                ActionPlace at = ActionPlace.Anywhere;
                string where = doc["at"].AsString("anywhere");
                switch (where)
                {
                    case "anywhere": at = ActionPlace.Anywhere; break;
                    case "fire": at = ActionPlace.Fire; break;
                    case "store": at = ActionPlace.Store; break;
                    case "home": at = ActionPlace.Home; break;
                    case "bed": at = ActionPlace.Bed; break;
                    case "water": at = ActionPlace.Water; break;
                    case "people": at = ActionPlace.People; break;
                    case "task": at = ActionPlace.Task; break;
                    case "wild": at = ActionPlace.Wild; break;
                    default: if (fault == null) fault = "is done at '" + where + "', which is not a place anyone can go"; break;
                }

                if (fault != null)
                {
                    problems.Add("activity '" + id + "' " + fault + ".");
                    continue;
                }

                loaded.Add(new Activity
                {
                    Id = Symbol.For("activity." + id),
                    Name = id,
                    Base = doc["base"].AsDouble(0.0),
                    Requires = requires,
                    Productive = doc["productive"].AsBool(false),
                    Relieves = relieves,
                    At = at,
                    UsesFood = doc["uses"]["food"].AsDouble(0.0),
                    GathersFood = doc["gathers"]["food"].AsDouble(0.0),
                    Takes = SimMath.Clamp(doc["takes"].AsDouble(1.0), 0.05, 1.0),
                    Doing = doc["doing"].AsString(id),
                    Pose = doc["pose"].AsString("stand"),
                });
            }

            loaded.Sort((a, b) => a.Id.CompareTo(b.Id));
            return new ActivityTable(loaded.ToArray(), problems);
        }

        public int IndexOf(string name)
        {
            Symbol id = Symbol.For("activity." + name);
            for (int i = 0; i < _activities.Length; i++) if (_activities[i].Id == id) return i;
            return -1;
        }
    }

    /// <summary>The needs and activities one world runs on, loaded once.</summary>
    public sealed class DriveRules
    {
        public NeedTable Needs { get; private set; }
        public ActivityTable Activities { get; private set; }

        public DriveRules(NeedTable needs, ActivityTable activities) { Needs = needs; Activities = activities; }

        public static DriveRules FromContent(ContentDatabase content)
        {
            NeedTable needs = NeedTable.FromContent(content);
            return new DriveRules(needs, ActivityTable.FromContent(content, needs));
        }

        public IReadOnlyList<string> Problems()
        {
            var all = new List<string>(Needs.Problems);
            all.AddRange(Activities.Problems);
            return all;
        }
    }
}
