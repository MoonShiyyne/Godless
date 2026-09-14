using System.Collections.Generic;
using Godless.Sim.Annals;
using Godless.Sim.Core;
using Godless.Sim.Drives;
using Godless.Sim.World;

namespace Godless.Sim.Settlements
{
    /// <summary>
    /// One settlement: its people, its hearth, its roofs. The thing drives,
    /// intents, stock and the genome all belong to.
    ///
    /// Stratum 1 has exactly one, placed by whoever sets the scenario up.
    /// Founding a settlement by decision — the quorum over candidate sites —
    /// is S3A and S30, two strata away. What exists here is the minimum those
    /// systems will own: a place, a roll of people, and a founding record
    /// that every later cause chain in this settlement ends at.
    /// </summary>
    public sealed class Settlement
    {
        public static readonly Symbol FoundedKind = Symbol.For("settlement.founded");

        readonly List<Agent> _people = new List<Agent>();
        DriveRules _rules;

        Settlement() { }

        public Symbol Id { get; private set; }

        /// <summary>The voxel the fire burns on. Where the people are, until S13 lets them move.</summary>
        public Int3 Hearth { get; private set; }

        public int HearthParcelX { get { return Hearth.X / ParcelGrid.Size; } }
        public int HearthParcelZ { get { return Hearth.Z / ParcelGrid.Size; } }

        /// <summary>The biome the hearth stands in, which sets its weather.</summary>
        public Biome Biome { get; private set; }

        public RecordId Founded { get; private set; }

        public IReadOnlyList<Agent> People { get { return _people; } }

        /// <summary>
        /// Sleeping places under a roof. Zero at founding: the first people
        /// arrive with nothing, sleep in the open, and that is where the first
        /// pressure comes from. S1A raises it as shelters are finished.
        /// </summary>
        public int ShelterCapacity { get; set; }

        /// <summary>Where unmet needs go: the intent bus once attached, a plain tally before.</summary>
        public IPressureSink Pressure { get; set; }

        /// <summary>The settlement's build intents (S14), or null in a settlement that cannot ask for anything.</summary>
        public Build.IntentBus Intents { get; private set; }

        /// <summary>Routes this settlement's pressure into an intent bus. From now on unmet needs can commission.</summary>
        public void AttachIntents(Build.IntentBus bus)
        {
            Intents = bus;
            Pressure = bus;
        }

        /// <summary>Meals in the store (S1E).</summary>
        public double Food { get; set; }

        // Farms, in the order they were laid (S2I).
        internal readonly List<Farm> FarmList = new List<Farm>();

        // Plots given up to buildings, waiting for their plants to be cleared (S2I).
        internal readonly List<Plot> GivenUpPlots = new List<Plot>();

        // The farm.laid record indices, so "is this claim a field?" is a lookup (S2I). Never iterated.
        internal readonly HashSet<int> FarmRecords = new HashSet<int>();

        /// <summary>Whether a parcel is claimed by one of the farms.</summary>
        public bool IsField(int px, int pz)
        {
            int owner;
            return World.ParcelGrid.InBounds(px, pz) && _claims.TryGetValue(pz * World.ParcelGrid.Width + px, out owner) && FarmRecords.Contains(owner);
        }
        public IReadOnlyList<Farm> Farms { get { return FarmList; } }

        // S2Z. The ground kept round the first fire: claimed, so nothing is built on
        // it, and walked across like a field.
        internal readonly HashSet<int> CommonsRecords = new HashSet<int>();

        /// <summary>Ground kept clear round the fire (S2Z).</summary>
        public bool IsCommons(int px, int pz)
        {
            int owner;
            return CommonsRecords.Count > 0 && World.ParcelGrid.InBounds(px, pz)
                && _claims.TryGetValue(pz * World.ParcelGrid.Width + px, out owner) && CommonsRecords.Contains(owner);
        }

        /// <summary>The fire, the ground round it and what the town has made of them (S2Z), or null before the commons system has seen it.</summary>
        public Commons Commons { get; internal set; }

        // S2Z. The latest of what a town gathers for, kept as it happens so the
        // commons need not search the annals.
        internal RecordId LastDeath = RecordId.None, LastHarvest = RecordId.None, LastOutgrown = RecordId.None;
        internal long LastDeathTick = -1, LastHarvestTick = -1, LastOutgrownTick = -1;

        // Heaps waiting to be carried in (S2X), in the order they were started.
        internal readonly List<Pile> PileList = new List<Pile>();
        public IReadOnlyList<Pile> Piles { get { return PileList; } }

        /// <summary>Meals lying in the fields that there is somewhere to put, as of the last tick (S2X).</summary>
        public double HarvestToFetch { get; internal set; }

        /// <summary>Daylight agent-ticks, and how many of them were spent within a stone's throw of the fire (S2V).</summary>
        public long DaylightTicks { get; internal set; }
        public long DaylightAtFire { get; internal set; }

        /// <summary>Meals rotting a day, lately: a slow average the farms read before they grow (S2I).</summary>
        public double RotPerDay { get; internal set; }
        internal double SpoiledYesterday;

