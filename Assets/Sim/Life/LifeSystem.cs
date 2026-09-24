using System.Collections.Generic;
using Godless.Sim.Annals;
using Godless.Sim.Content;
using Godless.Sim.Core;
using Godless.Sim.Harness;
using Godless.Sim.World;

namespace Godless.Sim.Life
{
    /// <summary>How life runs, as content declares it (v2 M1).</summary>
    public sealed class LifeRules
    {
        public double RegrowPerMonth = 0.2;
        public int StarveDays = 20;
        public double CampRadius = 18, CampSearch = 90;
        public int SettleMonths = 12;
        public int StartBands = 3, BandSize = 12;
        public double BandSpacing = 60;
        public string Tell = "";

        public static LifeRules FromContent(ContentDatabase content)
        {
            var r = new LifeRules();
            foreach (string id in content.Ids("life"))
            {
                JsonValue d = content.Get("life", id);
                r.Tell = d["tell"].AsString("");
                r.RegrowPerMonth = SimMath.Clamp01(d["regrowPerMonth"].AsDouble(r.RegrowPerMonth));
                r.StarveDays = System.Math.Max(1, d["starveDays"].AsInt32(r.StarveDays));
                r.CampRadius = d["campRadius"].AsDouble(r.CampRadius);
                r.CampSearch = d["campSearch"].AsDouble(r.CampSearch);
                r.SettleMonths = System.Math.Max(1, d["settleMonths"].AsInt32(r.SettleMonths));
                r.StartBands = System.Math.Max(0, d["startBands"].AsInt32(r.StartBands));
                r.BandSize = System.Math.Max(1, d["bandSize"].AsInt32(r.BandSize));
                r.BandSpacing = d["bandSpacing"].AsDouble(r.BandSpacing);
                break;
            }
            return r;
        }
    }

    /// <summary>Everything alive in a world: the creatures, their groups, and the land's food (v2 M1).</summary>
    public sealed class Living
    {
        public Creatures Creatures { get; } = new Creatures();
        /// <summary>Every group there has been: bands keep their number for ever, a dissolved herd's number is reused.</summary>
        public List<Group> Groups { get; } = new List<Group>();
        public Grazing Grazing { get; internal set; }
        public SpeciesTable Species { get; internal set; }
        public LifeRules Rules { get; internal set; }

        /// <summary>Species that lived on this map when it was made: the ones the wild keeps (the delta has no sheep country).</summary>
        public bool[] Native { get; internal set; } = new bool[0];

        /// <summary>Choices made so far: creatures that joined a group, left one, or struck out and took others with them.</summary>
        public long Joined { get; internal set; }
        public long Left { get; internal set; }
        public long StruckOut { get; internal set; }

        internal readonly Stack<int> FreeGroups = new Stack<int>();

        public Group GroupOf(int number) { return number >= 0 && number < Groups.Count ? Groups[number] : null; }

        /// <summary>Bands of people, in founding order, gone ones included.</summary>
        public IEnumerable<Group> Bands
        {
            get { foreach (Group g in Groups) if (g.IsBand) yield return g; }
        }

        public int CountOf(int species)
        {
            Creatures c = Creatures;
            int n = 0;
            for (int i = 0; i < c.Length; i++) if (c.Alive[i] && c.Species[i] == species) n++;
            return n;
        }
    }

    /// <summary>
    /// Life on the land, a step at a time (v2 M1).
    ///
    /// Every creature is its own: it ages, grows hungry, and does what it
    /// judges most pressing — run from what hunts it, fight what attacks its
    /// own, eat where it stands, chase what it hunts, walk to better grazing —
    /// by its own temperament and at its own pace. Nobody moves a group. A
    /// member keeps near its own place by its leader or its band's camp, as
    /// close as its temperament likes, and about once a month it weighs its
    /// company: stay, leave, strike out and take others with it, or, alone,
    /// join the next of its kind it meets. Well fed and grown, it breeds
    /// unless its kind is crowded round it. It dies of age, hunger, teeth, or
    /// the god.
    ///
    /// Once a month the grazing grows back and every band's leader weighs its
    /// camp.
    ///
    /// The tell: a herd strung out over a meadow, some grazing, some resting,
    /// a straggler trotting to catch up; a bold stag holding its ground while
    /// the rest bolt; a few young deer breaking off and trotting away
    /// together; a lone person walking into a camp and staying.
    /// </summary>
    public sealed class LifeSystem : ISimSystem
    {
        public static readonly Symbol SystemId = Symbol.For("system.life");
        public static readonly Symbol BandFoundedKind = Symbol.For("life.band-founded");
        public static readonly Symbol BandMovedKind = Symbol.For("life.band-moved");
        public static readonly Symbol BandSettledKind = Symbol.For("life.band-settled");
        public static readonly Symbol BandGoneKind = Symbol.For("life.band-gone");
        public static readonly Symbol BandSplitKind = Symbol.For("life.band-split");
        public static readonly Symbol PersonDiedKind = Symbol.For("life.person-died");
        public static readonly Symbol PersonLeftKind = Symbol.For("life.person-left");
        public static readonly Symbol PersonJoinedKind = Symbol.For("life.person-joined");
        public const string StreamId = "life.behaviour";

        /// <summary>Voxels a neighbour-grid cell spans.</summary>
        const int Cell = 8;
        const int CellsX = Voxels.ChunkStore.SizeX / Cell, CellsZ = Voxels.ChunkStore.SizeZ / Cell;

        /// <summary>Hunger a mouthful of grazing eases, and how much grazing it takes.</summary>
        const double BiteEases = 0.12, BiteTakes = 0.06;
        /// <summary>Highest step a creature climbs in a stride, in voxels.</summary>
        const int Climb = 2;
        /// <summary>Steps between a creature's thoughts about its company: about a month, each on its own day.</summary>
        const int WeighEvery = 30;

        readonly ParcelGrid _grid;
        readonly BiomeTable _biomes;
        readonly Living _life;
        readonly int[] _cellStart = new int[CellsX * CellsZ + 1];
        readonly int[] _fill = new int[CellsX * CellsZ + 1];
        static readonly double[] Turns = { 0.0, 0.6, -0.6, 1.2, -1.2, 2.0, -2.0 };
        int[] _cellItems = new int[256];
        readonly List<int> _scratch = new List<int>();
        bool _populated;

        public LifeSystem(ParcelGrid grid, BiomeTable biomes, SpeciesTable species, LifeRules rules)
        {
            _grid = grid;
            _biomes = biomes;
            _life = new Living { Species = species, Rules = rules, Grazing = new Grazing(ParcelGrid.Width * ParcelGrid.Depth) };
        }

        public Symbol Id { get { return SystemId; } }
        public Living Life { get { return _life; } }

