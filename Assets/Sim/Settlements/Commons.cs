using System.Collections.Generic;
using Godless.Sim.Annals;
using Godless.Sim.Build;
using Godless.Sim.Content;
using Godless.Sim.Core;
using Godless.Sim.Drives;
using Godless.Sim.Harness;
using Godless.Sim.Voxels;
using Godless.Sim.World;

namespace Godless.Sim.Settlements
{
    /// <summary>One form a town's gathering place takes, as content declares it. S2Z.</summary>
    public sealed class CommonsStage
    {
        public string Id { get; internal set; }
        public string Name { get; internal set; }
        /// <summary>People a gathering here holds before it is crowded.</summary>
        public int Holds { get; internal set; }
        public int Seats { get; internal set; }
        /// <summary>Voxels from the fire the seats, and the people gathered, stand in a ring.</summary>
        public int Ring { get; internal set; }
        /// <summary>Voxels from the fire the ground is paved, or zero.</summary>
        public int Pave { get; internal set; }
        public string Fire { get; internal set; } = "";
        /// <summary>Commissioned through the intent bus as a building, rather than laid by hand.</summary>
        public bool Commissions { get; internal set; }

        /// <summary>What the place is called by the intent kind of the building that made it.</summary>
        internal readonly Dictionary<string, string> Buildings = new Dictionary<string, string>();

        /// <summary>The intent kinds whose buildings are gathered inside of an evening.</summary>
        internal readonly List<string> Inside = new List<string>();
    }

    /// <summary>What brings a town together at its fire, as content declares it. S2Z.</summary>
    public sealed class GatheringKind
    {
        public string Name { get; internal set; }
        public Symbol Id { get; internal set; }
        public string Trigger { get; internal set; }
        public double Share { get; internal set; }
        public int EveryDays { get; internal set; }
        public string Doing { get; internal set; }
        public string Pose { get; internal set; }
        public double Takes { get; internal set; }
        internal double[] Relieves;
    }

    /// <summary>How the commons grows and what gathers on it. S2Z.</summary>
    public sealed class CommonsRules
    {
        public readonly List<CommonsStage> Stages = new List<CommonsStage>();
        public readonly List<GatheringKind> Gatherings = new List<GatheringKind>();
        public readonly List<string> Problems = new List<string>();
        public int ReserveParcels = 2;
        public int CrowdedGatherings = 3;
        public int WorkParty = 6;
        public int SeatsPerWorker = 1;
        public int PavingPerWorker = 8;
        public int SeatVoxels = 4;
        public readonly List<KeyValuePair<Symbol, string>> SeatModels = new List<KeyValuePair<Symbol, string>>();
        public readonly List<Symbol> Paving = new List<Symbol>();
        public string BareGround = "soil";
        public string IndoorsGene = "communal_ratio";
        public double IndoorsAbove = 0.5;
        public string Tell = "";