        /// <summary>Meals lost to rot so far (S2H).</summary>
        public double FoodSpoiled { get; internal set; }

        // The last food.spoiled record and when, so rotting is on record weekly rather than daily (S2H).
        internal Annals.RecordId SpoiledRecord = Annals.RecordId.None;
        internal long SpoiledTick;

        // The last food.short record and when (S2I).
        internal Annals.RecordId ShortRecord = Annals.RecordId.None;
        internal long ShortTick;

        /// <summary>Whether everyone ate this morning.</summary>
        public bool Fed { get; internal set; }

        /// <summary>Days in a row somebody has gone without.</summary>
        public int HungryDays { get; internal set; }

        /// <summary>Everyone who has ever been born here or died here, for the record.</summary>
        public int Born { get; internal set; }
        public int Died { get; internal set; }

        /// <summary>Takes in a person, with a stable id of their own. The task board grows with them.</summary>
        public Agent Add(StreamRegistry streams)
        {
            var person = new Agent(Symbol.For(Id + ".person." + Born.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".b"),
                                   _people.Count, _rules.Needs);
            person.PlaceAt(HearthParcelX, HearthParcelZ);
            _people.Add(person);
            Born++;
            if (Tasks != null) Tasks.Sync(this, streams);
            return person;
        }

        /// <summary>Lets one go elsewhere, alive (S2Y): out of their family and off the roll, not a death.</summary>
        internal Agent Release(int index)
        {
            Agent a = _people[index];
            Settlements.Households.Leave(this, a);
            _people.RemoveAt(index);
            return a;
        }

        /// <summary>Takes in someone arriving from elsewhere (S2Y), with the life they had: their id, needs and temperament.</summary>
        internal void Welcome(Agent a)
        {
            a.LastWorkX = a.LastWorkZ = -1;
            a.FieldX = a.FieldZ = a.FieldFarm = -1;
            a.WorkingAt = -1;
            a.Path = null;
            _people.Add(a);
        }

        /// <summary>The borders this town and its neighbours build within (S2Y), or null before any are drawn.</summary>
        public Territory Borders { get; internal set; }

        /// <summary>Days in a row a substantial share of the town has had no roof (S2Y).</summary>
        public int HomelessDays { get; internal set; }

        // The tick a house could last not be sited anywhere, and the day people last left to found a town (S2Y).
        internal long LastNoSiteTick = -1;
        internal long LastEmigrationDay = -1;

        /// <summary>The tick a house last found nowhere to go, or -1 (S2Y).</summary>
        public long NoSiteTick { get { return LastNoSiteTick; } }

        /// <summary>Loses one. The board forgets them and keeps everyone else's thresholds.</summary>
        public void Remove(int index, StreamRegistry streams)
        {
            if (index < 0 || index >= _people.Count) return;
            Settlements.Households.Leave(this, _people[index]);
            _people.RemoveAt(index);
            Died++;
            if (Tasks != null) Tasks.Sync(this, streams);
        }

        // S2V. Where the water is, found once.
        internal List<Int3> WaterPointList;

        // Tonight's beds (S2V), allotted once a tick by Places.AllotBeds: who
        // sleeps in which, and which of those are guests in a spare one.
        internal readonly Dictionary<ulong, Build.Furnishing.Bed> BedByPerson = new Dictionary<ulong, Build.Furnishing.Bed>();
        internal readonly HashSet<ulong> BedGuests = new HashSet<ulong>();

        // S2N. Families, in the order they formed.
        internal readonly List<Household> HouseholdList = new List<Household>();
        internal int NextHousehold;

        /// <summary>The families (S2N). Empty in a settlement founded without household rules.</summary>
        public IReadOnlyList<Household> Households { get { return HouseholdList; } }

        /// <summary>How families grow and split (S2N), or null.</summary>
        public HouseholdRules HouseholdRules { get; internal set; }

        /// <summary>What the settlement holds to build with (S11). Null where nothing can be gathered.</summary>
        public MaterialStock Stock { get; set; }

        /// <summary>What the land within hauling range offers (S11). Surveyed at founding.</summary>
        public Catchment Catchment { get; set; }

        // Parcels this settlement has taken, and what took each of them (S1B).
        // Sorted, because everything that walks it has to walk it the same way
        // on every machine (L2).
        readonly SortedDictionary<int, int> _claims = new SortedDictionary<int, int>();

        /// <param name="owner">The record that took it: a structure's site, or the founding for the hearth.</param>
        public void ClaimParcel(int px, int pz, RecordId owner)
        {
            if (!World.ParcelGrid.InBounds(px, pz)) return;
            _claims[pz * World.ParcelGrid.Width + px] = owner.Index;
        }

        /// <summary>Gives back every parcel a record holds (S2T): a fallen building's ground.</summary>
        public void ReleaseClaims(RecordId owner)
        {
            var mine = new List<int>();
            foreach (KeyValuePair<int, int> c in _claims) if (c.Value == owner.Index) mine.Add(c.Key);
            foreach (int parcel in mine) _claims.Remove(parcel);
        }