        public void Tick(SimWorld world)
        {
            if (world.Life == null) world.Life = _life;
            if (!_populated) { _populated = true; Measure(world); Populate(world); }

            long tick = world.Clock.Tick;
            if (world.Clock.IsFirstTickOfMonth) { Regrow(); WeighCamps(world); Replenish(world); }

            Index();
            RngStream rng = world.Streams.Get(StreamId);
            Creatures c = _life.Creatures;
            int length = c.Length;   // born this step start acting next step
            for (int i = 0; i < length; i++)
                if (c.Alive[i]) Act(world, i, tick, rng);
        }

        // ── the land's food ─────────────────────────────────────────────────

        /// <summary>What each parcel holds at full growth, from its biome; water holds none.</summary>
        void Measure(SimWorld world)
        {
            Grazing g = _life.Grazing;
            for (int pz = 0; pz < ParcelGrid.Depth; pz++)
                for (int px = 0; px < ParcelGrid.Width; px++)
                {
                    int p = pz * ParcelGrid.Width + px;
                    if (!_grid.IsLand(px, pz) || world.Island == null) { g.Capacity[p] = 0.0; g.Food[p] = 0.0; continue; }
                    int b = world.Island.BiomeAt(px * ParcelGrid.Size + 2, pz * ParcelGrid.Size + 2);
                    double cap = b >= 0 ? _biomes.At(b).Grazing : 0.0;
                    cap *= 1.0 - _grid.WetColumns(px, pz) / (double)(ParcelGrid.Size * ParcelGrid.Size);
                    g.Capacity[p] = cap;
                    g.Food[p] = cap;
                }
        }

        void Regrow()
        {
            Grazing g = _life.Grazing;
            double rate = _life.Rules.RegrowPerMonth;
            for (int p = 0; p < g.Food.Length; p++)
            {
                if (g.Capacity[p] <= 0.0) continue;
                double r = g.RainMonths[p] > 0 ? SimMath.Clamp01(rate * 3.0) : rate;
                if (g.RainMonths[p] > 0) g.RainMonths[p]--;
                g.Food[p] += (g.Capacity[p] - g.Food[p]) * r;
            }
        }

        // ── the world's first creatures ─────────────────────────────────────

        /// <summary>
        /// Wild herds where each species lives, and a few bands of people on
        /// good ground near water — so a world is alive the moment it is made.
        /// </summary>
        void Populate(SimWorld world)
        {
            RngStream rng = world.Streams.Get("life.wild");
            SpeciesTable species = _life.Species;
            long tick = world.Clock.Tick;
            for (int s = 0; s < species.Count; s++)
            {
                Species sp = species[s];
                if (sp.WildBiomes.Count == 0 || sp.HerdsPerThousandParcels <= 0.0 || world.Island == null) continue;
                var parcels = new List<int>();
                for (int pz = 4; pz < ParcelGrid.Depth - 4; pz++)
                    for (int px = 4; px < ParcelGrid.Width - 4; px++)
                    {
                        if (!_grid.IsLand(px, pz) || _grid.WetColumns(px, pz) > 0) continue;
                        int b = world.Island.BiomeAt(px * ParcelGrid.Size + 2, pz * ParcelGrid.Size + 2);
                        if (b < 0 || !Contains(sp.WildBiomes, _biomes.At(b).Name)) continue;
                        parcels.Add(pz * ParcelGrid.Width + px);
                    }
                int herds = (int)SimMath.Round(parcels.Count * sp.HerdsPerThousandParcels / 1000.0);
                for (int h = 0; h < herds && parcels.Count > 0; h++)
                {
                    int p = parcels[rng.NextInt(parcels.Count)];
                    double cx = (p % ParcelGrid.Width) * ParcelGrid.Size + 2, cz = (p / ParcelGrid.Width) * ParcelGrid.Size + 2;
                    SetDown(world, s, cx, cz, sp.HerdSize, tick, rng);
                }
            }

            int person = -1;
            for (int s = 0; s < species.Count; s++) if (species[s].Person) { person = s; break; }
            _life.Native = new bool[species.Count];
            for (int s = 0; s < species.Count; s++) _life.Native[s] = _life.CountOf(s) > 0 || species[s].Person;
            if (person < 0 || _life.Rules.StartBands <= 0) return;
            var sites = GoodSites(world, _life.Rules.StartBands, _life.Rules.BandSpacing);
            foreach (int p in sites)
            {
                double x = (p % ParcelGrid.Width) * ParcelGrid.Size + 2, z = (p / ParcelGrid.Width) * ParcelGrid.Size + 2;
                FoundBand(world, person, x, z, _life.Rules.BandSize, RecordId.None, rng);
            }
        }

        /// <summary>
        /// A herd of grown animals round a point, keeping together as a group
        /// (the boldest leads). Returns how many were set down.
        /// </summary>
        public int SetDown(SimWorld world, int species, double cx, double cz, int count, long tick, RngStream rng)
        {
            Species sp = _life.Species[species];
            _scratch.Clear();
            for (int k = 0; k < count; k++)
            {
                double x = cx + rng.NextInt(-4, 5), z = cz + rng.NextInt(-4, 5);
                if (!Passable((int)x, (int)z)) { x = cx; z = cz; }
                long born = tick - rng.NextInt(sp.AdultDays, System.Math.Max(sp.AdultDays + 1, sp.LifeDays / 2));
                _scratch.Add(Spawn(sp, species, x, z, born, -1, rng));
            }
            if (sp.Groups && _scratch.Count >= 2) Gather(world, species, _scratch, rng);
            return _scratch.Count;
        }

        public static readonly Symbol WanderedInKind = Symbol.For("life.wandered-in");

        /// <summary>
        /// The wild does not stay empty: a species below its floor gets a herd
        /// wandering in each month, on its own kind of ground as far from people
        /// as can be found. The god can still clear a valley; the island
        /// refills from its far corners.
        /// </summary>
        void Replenish(SimWorld world)
        {
            SpeciesTable species = _life.Species;
            RngStream rng = world.Streams.Get("life.wild");
            for (int s = 0; s < species.Count; s++)
            {
                Species sp = species[s];
                if (sp.WildFloor <= 0 || sp.WildBiomes.Count == 0 || world.Island == null) continue;
                if (_life.CountOf(s) >= sp.WildFloor) continue;
                int best = -1;
                double farthest = -1.0;
                for (int k = 0; k < 40; k++)
                {
                    int px = rng.NextInt(4, ParcelGrid.Width - 4), pz = rng.NextInt(4, ParcelGrid.Depth - 4);
                    if (!_grid.IsLand(px, pz) || _grid.WetColumns(px, pz) > 0) continue;
                    int b = world.Island.BiomeAt(px * ParcelGrid.Size + 2, pz * ParcelGrid.Size + 2);
                    if (b < 0 || !Contains(sp.WildBiomes, _biomes.At(b).Name)) continue;
                    double near = double.MaxValue;
                    foreach (Group band in _life.Groups)
                        if (band.IsBand && !band.Gone) near = System.Math.Min(near, Distance(px * ParcelGrid.Size, pz * ParcelGrid.Size, band.CampX, band.CampZ));
                    if (near > farthest) { farthest = near; best = pz * ParcelGrid.Width + px; }
                }
                if (best < 0) continue;
                double cx = (best % ParcelGrid.Width) * ParcelGrid.Size + 2, cz = (best / ParcelGrid.Width) * ParcelGrid.Size + 2;
                world.Annals.Write(world.Clock.Tick, WanderedInKind, sp.Id, new Int3((int)cx, 0, (int)cz), RecordId.None, sp.HerdSize);
                SetDown(world, s, cx, cz, sp.HerdSize, world.Clock.Tick, rng);
            }
        }

