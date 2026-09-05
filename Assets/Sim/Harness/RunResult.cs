using System.Collections.Generic;
using Godless.Sim.Core;

namespace Godless.Sim.Harness
{
    /// <summary>
    /// What one seeded run produced. Digests answer "did these two runs do
    /// the same thing"; metrics answer "was what they did any good".
    /// </summary>
    public sealed class RunResult
    {
        readonly SortedDictionary<string, double> _metrics =
            new SortedDictionary<string, double>(System.StringComparer.Ordinal);

        public ulong Seed { get; internal set; }
        public int Years { get; internal set; }
        public long Ticks { get; internal set; }

        public ulong AnnalsDigest { get; internal set; }
        public ulong WorldDigest { get; internal set; }
        public ulong DeltaDigest { get; internal set; }

        public int AnnalCount { get; internal set; }
        public int DeltaCount { get; internal set; }

        /// <summary>
        /// Sorted, so iterating a report is L2-safe and two runs print their
        /// metrics in the same order.
        /// </summary>
        public IReadOnlyDictionary<string, double> Metrics { get { return _metrics; } }

        public void Record(string name, double value) { _metrics[name] = value; }

        public double Metric(string name, double fallback = 0.0)
        {
            double v;
            return _metrics.TryGetValue(name, out v) ? v : fallback;
        }

        /// <summary>One number standing for the whole run, for a fast comparison.</summary>
        public ulong Digest()
        {
            var d = new Digest();
            d.Add(AnnalsDigest);
            d.Add(WorldDigest);
            d.Add(DeltaDigest);
            return d.Value;
        }
    }
}