        public bool IsClaimed(int px, int pz)
        {
            return World.ParcelGrid.InBounds(px, pz) && _claims.ContainsKey(pz * World.ParcelGrid.Width + px);
        }

        /// <summary>What holds this parcel, or RecordId.None where nothing does.</summary>
        public RecordId ClaimOn(int px, int pz)
        {
            int owner;
            return World.ParcelGrid.InBounds(px, pz) && _claims.TryGetValue(pz * World.ParcelGrid.Width + px, out owner)
                ? new RecordId(owner) : RecordId.None;
        }

        /// <summary>Claimed parcels, in grid order.</summary>
        public IEnumerable<int> Claims { get { return _claims.Keys; } }

        public int ClaimCount { get { return _claims.Count; } }

        /// <summary>The culture's dispositions (S17). What the grammar and the siting read.</summary>
        public Culture.Genome Genome { get; set; }

        // S2T. What fell, and the heaps it left.
        internal readonly List<Build.RubbleCell> RubbleList = new List<Build.RubbleCell>();
        internal readonly List<Build.Project> RuinList = new List<Build.Project>();

        /// <summary>Rubble lying in the settlement, waiting to be carried off or built over.</summary>
        public IReadOnlyList<Build.RubbleCell> Rubble { get { return RubbleList; } }

        /// <summary>Buildings that came down, in the order they fell.</summary>
        public IReadOnlyList<Build.Project> Ruins { get { return RuinList; } }

        /// <summary>Buildings commissioned and not yet standing (S15, S1A).</summary>
        public List<Build.Project> Projects { get; } = new List<Build.Project>();

        public static readonly Symbol TrafficField = Symbol.For("field.traffic");

        /// <summary>
        /// Footsteps per parcel, ever (S2G). Where a road will wear (S2K) and
        /// a bridge will be wanted (S2M). Null until anybody has walked.
        /// </summary>
        public InfluenceMap Traffic { get; internal set; }

        /// <summary>Who does what (S1C). Null in a settlement with nothing to do.</summary>
        public Collective.TaskBoard Tasks { get; set; }

        /// <summary>Agent-ticks spent on each activity, ever. Indexed like the ActivityTable.</summary>
        public long[] ActivityTicks { get; private set; }

        // The current run of nights in the open (see DriveSystem). One record
        // covers a spell with the same shape rather than one per night.
        internal RecordId SpellRecord = RecordId.None;
        internal long SpellSignature = -1;

        public RecordId CurrentExposure { get { return SpellRecord; } }

        /// <param name="name">
        /// Becomes the id "settlement.NAME" and the agents' ids under it. A
        /// string, not a Symbol, because an id derived from a Symbol's printed
        /// name would depend on whether anything had registered that name —
        /// which is execution order, which is L2.
        /// </param>
        public static Settlement Found(string name, Int3 hearth, Biome biome, int people, DriveRules rules,
                                       long tick, Annalist annals, RecordId cause)
        {
            string id = "settlement." + name;
            var s = new Settlement
            {
                Id = Symbol.For(id),
                Hearth = hearth,
                Biome = biome,
                ActivityTicks = new long[rules.Activities.Count],
                _rules = rules,
            };
            s.Founded = annals.Write(tick, FoundedKind, s.Id, hearth, cause, people);
            for (int i = 0; i < people; i++)
            {
                var person = new Agent(Symbol.For(id + ".person." + i.ToString(System.Globalization.CultureInfo.InvariantCulture)), i, rules.Needs);
                person.PlaceAt(s.HearthParcelX, s.HearthParcelZ);
                s._people.Add(person);
            }
            s.Pressure = new PressureTally(rules.Needs.Count);

            // The fire is the one piece of ground nobody builds on.
            s.ClaimParcel(s.HearthParcelX, s.HearthParcelZ, s.Founded);
            return s;
        }

        public ulong Digest()
        {
            var d = new Digest();
            d.Add(Id.Hash);
            d.Add(Hearth.X); d.Add(Hearth.Y); d.Add(Hearth.Z);
            d.Add(ShelterCapacity);
            d.Add(System.BitConverter.DoubleToInt64Bits(Food));
            d.Add(Fed ? 1 : 0); d.Add(HungryDays); d.Add(Born); d.Add(Died);
            d.Add(SpellRecord.Index);
            d.Add(SpellSignature);
            foreach (Agent a in _people) a.AddTo(ref d);
            Settlements.Households.AddTo(this, ref d);
            if (Stock != null) d.Add(Stock.Digest());
            if (Tasks != null) d.Add(Tasks.Digest());
            foreach (KeyValuePair<int, int> claim in _claims) { d.Add(claim.Key); d.Add(claim.Value); }
            if (Genome != null) d.Add(Genome.Digest());
            foreach (Build.Project p in Projects)
            {
                d.Add(p.Site.Record.Index);
                d.Add(p.Placed);
                d.Add(p.Built.Digest());
            }
            for (int i = 0; i < ActivityTicks.Length; i++) d.Add(ActivityTicks[i]);
            return d.Value;
        }
    }
}