        static bool Contains(IReadOnlyList<string> list, string name)
        {
            for (int i = 0; i < list.Count; i++) if (list[i] == name) return true;
            return false;
        }

        /// <summary>The best camping ground: well fed, flat, by water, spaced apart.</summary>
        List<int> GoodSites(SimWorld world, int count, double spacing)
        {
            var scored = new List<KeyValuePair<double, int>>();
            Grazing g = _life.Grazing;
            for (int pz = 8; pz < ParcelGrid.Depth - 8; pz += 2)
                for (int px = 8; px < ParcelGrid.Width - 8; px += 2)
                {
                    if (!_grid.IsLand(px, pz) || _grid.WetColumns(px, pz) > 0) continue;
                    double water = _grid.WaterDistance[px, pz];
                    if (water < 1.0 || water > 5.0 || _grid.Slope[px, pz] > 3.0) continue;
                    double food = 0.0;
                    for (int dz = -3; dz <= 3; dz++)
                        for (int dx = -3; dx <= 3; dx++)
                            if (ParcelGrid.InBounds(px + dx, pz + dz)) food += g.Capacity[(pz + dz) * ParcelGrid.Width + px + dx];
                    scored.Add(new KeyValuePair<double, int>(food - water - _grid.Slope[px, pz], pz * ParcelGrid.Width + px));
                }
            scored.Sort((a, b) => { int c = b.Key.CompareTo(a.Key); return c != 0 ? c : a.Value.CompareTo(b.Value); });
            var chosen = new List<int>();
            double apart = spacing / ParcelGrid.Size;
            foreach (KeyValuePair<double, int> s in scored)
            {
                if (chosen.Count >= count) break;
                int x = s.Value % ParcelGrid.Width, z = s.Value / ParcelGrid.Width;
                bool clear = true;
                foreach (int c in chosen)
                {
                    double dx = x - c % ParcelGrid.Width, dz = z - c / ParcelGrid.Width;
                    if (dx * dx + dz * dz < apart * apart) { clear = false; break; }
                }
                if (clear) chosen.Add(s.Value);
            }
            return chosen;
        }

        /// <summary>A band of people making camp at a place, on record. Returns the band.</summary>
        public Group FoundBand(SimWorld world, int species, double x, double z, int people, RecordId cause, RngStream rng)
        {
            Group band = NewBand(world, species, x, z, people, cause, BandFoundedKind);
            Species sp = _life.Species[species];
            for (int k = 0; k < people; k++)
            {
                double px = x + rng.NextInt(-3, 4), pz = z + rng.NextInt(-3, 4);
                if (!Passable((int)px, (int)pz)) { px = x; pz = z; }
                long born = world.Clock.Tick - rng.NextInt(sp.AdultDays, sp.AdultDays + (sp.LifeDays - sp.AdultDays) / 2);
                int i = Spawn(sp, species, px, pz, born, -1, rng);
                Join(i, band.Number, rng);
            }
            ChooseLeader(band, world.Clock.Tick);
            return band;
        }

        Group NewBand(SimWorld world, int species, double x, double z, int people, RecordId cause, Symbol kind)
        {
            var band = new Group
            {
                Number = _life.Groups.Count, Species = species, IsBand = true, CampX = x, CampZ = z,
                Founded = world.Clock.Tick, CampSince = world.Clock.Tick,
            };
            band.Record = world.Annals.Write(world.Clock.Tick, kind, Symbol.For("band." + band.Number), new Int3((int)x, 0, (int)z), cause, people);
            _life.Groups.Add(band);
            return band;
        }

        /// <summary>
        /// One creature, its own lifespan, temperament and pace drawn about its
        /// kind's — or, with a parent, taken from the parent with a little
        /// drift. It belongs to no group until it joins one.
        /// </summary>
        public int Spawn(Species sp, int species, double x, double z, long born, int parent, RngStream rng)
        {
            int life = (int)(sp.LifeDays * (0.8 + 0.4 * Unit(rng)));
            Creatures c = _life.Creatures;
            int i = c.Add(species, x, z, born, life, sp.Health);
            c.Bold[i] = Temper(sp.Bold, sp.Spread, parent >= 0 ? c.Bold[parent] : -1.0, rng);
            c.Social[i] = Temper(sp.Social, sp.Spread, parent >= 0 ? c.Social[parent] : -1.0, rng);
            c.Restless[i] = Temper(sp.Restless, sp.Spread, parent >= 0 ? c.Restless[parent] : -1.0, rng);
            c.Pace[i] = 0.85 + 0.3 * Unit(rng);
            return i;
        }

        static double Temper(double mean, double spread, double parent, RngStream rng)
        {
            double drawn = SimMath.Clamp01(mean + (Unit(rng) + Unit(rng) - 1.0) * spread);
            return parent < 0.0 ? drawn : SimMath.Clamp01(parent * 0.7 + drawn * 0.3);
        }

        // ── groups: every change is one creature's choice ───────────────────

        /// <summary>A new herd or pack from creatures already on the land; the boldest grown one leads.</summary>
        Group Gather(SimWorld world, int species, List<int> rows, RngStream rng)
        {
            Group g = NewGroup(species, world.Clock.Tick);
            foreach (int i in rows) Join(i, g.Number, rng);
            ChooseLeader(g, world.Clock.Tick);
            return g;
        }

        Group NewGroup(int species, long tick)
        {
            Group g;
            if (_life.FreeGroups.Count > 0)
            {
                g = _life.Groups[_life.FreeGroups.Pop()];
                g.Species = species; g.Gone = false; g.Leader = -1; g.Members = 0; g.Founded = tick;
                return g;
            }
            g = new Group { Number = _life.Groups.Count, Species = species, Founded = tick };
            _life.Groups.Add(g);
            return g;
        }

        /// <summary>Into a group, with a place of its own in it: the more social, the nearer the middle.</summary>
        void Join(int i, int group, RngStream rng)
        {
            Creatures c = _life.Creatures;
            Group g = _life.Groups[group];
            c.Group[i] = group;
            g.Members++;
            if (g.Leader < 0) g.Leader = i;
            double keep = g.IsBand ? _life.Rules.CampRadius : _life.Species[c.Species[i]].GroupKeep;
            double ang = Unit(rng) * 6.283185307179586;
            double r = keep * (0.2 + 0.6 * (1.0 - c.Social[i])) * (0.4 + 0.6 * Unit(rng));
            c.SlotX[i] = SimMath.Cos(ang) * r;
            c.SlotZ[i] = SimMath.Sin(ang) * r;
        }

