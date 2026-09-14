using Godless.Sim.Annals;
using Godless.Sim.Core;

namespace Godless.Sim.Drives
{
    /// <summary>
    /// One person: a level per need, and what raised each one. S12.
    ///
    /// The cause per need is what lets pressure carry provenance (L3). When
    /// a need rises under a condition that has a record behind it — a night
    /// in the open — the agent remembers which record, and every unit of
    /// pressure it emits for that need names it. Twelve bytes a need.
    /// </summary>
    public sealed class Agent
    {
        internal readonly double[] Levels;
        internal readonly RecordId[] Causes;

        public Agent(Symbol id, int index, NeedTable needs)
        {
            Id = id;
            Index = index;
            Levels = new double[needs.Count];
            Causes = new RecordId[needs.Count];
            for (int n = 0; n < needs.Count; n++) { Levels[n] = needs[n].Start; Causes[n] = RecordId.None; }
            Activity = -1;
        }

        public Symbol Id { get; private set; }

        /// <summary>Position in the settlement's roll, which is founding order.</summary>
        public int Index { get; private set; }

        public double Level(int need) { return Levels[need]; }

        /// <summary>The record behind the most recent rise in this need, or None.</summary>
        public RecordId CauseOf(int need) { return Causes[need]; }

        /// <summary>What this agent did last tick, as an activity index, or -1.</summary>
        public int Activity { get; internal set; }

        public bool ShelteredLastNight { get; internal set; }

        /// <summary>The family this person belongs to (S2N), by number, or -1.</summary>
        public int Household { get; internal set; } = -1;

        /// <summary>Slept last night lodging, or in a family that did not fit its roof (S2N).</summary>
        public bool Crowded { get; internal set; }

        /// <summary>Days in a row this person has been starving (S2V). Forty and they are lost.</summary>
        public int HungryDays { get; internal set; }

        /// <summary>At the place their activity is done this tick, rather than still walking there (S2V).</summary>
        public bool Arrived { get; internal set; }

        /// <summary>How their body looks this tick — stand, walk, sit, kneel, lie, work. Presentation reads it.</summary>
        public string Pose { get; internal set; } = "stand";

        /// <summary>Share of this tick left for their main activity after errands (S2V). Work is done at this rate.</summary>
        public double LabourShare { get; internal set; } = 1.0;

        /// <summary>The last errand this tick that had somewhere to go, by activity index, or -1 (S2V).</summary>
        public int ErrandActivity { get; internal set; } = -1;

        /// <summary>Errands done on the side this tick, in words ("eating", "drinking"), for anyone reading them.</summary>
        public string Errands { get; internal set; } = "";

        /// <summary>The day their own foraging spot was picked for (S2V), so it moves on daily.</summary>
        internal long ForageDay;

        /// <summary>Arrived and talking with others this tick (S2V), so others can come and join them.</summary>
        public bool Talking { get; internal set; }

        /// <summary>
        /// Where this person stands, as a world column (S2G). Everyone starts
        /// at the hearth. The planning grid's parcel follows from it.
        /// </summary>
        public int X { get; internal set; }
        public int Z { get; internal set; }

        /// <summary>Where this person is, on the planning grid (S13).</summary>
        public int ParcelX
        {
            get { return X / World.ParcelGrid.Size; }
            internal set { X = value * World.ParcelGrid.Size + World.ParcelGrid.Size / 2; }
        }

        public int ParcelZ
        {
            get { return Z / World.ParcelGrid.Size; }
            internal set { Z = value * World.ParcelGrid.Size + World.ParcelGrid.Size / 2; }
        }

        public void PlaceAt(int px, int pz) { ParcelX = px; ParcelZ = pz; }

        /// <summary>Where this person is headed this tick, as a world column (S2G).</summary>
        public int GoalX { get; internal set; }
        public int GoalZ { get; internal set; }

        /// <summary>Why they are headed there, in a word a stranger can read: "felling oak", "at the fire".</summary>
        public string Doing { get; internal set; } = "";

        // The way to the current goal, as parcels, and how far along it. Derived
        // from the position and the goal, so a digest need not carry it.
        internal System.Collections.Generic.List<int> Path;
        internal int PathGoal = -1;
        internal int PathStep;

        /// <summary>The deposit feature this person last worked (S2F), or -1.</summary>
        public int WorkingAt { get; internal set; } = -1;

        // S2X: the last load carried — from where, to where, what, and the place's name.
        public int HaulFromX { get; internal set; }
        public int HaulFromZ { get; internal set; }
        public int HaulToX { get; internal set; }
        public int HaulToZ { get; internal set; }
        public string HaulWhat { get; internal set; } = "";
        public string HaulTo { get; internal set; } = "";

        /// <summary>Ticks spent on productive activity, ever.</summary>
        public long ProductiveTicks { get; internal set; }

        /// <summary>Test and tooling seam: set a need directly, cause and all.</summary>
        public void SetLevel(int need, double level, RecordId cause)
        {
            Levels[need] = SimMath.Clamp01(level);
            Causes[need] = cause;
        }

        public void AddTo(ref Digest d)
        {
            d.Add(Id.Hash);
            for (int n = 0; n < Levels.Length; n++)
            {
                d.Add(System.BitConverter.DoubleToInt64Bits(Levels[n]));
                d.Add(Causes[n].Index);
            }
            d.Add(Activity);
            d.Add(ShelteredLastNight ? 1 : 0);
            d.Add(Household); d.Add(Crowded ? 1 : 0); d.Add(HungryDays); d.Add(Arrived ? 1 : 0);
            d.Add(X); d.Add(Z);
            d.Add(ProductiveTicks);
        }
    }
}
