using System.Collections.Generic;
using Godless.Sim.Annals;
using Godless.Sim.Build;
using Godless.Sim.Content;
using Godless.Sim.Core;
using Godless.Sim.Culture;
using Godless.Sim.Drives;

namespace Godless.Sim.Settlements
{
    /// <summary>
    /// A family: the people who share a hearth and a roof. S2N.
    ///
    /// Until this nobody lived anywhere in particular: the roofs went each night
    /// to whoever needed one most, so a settlement one bed short spread the
    /// misery thin, never asked for a house, and stopped growing at its beds
    /// plus one for ever. A household owns its home, so a crowded family is
    /// crowded at its own door, and it is that family — not an average over the
    /// village — that asks for more room.
    /// </summary>
    public sealed class Household
    {
        internal readonly List<ulong> Members = new List<ulong>();
        internal readonly List<Project> Homes = new List<Project>();

        public int Number { get; internal set; }

        /// <summary>The family this one split from (S2N), by number, or -1 for founders.</summary>
        public int Kin { get; internal set; } = -1;
        public Symbol Id { get; internal set; }
        public RecordId Formed { get; internal set; }

        public int Size { get { return Members.Count; } }
        public IReadOnlyList<Project> Home { get { return Homes; } }
        public bool Housed { get { return Homes.Count > 0; } }

        /// <summary>Beds under this family's own roofs, after anyone who shares them.</summary>
        public int Beds { get; internal set; }

        public bool Crowded { get { return Housed && Members.Count > Beds; } }
    }

    /// <summary>How big families grow, how fast people are born and die. Content (S2N).</summary>
    public sealed class HouseholdRules
    {
        Expr _size, _splits;

        public double BirthsPerPersonYear { get; private set; }
        public double DeathsPerPersonYear { get; private set; }
        public double CrowdedFertility { get; private set; }
        public string Tell { get; private set; }

        /// <summary>Null when content declares no households: stratum 1's roofs-by-need then stands.</summary>
        public static HouseholdRules FromContent(ContentDatabase content)
        {
            if (!content.Contains("households", "base")) return null;
            JsonValue doc = content.Get("households", "base");
            var r = new HouseholdRules
            {
                BirthsPerPersonYear = doc["birthsPerPersonYear"].AsDouble(0.3),
                DeathsPerPersonYear = doc["deathsPerPersonYear"].AsDouble(0.015),
                CrowdedFertility = SimMath.Clamp01(doc["crowdedFertility"].AsDouble(0.3)),
                Tell = doc["tell"].AsString(""),
            };
            try
            {
                r._size = Expr.Parse(doc["size"].AsString("6"));
                r._splits = Expr.Parse(doc["splitsAbove"].AsString("10"));
            }
            catch (System.Exception e) { throw new ContentException("households/base: " + e.Message); }
            return r;
        }

        public int Size(Genome genome) { return System.Math.Max(1, (int)SimMath.Round(_size.Eval(new GeneScope(genome)))); }

        public int SplitsAbove(Genome genome)
        {
            return System.Math.Max(Size(genome) + 1, (int)SimMath.Round(_splits.Eval(new GeneScope(genome))));
        }

        sealed class GeneScope : IExprScope
        {
            readonly Genome _genome;
            public GeneScope(Genome genome) { _genome = genome; }
            public double Resolve(string name)
            {
                if (_genome == null || !name.StartsWith("gene.", System.StringComparison.Ordinal)) return 0.5;
                double v = _genome[Symbol.For(name)];
                return double.IsNaN(v) ? 0.5 : v;
            }
        }
    }

    /// <summary>Forming, joining, splitting and housing families. S2N.</summary>
    public static class Households
    {
        public static readonly Symbol FormedKind = Symbol.For("household.formed");
        public static readonly Symbol MovedInKind = Symbol.For("household.moved-in");