        /// <summary>Out of its group. A herd left with one member is no herd; a band keeps its camp until the last is gone.</summary>
        void Leave(SimWorld world, int i)
        {
            Creatures c = _life.Creatures;
            int n = c.Group[i];
            if (n < 0) return;
            Group g = _life.Groups[n];
            c.Group[i] = -1;
            g.Members--;
            if (!g.IsBand && g.Members < 2) { Dissolve(g); return; }
            if (g.Leader == i) ChooseLeader(g, world.Clock.Tick);
        }

        void Dissolve(Group g)
        {
            Creatures c = _life.Creatures;
            for (int j = 0; j < c.Length; j++)
                if (c.Alive[j] && c.Group[j] == g.Number) c.Group[j] = -1;
            g.Members = 0;
            g.Leader = -1;
            g.Gone = true;
            _life.FreeGroups.Push(g.Number);
        }

        /// <summary>The group's new leader: a grown member before a young one, the boldest first, the lowest row on a tie.</summary>
        void ChooseLeader(Group g, long tick)
        {
            Creatures c = _life.Creatures;
            Species sp = _life.Species[g.Species];
            int best = -1;
            double score = double.MinValue;
            for (int j = 0; j < c.Length; j++)
            {
                if (!c.Alive[j] || c.Group[j] != g.Number) continue;
                double s = c.Bold[j] + (tick - c.Born[j] >= sp.AdultDays ? 1.0 : 0.0);
                if (s > score) { score = s; best = j; }
            }
            g.Leader = best;
        }

        /// <summary>
        /// About once a month each grown creature weighs its company. In a
        /// group grown past its kind's size, or hungry, the restless leave —
        /// alone, or, bold as well, striking out and taking the restless near
        /// them. Alone or nearly, it joins the next of its kind it meets, the
        /// social readily. True if it changed its company.
        /// </summary>
        bool Weigh(SimWorld world, int i, long tick, RngStream rng)
        {
            Creatures c = _life.Creatures;
            Species sp = _life.Species[c.Species[i]];
            if (!sp.Groups || tick - c.Born[i] < sp.AdultDays) return false;
            int n = c.Group[i];
            Group g = _life.GroupOf(n);

            if (g != null && g.Leader != i && g.Members >= 3)
            {
                double pressure = 0.02 + (g.Members > sp.GroupMost ? 0.6 : 0.0) + (c.Hunger[i] > 0.6 ? 0.15 : 0.0);
                if (Unit(rng) < pressure * c.Restless[i])
                {
                    if (c.Bold[i] + c.Restless[i] > 1.0 && g.Members >= 4) StrikeOut(world, i, rng);
                    else GoAlone(world, i, rng);
                    return true;
                }
            }

            if (g != null && g.Members > 2) return false;
            int j = Nearest(i, sp.Sense, Want.Kin, sp);
            if (j < 0) return false;
            Group other = _life.GroupOf(c.Group[j]);
            if (other != null)
            {
                if (other.Members + 1 > sp.GroupMost) return false;
                // The smaller comes to the larger; equals, the later-numbered to the earlier.
                if (g != null && (g.Members > other.Members || (g.Members == other.Members && g.Number < other.Number))) return false;
                if (Unit(rng) >= 0.3 + 0.7 * c.Social[i]) return false;
                if (g != null) Leave(world, i);
                Join(i, other.Number, rng);
                _life.Joined++;
                if (other.IsBand)
                    world.Annals.Write(tick, PersonJoinedKind, Symbol.For("band." + other.Number), new Int3((int)c.X[i], 0, (int)c.Z[i]), other.Record, other.Members);
                return true;
            }
            // Two alone: if this one wants company, they keep together from now on.
            if (g != null || tick - c.Born[j] < sp.AdultDays || Unit(rng) >= c.Social[i]) return false;
            if (sp.Person)
            {
                Group band = NewBand(world, c.Species[i], (c.X[i] + c.X[j]) * 0.5, (c.Z[i] + c.Z[j]) * 0.5, 2, RecordId.None, BandFoundedKind);
                Join(i, band.Number, rng); Join(j, band.Number, rng);
                ChooseLeader(band, tick);
            }
            else
            {
                _scratch.Clear(); _scratch.Add(i); _scratch.Add(j);
                Gather(world, c.Species[i], _scratch, rng);
            }
            _life.Joined += 2;
            return true;
        }

        /// <summary>Leaves its group to go its own way, a fair distance off.</summary>
        void GoAlone(SimWorld world, int i, RngStream rng)
        {
            Creatures c = _life.Creatures;
            Group g = _life.GroupOf(c.Group[i]);
            Leave(world, i);
            _life.Left++;
            if (g != null && g.IsBand)
                world.Annals.Write(world.Clock.Tick, PersonLeftKind, Symbol.For("band." + g.Number), new Int3((int)c.X[i], 0, (int)c.Z[i]), g.Record, g.Members);
            RoamGoal(i, 30.0, 80.0, rng);
            c.Doing[i] = Doing.Roaming;
        }

        /// <summary>
        /// Leaves its group and leads a new one, and each member near it
        /// chooses whether to follow — the restless readily. A person makes
        /// camp on the best ground a day or two off, on record as a split.
        /// </summary>
        void StrikeOut(SimWorld world, int i, RngStream rng)
        {
            Creatures c = _life.Creatures;
            Species sp = _life.Species[c.Species[i]];
            int from = c.Group[i];
            Group old = _life.Groups[from];
            long tick = world.Clock.Tick;

            double bx = 0.0, bz = 0.0;
            if (old.IsBand && (!BestGroundNear(old.CampX, old.CampZ, _life.Rules.CampSearch * 1.5, out bx, out bz)
                               || Distance(bx, bz, old.CampX, old.CampZ) < _life.Rules.BandSpacing * 0.6))
            {
                GoAlone(world, i, rng);   // nowhere worth going together: it goes alone
                return;
            }

            // Who goes with it: each member near enough to see it go decides for itself, up to half the group.
            double reach = old.IsBand ? _life.Rules.CampRadius * 2.0 : sp.GroupKeep * 2.5;
            var going = new List<int> { i };
            int most = old.Members / 2;
            for (int j = 0; j < c.Length && going.Count < most; j++)
            {
                if (j == i || !c.Alive[j] || c.Group[j] != from || j == old.Leader) continue;
                if (Distance(c.X[j], c.Z[j], c.X[i], c.Z[i]) > reach) continue;
                if (Unit(rng) < 0.1 + 0.6 * c.Restless[j]) going.Add(j);
            }
            foreach (int j in going) Leave(world, j);
            _life.Left += going.Count;
            _life.StruckOut++;

            Group led;
            if (old.IsBand)
            {
                RecordId split = world.Annals.Write(tick, BandSplitKind, Symbol.For("band." + old.Number), new Int3((int)bx, 0, (int)bz),
                                                    old.Record, going.Count, old.Members);
                led = NewBand(world, c.Species[i], bx, bz, going.Count, split, BandFoundedKind);
                foreach (int j in going) Join(j, led.Number, rng);
                led.Leader = i;
                foreach (int j in going)
                {
                    c.GoalX[j] = bx + c.SlotX[j]; c.GoalZ[j] = bz + c.SlotZ[j];
                    c.Doing[j] = Doing.Roaming;
                }
                return;
            }
            RoamGoal(i, 40.0, 90.0, rng);
            c.Doing[i] = Doing.Roaming;
            if (going.Count < 2) return;   // nobody followed: it goes alone
            led = NewGroup(c.Species[i], tick);
            foreach (int j in going) Join(j, led.Number, rng);
            led.Leader = i;
        }

