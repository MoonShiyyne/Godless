using System.Collections.Generic;
using Godless.Sim.Content;
using Godless.Sim.Core;

namespace Godless.Sim.Settlements
{
    /// <summary>When in a person's day a pastime fits. S2W.</summary>
    public enum PastimeWhen
    {
        /// <summary>First thing, before they leave home or the camp.</summary>
        Morning,
        /// <summary>A pause in the middle of work.</summary>
        Midday,
        /// <summary>At the end of the day, before they lie down.</summary>
        Evening,
        /// <summary>A tick with nothing asked of them.</summary>
        Idle,
    }

    /// <summary>Where a pastime is done. S2W.</summary>
    public enum PastimeAt
    {
        /// <summary>Wherever they are.</summary>
        Here,
        /// <summary>A few steps from wherever they are: they walk a little way and back.</summary>
        Nearby,
        /// <summary>At home, for a family with one.</summary>
        Home,
        /// <summary>At the water.</summary>
        Water,
        /// <summary>By the settlement's fire, or wherever the roofless camp.</summary>
        Camp,
    }

    /// <summary>
    /// A small thing people do between the things they must: stoke the
    /// hearth, wash at the river, sit at the door, sharpen a blade. S2W.
    ///
    /// Pastimes meet no need and the drives never choose one. They are what
    /// the time between is spent on, drawn so a village is more than people
    /// walking to their work and back, and chosen by who the person is and
    /// the hour, so the same person does the same few things and neighbours
    /// do different ones. Content, like everything a stranger sees.
    /// </summary>
    public sealed class Pastime
    {
        public string Name { get; internal set; }
        public string Doing { get; internal set; }
        public string Pose { get; internal set; }
        public PastimeAt At { get; internal set; }
        public double Takes { get; internal set; }
        public double Weight { get; internal set; }
        internal bool[] When = new bool[4];

        public bool Fits(PastimeWhen when) { return When[(int)when]; }
    }

    /// <summary>Every pastime content declares, in id order. S2W.</summary>
    public sealed class PastimeTable
    {
        readonly List<Pastime> _all = new List<Pastime>();
        readonly List<string> _problems = new List<string>();

        public IReadOnlyList<Pastime> All { get { return _all; } }
        public IReadOnlyList<string> Problems { get { return _problems; } }

        /// <summary>No pastimes: a village with no content for them spends the time between standing.</summary>
        public static readonly PastimeTable None = new PastimeTable();

        public static PastimeTable FromContent(ContentDatabase content)
        {
            var table = new PastimeTable();
            foreach (string id in content.Ids("pastime"))
            {
                JsonValue doc = content.Get("pastime", id);
                var p = new Pastime
                {
                    Name = id,
                    Doing = doc["doing"].AsString(id),
                    Pose = doc["pose"].AsString("stand"),
                    Takes = doc["takes"].AsDouble(0.1),
                    Weight = doc["weight"].AsDouble(1.0),
                };
                string fault = null;
                switch (doc["at"].AsString("here"))
                {
                    case "here": p.At = PastimeAt.Here; break;
                    case "nearby": p.At = PastimeAt.Nearby; break;
                    case "home": p.At = PastimeAt.Home; break;
                    case "water": p.At = PastimeAt.Water; break;
                    case "camp": p.At = PastimeAt.Camp; break;
                    default: fault = "is done at '" + doc["at"].AsString("") + "', which is not a place for one"; break;
                }
                JsonValue when = doc["when"];
                for (int i = 0; i < when.Count && fault == null; i++)
                    switch (when[i].AsString(""))
                    {
                        case "morning": p.When[(int)PastimeWhen.Morning] = true; break;
                        case "midday": p.When[(int)PastimeWhen.Midday] = true; break;
                        case "evening": p.When[(int)PastimeWhen.Evening] = true; break;
                        case "idle": p.When[(int)PastimeWhen.Idle] = true; break;
                        default: fault = "fits '" + when[i].AsString("") + "', which is not a time of day"; break;
                    }
                if (fault == null && !(p.Takes > 0.0 && p.Takes < 1.0)) fault = "takes " + p.Takes + " of a tick; it must be a share of one";
                if (fault == null && !(p.Weight > 0.0)) fault = "has no weight";
                if (fault != null) { table._problems.Add("pastime '" + id + "' " + fault + "."); continue; }
                table._all.Add(p);
            }
            return table;
        }

        /// <summary>
        /// A pastime for a person at a time of day, or null. Weighted, and
        /// picked by the person and the day and a salt, so it is theirs and
        /// the same on every machine (L2); a pastime whose place they have not
        /// got is passed over.
        /// </summary>
        public Pastime Pick(PastimeWhen when, ulong person, long day, ulong salt, bool housed, bool water)
        {
            double total = 0.0;
            foreach (Pastime p in _all)
                if (Possible(p, when, housed, water)) total += p.Weight;
            if (total <= 0.0) return null;

            ulong h = StableHash.Combine(StableHash.Combine(person, (ulong)day), salt);
            double roll = (h % 1000000UL) / 1000000.0 * total;
            foreach (Pastime p in _all)
            {
                if (!Possible(p, when, housed, water)) continue;
                if (roll < p.Weight) return p;
                roll -= p.Weight;
            }
            return null;
        }

        static bool Possible(Pastime p, PastimeWhen when, bool housed, bool water)
        {
            if (!p.Fits(when)) return false;
            if (p.At == PastimeAt.Home && !housed) return false;
            if (p.At == PastimeAt.Camp && housed) return false;
            if (p.At == PastimeAt.Water && !water) return false;
            return true;
        }
    }
}