        /// <summary>The rules, or null where content declares no commons.</summary>
        public static CommonsRules FromContent(ContentDatabase content, NeedTable needs)
        {
            string id = null;
            foreach (string i in content.Ids("commons")) { id = i; break; }
            if (id == null) return null;
            JsonValue doc = content.Get("commons", id);
            var r = new CommonsRules
            {
                Tell = doc["tell"].AsString(""),
                ReserveParcels = doc["reserveParcels"].AsInt32(2),
                CrowdedGatherings = System.Math.Max(1, doc["crowdedGatherings"].AsInt32(3)),
                WorkParty = System.Math.Max(1, doc["workParty"].AsInt32(6)),
                SeatsPerWorker = System.Math.Max(1, doc["seatsPerWorker"].AsInt32(1)),
                PavingPerWorker = System.Math.Max(1, doc["pavingPerWorker"].AsInt32(8)),
                SeatVoxels = System.Math.Max(0, doc["seatVoxels"].AsInt32(4)),
                BareGround = doc["bareGround"].AsString("soil"),
                IndoorsGene = doc["indoorsGene"].AsString("communal_ratio"),
                IndoorsAbove = doc["indoorsAbove"].AsDouble(0.5),
            };
            JsonValue seats = doc["seatModels"];
            foreach (string k in seats.Keys) r.SeatModels.Add(new KeyValuePair<Symbol, string>(Symbol.For("class." + k), seats[k].AsString("")));
            JsonValue paving = doc["paving"];
            for (int i = 0; i < paving.Count; i++) r.Paving.Add(Symbol.For("class." + paving[i].AsString("")));

            JsonValue stages = doc["stages"];
            for (int i = 0; i < stages.Count; i++)
            {
                JsonValue st = stages[i];
                var stage = new CommonsStage
                {
                    Id = st["id"].AsString("stage" + i),
                    Name = st["name"].AsString(st["id"].AsString("the commons")),
                    Holds = st["holds"].AsInt32(20),
                    Seats = st["seats"].AsInt32(0),
                    Ring = System.Math.Max(2, st["ring"].AsInt32(3)),
                    Pave = st["pave"].AsInt32(0),
                    Fire = st["fire"].AsString(""),
                    Commissions = st["commissions"].AsBool(false),
                };
                JsonValue named = st["buildings"];
                foreach (string k in named.Keys) stage.Buildings[k] = named[k].AsString(k);
                JsonValue inside = st["inside"];
                for (int k = 0; k < inside.Count; k++) stage.Inside.Add(inside[k].AsString(""));
                r.Stages.Add(stage);
            }
            if (r.Stages.Count == 0) r.Problems.Add("commons '" + id + "' declares no stages.");

            foreach (string g in content.Ids("gathering"))
            {
                JsonValue gd = content.Get("gathering", g);
                var kind = new GatheringKind
                {
                    Name = g,
                    Id = Symbol.For("gathering." + g),
                    Trigger = gd["trigger"].AsString(""),
                    Share = SimMath.Clamp01(gd["share"].AsDouble(1.0)),
                    EveryDays = gd["everyDays"].AsInt32(0),
                    Doing = gd["doing"].AsString(g),
                    Pose = gd["pose"].AsString("stand"),
                    Takes = gd["takes"].AsDouble(0.3),
                    Relieves = new double[needs.Count],
                };
                string fault = null;
                switch (kind.Trigger)
                {
                    case "death": case "emigration": case "founding-day": case "harvest": case "evenings": break;
                    default: fault = "is triggered by '" + kind.Trigger + "', which nothing sets off"; break;
                }
                JsonValue rel = gd["relieves"];
                foreach (string need in rel.Keys)
                {
                    int n = needs.IndexOf(need);
                    if (n < 0) { fault = "relieves need '" + need + "', which is not loaded"; break; }
                    kind.Relieves[n] = rel[need].AsDouble(0.0);
                }
                if (kind.Trigger == "evenings" && kind.EveryDays < 1) fault = "happens some evenings but does not say how often";
                if (!(kind.Takes > 0.0 && kind.Takes < 1.0)) fault = "takes " + kind.Takes + " of an evening; it must be a share of one";
                if (fault != null) { r.Problems.Add("gathering '" + g + "' " + fault + "."); continue; }
                r.Gatherings.Add(kind);
            }
            return r;
        }
    }

    /// <summary>One evening's gathering at a town's fire. S2Z.</summary>
    public sealed class Gathering
    {
        public GatheringKind Kind { get; internal set; }
        public RecordId Record { get; internal set; }
        public long Day { get; internal set; }
        public int Attending { get; internal set; }
        /// <summary>Where it is held, in words: "the fire circle", "the hall".</summary>
        public string Place { get; internal set; }
    }

    /// <summary>
    /// A town's first fire and what it becomes. S2Z.
    ///
    /// The fire the founders light is kept: ground round it is set aside the
    /// day it is lit, and the town comes back to it — not every evening, and
    /// not to eat, but when someone dies, when a field is cut, on the day the
    /// fire was first lit, when families leave, and some evenings just to talk.
    /// A gathering bigger than the place holds is remembered, and when that has
    /// happened often enough the town makes the place bigger: logs to sit on,
    /// then a paved meeting ground, laid by whoever has an afternoon to spare,
    /// and at last a hall or a colonnade commissioned like any other building.
    ///
    /// The tell: the same spot in the middle of the village at every age of it,
    /// a ring of stones, then a ring of logs, then a paved square, then a hall
    /// with its door on the old fire, and the whole village standing there the
    /// evening after a death.
    /// </summary>
    public sealed class Commons
    {
        public static readonly Symbol KeptKind = Symbol.For("commons.kept");
        public static readonly Symbol WorksKind = Symbol.For("commons.works");
        public static readonly Symbol RaisedKind = Symbol.For("commons.raised");
        public static readonly Symbol GatheredKind = Symbol.For("commons.gathered");