        // ── bands and camps ─────────────────────────────────────────────────

        /// <summary>
        /// Once a month: a band with nobody left is gone. A band's leader
        /// weighs the food round its camp and, when it is eaten bare, moves the
        /// camp to the best ground within reach — the more restless the leader,
        /// the sooner — and each member chooses whether to go too. A camp that
        /// has stayed put long enough has settled.
        /// </summary>
        void WeighCamps(SimWorld world)
        {
            long tick = world.Clock.Tick;
            int daysPerMonth = world.Clock.DaysPerMonth * world.Clock.TicksPerDay;
            RngStream rng = world.Streams.Get(StreamId);
            Creatures c = _life.Creatures;
            int count = _life.Groups.Count;   // bands made this month are weighed next month
            for (int n = 0; n < count; n++)
            {
                Group b = _life.Groups[n];
                if (!b.IsBand || b.Gone) continue;
                if (b.Members <= 0)
                {
                    b.Gone = true;
                    b.Leader = -1;
                    world.Annals.Write(tick, BandGoneKind, Symbol.For("band." + b.Number), new Int3((int)b.CampX, 0, (int)b.CampZ), b.Record, b.Moves);
                    continue;
                }

                double restless = b.Leader >= 0 ? c.Restless[b.Leader] : 0.5;
                double share = FoodShareAround(b.CampX, b.CampZ, _life.Rules.CampRadius);
                if (share < 0.2 + 0.2 * restless)
                {
                    double bx, bz;
                    if (BestGroundNear(b.CampX, b.CampZ, _life.Rules.CampSearch, out bx, out bz)
                        && (bx - b.CampX) * (bx - b.CampX) + (bz - b.CampZ) * (bz - b.CampZ) > 16.0 * 16.0)
                    {
                        b.CampX = bx; b.CampZ = bz; b.CampSince = tick; b.Settled = false; b.Moves++;
                        world.Annals.Write(tick, BandMovedKind, Symbol.For("band." + b.Number), new Int3((int)bx, 0, (int)bz), b.Record, b.Members, b.Moves);
                        // Each chooses: the unsociable and restless may stay behind.
                        for (int i = 0; i < c.Length; i++)
                        {
                            if (!c.Alive[i] || c.Group[i] != b.Number || i == b.Leader) continue;
                            if (tick - c.Born[i] < _life.Species[c.Species[i]].AdultDays) continue;
                            if (Unit(rng) < (1.0 - c.Social[i]) * c.Restless[i] * 0.5)
                            {
                                Leave(world, i);
                                _life.Left++;
                                world.Annals.Write(tick, PersonLeftKind, Symbol.For("band." + b.Number), new Int3((int)c.X[i], 0, (int)c.Z[i]), b.Record, b.Members);
                            }
                        }
                    }
                }
                if (!b.Settled && tick - b.CampSince >= (long)_life.Rules.SettleMonths * daysPerMonth)
                {
                    b.Settled = true;
                    world.Annals.Write(tick, BandSettledKind, Symbol.For("band." + b.Number), new Int3((int)b.CampX, 0, (int)b.CampZ), b.Record, b.Members);
                }
            }
        }

        double FoodShareAround(double x, double z, double radius)
        {
            Grazing g = _life.Grazing;
            int r = (int)(radius / ParcelGrid.Size) + 1, cx = (int)x / ParcelGrid.Size, cz = (int)z / ParcelGrid.Size;
            double food = 0.0, cap = 0.0;
            for (int dz = -r; dz <= r; dz++)
                for (int dx = -r; dx <= r; dx++)
                {
                    if (!ParcelGrid.InBounds(cx + dx, cz + dz)) continue;
                    int p = (cz + dz) * ParcelGrid.Width + cx + dx;
                    food += g.Food[p]; cap += g.Capacity[p];
                }
            return cap > 0.0 ? food / cap : 0.0;
        }

        bool BestGroundNear(double x, double z, double radius, out double bx, out double bz)
        {
            bx = x; bz = z;
            Grazing g = _life.Grazing;
            int r = (int)(radius / ParcelGrid.Size), cx = (int)x / ParcelGrid.Size, cz = (int)z / ParcelGrid.Size;
            double best = -1.0;
            for (int dz = -r; dz <= r; dz += 2)
                for (int dx = -r; dx <= r; dx += 2)
                {
                    int px = cx + dx, pz = cz + dz;
                    if (!ParcelGrid.InBounds(px, pz) || !_grid.IsLand(px, pz) || _grid.WetColumns(px, pz) > 0) continue;
                    if (dx * dx + dz * dz > r * r) continue;
                    double food = 0.0;
                    for (int ez = -2; ez <= 2; ez++)
                        for (int ex = -2; ex <= 2; ex++)
                            if (ParcelGrid.InBounds(px + ex, pz + ez)) food += g.Food[(pz + ez) * ParcelGrid.Width + px + ex];
                    food -= _grid.WaterDistance[px, pz] * 0.5;
                    if (food > best) { best = food; bx = px * ParcelGrid.Size + 2; bz = pz * ParcelGrid.Size + 2; }
                }
            return best > 0.0;
        }

        // ── who is near whom ────────────────────────────────────────────────

        /// <summary>Every living creature sorted into the neighbour grid, in row order (a counting sort: no allocation, no dictionary).</summary>
        void Index()
        {
            Creatures c = _life.Creatures;
            if (_cellItems.Length < c.Length) _cellItems = new int[System.Math.Max(c.Length, _cellItems.Length * 2)];
            System.Array.Clear(_cellStart, 0, _cellStart.Length);
            for (int i = 0; i < c.Length; i++) if (c.Alive[i]) _cellStart[CellOf(c.X[i], c.Z[i]) + 1]++;
            for (int k = 1; k < _cellStart.Length; k++) _cellStart[k] += _cellStart[k - 1];
            System.Array.Copy(_cellStart, _fill, _cellStart.Length);
            for (int i = 0; i < c.Length; i++) if (c.Alive[i]) _cellItems[_fill[CellOf(c.X[i], c.Z[i])]++] = i;
        }

        static int CellOf(double x, double z)
        {
            int cx = (int)x / Cell, cz = (int)z / Cell;
            if (cx < 0) cx = 0; if (cx >= CellsX) cx = CellsX - 1;
            if (cz < 0) cz = 0; if (cz >= CellsZ) cz = CellsZ - 1;
            return cz * CellsX + cx;
        }