        /// <summary>The founders, divided into families of the culture's size, in roll order.</summary>
        public static void Found(Settlement s, HouseholdRules rules, long tick, Annalist annals)
        {
            s.HouseholdRules = rules;
            int size = rules.Size(s.Genome);
            Household current = null;
            foreach (Agent a in s.People)
            {
                if (current == null || current.Size >= size) current = Form(s, tick, annals, s.Founded);
                Join(s, a, current);
            }
        }

        static Household Form(Settlement s, long tick, Annalist annals, RecordId cause)
        {
            int number = s.NextHousehold++;
            var h = new Household
            {
                Number = number,
                Id = Symbol.For(s.Id + ".household." + number.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            };
            h.Formed = annals.Write(tick, FormedKind, h.Id, s.Hearth, cause, 0, 0);
            s.HouseholdList.Add(h);
            return h;
        }

        public static Household Of(Settlement s, Agent a)
        {
            foreach (Household h in s.HouseholdList) if (h.Number == a.Household) return h;
            return null;
        }

        public static void Join(Settlement s, Agent a, Household h)
        {
            Household was = Of(s, a);
            if (was != null) was.Members.Remove(a.Id.Hash);
            h.Members.Add(a.Id.Hash);
            a.Household = h.Number;
        }

        /// <summary>Someone has gone. An empty family is gone with them, and its roof is free.</summary>
        public static void Leave(Settlement s, Agent a)
        {
            Household h = Of(s, a);
            if (h == null) return;
            h.Members.Remove(a.Id.Hash);
            a.Household = -1;
            if (h.Members.Count == 0) s.HouseholdList.Remove(h);
        }

        /// <summary>
        /// A new child joins a parent's family; a family grown past the
        /// culture's limit splits, the youngest half forming a family of their
        /// own with no roof yet — which is what asks for the next house.
        /// </summary>
        public static void Born(Settlement s, Agent child, Agent parent, long tick, Annalist annals, RecordId birth)
        {
            Household h = Of(s, parent) ?? (s.HouseholdList.Count > 0 ? s.HouseholdList[0] : Form(s, tick, annals, birth));
            Join(s, child, h);

            if (s.HouseholdRules == null || h.Size <= s.HouseholdRules.SplitsAbove(s.Genome)) return;
            Household young = Form(s, tick, annals, birth);
            young.Kin = h.Number;
            int leaving = h.Size / 2;
            var movers = h.Members.GetRange(h.Size - leaving, leaving);
            foreach (ulong id in movers)
                foreach (Agent a in s.People) if (a.Id.Hash == id) { Join(s, a, young); break; }
        }

        /// <summary>
        /// A finished house takes in the families with no roof, largest first,
        /// as many as it holds — several at once in a long house. With nobody
        /// roofless, the most crowded family's overflow moves in as a family of
        /// its own. Returns how many families moved in.
        /// </summary>
        public static int MoveIn(Settlement s, Project home, int capacity, long tick, Annalist annals, RecordId cause)
        {
            if (s.HouseholdList.Count == 0) return 0;
            int free = capacity, moved = 0;

            var homeless = new List<Household>();
            foreach (Household h in s.HouseholdList) if (!h.Housed) homeless.Add(h);

            // The families the house was planned for come first (S2O), then
            // the largest of anyone else with no roof.
            homeless.Sort((a, b) =>
            {
                bool pa = home.ForFamilies.Contains(a.Number), pb = home.ForFamilies.Contains(b.Number);
                if (pa != pb) return pa ? -1 : 1;
                return b.Size != a.Size ? b.Size.CompareTo(a.Size) : a.Number.CompareTo(b.Number);
            });

            foreach (Household h in homeless)
            {
                if (moved > 0 && h.Size > free) continue;
                h.Homes.Add(home);
                free -= h.Size;
                moved++;
                annals.Write(tick, MovedInKind, h.Id, s.Hearth, cause, h.Size, capacity);
                if (free <= 0) break;
            }
            if (moved > 0) return moved;

            // Nobody roofless: the family most over its beds sends its overflow.
            Settle(s);
            Household crowded = null;
            foreach (Household h in s.HouseholdList)
                if (h.Crowded && (crowded == null || h.Size - h.Beds > crowded.Size - crowded.Beds)) crowded = h;
            if (crowded == null) return 0;

            Household overflow = Form(s, tick, annals, cause);
            overflow.Kin = crowded.Number;
            int leaving = System.Math.Min(crowded.Size - crowded.Beds, capacity);
            var movers = crowded.Members.GetRange(crowded.Size - leaving, leaving);
            foreach (ulong id in movers)
                foreach (Agent a in s.People) if (a.Id.Hash == id) { Join(s, a, overflow); break; }
            overflow.Homes.Add(home);
            annals.Write(tick, MovedInKind, overflow.Id, s.Hearth, cause, overflow.Size, capacity);
            return 1;
        }

        /// <summary>
        /// Families with no roof take any standing home with room enough for
        /// all of them, fullest-fitting first — before they lodge, and before
        /// anyone asks for another house. Without this a family formed after
        /// the last house went up slept beside empty ones and asked for more.
        ///
        /// Then a crowded family with no room already being added moves, all
        /// of it, into a standing home nobody holds that sleeps them all,
        /// rather than building on beside an empty house.
        /// </summary>
        public static void Rehouse(Settlement s, long tick, Annalist annals)
        {
            if (s.HouseholdList.Count == 0) return;
            bool anyHomeless = false, anyCrowded = false;
            Settle(s);
            foreach (Household h in s.HouseholdList)
            {
                if (!h.Housed) anyHomeless = true;
                else if (h.Crowded) anyCrowded = true;
            }
            if (!anyHomeless && !anyCrowded) return;

            var homes = new List<Project>();
            foreach (Project p in s.Projects) if (p.Complete && p.Host == null) homes.Add(p);
            if (homes.Count == 0) return;

            var occupied = new int[homes.Count];
            foreach (Household h in s.HouseholdList)
                foreach (Project home in h.Homes)
                {
                    int i = homes.IndexOf(home);
                    if (i >= 0) occupied[i] += h.Size;
                }

            var homeless = new List<Household>();
            foreach (Household h in s.HouseholdList) if (!h.Housed) homeless.Add(h);
            homeless.Sort((a, b) => b.Size != a.Size ? b.Size.CompareTo(a.Size) : a.Number.CompareTo(b.Number));

            foreach (Household h in homeless)
            {
                int best = -1, bestLeft = int.MaxValue;
                for (int i = 0; i < homes.Count; i++)
                {
                    int left = CapacityOf(homes[i]) - occupied[i] - h.Size;
                    if (left >= 0 && left < bestLeft) { bestLeft = left; best = i; }
                }
                if (best < 0) continue;
                h.Homes.Add(homes[best]);
                occupied[best] += h.Size;
                annals.Write(tick, MovedInKind, h.Id, s.Hearth, homes[best].Site.Record, h.Size, CapacityOf(homes[best]));
            }

            Settle(s);
            foreach (Household h in s.HouseholdList)
            {
                if (!h.Housed || !h.Crowded) continue;
                bool adding = false;
                foreach (Project home in h.Homes)
                    foreach (Project added in home.Added) if (!added.Complete) adding = true;
                if (adding) continue;

                int best = -1, bestLeft = int.MaxValue;
                for (int i = 0; i < homes.Count; i++)
                {
                    if (occupied[i] > 0) continue;
                    int left = CapacityOf(homes[i]) - h.Size;
                    if (left >= 0 && left < bestLeft) { bestLeft = left; best = i; }
                }
                if (best < 0) continue;

                foreach (Project home in h.Homes)
                {
                    int was = homes.IndexOf(home);
                    if (was >= 0) occupied[was] -= h.Size;
                }
                h.Homes.Clear();
                h.Homes.Add(homes[best]);
                occupied[best] += h.Size;
                annals.Write(tick, MovedInKind, h.Id, s.Hearth, homes[best].Site.Record, h.Size, CapacityOf(homes[best]));
                Settle(s);
            }
        }

        /// <summary>Beds each family has under its own roofs, homes shared in family order.</summary>
        public static void Settle(Settlement s)
        {
            var used = new Dictionary<int, int>();   // lookup only; never iterated
            foreach (Household h in s.HouseholdList)
            {
                h.Beds = 0;
                foreach (Project home in h.Homes)
                {
                    int key = home.Site.Record.Index, taken;
                    used.TryGetValue(key, out taken);
                    int spare = CapacityOf(home) - taken;
                    int wanted = h.Size - h.Beds;
                    int take = spare < wanted ? spare : wanted;
                    if (take < 0) take = 0;
                    h.Beds += take;
                    used[key] = taken + take;
                }
            }
        }

        /// <summary>Beds under a home: its own, and every wing and storey added to it that stands (S2P).</summary>
        public static int CapacityOf(Project home)
        {
            int beds = home.Plan.Capacity;
            foreach (Project added in home.Additions) if (added.Complete) beds += added.Plan.Capacity;
            return beds;
        }

        /// <summary>
        /// Tonight's roofs. Each family sleeps under its own; whoever does not
        /// fit lodges in a spare bed elsewhere if there is one, and otherwise
        /// sleeps in the open. A family with anyone lodging or outside is
        /// crowded, all of it, at its own door — the pressure that asks for
        /// more room lands where the room is wanted.
        /// </summary>
        public static void AssignRoofs(Settlement s, int shelterNeed)
        {
            Settle(s);
            IReadOnlyList<Agent> people = s.People;
            int ownBeds = 0;
            foreach (Household h in s.HouseholdList) ownBeds += h.Beds;

            // Spare: beds in homes nobody fills, and roofs belonging to no home.
            int spare = System.Math.Max(0, s.ShelterCapacity - ownBeds);

            var byNeed = new List<int>(people.Count);
            for (int i = 0; i < people.Count; i++) byNeed.Add(i);
            if (shelterNeed >= 0)
                byNeed.Sort((x, y) =>
                {
                    int c = people[y].Levels[shelterNeed].CompareTo(people[x].Levels[shelterNeed]);
                    return c != 0 ? c : x.CompareTo(y);
                });

            var bedsLeft = new Dictionary<int, int>();
            foreach (Household h in s.HouseholdList) bedsLeft[h.Number] = h.Beds;

            var waiting = new List<int>();
            foreach (int i in byNeed)
            {
                Agent a = people[i];
                int left;
                if (bedsLeft.TryGetValue(a.Household, out left) && left > 0)
                {
                    bedsLeft[a.Household] = left - 1;
                    a.ShelteredLastNight = true;
                    a.Crowded = false;
                }
                else waiting.Add(i);
            }

            var crowdedFamilies = new HashSet<int>();
            foreach (int i in waiting)
            {
                Agent a = people[i];
                a.ShelteredLastNight = spare > 0;
                if (spare > 0) spare--;
                a.Crowded = a.ShelteredLastNight;
                crowdedFamilies.Add(a.Household);
            }

            // Everyone in a family that did not fit feels it.
            foreach (Agent a in people)
                if (a.ShelteredLastNight && crowdedFamilies.Contains(a.Household)) a.Crowded = true;
        }

        /// <summary>The parcel a person's pressure is felt at: their family's roof, or the fire.</summary>
        public static void PressurePoint(Settlement s, Agent a, out int px, out int pz)
        {
            px = s.HearthParcelX; pz = s.HearthParcelZ;
            Household h = Of(s, a);
            if (h == null || h.Homes.Count == 0) return;
            px = h.Homes[0].Site.ParcelX;
            pz = h.Homes[0].Site.ParcelZ;
        }

        public static void AddTo(Settlement s, ref Digest d)
        {
            foreach (Household h in s.HouseholdList)
            {
                d.Add(h.Number); d.Add(h.Formed.Index); d.Add(h.Beds);
                foreach (ulong m in h.Members) d.Add(m);
                foreach (Project p in h.Homes) d.Add(p.Site.Record.Index);
            }
        }
    }
}
