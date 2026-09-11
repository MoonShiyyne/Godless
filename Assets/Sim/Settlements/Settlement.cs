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

        /// <summary>What the settlement holds to build with (S11). Null where nothing can be gathered.</summary>
        public MaterialStock Stock { get; set; }

        /// <summary>What the land within hauling range offers (S11). Surveyed at founding.</summary>
        public Catchment Catchment { get; set; }

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
            };
            s.Founded = annals.Write(tick, FoundedKind, s.Id, hearth, cause, people);
            for (int i = 0; i < people; i++)
                s._people.Add(new Agent(Symbol.For(id + ".person." + i.ToString(System.Globalization.CultureInfo.InvariantCulture)), i, rules.Needs));
            s.Pressure = new PressureTally(rules.Needs.Count);
            return s;
        }

        public ulong Digest()
        {
            var d = new Digest();
            d.Add(Id.Hash);
            d.Add(Hearth.X); d.Add(Hearth.Y); d.Add(Hearth.Z);
            d.Add(ShelterCapacity);
            d.Add(SpellRecord.Index);
            d.Add(SpellSignature);
            foreach (Agent a in _people) a.AddTo(ref d);
            if (Stock != null) d.Add(Stock.Digest());
            if (Tasks != null) d.Add(Tasks.Digest());
            for (int i = 0; i < ActivityTicks.Length; i++) d.Add(ActivityTicks[i]);
            return d.Value;
        }
    }
}
