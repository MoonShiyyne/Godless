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

    /// <summary>Everything alive in a world: the creatures, the bands, and the land's food (v2 M1).</summary>
    public sealed class Living
    {
        public Creatures Creatures { get; } = new Creatures();
        public List<Band> Bands { get; } = new List<Band>();
        public Grazing Grazing { get; internal set; }
        public SpeciesTable Species { get; internal set; }
        public LifeRules Rules { get; internal set; }

        /// <summary>Species that lived on this map when it was made: the ones the wild keeps (the delta has no sheep country).</summary>
        public bool[] Native { get; internal set; } = new bool[0];

        public Band BandOf(int number) { return number >= 0 && number < Bands.Count ? Bands[number] : null; }

        public int CountOf(int species)
        {
            int n = 0;
            Creatures c = Creatures;
            for (int i = 0; i < c.Length; i++) if (c.Alive[i] && c.Species[i] == species) n++;
            return n;
        }
    }

    /// <summary>
    /// Life on the land, a step at a time (v2 M1).
    ///
    /// Each creature ages, grows hungry, and does the most pressing thing it
    /// can: run from what hunts it, fight what attacks its own, eat where it
    /// stands, chase what it hunts, walk to better grazing, or keep near its
    /// herd or its band's camp. Well fed and grown, it breeds unless its kind
    /// is crowded round it. It dies of age, hunger, teeth, or the god.
    ///
    /// Once a month the grazing grows back and every band weighs its camp.
    ///
    /// The tell: herds drift across the meadows and scatter when wolves come
    /// out of the wood; people walk out from a camp and bring deer back; a
    /// band that has eaten its valley bare packs up and moves on.
    /// </summary>
    public sealed class LifeSystem : ISimSystem
    {
        public static readonly Symbol SystemId = Symbol.For("system.life");
        public static readonly Symbol BandFoundedKind = Symbol.For("life.band-founded");
        public static readonly Symbol BandMovedKind = Symbol.For("life.band-moved");
        public static readonly Symbol BandSettledKind = Symbol.For("life.band-settled");
        public static readonly Symbol BandGoneKind = Symbol.For("life.band-gone");
        public static readonly Symbol PersonDiedKind = Symbol.For("life.person-died");
        public const string StreamId = "life.behaviour";

        /// <summary>Voxels a neighbour-grid cell spans.</summary>
        const int Cell = 8;
        const int CellsX = Voxels.ChunkStore.SizeX / Cell, CellsZ = Voxels.ChunkStore.SizeZ / Cell;

        /// <summary>Hunger a mouthful of grazing eases, and how much grazing it takes.</summary>
        const double BiteEases = 0.12, BiteTakes = 0.06;
        /// <summary>Highest step a creature climbs in a stride, in voxels.</summary>
        const int Climb = 2;

        readonly ParcelGrid _grid;
        readonly BiomeTable _biomes;
        readonly Living _life;
        readonly int[] _cellStart = new int[CellsX * CellsZ + 1];
        readonly int[] _fill = new int[CellsX * CellsZ + 1];
        static readonly double[] Turns = { 0.0, 0.6, -0.6, 1.2, -1.2, 2.0, -2.0 };
        int[] _cellItems = new int[256];
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
                    for (int k = 0; k < sp.HerdSize; k++)
                    {
                        double x = cx + rng.NextInt(-4, 5), z = cz + rng.NextInt(-4, 5);
                        if (!Passable((int)x, (int)z)) { x = cx; z = cz; }
                        long born = tick - rng.NextInt(sp.AdultDays, System.Math.Max(sp.AdultDays + 1, sp.LifeDays / 2));
                        Spawn(sp, s, x, z, born, -1, rng);
                    }
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
                    foreach (Band band in _life.Bands)
                        if (!band.Gone) near = System.Math.Min(near, Distance(px * ParcelGrid.Size, pz * ParcelGrid.Size, band.CampX, band.CampZ));
                    if (near > farthest) { farthest = near; best = pz * ParcelGrid.Width + px; }
                }
                if (best < 0) continue;
                double cx = (best % ParcelGrid.Width) * ParcelGrid.Size + 2, cz = (best / ParcelGrid.Width) * ParcelGrid.Size + 2;
                world.Annals.Write(world.Clock.Tick, WanderedInKind, sp.Id, new Int3((int)cx, 0, (int)cz), RecordId.None, sp.HerdSize);
                for (int k = 0; k < sp.HerdSize; k++)
                {
                    double x = cx + rng.NextInt(-4, 5), z = cz + rng.NextInt(-4, 5);
                    if (!Passable((int)x, (int)z)) { x = cx; z = cz; }
                    Spawn(sp, s, x, z, world.Clock.Tick - rng.NextInt(sp.AdultDays, sp.AdultDays * 2 + 1), -1, rng);
                }
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
        public Band FoundBand(SimWorld world, int species, double x, double z, int people, RecordId cause, RngStream rng)
        {
            var band = new Band
            {
                Number = _life.Bands.Count, Species = species, CampX = x, CampZ = z,
                Founded = world.Clock.Tick, CampSince = world.Clock.Tick,
            };
            band.Record = world.Annals.Write(world.Clock.Tick, BandFoundedKind, Symbol.For("band." + band.Number),
                                             new Int3((int)x, 0, (int)z), cause, people);
            _life.Bands.Add(band);
            Species sp = _life.Species[species];
            for (int k = 0; k < people; k++)
            {
                double px = x + rng.NextInt(-3, 4), pz = z + rng.NextInt(-3, 4);
                if (!Passable((int)px, (int)pz)) { px = x; pz = z; }
                long born = world.Clock.Tick - rng.NextInt(sp.AdultDays, sp.AdultDays + (sp.LifeDays - sp.AdultDays) / 2);
                Spawn(sp, species, px, pz, born, band.Number, rng);
            }
            return band;
        }

        /// <summary>One creature, its own lifespan drawn about its kind's.</summary>
        public int Spawn(Species sp, int species, double x, double z, long born, int band, RngStream rng)
        {
            int life = (int)(sp.LifeDays * (0.8 + 0.4 * Unit(rng)));
            int i = _life.Creatures.Add(species, x, z, born, life, sp.Health, band);
            if (band >= 0) _life.Bands[band].Members++;
            return i;
        }

        // ── bands and camps ─────────────────────────────────────────────────

        /// <summary>
        /// Once a month: a band with nobody left is gone; one whose camp is
        /// eaten bare moves it to the best ground within reach; one that has
        /// stayed put long enough has settled.
        /// </summary>
        void WeighCamps(SimWorld world)
        {
            long tick = world.Clock.Tick;
            int daysPerMonth = world.Clock.DaysPerMonth * world.Clock.TicksPerDay;
            int bands = _life.Bands.Count;   // bands split off this month are weighed next month
            for (int n = 0; n < bands; n++)
            {
                Band b = _life.Bands[n];
                if (b.Gone) continue;
                if (b.Members <= 0)
                {
                    b.Gone = true;
                    world.Annals.Write(tick, BandGoneKind, Symbol.For("band." + b.Number), new Int3((int)b.CampX, 0, (int)b.CampZ), b.Record, b.Moves);
                    continue;
                }
                // Grown past twice a founding band: half go to make camp of their own.
                if (b.Members >= _life.Rules.BandSize * 2 && b.Settled) Split(world, b);

                double share = FoodShareAround(b.CampX, b.CampZ, _life.Rules.CampRadius);
                if (share < 0.3)
                {
                    double bx, bz;
                    if (BestGroundNear(b.CampX, b.CampZ, _life.Rules.CampSearch, out bx, out bz)
                        && (bx - b.CampX) * (bx - b.CampX) + (bz - b.CampZ) * (bz - b.CampZ) > 16.0 * 16.0)
                    {
                        b.CampX = bx; b.CampZ = bz; b.CampSince = tick; b.Settled = false; b.Moves++;
                        world.Annals.Write(tick, BandMovedKind, Symbol.For("band." + b.Number), new Int3((int)bx, 0, (int)bz), b.Record, b.Members, b.Moves);
                    }
                }
                if (!b.Settled && tick - b.CampSince >= (long)_life.Rules.SettleMonths * daysPerMonth)
                {
                    b.Settled = true;
                    world.Annals.Write(tick, BandSettledKind, Symbol.For("band." + b.Number), new Int3((int)b.CampX, 0, (int)b.CampZ), b.Record, b.Members);
                }
            }
        }

        public static readonly Symbol BandSplitKind = Symbol.For("life.band-split");

        /// <summary>Half a band — the youngest half — leaves to make camp on the best ground a day or two off.</summary>
        void Split(SimWorld world, Band from)
        {
            double bx, bz;
            if (!BestGroundNear(from.CampX, from.CampZ, _life.Rules.CampSearch * 1.5, out bx, out bz)) return;
            if (Distance(bx, bz, from.CampX, from.CampZ) < _life.Rules.BandSpacing * 0.6) return;
            Creatures c = _life.Creatures;
            var members = new List<int>();
            for (int i = 0; i < c.Length; i++) if (c.Alive[i] && c.Band[i] == from.Number) members.Add(i);
            members.Sort((p, q) => { int k = c.Born[q].CompareTo(c.Born[p]); return k != 0 ? k : p.CompareTo(q); });
            int leaving = members.Count / 2;
            RecordId split = world.Annals.Write(world.Clock.Tick, BandSplitKind, Symbol.For("band." + from.Number),
                                                new Int3((int)bx, 0, (int)bz), from.Record, leaving, from.Members - leaving);
            var band = new Band
            {
                Number = _life.Bands.Count, Species = from.Species, CampX = bx, CampZ = bz,
                Founded = world.Clock.Tick, CampSince = world.Clock.Tick,
            };
            band.Record = world.Annals.Write(world.Clock.Tick, BandFoundedKind, Symbol.For("band." + band.Number),
                                             new Int3((int)bx, 0, (int)bz), split, leaving);
            _life.Bands.Add(band);
            for (int k = 0; k < leaving; k++)
            {
                int i = members[k];
                c.Band[i] = band.Number;
                c.GoalX[i] = bx; c.GoalZ[i] = bz;
                c.Doing[i] = Doing.Roaming;
                from.Members--; band.Members++;
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

        enum Want { Any, Threat, Prey, PreyStarving, Attacker }

        /// <summary>Whether creature j is what creature i is looking for.</summary>
        bool Wanted(Want want, Species sp, int j)
        {
            int s = _life.Creatures.Species[j];
            switch (want)
            {
                case Want.Any: return true;
                case Want.Threat: return sp.FleesFrom(s);
                case Want.Prey: return System.Array.IndexOf(sp.Hunts, s) >= 0;
                case Want.PreyStarving: return System.Array.IndexOf(sp.Hunts, s) >= 0 || System.Array.IndexOf(sp.HuntsWhenStarving, s) >= 0;
                case Want.Attacker: return System.Array.IndexOf(sp.FightsBack, s) >= 0;
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
                        if (j == self || !c.Alive[j] || !Wanted(want, sp, j)) continue;
                        double dx = c.X[j] - x, dz = c.Z[j] - z, d = dx * dx + dz * dz;
                        if (d < best || (d == best && j < found)) { best = d; found = j; }
                    }
                }
            }
            return found;
        }

        /// <summary>How many living creatures of a species are within a radius, and their middle.</summary>
        int Around(int self, int species, double radius, out double mx, out double mz)
        {
            Creatures c = _life.Creatures;
            double x = c.X[self], z = c.Z[self], r2 = radius * radius, sx = 0.0, sz = 0.0;
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
                        if (dx * dx + dz * dz > r2) continue;
                        n++; sx += c.X[j]; sz += c.Z[j];
                    }
                }
            }
            mx = n > 0 ? sx / n : x; mz = n > 0 ? sz / n : z;
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

            double speed = sp.Speed * (blessed ? 1.25 : 1.0);
            int me = c.Species[i];

            // The cursed attack whatever is nearest, their own kind included.
            if (cursed)
            {
                int victim = Nearest(i, 6.0, Want.Any, sp);
                if (victim >= 0 && Chase(world, i, victim, speed, sp.Attack + 0.2)) return;
            }

            // Run from what hunts it.
            if (sp.Flees.Length > 0)
            {
                int threat = Nearest(i, sp.Sense, Want.Threat, sp);
                if (threat >= 0)
                {
                    double ax = c.X[i] - c.X[threat], az = c.Z[i] - c.Z[threat];
                    double len = SimMath.Sqrt(ax * ax + az * az);
                    if (len < 1e-6) { ax = 1; az = 0; len = 1; }
                    Move(i, c.X[i] + ax / len * 8.0, c.Z[i] + az / len * 8.0, speed);
                    c.Doing[i] = Doing.Fleeing;
                    return;
                }
            }

            // Stand up for its own against what attacks them.
            if (sp.FightsBack.Length > 0)
            {
                int attacker = Nearest(i, 8.0, Want.Attacker, sp);
                if (attacker >= 0 && Chase(world, i, attacker, speed, sp.Attack)) return;
            }

            // Hungry: eat where it stands, hunt, or walk to better grazing.
            if (c.Hunger[i] > 0.35)
            {
                int px = (int)c.X[i] / ParcelGrid.Size, pz = (int)c.Z[i] / ParcelGrid.Size;
                int p = pz * ParcelGrid.Width + px;
                Grazing g = _life.Grazing;
                if (sp.Graze > 0.0 && g.Food[p] >= BiteTakes)
                {
                    g.Food[p] -= BiteTakes;
                    c.Hunger[i] = System.Math.Max(0.0, c.Hunger[i] - BiteEases * sp.Graze);
                    // A mouthful on the way does not end the journey.
                    if (c.Doing[i] != Doing.Roaming) c.Doing[i] = Doing.Grazing;
                    return;
                }
                bool starving = c.Hunger[i] > 0.75;
                // Something that can also graze hunts only when grazing has not kept it fed.
                bool hunts = sp.Graze <= 0.0 || c.Hunger[i] > 0.55;
                int prey = hunts && (sp.Hunts.Length > 0 || (starving && sp.HuntsWhenStarving.Length > 0))
                    ? Nearest(i, sp.Sense, starving ? Want.PreyStarving : Want.Prey, sp)
                    : -1;
                if (prey >= 0 && Chase(world, i, prey, speed, sp.Attack)) return;
                if (sp.Graze <= 0.0 && (sp.Hunts.Length > 0 || sp.HuntsWhenStarving.Length > 0))
                {
                    // Nothing to hunt in sight: range out and look.
                    if (c.Doing[i] != Doing.Roaming || Arrived(i)) RoamGoal(i, 30.0, 60.0, rng);
                    Move(i, c.GoalX[i], c.GoalZ[i], speed * 0.8);
                    c.Doing[i] = Doing.Roaming;
                    return;
                }
                if (sp.Graze > 0.0 && c.Doing[i] == Doing.Roaming && !Arrived(i))
                {
                    Move(i, c.GoalX[i], c.GoalZ[i], speed * 0.8);   // on its way: better ground is where it is going
                    return;
                }
                if (sp.Graze > 0.0)
                {
                    if (c.Doing[i] != Doing.Seeking || Arrived(i))
                    {
                        double best = -1.0, bx = c.X[i], bz = c.Z[i];
                        for (int k = 0; k < 6; k++)
                        {
                            double ang = Unit(rng) * 6.283185307179586, dist = 8.0 + Unit(rng) * 16.0;
                            double tx = c.X[i] + SimMath.Cos(ang) * dist, tz = c.Z[i] + SimMath.Sin(ang) * dist;
                            if (!Passable((int)tx, (int)tz)) continue;
                            double food = g.At((int)tx / ParcelGrid.Size, (int)tz / ParcelGrid.Size);
                            if (sp.Person) food -= Distance(tx, tz, CampOf(i)) * 0.02;
                            if (food > best) { best = food; bx = tx; bz = tz; }
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
                double mx, mz;
                int kin = Around(i, me, 16.0, out mx, out mz);
                if (kin >= sp.Crowding && !sp.Person)
                {
                    // Crowded: this one leaves to find ground of its own, and a new herd starts there.
                    RoamGoal(i, 40.0, 90.0, rng);
                    c.Doing[i] = Doing.Roaming;
                }
                else if (kin < sp.Crowding)
                {
                    c.LastBirth[i] = tick;
                    for (int k = 0; k < sp.Litter; k++)
                    {
                        int child = Spawn(sp, me, c.X[i], c.Z[i], tick, c.Band[i], rng);
                        c.Hunger[child] = c.Hunger[i];
                        c.Flags[child] = (byte)(c.Flags[i] & Creatures.Blessed);
                    }
                }
            }

            // On its way somewhere new: keep going until it gets there.
            if (c.Doing[i] == Doing.Roaming && !Arrived(i))
            {
                Move(i, c.GoalX[i], c.GoalZ[i], speed * 0.8);
                return;
            }

            // Otherwise keep near its band's camp or its herd, and wander.
            if (sp.Person && c.Band[i] >= 0)
            {
                Band b = _life.Bands[c.Band[i]];
                if (Distance(c.X[i], c.Z[i], b.CampX, b.CampZ) > _life.Rules.CampRadius || Arrived(i) || c.Doing[i] != Doing.Wandering)
                {
                    if (Arrived(i) && c.Doing[i] == Doing.Wandering && Unit(rng) < 0.7) { c.Doing[i] = Doing.Resting; return; }
                    double ang = Unit(rng) * 6.283185307179586, dist = Unit(rng) * _life.Rules.CampRadius;
                    c.GoalX[i] = b.CampX + SimMath.Cos(ang) * dist; c.GoalZ[i] = b.CampZ + SimMath.Sin(ang) * dist;
                }
                Move(i, c.GoalX[i], c.GoalZ[i], speed * 0.7);
                c.Doing[i] = Doing.Wandering;
                return;
            }
            if (sp.Herd > 0.0)
            {
                double mx, mz;
                int n = Around(i, me, sp.Herd * 2.5, out mx, out mz);
                if (n > 0 && Distance(c.X[i], c.Z[i], mx, mz) > sp.Herd)
                {
                    Move(i, mx, mz, speed * 0.6);
                    c.Doing[i] = Doing.Following;
                    return;
                }
            }
            if (Arrived(i) || c.Doing[i] != Doing.Wandering)
            {
                if (Unit(rng) < 0.85) { c.Doing[i] = c.Hunger[i] < 0.2 ? Doing.Resting : Doing.Idle; return; }
                double ang = Unit(rng) * 6.283185307179586, dist = 4.0 + Unit(rng) * 10.0;
                c.GoalX[i] = c.X[i] + SimMath.Cos(ang) * dist; c.GoalZ[i] = c.Z[i] + SimMath.Sin(ang) * dist;
            }
            Move(i, c.GoalX[i], c.GoalZ[i], speed * 0.5);
            c.Doing[i] = Doing.Wandering;
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
            int band = c.Band[i];
            if (_life.Species[c.Species[i]].Person)
                world.Annals.Write(world.Clock.Tick, PersonDiedKind, Symbol.For("band." + band), new Int3((int)c.X[i], 0, (int)c.Z[i]),
                                   why.Exists ? why : (band >= 0 ? _life.Bands[band].Record : RecordId.None), (long)cause);
            c.Kill(i, cause);
            if (band >= 0 && band < _life.Bands.Count) _life.Bands[band].Members--;
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

        Band CampOf(int i)
        {
            int b = _life.Creatures.Band[i];
            return b >= 0 ? _life.Bands[b] : null;
        }

        static double Distance(double ax, double az, double bx, double bz)
        {
            double dx = ax - bx, dz = az - bz;
            return SimMath.Sqrt(dx * dx + dz * dz);
        }

        static double Distance(double x, double z, Band b) { return b == null ? 0.0 : Distance(x, z, b.CampX, b.CampZ); }

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