        enum Want { Any, Threat, Prey, PreyStarving, Attacker, Kin }

        /// <summary>Whether creature j is what creature self is looking for.</summary>
        bool Wanted(Want want, Species sp, int self, int j)
        {
            Creatures c = _life.Creatures;
            int s = c.Species[j];
            switch (want)
            {
                case Want.Any: return true;
                case Want.Threat: return sp.FleesFrom(s);
                case Want.Prey: return System.Array.IndexOf(sp.Hunts, s) >= 0;
                case Want.PreyStarving: return System.Array.IndexOf(sp.Hunts, s) >= 0 || System.Array.IndexOf(sp.HuntsWhenStarving, s) >= 0;
                case Want.Attacker: return System.Array.IndexOf(sp.FightsBack, s) >= 0;
                case Want.Kin: return s == c.Species[self] && (c.Group[self] < 0 || c.Group[j] != c.Group[self]);
            }
            return false;
        }

        /// <summary>The nearest living creature within a radius that is what it wants; -1 if none. No allocation (L10).</summary>
        int Nearest(int self, double radius, Want want, Species sp)
        {
            Creatures c = _life.Creatures;
            double x = c.X[self], z = c.Z[self], best = radius * radius;
            int found = -1, r = (int)(radius / Cell) + 1, cx = (int)x / Cell, cz = (int)z / Cell;
            for (int gz = cz - r; gz <= cz + r; gz++)
            {
                if (gz < 0 || gz >= CellsZ) continue;
                for (int gx = cx - r; gx <= cx + r; gx++)
                {
                    if (gx < 0 || gx >= CellsX) continue;
                    int cell = gz * CellsX + gx;
                    for (int k = _cellStart[cell]; k < _cellStart[cell + 1]; k++)
                    {
                        int j = _cellItems[k];
                        if (j == self || !c.Alive[j] || !Wanted(want, sp, self, j)) continue;
                        double dx = c.X[j] - x, dz = c.Z[j] - z, d = dx * dx + dz * dz;
                        if (d < best || (d == best && j < found)) { best = d; found = j; }
                    }
                }
            }
            return found;
        }

        /// <summary>How many living creatures of a species are within a radius.</summary>
        int Around(int self, int species, double radius)
        {
            Creatures c = _life.Creatures;
            double x = c.X[self], z = c.Z[self], r2 = radius * radius;
            int n = 0, r = (int)(radius / Cell) + 1, cx = (int)x / Cell, cz = (int)z / Cell;
            for (int gz = cz - r; gz <= cz + r; gz++)
            {
                if (gz < 0 || gz >= CellsZ) continue;
                for (int gx = cx - r; gx <= cx + r; gx++)
                {
                    if (gx < 0 || gx >= CellsX) continue;
                    int cell = gz * CellsX + gx;
                    for (int k = _cellStart[cell]; k < _cellStart[cell + 1]; k++)
                    {
                        int j = _cellItems[k];
                        if (j == self || !c.Alive[j] || c.Species[j] != species) continue;
                        double dx = c.X[j] - x, dz = c.Z[j] - z;
                        if (dx * dx + dz * dz <= r2) n++;
                    }
                }
            }
            return n;
        }

        // ── one creature's step ─────────────────────────────────────────────