        /// <summary>Which of content's stages it has reached.</summary>
        public int Stage { get; internal set; }

        /// <summary>The record that set the ground aside, and the one behind its latest stage.</summary>
        public RecordId Kept { get; internal set; }
        public RecordId Latest { get; internal set; }

        /// <summary>Gatherings in a row, near enough, that it was too small for.</summary>
        public int Crowded { get; internal set; }

        /// <summary>Tonight's gathering, or null.</summary>
        public Gathering Tonight { get; internal set; }

        /// <summary>The last gathering held, or null.</summary>
        public Gathering Last { get; internal set; }

        public int GatheringsHeld { get; internal set; }

        /// <summary>The hall or colonnade, once commissioned.</summary>
        public Project Building { get; internal set; }

        /// <summary>Seats and paving still to lay for the next stage, and the record they are laid under.</summary>
        internal readonly List<int> WorkLeft = new List<int>();   // encoded columns: z * SizeX + x; seats negative - 1 - index
        internal RecordId Works = RecordId.None;
        internal int WorksStage = -1;
        internal readonly HashSet<ulong> Party = new HashSet<ulong>();
        internal long PartyDay = -1;

        /// <summary>The stage being laid by hand, or -1.</summary>
        public int Underway { get { return WorksStage; } }

        /// <summary>The detail drawn as the fire, and the seats laid, by detail id.</summary>
        internal int FireDetail = -1;

        /// <summary>The detail the fire is drawn as, or -1 once a hall has taken it for its hearth.</summary>
        public int Fire { get { return FireDetail; } }
        internal readonly List<int> SeatDetails = new List<int>();
        public IReadOnlyList<int> Seats { get { return SeatDetails; } }
        public int Paved { get; internal set; }

        internal long LastHeld = long.MinValue / 2;
        internal bool Cleared;
        internal readonly Dictionary<string, long> LastOfKind = new Dictionary<string, long>();

        /// <summary>What the place is called now: its stage's name, or the building's that took it over.</summary>
        public string PlaceName(CommonsRules rules)
        {
            CommonsStage stage = rules.Stages[System.Math.Min(Stage, rules.Stages.Count - 1)];
            string named;
            if (stage.Commissions && Building != null && stage.Buildings.TryGetValue(Building.Intent.Kind.Name, out named)) return named;
            return stage.Name;
        }

        /// <summary>Whether people gather inside a building rather than round the fire in the open.</summary>
        public bool Indoors(CommonsRules rules)
        {
            if (Building == null || !Building.Complete || Building.Destroyed) return false;
            foreach (CommonsStage st in rules.Stages) if (st.Inside.Contains(Building.Intent.Kind.Name)) return true;
            return false;
        }

        /// <summary>Whether a person comes to tonight's gathering: theirs to decide by the day, the same on every machine (L2).</summary>
        public bool Attends(Agent a)
        {
            Gathering g = Tonight;
            if (g == null) return false;
            if (g.Kind.Share >= 1.0) return true;
            ulong h = StableHash.Combine(StableHash.Combine(a.Id.Hash, (ulong)g.Day), g.Kind.Id.Hash);
            return (h % 1000UL) < (ulong)(g.Kind.Share * 1000.0);
        }

        /// <summary>Whether a person is in today's work party on the next stage.</summary>
        public bool InParty(Agent a, long day) { return WorksStage >= 0 && PartyDay == day && Party.Contains(a.Id.Hash); }

        /// <summary>
        /// Where the k-th person at a gathering stands: inside the hall once there
        /// is one and it is an evening, else in rings round the fire, a pace apart.
        /// </summary>
        public void Spot(Settlement s, IslandMap island, CommonsRules rules, int k, bool evening, out int x, out int z)
        {
            if (evening && Indoors(rules))
            {
                int w = System.Math.Max(1, Building.Plan.Width - 2 * Grammar.Margin - 4);
                int d = System.Math.Max(1, Building.Plan.Depth - 2 * Grammar.Margin - 4);
                Int3 c = Construction.World(Building, Grammar.Margin + 2 + k % w, 0, Grammar.Margin + 2 + (k / w) % d);
                x = c.X; z = c.Z;
                return;
            }
            int ring0 = rules.Stages[System.Math.Min(Stage, rules.Stages.Count - 1)].Ring;
            int ring = 0, left = k;
            while (true)
            {
                int r = ring0 + 2 * ring;
                int around = System.Math.Max(6, (int)(2.0 * 3.14159265358979 * r / 2.0));
                if (left < around || ring > 8)
                {
                    double angle = 2.0 * 3.14159265358979 * (left % around) / around + ring * 0.37;
                    x = s.Hearth.X + (int)SimMath.Round(SimMath.Cos(angle) * r);
                    z = s.Hearth.Z + (int)SimMath.Round(SimMath.Sin(angle) * r);
                    if (island != null && (x < 0 || z < 0 || x >= ChunkStore.SizeX || z >= ChunkStore.SizeZ || !island.IsLand(x, z)))
                    { x = s.Hearth.X; z = s.Hearth.Z; }
                    return;
                }
                left -= around;
                ring++;
            }
        }
    }

