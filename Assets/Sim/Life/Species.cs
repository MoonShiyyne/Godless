using System.Collections.Generic;
using Godless.Sim.Content;
using Godless.Sim.Core;

namespace Godless.Sim.Life
{
    /// <summary>One kind of creature, as content declares it (v2 M1, L5).</summary>
    public sealed class Species
    {
        public Symbol Id { get; internal set; }
        public string Name { get; internal set; }
        public string Singular { get; internal set; }
        public string Plural { get; internal set; }
        public string Tell { get; internal set; }

        /// <summary>People: live in bands with a camp, gather and hunt.</summary>
        public bool Person { get; internal set; }

        /// <summary>Voxels a step, and how far it notices things.</summary>
        public double Speed { get; internal set; }
        public double Sense { get; internal set; }

        public int AdultDays { get; internal set; }
        public int LifeDays { get; internal set; }
        public double BirthsPerYear { get; internal set; }
        public int Litter { get; internal set; }

        public double HungerPerDay { get; internal set; }
        /// <summary>How well it lives off grazing: 0 eats none, 1 is a grazer.</summary>
        public double Graze { get; internal set; }
        public double Health { get; internal set; }
        public double Attack { get; internal set; }
        /// <summary>How much hunger eating one eases.</summary>
        public double Meat { get; internal set; }

        /// <summary>
        /// Whether it keeps in groups, how loosely (voxels a member may stray
        /// from its place before it walks back), and how many a group holds
        /// before its restless members start to leave. People keep to their
        /// band's camp instead of a leader.
        /// </summary>
        public bool Groups { get { return GroupMost > 1; } }
        public double GroupKeep { get; internal set; }
        public int GroupMost { get; internal set; }

        /// <summary>The temperament of its kind: where each creature's own is drawn about, and how widely.</summary>
        public double Bold { get; internal set; }
        public double Social { get; internal set; }
        public double Restless { get; internal set; }
        public double Spread { get; internal set; }
        /// <summary>Most of its own kind within 16 voxels before it stops breeding.</summary>
        public int Crowding { get; internal set; }

        internal int[] Hunts = new int[0], HuntsWhenStarving = new int[0], Flees = new int[0], FightsBack = new int[0];
        internal readonly List<string> HuntNames = new List<string>(), StarvingNames = new List<string>(),
                                       FleeNames = new List<string>(), FightNames = new List<string>();

        /// <summary>Where it lives wild at the world's start: biome names, herds per thousand land parcels, head per herd.</summary>
        public IReadOnlyList<string> WildBiomes { get { return _wildBiomes; } }
        internal readonly List<string> _wildBiomes = new List<string>();
        public double HerdsPerThousandParcels { get; internal set; }

        /// <summary>Fewest of it the wild keeps: below this, a herd wanders in from remote ground each month.</summary>
        public int WildFloor { get; internal set; }
        public int HerdSize { get; internal set; }

        /// <summary>Detail models to draw it with: standing, walking frames, at work (grazing, gathering) and resting.</summary>
        public string StandModel { get; internal set; }
        public IReadOnlyList<string> WalkModels { get { return _walk; } }
        internal readonly List<string> _walk = new List<string>();
        public string ActModel { get; internal set; }
        public string RestModel { get; internal set; }

        public bool IsHunting(int species) { return System.Array.IndexOf(Hunts, species) >= 0; }
        public bool FleesFrom(int species) { return System.Array.IndexOf(Flees, species) >= 0; }
    }

    /// <summary>Every species content declares, in id order. v2 M1.</summary>
    public sealed class SpeciesTable
    {
        readonly List<Species> _all = new List<Species>();
        readonly List<string> _problems = new List<string>();

        public int Count { get { return _all.Count; } }
        public Species this[int i] { get { return _all[i]; } }
        public IReadOnlyList<Species> All { get { return _all; } }
        public IReadOnlyList<string> Problems { get { return _problems; } }

        public int IndexOf(string name)
        {
            for (int i = 0; i < _all.Count; i++) if (_all[i].Name == name) return i;
            return -1;
        }

