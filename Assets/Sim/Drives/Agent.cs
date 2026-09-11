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

        /// <summary>Where this person is, on the planning grid (S13). Everyone starts at the hearth.</summary>
        public int ParcelX { get; internal set; }
        public int ParcelZ { get; internal set; }

        public void PlaceAt(int px, int pz) { ParcelX = px; ParcelZ = pz; }

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
            d.Add(ParcelX); d.Add(ParcelZ);
            d.Add(ProductiveTicks);
        }
    }
}