    /// <summary>Keeps every town's commons: the ground, the fire, the gatherings, and the works that grow it. S2Z.</summary>
    public sealed class CommonsSystem : ISimSystem
    {
        public static readonly Symbol SystemId = Symbol.For("system.commons");

        readonly CommonsRules _rules;
        readonly ParcelGrid _grid;
        readonly DetailModelTable _models;

        public CommonsSystem(CommonsRules rules, ParcelGrid grid, DetailModelTable models)
        {
            _rules = rules;
            _grid = grid;
            _models = models;
        }

        public Symbol Id { get { return SystemId; } }
        public CommonsRules Rules { get { return _rules; } }

        public void Tick(SimWorld world)
        {
            if (_rules == null || _rules.Stages.Count == 0) return;
            SimClock clock = world.Clock;
            bool night = clock.TickOfDay == clock.TicksPerDay - 1;
            foreach (Settlement s in world.Settlements)
            {
                if (s.Commons == null) Keep(s, world);
                Commons c = s.Commons;
                if (clock.IsFirstTickOfDay)
                {
                    Watch(s, c, world);
                    Muster(s, c, clock.TotalDays);
                }
                if (clock.TickOfDay == 2 % clock.TicksPerDay) Work(s, c, world);
                if (night) Gather(s, c, world);
                else if (c.Tonight != null && c.Tonight.Day != clock.TotalDays) c.Tonight = null;
            }
        }

        /// <summary>The day the fire is lit: the ground round it set aside, and the fire itself drawn.</summary>
        void Keep(Settlement s, SimWorld world)
        {
            var c = new Commons();
            s.Commons = c;
            c.Kept = world.Annals.Write(world.Clock.Tick, Commons.KeptKind, s.Id, s.Hearth, s.Founded, _rules.ReserveParcels);
            c.Latest = c.Kept;
            s.CommonsRecords.Add(c.Kept.Index);

            int r = _rules.ReserveParcels;
            for (int dz = -r; dz <= r; dz++)
                for (int dx = -r; dx <= r; dx++)
                {
                    if (dx * dx + dz * dz > r * r + r) continue;
                    int px = s.HearthParcelX + dx, pz = s.HearthParcelZ + dz;
                    if (!ParcelGrid.InBounds(px, pz) || !_grid.IsLand(px, pz)) continue;
                    // The hearth's own parcel moves to the commons; anything else already claimed stays whose it is.
                    if (s.IsClaimed(px, pz) && !(dx == 0 && dz == 0)) continue;
                    s.ClaimParcel(px, pz, c.Kept);
                }

            CommonsStage first = _rules.Stages[0];
            DetailModel fire = _models != null && first.Fire.Length > 0 ? _models.Find(first.Fire) : null;
            if (fire != null && world.Details != null)
            {
                int cells = DetailModelTable.CellsPerVoxel;
                int y = _grid.GroundAt(s.Hearth.X, s.Hearth.Z) + 1;
                c.FireDetail = world.Details.Place(fire, s.Hearth.X * cells + (cells - fire.SizeX) / 2, y * cells,
                                                   s.Hearth.Z * cells + (cells - fire.SizeZ) / 2, 0, null, world.Clock.Tick, c.Kept);
            }
        }

