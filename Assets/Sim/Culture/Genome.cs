using Godless.Sim.Annals;
using Godless.Sim.Core;

namespace Godless.Sim.Culture
{
    /// <summary>
    /// A settlement's style dispositions: one value per gene. S17.
    ///
    /// These are instincts, not decisions (Part 04). Nothing here plans or
    /// chooses; the grammar (S18) reads the numbers and every building it
    /// produces leans the way they lean.
    ///
    /// Every change after founding goes through Mutate, which writes an annal
    /// record naming the gene, the old and new values, and the cause — so the
    /// chronicle can say "walls thickened following the Ash Raid" and the
    /// Silence can measure how far a culture drifted from what you made it
    /// (L3). There is no public setter to go around it.
    ///
    /// Events never set genes directly; that is Part 04's third rule, and it
    /// is S25's job to turn scar mass into mutation rates. This type only
    /// guarantees that whatever moves a gene says why.
    /// </summary>
    public sealed class Genome
    {
        public static readonly Symbol MutatedKind = Symbol.For("gene.mutated");

        /// <summary>Annal values are integers; genes are stored there in millionths.</summary>
        public const double RecordScale = 1000000.0;

        readonly GeneTable _table;
        readonly double[] _values;

        /// <summary>A founding genome: every gene at its declared default.</summary>
        public Genome(GeneTable table)
        {
            _table = table;
            _values = new double[table.Count];
            for (int i = 0; i < table.Count; i++) _values[i] = table[i].Default;
        }

        Genome(GeneTable table, double[] values)
        {
            _table = table;
            _values = values;
        }

        public GeneTable Table { get { return _table; } }

        /// <summary>The gene's value, or NaN if the loaded content does not declare it.</summary>
        public double this[Symbol gene]
        {
            get
            {
                int i = _table.IndexOf(gene);
                return i < 0 ? double.NaN : _values[i];
            }
        }

        public double Normalized(Symbol gene)
        {
            int i = _table.IndexOf(gene);
            return i < 0 ? double.NaN : _table[i].Normalize(_values[i]);
        }

        /// <summary>
        /// Moves one gene, clamped to its range, and records why. Returns the
        /// record, or RecordId.None if the gene is unknown or did not move.
        /// </summary>
        public RecordId Mutate(Symbol gene, double value, long tick, Symbol subject,
                               RecordId cause, Annalist annals)
        {
            int i = _table.IndexOf(gene);
            if (i < 0) return RecordId.None;

            double next = _table[i].Clamp(value);
            double previous = _values[i];
            if (next == previous) return RecordId.None;

            _values[i] = next;
            return annals.Write(tick, MutatedKind, subject, Int3.Nowhere, cause,
                                ToRecord(previous), ToRecord(next), new[] { gene });
        }

        static long ToRecord(double v) { return (long)SimMath.Round(v * RecordScale); }

        /// <summary>
        /// How far apart two genomes are: root-mean-square of the per-gene
        /// difference on the normalised 0..1 scale, so a gene with a wide range
        /// counts no more than one with a narrow one. 0 is identical, 1 is
        /// every gene at opposite ends. This is the "formal persistence"
        /// distance the Silence scores against (Part 07).
        /// </summary>
        public double DistanceTo(Genome other)
        {
            if (_table.Count == 0) return 0.0;
            double sum = 0.0;
            int n = 0;
            for (int i = 0; i < _table.Count; i++)
            {
                double theirs = other[_table[i].Id];
                if (double.IsNaN(theirs)) continue;
                double d = _table[i].Normalize(_values[i]) - _table[i].Normalize(theirs);
                sum += d * d;
                n++;
            }
            return n == 0 ? 0.0 : SimMath.Sqrt(sum / n);
        }

        public Genome Clone() { return new Genome(_table, (double[])_values.Clone()); }

        public ulong Digest()
        {
            var d = new Digest();
            for (int i = 0; i < _values.Length; i++)
            {
                d.Add(_table[i].Id.Hash);
                d.Add(System.BitConverter.DoubleToInt64Bits(_values[i]));
            }
            return d.Value;
        }
    }
}
