using System.Collections.Generic;
using Godless.Sim.Annals;
using Godless.Sim.Build;
using Godless.Sim.Content;
using Godless.Sim.Core;
using Godless.Sim.Harness;
using Godless.Sim.World;

namespace Godless.Sim.Settlements
{
    /// <summary>How towns hold ground and when one outgrows itself, as content declares it (`towns/*.json`). S2Y.</summary>
    public sealed class TownRules
    {
        public string Tell { get; private set; }

        /// <summary>Parcels of land a town holds beyond its fire and everything it has claimed.</summary>
        public int BorderParcels { get; private set; }

        /// <summary>Share of a town with no roof of their own that counts as a homeless population worth leaving over.</summary>
        public double HomelessShare { get; private set; }

        /// <summary>People with no roof, at the least, before any leave.</summary>
        public int HomelessAtLeast { get; private set; }

        /// <summary>Days the homeless must have gone unhoused, with no ground found for a house, before they leave.</summary>
        public int HomelessDays { get; private set; }

        /// <summary>How recently the town must have failed to find ground for a house.</summary>
        public int NoSiteWithinDays { get; private set; }

        /// <summary>Parcels a new town keeps from every other town's fire, at the least.</summary>
        public int NearestTownParcels { get; private set; }

        /// <summary>Parcels from the town they leave that people will go to found another, at most.</summary>
        public int FarthestParcels { get; private set; }

        /// <summary>Parcels between the places looked at when searching the map for a new site.</summary>
        public int SearchStepParcels { get; private set; }

        /// <summary>Days between one town sending people away and the next time it may.</summary>
        public int BetweenFoundingsDays { get; private set; }

        /// <summary>Towns there may be on the island, at most.</summary>
        public int MostTowns { get; private set; }

        /// <summary>People, at the least, it takes to found a town.</summary>
        public int FewestFounders { get; private set; }

        public static TownRules FromContent(ContentDatabase content)
        {
            foreach (string id in content.Ids("towns"))
            {
                JsonValue doc = content.Get("towns", id);
                return new TownRules
                {
                    Tell = doc["tell"].AsString(""),
                    BorderParcels = doc["borderParcels"].AsInt32(8),
                    HomelessShare = doc["homelessShare"].AsDouble(0.12),
                    HomelessAtLeast = doc["homelessAtLeast"].AsInt32(10),
                    HomelessDays = doc["homelessDays"].AsInt32(30),
                    NoSiteWithinDays = doc["noSiteWithinDays"].AsInt32(30),
                    NearestTownParcels = doc["nearestTownParcels"].AsInt32(32),
                    FarthestParcels = doc["farthestParcels"].AsInt32(110),
                    SearchStepParcels = doc["searchStepParcels"].AsInt32(3),
                    BetweenFoundingsDays = doc["betweenFoundingsDays"].AsInt32(365),
                    MostTowns = doc["mostTowns"].AsInt32(12),
                    FewestFounders = doc["fewestFounders"].AsInt32(8),
                };
            }
            return null;
        }
    }

    /// <summary>
    /// Town borders. S2Y.
    ///
    /// Every parcel of land belongs to at most one town: the town whose fire
    /// or whose claims — houses, stores, fields — lie nearest, within a reach
    /// beyond them. A town's border grows as it builds and farms outward, and
    /// stops where a neighbour's begins. No town builds or ploughs inside
    /// another's border.
    ///
    /// The tell: a line round each village that swells as it grows, and two
    /// villages whose lines meet and go no further.
    /// </summary>
    public sealed class Territory
    {
        readonly Settlement[] _owner = new Settlement[ParcelGrid.Width * ParcelGrid.Depth];

        /// <summary>Changes every time the borders are redrawn, so a view knows when to redraw its lines.</summary>
        public int Version { get; private set; }

        /// <summary>The town a parcel belongs to, or null.</summary>
        public Settlement Owner(int px, int pz)
        {
            return ParcelGrid.InBounds(px, pz) ? _owner[pz * ParcelGrid.Width + px] : null;
        }

        /// <summary>Whether a parcel lies inside some other town's border.</summary>
        public bool BelongsToAnother(Settlement s, int px, int pz)
        {
            Settlement o = Owner(px, pz);
            return o != null && o != s;
        }

        /// <summary>Parcels a town holds.</summary>
        public int Area(Settlement s)
        {
            int n = 0;
            foreach (Settlement o in _owner) if (o == s) n++;
            return n;
        }