        /// <summary>A hall or colonnade the town commissioned, once it stands, is the last stage.</summary>
        void Watch(Settlement s, Commons c, SimWorld world)
        {
            if (c.Building == null)
                foreach (Project p in s.Projects)
                    if (p.IsCommons && !p.Destroyed) { c.Building = p; break; }
            // Once it is begun, the fire and the seats under its footprint are taken up: a
            // hall built over the old fire has the fire for its hearth.
            if (c.Building != null && !c.Cleared && c.Building.Begun.Exists && c.Building.Site != null && world.Details != null)
            {
                c.Cleared = true;
                Site site = c.Building.Site;
                if (c.FireDetail >= 0 && Under(site, s.Hearth.X, s.Hearth.Z))
                {
                    world.Details.Remove(c.FireDetail, world.Clock.Tick, c.Building.Begun);
                    c.FireDetail = -1;
                }
                for (int k = c.SeatDetails.Count - 1; k >= 0; k--)
                {
                    DetailInstance seat = world.Details.Get(c.SeatDetails[k]);
                    if (seat != null && !Under(site, seat.VoxelX, seat.VoxelZ)) continue;
                    if (seat != null) world.Details.Remove(c.SeatDetails[k], world.Clock.Tick, c.Building.Begun);
                    c.SeatDetails.RemoveAt(k);
                }
            }
            int last = _rules.Stages.Count - 1;
            if (c.Building != null && c.Building.Complete && c.Stage < last && _rules.Stages[last].Commissions)
                Raise(s, c, world, last, c.Building.Begun.Exists ? c.Building.Begun : c.Latest);
        }

        /// <summary>Today's work party on the next stage: whoever has nothing asked of them, then foragers, a few a day in turn.</summary>
        void Muster(Settlement s, Commons c, long day)
        {
            c.Party.Clear();
            c.PartyDay = day;
            if (c.WorksStage < 0 || s.People.Count == 0) return;
            int n = s.People.Count, start = (int)(day % n);
            for (int pass = 0; pass < 2 && c.Party.Count < _rules.WorkParty; pass++)
                for (int k = 0; k < n && c.Party.Count < _rules.WorkParty; k++)
                {
                    int i = (start + k) % n;
                    int task = s.Tasks != null ? s.Tasks.CurrentTask(i) : -1;
                    string verb = task >= 0 ? s.Tasks.KindOf(task).Verb : "";
                    bool free = task < 0;
                    if (pass == 0 ? free : verb == "forage") c.Party.Add(s.People[i].Id.Hash);
                }
        }

        /// <summary>An afternoon's work on the next stage: seats laid from what the yard can spare, ground paved.</summary>
        void Work(Settlement s, Commons c, SimWorld world)
        {
            if (c.WorksStage < 0 || c.PartyDay != world.Clock.TotalDays || c.Party.Count == 0) return;
            int seats = c.Party.Count * _rules.SeatsPerWorker, paving = c.Party.Count * _rules.PavingPerWorker;
            long tick = world.Clock.Tick;
            int cells = DetailModelTable.CellsPerVoxel;
            CommonsStage stage = _rules.Stages[c.WorksStage];

            while (c.WorkLeft.Count > 0)
            {
                int item = c.WorkLeft[0];
                if (item < 0)
                {
                    if (seats <= 0) break;
                    int index = -1 - item;
                    string modelName;
                    int material = SeatMaterial(s, out modelName);
                    if (material < 0 && _rules.SeatVoxels > 0) break;   // nothing to make a seat of: the works wait
                    DetailModel model = _models != null ? _models.Find(modelName) : null;
                    if (material >= 0) s.Stock.Remove(material, _rules.SeatVoxels);
                    double angle = 2.0 * 3.14159265358979 * index / System.Math.Max(1, stage.Seats) + c.WorksStage * 0.3;
                    int x = s.Hearth.X + (int)SimMath.Round(SimMath.Cos(angle) * stage.Ring);
                    int z = s.Hearth.Z + (int)SimMath.Round(SimMath.Sin(angle) * stage.Ring);
                    if (model != null && world.Details != null && InWorld(x, z) && (world.Island == null || world.Island.IsLand(x, z)))
                    {
                        // Along the ring, so it is sat on facing the fire.
                        int turn = System.Math.Abs(SimMath.Cos(angle)) > System.Math.Abs(SimMath.Sin(angle)) ? 1 : 0;
                        int y = _grid.GroundAt(x, z) + 1;
                        int sx = turn == 1 ? model.SizeZ : model.SizeX, sz = turn == 1 ? model.SizeX : model.SizeZ;
                        c.SeatDetails.Add(world.Details.Place(model, x * cells + (cells - sx) / 2, y * cells, z * cells + (cells - sz) / 2,
                                                              turn, null, tick, c.Works));
                    }
                    seats--;
                }
                else
                {
                    if (paving <= 0) break;
                    int x = item % ChunkStore.SizeX, z = item / ChunkStore.SizeX;
                    int g = _grid.GroundAt(x, z);
                    if (world.Voxels.Get(x, g + 1, z) == VoxelTypes.AirId && world.Voxels.Get(x, g, z) != VoxelTypes.AirId)
                    {
                        ushort pave = Paving(s, world);
                        if (pave != VoxelTypes.AirId && world.Voxels.Get(x, g, z) != pave)
                        {
                            world.Voxels.Set(new Int3(x, g, z), pave, tick, c.Works);
                            c.Paved++;
                        }
                    }
                    paving--;
                }
                c.WorkLeft.RemoveAt(0);
            }
            if (c.WorkLeft.Count == 0) Raise(s, c, world, c.WorksStage, c.Works);
        }

