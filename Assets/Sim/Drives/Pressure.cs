using Godless.Sim.Annals;

namespace Godless.Sim.Drives
{
    /// <summary>
    /// Where agents' unmet needs go. S12 emits; S14's intent bus listens.
    ///
    /// Part 03: agents never decide to build, they generate pressure. This is
    /// the whole interface between the two layers — a need, a parcel, an
    /// amount and the record behind it — so the drive layer cannot reach
    /// past it and commission anything itself.
    /// </summary>
    public interface IPressureSink
    {
        /// <param name="need">Index into the NeedTable.</param>
        /// <param name="px">Parcel the agent is on.</param>
        /// <param name="amount">How far over threshold, for one tick.</param>
        /// <param name="cause">The record behind the need, or None.</param>
        void Add(int need, int px, int pz, double amount, RecordId cause);
    }

    /// <summary>Sums pressure per need. What a settlement listens with until S14 exists.</summary>
    public sealed class PressureTally : IPressureSink
    {
        readonly double[] _total;
        readonly double[] _caused;

        public PressureTally(int needs) { _total = new double[needs]; _caused = new double[needs]; }

        public void Add(int need, int px, int pz, double amount, RecordId cause)
        {
            _total[need] += amount;
            if (cause.Exists) _caused[need] += amount;
        }

        public double Total(int need) { return _total[need]; }

        /// <summary>The part of the total that names a record. L3 wants this to be all of it.</summary>
        public double Caused(int need) { return _caused[need]; }
    }
}