        /// <summary>
        /// Redraws every border: a flood outward over land from each town's
        /// fire and claims at once, a parcel a step, the first town to reach a
        /// parcel keeping it — towns in founding order, parcels in grid order, so
        /// a tie always falls the same way (L2).
        /// </summary>
        public void Redraw(IReadOnlyList<Settlement> towns, ParcelGrid grid, int reach)
        {
            System.Array.Clear(_owner, 0, _owner.Length);
            var frontier = new List<int>();

            foreach (Settlement s in towns)
            {
                Seed(s, s.HearthParcelZ * ParcelGrid.Width + s.HearthParcelX, grid, frontier);
                foreach (int parcel in s.Claims) Seed(s, parcel, grid, frontier);
            }

            for (int step = 1; step <= reach && frontier.Count > 0; step++)
            {
                var next = new List<int>();
                foreach (int at in frontier)
                {
                    Settlement s = _owner[at];
                    int ax = at % ParcelGrid.Width, az = at / ParcelGrid.Width;
                    for (int dz = -1; dz <= 1; dz++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dz == 0) continue;
                            int x = ax + dx, z = az + dz;
                            if (!ParcelGrid.InBounds(x, z) || !grid.IsLand(x, z)) continue;
                            int p = z * ParcelGrid.Width + x;
                            if (_owner[p] != null) continue;
                            _owner[p] = s;
                            next.Add(p);
                        }
                }
                frontier = next;
            }
            Version++;
        }

        void Seed(Settlement s, int parcel, ParcelGrid grid, List<int> frontier)
        {
            if (parcel < 0 || parcel >= _owner.Length || _owner[parcel] != null) return;
            _owner[parcel] = s;
            frontier.Add(parcel);
        }