        void Act(SimWorld world, int i, long tick, RngStream rng)
        {
            Creatures c = _life.Creatures;
            Species sp = _life.Species[c.Species[i]];
            bool blessed = c.Has(i, Creatures.Blessed), cursed = c.Has(i, Creatures.Cursed);

            // Age and hunger.
            long age = tick - c.Born[i];
            if (age > (blessed ? c.LifeDays[i] * 3 / 2 : cursed ? c.LifeDays[i] * 2 / 3 : c.LifeDays[i])) { Die(world, i, Death.OldAge, RecordId.None); return; }
            c.Hunger[i] += sp.HungerPerDay * (cursed ? 2.0 : blessed ? 0.5 : 1.0);
            if (c.Hunger[i] >= 1.0)
            {
                c.Hunger[i] = 1.0;
                if (++c.StarvingDays[i] >= _life.Rules.StarveDays) { Die(world, i, Death.Starved, RecordId.None); return; }
            }
            else c.StarvingDays[i] = 0;
            if (blessed && c.Health[i] < sp.Health * 1.5) c.Health[i] += 0.01;
            else if (c.Health[i] < sp.Health && c.Hunger[i] < 0.5) c.Health[i] = System.Math.Min(sp.Health, c.Health[i] + 0.005);

            double speed = sp.Speed * c.Pace[i] * (blessed ? 1.25 : 1.0);
            int me = c.Species[i];
            Group g = _life.GroupOf(c.Group[i]);

            // The cursed attack whatever is nearest, their own kind included.
            if (cursed)
            {
                int victim = Nearest(i, 6.0, Want.Any, sp);
                if (victim >= 0 && Chase(world, i, victim, speed, sp.Attack + 0.2)) return;
            }

            // Run from what hunts it. The timid look round every step and bolt
            // early; the bold look less often and let danger come closer. Each
            // runs its own way, so a herd scatters rather than moving as one.
            if (sp.Flees.Length > 0)
            {
                int every = c.Doing[i] == Doing.Fleeing ? 1 : 1 + (int)(c.Bold[i] * 2.99);
                if ((tick + (long)c.Id[i]) % every == 0)
                {
                    int threat = Nearest(i, sp.Sense * (1.2 - 0.5 * c.Bold[i]), Want.Threat, sp);
                    if (threat >= 0)
                    {
                        double ax = c.X[i] - c.X[threat], az = c.Z[i] - c.Z[threat];
                        double len = SimMath.Sqrt(ax * ax + az * az);
                        if (len < 1e-6) { ax = 1; az = 0; len = 1; }
                        double bend = ((long)(c.Id[i] % 7) - 3) * 0.2, cs = SimMath.Cos(bend), sn = SimMath.Sin(bend);
                        double ux = (ax * cs - az * sn) / len, uz = (ax * sn + az * cs) / len;
                        Move(i, c.X[i] + ux * 8.0, c.Z[i] + uz * 8.0, speed * 1.1);
                        c.Doing[i] = Doing.Fleeing;
                        return;
                    }
                }
            }

            // Stand up for its own against what attacks them: the bold from further off.
            if (sp.FightsBack.Length > 0)
            {
                int attacker = Nearest(i, 3.0 + 8.0 * c.Bold[i], Want.Attacker, sp);
                if (attacker >= 0 && Chase(world, i, attacker, speed, sp.Attack)) return;
            }

            // Hungry: eat where it stands, hunt, or walk to better grazing.
            if (c.Hunger[i] > 0.35)
            {
                int px = (int)c.X[i] / ParcelGrid.Size, pz = (int)c.Z[i] / ParcelGrid.Size;
                int p = pz * ParcelGrid.Width + px;
                Grazing food = _life.Grazing;
                if (sp.Graze > 0.0 && food.Food[p] >= BiteTakes)
                {
                    food.Food[p] -= BiteTakes;
                    c.Hunger[i] = System.Math.Max(0.0, c.Hunger[i] - BiteEases * sp.Graze);
                    // A mouthful on the way does not end the journey.
                    if (c.Doing[i] != Doing.Roaming) c.Doing[i] = Doing.Grazing;
                    return;
                }
                bool starving = c.Hunger[i] > 0.75;
                // Something that can also graze hunts only when grazing has not kept it fed.
                bool hunts = sp.Graze <= 0.0 || c.Hunger[i] > 0.55;
                // A pack hunts together: a hungry member joins its leader's chase.
                if (hunts && g != null && g.Leader >= 0 && g.Leader != i && c.Doing[g.Leader] == Doing.Hunting)
                {
                    int quarry = c.Target[g.Leader];
                    if (quarry >= 0 && c.Alive[quarry] && Distance(c.X[i], c.Z[i], c.X[quarry], c.Z[quarry]) <= sp.Sense
                        && Chase(world, i, quarry, speed, sp.Attack)) return;
                }
                int prey = hunts && (sp.Hunts.Length > 0 || (starving && sp.HuntsWhenStarving.Length > 0))
                    ? Nearest(i, sp.Sense, starving ? Want.PreyStarving : Want.Prey, sp)
                    : -1;
                if (prey >= 0 && Chase(world, i, prey, speed, sp.Attack)) return;
                if (sp.Graze <= 0.0 && (sp.Hunts.Length > 0 || sp.HuntsWhenStarving.Length > 0))
                {
                    // Nothing to hunt in sight: range out and look. A pack's members range with their leader.
                    if (g == null || g.Leader == i)
                    {
                        if (c.Doing[i] != Doing.Roaming || Arrived(i)) RoamGoal(i, 30.0, 60.0, rng);
                        Move(i, c.GoalX[i], c.GoalZ[i], speed * 0.8);
                        c.Doing[i] = Doing.Roaming;
                        return;
                    }
                }
                else if (sp.Graze > 0.0 && c.Doing[i] == Doing.Roaming && !Arrived(i))
                {
                    Move(i, c.GoalX[i], c.GoalZ[i], speed * 0.8);   // on its way: better ground is where it is going
                    return;
                }
                else if (sp.Graze > 0.0)
                {
                    if (c.Doing[i] != Doing.Seeking || Arrived(i))
                    {
                        // Its own pick of the grass round about, kept near its group.
                        double ox, oz;
                        bool anchored = Anchor(i, g, out ox, out oz);
                        double best = -1.0, bx = c.X[i], bz = c.Z[i];
                        for (int k = 0; k < 6; k++)
                        {
                            double ang = Unit(rng) * 6.283185307179586, dist = 8.0 + Unit(rng) * 16.0;
                            double tx = c.X[i] + SimMath.Cos(ang) * dist, tz = c.Z[i] + SimMath.Sin(ang) * dist;
                            if (!Passable((int)tx, (int)tz)) continue;
                            double here = food.At((int)tx / ParcelGrid.Size, (int)tz / ParcelGrid.Size);
                            if (anchored) here -= Distance(tx, tz, ox, oz) * 0.02 * (0.5 + c.Social[i]);
                            if (here > best) { best = here; bx = tx; bz = tz; }
                        }
                        c.GoalX[i] = bx; c.GoalZ[i] = bz;
                    }
                    Move(i, c.GoalX[i], c.GoalZ[i], speed);
                    c.Doing[i] = Doing.Seeking;
                    return;
                }
            }

            // Breed: grown, fed, not crowded, and not too soon after the last. A
            // lone creature breeds too, so one that wandered off starts a herd.
            if (c.Doing[i] != Doing.Roaming && age >= sp.AdultDays && c.Hunger[i] < 0.5 && tick - c.LastBirth[i] > world.Clock.DaysPerYear / 3
                && Unit(rng) < sp.BirthsPerYear / world.Clock.DaysPerYear)
            {
                int kin = Around(i, me, 16.0);
                if (kin >= sp.Crowding && !sp.Person)
                {
                    // Crowded: this one goes to find ground of its own, and some may go with it.
                    if (g != null && g.Leader != i && g.Members >= 4) StrikeOut(world, i, rng);
                    else if (g == null) { RoamGoal(i, 40.0, 90.0, rng); c.Doing[i] = Doing.Roaming; }
                }
                else if (kin < sp.Crowding)
                {
                    c.LastBirth[i] = tick;
                    for (int k = 0; k < sp.Litter; k++)
                    {
                        int child = Spawn(sp, me, c.X[i], c.Z[i], tick, i, rng);
                        c.Hunger[child] = c.Hunger[i];
                        c.Flags[child] = (byte)(c.Flags[i] & Creatures.Blessed);
                        g = _life.GroupOf(c.Group[i]);
                        if (g != null) Join(child, g.Number, rng);
                        else if (sp.Groups) { _scratch.Clear(); _scratch.Add(i); _scratch.Add(child); g = Gather(world, me, _scratch, rng); }
                    }
                }
            }

            // About once a month, on its own day, it weighs its company.
            if ((tick + (long)c.Id[i]) % WeighEvery == 0 && Weigh(world, i, tick, rng)) return;
            g = _life.GroupOf(c.Group[i]);

            // On its way somewhere new: keep going until it gets there.
            if (c.Doing[i] == Doing.Roaming && !Arrived(i))
            {
                Move(i, c.GoalX[i], c.GoalZ[i], speed * 0.8);
                return;
            }

            // A band's member keeps to its own place round the camp, as near as it likes.
            if (g != null && g.IsBand)
            {
                double own = _life.Rules.CampRadius * (0.45 + 0.8 * (1.0 - c.Social[i]));
                double hx = g.CampX + c.SlotX[i], hz = g.CampZ + c.SlotZ[i];
                if (Distance(c.X[i], c.Z[i], g.CampX, g.CampZ) > own + 2.0)
                {
                    Move(i, hx, hz, speed * 0.8);   // walking back, at its own pace
                    c.Doing[i] = Doing.Wandering;
                    return;
                }
                Amble(i, hx, hz, own * 0.6, speed * 0.7, rng);
                return;
            }

            // A herd or pack's member keeps to its own place by the leader; near it, it goes about its own business.
            if (g != null && g.Leader >= 0 && g.Leader != i)
            {
                int l = g.Leader;
                double keep = 1.5 + sp.GroupKeep * 0.5 * (1.2 - c.Social[i]);
                double ax = c.X[l] + c.SlotX[i], az = c.Z[l] + c.SlotZ[i];
                double d = Distance(c.X[i], c.Z[i], ax, az);
                if (d > keep)
                {
                    Move(i, ax, az, speed * (d > keep * 3.0 ? 1.0 : 0.75));
                    c.Doing[i] = Doing.Following;
                    return;
                }
                Amble(i, ax, az, keep * 0.8, speed * 0.5, rng);
                return;
            }

            // A leader, or one alone: its own way, the restless further.
            Amble(i, c.X[i], c.Z[i], 4.0 + 12.0 * c.Restless[i], speed * 0.5, rng);
        }