        /// <summary>Whether a column lies on a site's parcels, with a voxel to spare round them.</summary>
        static bool Under(Site site, int x, int z)
        {
            int x0 = site.ParcelX * ParcelGrid.Size - 1, z0 = site.ParcelZ * ParcelGrid.Size - 1;
            return x >= x0 && z >= z0 && x <= x0 + site.ParcelsWide * ParcelGrid.Size + 1 && z <= z0 + site.ParcelsDeep * ParcelGrid.Size + 1;
        }

        static bool InWorld(int x, int z) { return x >= 0 && z >= 0 && x < ChunkStore.SizeX && z < ChunkStore.SizeZ; }

        /// <summary>The material a seat is made of — the first class content names that the yard holds enough of — and its model.</summary>
        int SeatMaterial(Settlement s, out string model)
        {
            model = _rules.SeatModels.Count > 0 ? _rules.SeatModels[0].Value : "";
            if (s.Stock == null) return -1;
            foreach (KeyValuePair<Symbol, string> kv in _rules.SeatModels)
                for (int m = 0; m < s.Stock.Materials.Count; m++)
                    if (s.Stock.Materials[m].Class == kv.Key && s.Stock.Has(m, _rules.SeatVoxels)) { model = kv.Value; return m; }
            return -1;
        }

        /// <summary>A voxel to pave with: stone the yard holds, then earth, else the ground beaten bare.</summary>
        ushort Paving(Settlement s, SimWorld world)
        {
            if (s.Stock != null)
                foreach (Symbol cls in _rules.Paving)
                    for (int m = 0; m < s.Stock.Materials.Count; m++)
                        if (s.Stock.Materials[m].Class == cls && s.Stock.Has(m, 1))
                        {
                            s.Stock.Remove(m, 1);
                            return world.VoxelTypes.IdOf(s.Stock.Materials[m].Voxel);
                        }
            return world.VoxelTypes.IdOf(Symbol.For("voxel." + _rules.BareGround));
        }

        void Raise(Settlement s, Commons c, SimWorld world, int stage, RecordId cause)
        {
            c.Stage = stage;
            c.Crowded = 0;
            c.WorksStage = -1;
            c.WorkLeft.Clear();
            c.Latest = world.Annals.Write(world.Clock.Tick, Commons.RaisedKind, s.Id, s.Hearth, cause.Exists ? cause : c.Kept,
                                          stage, _rules.Stages[stage].Holds, new[] { Symbol.For("commons." + _rules.Stages[stage].Id) });
        }