        public ulong Digest()
        {
            var d = new Digest();
            for (int i = 0; i < _owner.Length; i++) d.Add(_owner[i] == null ? 0UL : _owner[i].Id.Hash);
            return d.Value;
        }
    }

    /// <summary>
    /// A town that has outgrown its ground. S2Y.
    ///
    /// When a town has nowhere left to build and a real share of it has gone
    /// a month with no roof, the roofless families take their share of the
    /// food and the yard and walk off to light a fire of their own: at the best
    /// ground in reach that no town holds, far enough from every other fire to
    /// grow. They carry the town's culture with them, and the chronicle can say
    /// which town they came from and why they left.
    /// </summary>
    public static class Towns
    {
        public static readonly Symbol OutgrownKind = Symbol.For("town.outgrown");

        /// <summary>Flat dry parcels within ten, at the least, for a site to be worth founding a town on.</summary>
        public const int MinimumRoom = 25;

        /// <summary>People in a town with no roof of their own.</summary>
        public static int Homeless(Settlement s)
        {
            int n = 0;
            foreach (Household h in s.Households) if (!h.Housed) n += h.Size;
            return n;
        }

        /// <summary>Whether the homeless are a substantial share of the town.</summary>
        public static bool ManyHomeless(Settlement s, TownRules rules)
        {
            int homeless = Homeless(s);
            return homeless >= rules.HomelessAtLeast && homeless >= s.People.Count * rules.HomelessShare;
        }

        /// <summary>
        /// The best place on the map for a new town, or false. Looked for on a
        /// coarse lattice within reach of the town people leave: ground where a
        /// fire may be lit, outside every border with a margin, and far enough
        /// from every fire. The few that look best at a glance — room and water —
        /// are appraised in full, and the one whose land offers most to eat and
        /// build with, for the shortest walk, is chosen.
        /// </summary>
        public static bool FindSite(SimWorld world, ContentDatabase content, ParcelGrid grid, BiomeTable biomes,
                                    Territory borders, Settlement from, TownRules rules, out int siteX, out int siteZ)
        {
            return FindSite(world, content, grid, biomes, borders, from, rules, out siteX, out siteZ, null);
        }

        /// <summary>
        /// Why no new town could be sited from a town, for tuning: how many
        /// lattice points each test turned away, in the order they are tried.
        /// </summary>
        public static string WhyNowhere(SimWorld world, ContentDatabase content, ParcelGrid grid, BiomeTable biomes,
                                        Territory borders, Settlement from, TownRules rules)
        {
            var rejected = new int[6];
            int x, z;
            FindSite(world, content, grid, biomes, borders, from, rules, out x, out z, rejected);
            return "water or off the map " + rejected[0] + ", too near a town " + rejected[1] + ", at a border " + rejected[2]
                 + ", unsettleable " + rejected[3] + ", too little room " + rejected[4] + ", failed a full look " + rejected[5];
        }

        static bool FindSite(SimWorld world, ContentDatabase content, ParcelGrid grid, BiomeTable biomes,
                             Territory borders, Settlement from, TownRules rules, out int siteX, out int siteZ, int[] rejected)
        {
            siteX = siteZ = -1;
            var glance = new List<KeyValuePair<double, int>>();
            int step = System.Math.Max(1, rules.SearchStepParcels), far = rules.FarthestParcels;
            for (int pz = from.HearthParcelZ - far; pz <= from.HearthParcelZ + far; pz += step)
                for (int px = from.HearthParcelX - far; px <= from.HearthParcelX + far; px += step)
                {
                    if (!ParcelGrid.InBounds(px, pz) || !grid.IsLand(px, pz)) { Count(rejected, 0); continue; }
                    if (TooNearATown(world, px, pz, rules.NearestTownParcels)) { Count(rejected, 1); continue; }
                    if (borders != null && NearABorder(borders, px, pz, 2)) { Count(rejected, 2); continue; }
                    SiteReport quick = Founding.Appraise(world, null, grid, biomes, px, pz);
                    if (!quick.CanSettle) { Count(rejected, 3); continue; }
                    if (quick.RoomNearby < MinimumRoom) { Count(rejected, 4); continue; }
                    double distance = Distance(px, pz, from.HearthParcelX, from.HearthParcelZ);
                    double score = quick.RoomNearby - quick.Slope * 4.0 - quick.WaterParcels * 2.0 - distance * 0.2;
                    glance.Add(new KeyValuePair<double, int>(score, pz * ParcelGrid.Width + px));
                }
            if (glance.Count == 0) return false;
            glance.Sort((a, b) => { int c = b.Key.CompareTo(a.Key); return c != 0 ? c : a.Value.CompareTo(b.Value); });

            double best = double.NegativeInfinity;
            for (int i = 0; i < glance.Count && i < 16; i++)
            {
                int px = glance[i].Value % ParcelGrid.Width, pz = glance[i].Value / ParcelGrid.Width;
                SiteReport full = Founding.Appraise(world, content, grid, biomes, px, pz);
                if (!full.CanSettle) { Count(rejected, 5); continue; }
                double materials = 0.0;
                foreach (KeyValuePair<string, double> m in full.Materials) materials += m.Value;
                double score = full.ForagePerDay * 0.5 + materials * 40.0 + full.RoomNearby * 0.5
                             - Distance(px, pz, from.HearthParcelX, from.HearthParcelZ) * 0.3;
                if (score > best) { best = score; siteX = px; siteZ = pz; }
            }
            return siteX >= 0;
        }

        static void Count(int[] rejected, int why) { if (rejected != null) rejected[why]++; }

        static bool TooNearATown(SimWorld world, int px, int pz, int nearest)
        {
            foreach (Settlement s in world.Settlements)
                if (System.Math.Abs(s.HearthParcelX - px) < nearest && System.Math.Abs(s.HearthParcelZ - pz) < nearest
                    && Distance(px, pz, s.HearthParcelX, s.HearthParcelZ) < nearest) return true;
            return false;
        }

        static bool NearABorder(Territory borders, int px, int pz, int margin)
        {
            for (int dz = -margin; dz <= margin; dz++)
                for (int dx = -margin; dx <= margin; dx++)
                    if (borders.Owner(px + dx, pz + dz) != null) return true;
            return false;
        }

        /// <summary>
        /// The roofless families of a town leave to found another at a parcel:
        /// on record as the town outgrown, then the new town founded from it,
        /// with those families, a share of the food and the yard by head, and
        /// the old town's culture. Returns the new town.
        /// </summary>
        public static Settlement Emigrate(SimWorld world, ContentDatabase content, ParcelGrid grid, BiomeTable biomes,
                                          Settlement from, int siteX, int siteZ, TownRules rules)
        {
            // The roofless families, largest first, as many as leave half the town behind.
            var roofless = new List<Household>();
            foreach (Household h in from.Households) if (!h.Housed) roofless.Add(h);
            roofless.Sort((a, b) => a.Size != b.Size ? b.Size.CompareTo(a.Size) : a.Number.CompareTo(b.Number));
            var leaving = new List<Household>();
            int movers = 0;
            foreach (Household h in roofless)
            {
                if (movers + h.Size > from.People.Count / 2) continue;
                leaving.Add(h);
                movers += h.Size;
            }
            if (movers < rules.FewestFounders) return null;

            long tick = world.Clock.Tick;
            RecordId why = from.Founded;
            foreach (BuildIntent intent in from.Intents.Intents)
                if (intent.Kind.Purpose == IntentPurpose.Home && (intent.Status == IntentStatus.Abandoned || intent.Status == IntentStatus.Open))
                    why = intent.Record;
            RecordId outgrown = world.Annals.Write(tick, OutgrownKind, from.Id, from.Hearth, why, movers, from.People.Count);
            from.LastOutgrown = outgrown;
            from.LastOutgrownTick = tick;

            string name = "town-" + (world.Settlements.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            Settlement town = Founding.Begin(world, content, grid, biomes, name, 0, siteX, siteZ, from.Genome != null ? from.Genome.Clone() : null,
                                             0, outgrown);

            // A share of what the old town holds, by head.
            double share = (double)movers / from.People.Count;
            double food = from.Food * share;
            from.Food -= food;
            town.Food = food;
            for (int m = 0; m < from.Stock.Materials.Count; m++)
            {
                long take = (long)(from.Stock.Of(m) * share);
                if (take <= 0) continue;
                from.Stock.Remove(m, take);
                town.Stock.Add(m, take);
            }

            // The families go, each as a family of the new town.
            town.HouseholdRules = from.HouseholdRules;
            foreach (Household h in leaving)
            {
                Household arrived = Households.FormIn(town, tick, world.Annals, outgrown);
                arrived.Kin = -1;
                var members = new List<ulong>(h.Members);
                foreach (ulong id in members)
                    for (int i = 0; i < from.People.Count; i++)
                    {
                        if (from.People[i].Id.Hash != id) continue;
                        Drives.Agent a = from.Release(i);
                        town.Welcome(a);
                        Households.Join(town, a, arrived);
                        break;
                    }
            }
            if (from.Tasks != null) from.Tasks.Sync(from, world.Streams);
            town.Tasks = new Collective.TaskBoard(Collective.TaskKindTable.FromContent(content), town,
                                                   Drives.DriveRules.FromContent(content), world.Streams);
            from.LastEmigrationDay = world.Clock.TotalDays;
            return town;
        }

        static double Distance(int ax, int az, int bx, int bz)
        {
            double dx = ax - bx, dz = az - bz;
            return SimMath.Sqrt(dx * dx + dz * dz);
        }
    }

    /// <summary>Redraws the borders and sends the people of an outgrown town to found another, once a day. S2Y.</summary>
    public sealed class TownSystem : ISimSystem
    {
        public static readonly Symbol SystemId = Symbol.For("system.towns");

        readonly TownRules _rules;
        readonly ContentDatabase _content;
        readonly ParcelGrid _grid;
        readonly BiomeTable _biomes;

        public TownSystem(TownRules rules, ContentDatabase content, ParcelGrid grid, BiomeTable biomes)
        {
            _rules = rules; _content = content; _grid = grid; _biomes = biomes;
        }

        /// <summary>The borders every town builds within. Shared by them all.</summary>
        public Territory Borders { get; } = new Territory();

        public Symbol Id { get { return SystemId; } }

        public void Tick(SimWorld world)
        {
            if (_rules == null || _grid == null) return;
            if (!world.Clock.IsFirstTickOfDay && Borders.Version > 0) return;

            Borders.Redraw(world.Settlements, _grid, _rules.BorderParcels);
            foreach (Settlement s in world.Settlements) s.Borders = Borders;
            if (!world.Clock.IsFirstTickOfDay) return;

            long day = world.Clock.TotalDays;
            int count = world.Settlements.Count;
            for (int i = 0; i < count && world.Settlements.Count < _rules.MostTowns; i++)
            {
                Settlement s = world.Settlements[i];
                if (s.Households.Count == 0) continue;
                if (Towns.ManyHomeless(s, _rules)) s.HomelessDays++;
                else { s.HomelessDays = 0; continue; }

                bool noSite = s.LastNoSiteTick >= 0 && world.Clock.Tick - s.LastNoSiteTick <= (long)_rules.NoSiteWithinDays * world.Clock.TicksPerDay;
                bool rested = s.LastEmigrationDay < 0 || day - s.LastEmigrationDay >= _rules.BetweenFoundingsDays;
                if (!noSite || !rested || s.HomelessDays < _rules.HomelessDays) continue;

                int px, pz;
                if (!Towns.FindSite(world, _content, _grid, _biomes, Borders, s, _rules, out px, out pz))
                {
                    s.HomelessDays = 0;   // nowhere at all: look again after another month of it
                    continue;
                }
                Settlement town = Towns.Emigrate(world, _content, _grid, _biomes, s, px, pz, _rules);
                if (town == null) continue;
                town.Borders = Borders;
                s.HomelessDays = 0;
                Borders.Redraw(world.Settlements, _grid, _rules.BorderParcels);
            }
        }
    }
}