        public static SpeciesTable FromContent(ContentDatabase content, int daysPerYear = SimClock.DefaultDaysPerYear)
        {
            var t = new SpeciesTable();
            var ids = new List<string>(content.Ids("species"));
            ids.Sort(System.StringComparer.Ordinal);
            foreach (string id in ids)
            {
                JsonValue d = content.Get("species", id);
                var s = new Species
                {
                    Id = Symbol.For("species." + id),
                    Name = id,
                    Singular = d["name"].AsString(id),
                    Plural = d["plural"].AsString(id),
                    Tell = d["tell"].AsString(""),
                    Person = d["person"].AsBool(false),
                    Speed = d["speed"].AsDouble(0.5),
                    Sense = d["sense"].AsDouble(16),
                    AdultDays = (int)(d["adultYears"].AsDouble(1) * daysPerYear),
                    LifeDays = (int)(d["lifeYears"].AsDouble(10) * daysPerYear),
                    BirthsPerYear = d["birthsPerYear"].AsDouble(0.5),
                    Litter = System.Math.Max(1, d["litter"].AsInt32(1)),
                    HungerPerDay = d["hungerPerDay"].AsDouble(0.02),
                    Graze = SimMath.Clamp01(d["graze"].AsDouble(1.0)),
                    Health = d["health"].AsDouble(1.0),
                    Attack = d["attack"].AsDouble(0.0),
                    Meat = d["meat"].AsDouble(0.5),
                    GroupKeep = d["group"]["keep"].AsDouble(8),
                    GroupMost = System.Math.Max(1, d["group"]["most"].AsInt32(1)),
                    Bold = SimMath.Clamp01(d["temperament"]["bold"].AsDouble(0.5)),
                    Social = SimMath.Clamp01(d["temperament"]["social"].AsDouble(0.5)),
                    Restless = SimMath.Clamp01(d["temperament"]["restless"].AsDouble(0.5)),
                    Spread = SimMath.Clamp01(d["temperament"]["spread"].AsDouble(0.2)),
                    Crowding = System.Math.Max(1, d["crowding"].AsInt32(8)),
                };
                Names(d["hunts"], s.HuntNames);
                Names(d["huntsWhenStarving"], s.StarvingNames);
                Names(d["flees"], s.FleeNames);
                Names(d["fightsBack"], s.FightNames);
                JsonValue wild = d["wild"];
                Names(wild["biomes"], s._wildBiomes);
                s.HerdsPerThousandParcels = wild["herdsPerThousandParcels"].AsDouble(0);
                s.HerdSize = System.Math.Max(1, wild["herdSize"].AsInt32(1));
                s.WildFloor = System.Math.Max(0, wild["floor"].AsInt32(0));
                JsonValue model = d["model"];
                s.StandModel = model["stand"].AsString("");
                Names(model["walk"], s._walk);
                s.ActModel = model["act"].AsString(s.StandModel);
                s.RestModel = model["rest"].AsString(s.StandModel);

                string fault = null;
                if (s.Tell.Trim().Length == 0) fault = "declares no tell";
                else if (!(s.Speed > 0.0 && s.Speed <= 4.0)) fault = "moves " + s.Speed + " voxels a step; it must be above 0 and at most 4";
                else if (s.LifeDays <= s.AdultDays) fault = "dies before it is grown";
                else if (!(s.HungerPerDay > 0.0 && s.HungerPerDay < 1.0)) fault = "grows hungry by " + s.HungerPerDay + " a day; it must be a share of one";
                else if (s.StandModel.Length == 0) fault = "names no model to draw it with";
                else if (s.Groups && !(s.GroupKeep > 0.0)) fault = "keeps in groups of " + s.GroupMost + " but keeps " + s.GroupKeep + " voxels from them; it must be above 0";
                if (fault != null) { t._problems.Add("species '" + id + "' " + fault + "."); continue; }
                t._all.Add(s);
            }
            // Who hunts and flees whom, by index, once everything is loaded.
            foreach (Species s in t._all)
            {
                s.Hunts = t.Resolve(s, s.HuntNames, "hunts");
                s.HuntsWhenStarving = t.Resolve(s, s.StarvingNames, "hunts when starving");
                s.Flees = t.Resolve(s, s.FleeNames, "flees");
                s.FightsBack = t.Resolve(s, s.FightNames, "fights back against");
            }
            return t;
        }

        static void Names(JsonValue list, List<string> into)
        {
            for (int i = 0; i < list.Count; i++) { string n = list[i].AsString(""); if (n.Length > 0) into.Add(n); }
        }

        int[] Resolve(Species s, List<string> names, string verb)
        {
            var found = new List<int>();
            foreach (string n in names)
            {
                int i = IndexOf(n);
                if (i < 0) _problems.Add("species '" + s.Name + "' " + verb + " '" + n + "', which is not loaded.");
                else found.Add(i);
            }
            return found.ToArray();
        }
    }
}