        /// <summary>
        /// The evening: whatever there is to gather for tonight, first come,
        /// held, attended, and felt; and a place too small for it remembered.
        /// </summary>
        void Gather(Settlement s, Commons c, SimWorld world)
        {
            c.Tonight = null;
            if (s.People.Count == 0) return;
            long day = world.Clock.TotalDays, tick = world.Clock.Tick;
            int dayTicks = world.Clock.TicksPerDay;

            GatheringKind chosen = null;
            RecordId cause = RecordId.None;
            foreach (GatheringKind kind in _rules.Gatherings)
            {
                long last;
                if (!c.LastOfKind.TryGetValue(kind.Name, out last)) last = long.MinValue / 2;
                if (kind.EveryDays > 0 && day - last < kind.EveryDays) continue;
                bool due = false;
                RecordId why = RecordId.None;
                switch (kind.Trigger)
                {
                    case "death": due = s.LastDeathTick >= 0 && tick - s.LastDeathTick < dayTicks; why = s.LastDeath; break;
                    case "emigration": due = s.LastOutgrownTick >= 0 && tick - s.LastOutgrownTick < dayTicks; why = s.LastOutgrown; break;
                    case "harvest": due = s.LastHarvestTick >= 0 && tick - s.LastHarvestTick < dayTicks; why = s.LastHarvest; break;
                    case "founding-day":
                    {
                        long founded = world.Annals.Get(s.Founded).Tick / dayTicks;
                        due = day > founded && (day - founded) % world.Clock.DaysPerYear == 0;
                        why = s.Founded;
                        break;
                    }
                    case "evenings":
                    {
                        // Some evenings, not on a timetable: a few days either way of how often.
                        ulong h = StableHash.Combine(s.Id.Hash, (ulong)day);
                        due = day - last >= kind.EveryDays && day - c.LastHeld >= 2 && h % 3UL == 0;
                        why = c.Latest;
                        break;
                    }
                }
                if (!due) continue;
                chosen = kind;
                cause = why.Exists ? why : c.Latest;
                break;
            }
            if (chosen == null) return;

            CommonsStage stage = _rules.Stages[System.Math.Min(c.Stage, _rules.Stages.Count - 1)];
            var g = new Gathering { Kind = chosen, Day = day, Place = c.PlaceName(_rules) };
            c.Tonight = g;
            int attending = 0;
            foreach (Agent a in s.People)
            {
                if (!c.Attends(a)) continue;
                attending++;
                for (int n = 0; n < chosen.Relieves.Length && n < a.Levels.Length; n++)
                    if (chosen.Relieves[n] != 0.0) a.Levels[n] = SimMath.Clamp01(a.Levels[n] - chosen.Relieves[n]);
            }
            g.Attending = attending;
            g.Record = world.Annals.Write(tick, Commons.GatheredKind, s.Id, s.Hearth, cause, attending, stage.Holds, new[] { chosen.Id });
            c.Last = g;
            c.LastHeld = day;
            c.LastOfKind[chosen.Name] = day;
            c.GatheringsHeld++;

            if (attending <= stage.Holds) { if (c.Crowded > 0 && chosen.Share >= 0.8) c.Crowded--; return; }
            c.Crowded++;
            if (c.Crowded < _rules.CrowdedGatherings || c.WorksStage >= 0) return;
            int next = c.Stage + 1;
            if (next >= _rules.Stages.Count) return;

            CommonsStage up = _rules.Stages[next];
            if (up.Commissions)
            {
                if (s.Intents == null || c.Building != null) return;
                double gene = s.Genome != null ? s.Genome[Symbol.For("gene." + _rules.IndoorsGene)] : double.NaN;
                bool indoors = double.IsNaN(gene) || gene > _rules.IndoorsAbove;
                // Pressed hard enough to raise it: the town has asked three times.
                s.Intents.Press(indoors ? "crowded-gathering-indoors" : "crowded-gathering-outdoors",
                                s.HearthParcelX, s.HearthParcelZ, 1000.0, g.Record);
                c.Crowded = 0;
                return;
            }

            // Laid by hand: the seats, then the ground from the fire outward.
            c.WorksStage = next;
            c.Works = world.Annals.Write(tick, Commons.WorksKind, s.Id, s.Hearth, g.Record, next, up.Seats + up.Pave,
                                         new[] { Symbol.For("commons." + up.Id) });
            c.WorkLeft.Clear();
            for (int k = 0; k < up.Seats; k++) c.WorkLeft.Add(-1 - k);
            if (up.Pave > 0)
            {
                var columns = new List<long>();
                for (int dz = -up.Pave; dz <= up.Pave; dz++)
                    for (int dx = -up.Pave; dx <= up.Pave; dx++)
                    {
                        long d = dx * dx + dz * dz;
                        if (d > (long)up.Pave * up.Pave || d <= 2) continue;
                        int x = s.Hearth.X + dx, z = s.Hearth.Z + dz;
                        if (!InWorld(x, z) || !s.IsCommons(x / ParcelGrid.Size, z / ParcelGrid.Size)) continue;
                        columns.Add((d << 24) | (long)(z * ChunkStore.SizeX + x));
                    }
                columns.Sort();
                foreach (long col in columns) c.WorkLeft.Add((int)(col & 0xFFFFFF));
            }
        }
    }
}