        /// <summary>
        /// Rest a while, stand, or stroll to somewhere within a radius of a
        /// point — each creature on its own dice, so a group at ease is some
        /// lying down, some grazing, some wandering.
        /// </summary>
        void Amble(int i, double cx, double cz, double radius, double speed, RngStream rng)
        {
            Creatures c = _life.Creatures;
            if (c.Doing[i] == Doing.Resting && Unit(rng) < 0.92) return;
            if (Arrived(i) || c.Doing[i] != Doing.Wandering)
            {
                if (Unit(rng) < 0.6 + 0.3 * (1.0 - c.Restless[i]))
                {
                    c.Doing[i] = c.Hunger[i] < 0.25 && Unit(rng) < 0.5 ? Doing.Resting : Doing.Idle;
                    return;
                }
                double ang = Unit(rng) * 6.283185307179586, dist = Unit(rng) * radius;
                c.GoalX[i] = cx + SimMath.Cos(ang) * dist; c.GoalZ[i] = cz + SimMath.Sin(ang) * dist;
            }
            Move(i, c.GoalX[i], c.GoalZ[i], speed);
            c.Doing[i] = Doing.Wandering;
        }

        /// <summary>Where its group keeps it: its band's camp, or its leader. False alone or leading.</summary>
        bool Anchor(int i, Group g, out double x, out double z)
        {
            Creatures c = _life.Creatures;
            x = c.X[i]; z = c.Z[i];
            if (g == null) return false;
            if (g.IsBand) { x = g.CampX; z = g.CampZ; return true; }
            if (g.Leader < 0 || g.Leader == i) return false;
            x = c.X[g.Leader]; z = c.Z[g.Leader];
            return true;
        }

        /// <summary>Runs at a creature and, once close, strikes it. True when it was a step spent on the chase.</summary>
        bool Chase(SimWorld world, int i, int target, double speed, double attack)
        {
            Creatures c = _life.Creatures;
            if (target < 0 || !c.Alive[target]) return false;
            c.Target[i] = target;
            double d = Distance(c.X[i], c.Z[i], c.X[target], c.Z[target]);
            if (d > 1.5)
            {
                Move(i, c.X[target], c.Z[target], speed * 1.15);
                c.Doing[i] = Doing.Hunting;
                return true;
            }
            c.Doing[i] = Doing.Fighting;
            Face(i, c.X[target] - c.X[i], c.Z[target] - c.Z[i]);
            c.Health[target] -= attack;
            if (c.Health[target] <= 0.0)
            {
                Species eaten = _life.Species[c.Species[target]];
                Die(world, target, Death.Killed, RecordId.None);
                c.Hunger[i] = System.Math.Max(0.0, c.Hunger[i] - eaten.Meat);
            }
            return true;
        }

        void Die(SimWorld world, int i, Death cause, RecordId why)
        {
            Creatures c = _life.Creatures;
            Group g = _life.GroupOf(c.Group[i]);
            if (_life.Species[c.Species[i]].Person)
                world.Annals.Write(world.Clock.Tick, PersonDiedKind, Symbol.For("band." + (g != null ? g.Number : -1)), new Int3((int)c.X[i], 0, (int)c.Z[i]),
                                   why.Exists ? why : (g != null && g.IsBand ? g.Record : RecordId.None), (long)cause);
            c.Kill(i, cause);
            Leave(world, i);
        }

        /// <summary>Kills a creature for a cause the caller names (the god's powers).</summary>
        public void Kill(SimWorld world, int i, Death cause, RecordId why)
        {
            if (i >= 0 && i < _life.Creatures.Length && _life.Creatures.Alive[i]) Die(world, i, cause, why);
        }

        /// <summary>A goal a fair way off on dry land, tried a few times.</summary>
        void RoamGoal(int i, double least, double most, RngStream rng)
        {
            Creatures c = _life.Creatures;
            for (int k = 0; k < 6; k++)
            {
                double ang = Unit(rng) * 6.283185307179586, dist = least + Unit(rng) * (most - least);
                double tx = c.X[i] + SimMath.Cos(ang) * dist, tz = c.Z[i] + SimMath.Sin(ang) * dist;
                if (!Passable((int)tx, (int)tz)) continue;
                c.GoalX[i] = tx; c.GoalZ[i] = tz;
                return;
            }
            c.GoalX[i] = c.X[i]; c.GoalZ[i] = c.Z[i];
        }

        bool Arrived(int i)
        {
            Creatures c = _life.Creatures;
            return Distance(c.X[i], c.Z[i], c.GoalX[i], c.GoalZ[i]) < 1.0;
        }

        static double Distance(double ax, double az, double bx, double bz)
        {
            double dx = ax - bx, dz = az - bz;
            return SimMath.Sqrt(dx * dx + dz * dz);
        }

        /// <summary>A stride toward a point, round water and cliffs: straight if it can, else turning a little either way.</summary>
        void Move(int i, double tx, double tz, double speed)
        {
            Creatures c = _life.Creatures;
            double dx = tx - c.X[i], dz = tz - c.Z[i];
            double len = SimMath.Sqrt(dx * dx + dz * dz);
            if (len < 1e-6) return;
            double step = len < speed ? len : speed;
            dx /= len; dz /= len;
            int fromGround = GroundAt((int)c.X[i], (int)c.Z[i]);
            foreach (double t in Turns)
            {
                double cs = SimMath.Cos(t), sn = SimMath.Sin(t);
                double ux = dx * cs - dz * sn, uz = dx * sn + dz * cs;
                double nx = c.X[i] + ux * step, nz = c.Z[i] + uz * step;
                if (!Passable((int)nx, (int)nz)) continue;
                if (System.Math.Abs(GroundAt((int)nx, (int)nz) - fromGround) > Climb) continue;
                c.X[i] = nx; c.Z[i] = nz;
                Face(i, ux, uz);
                return;
            }
        }

        void Face(int i, double x, double z)
        {
            double len = SimMath.Sqrt(x * x + z * z);
            if (len < 1e-6) return;
            _life.Creatures.FaceX[i] = x / len;
            _life.Creatures.FaceZ[i] = z / len;
        }

        int GroundAt(int x, int z)
        {
            if (x < 0 || z < 0 || x >= Voxels.ChunkStore.SizeX || z >= Voxels.ChunkStore.SizeZ) return 0;
            return _grid.GroundAt(x, z);
        }

        /// <summary>Dry land inside the map.</summary>
        public bool Passable(int x, int z)
        {
            if (x < 1 || z < 1 || x >= Voxels.ChunkStore.SizeX - 1 || z >= Voxels.ChunkStore.SizeZ - 1) return false;
            if (_grid.IsWetColumn(x, z)) return false;
            return _grid.IsLand(x / ParcelGrid.Size, z / ParcelGrid.Size);
        }

        /// <summary>A number in [0, 1) from a stream.</summary>
        public static double Unit(RngStream rng) { return rng.NextInt(1 << 30) / (double)(1 << 30); }
    }
}
