using Godless.Sim.Core;

namespace Godless.Sim.World
{
    /// <summary>
    /// One scalar per parcel: the unit of everything settlements "know" about
    /// the land. S10.
    ///
    /// Part 05's site scoring is influence maps over the coarse grid — slope,
    /// drainage and flood history, sun, wind, defensibility, distance to water
    /// and to what a building serves, proximity to sacred ground — weighted by
    /// the genome. Part 08 adds the one the whole memory system writes: the
    /// negative field a scar emits, so a settlement grows away from where the
    /// flood took people. Each is one of these, named by a Symbol so content
    /// and later systems can add their own without a registry of indices.
    /// </summary>
    public sealed class InfluenceMap
    {
        public const int Width = ParcelGrid.Width;
        public const int Depth = ParcelGrid.Depth;

        readonly double[] _values = new double[Width * Depth];

        public InfluenceMap(Symbol id) { Id = id; }

        public Symbol Id { get; private set; }

        public static bool InBounds(int px, int pz) { return px >= 0 && px < Width && pz >= 0 && pz < Depth; }

        public double this[int px, int pz]
        {
            get { return InBounds(px, pz) ? _values[pz * Width + px] : 0.0; }
            set { if (InBounds(px, pz)) _values[pz * Width + px] = value; }
        }

        public void Fill(double v) { for (int i = 0; i < _values.Length; i++) _values[i] = v; }

        /// <summary>Adds another map, scaled. How weighted fields combine into a score.</summary>
        public void Add(InfluenceMap other, double weight)
        {
            for (int i = 0; i < _values.Length; i++) _values[i] += other._values[i] * weight;
        }

        public double Max()
        {
            double m = double.NegativeInfinity;
            for (int i = 0; i < _values.Length; i++) if (_values[i] > m) m = _values[i];
            return m;
        }

        public double Min()
        {
            double m = double.PositiveInfinity;
            for (int i = 0; i < _values.Length; i++) if (_values[i] < m) m = _values[i];
            return m;
        }

        public void CopyFrom(InfluenceMap other) { System.Array.Copy(other._values, _values, _values.Length); }

        public ulong Digest()
        {
            var d = new Digest();
            d.Add(Id.Hash);
            for (int i = 0; i < _values.Length; i++) d.Add(System.BitConverter.DoubleToInt64Bits(_values[i]));
            return d.Value;
        }

        /// <summary>
        /// Distance from every parcel to the nearest source parcel, in parcels.
        ///
        /// A two-pass chamfer transform with weights 3 (straight) and 4
        /// (diagonal): integer arithmetic, a fixed scan order, and on open
        /// ground exactly the octile distance — so it is deterministic and
        /// cheap, 16k cells in two sweeps. Unreachable parcels (no source at
        /// all) read as <paramref name="none"/>.
        /// </summary>
        public static InfluenceMap DistanceTo(Symbol id, System.Func<int, int, bool> isSource, double none)
        {
            const int Far = int.MaxValue / 4;
            var d = new int[Width * Depth];
            bool any = false;
            for (int pz = 0; pz < Depth; pz++)
                for (int px = 0; px < Width; px++)
                {
                    bool src = isSource(px, pz);
                    d[pz * Width + px] = src ? 0 : Far;
                    any |= src;
                }

            // Forward pass: from the top-left neighbours.
            for (int pz = 0; pz < Depth; pz++)
                for (int px = 0; px < Width; px++)
                {
                    int i = pz * Width + px, v = d[i];
                    if (px > 0) v = Min(v, d[i - 1] + 3);
                    if (pz > 0)
                    {
                        v = Min(v, d[i - Width] + 3);
                        if (px > 0) v = Min(v, d[i - Width - 1] + 4);
                        if (px < Width - 1) v = Min(v, d[i - Width + 1] + 4);
                    }
                    d[i] = v;
                }

            // Backward pass: from the bottom-right neighbours.
            for (int pz = Depth - 1; pz >= 0; pz--)
                for (int px = Width - 1; px >= 0; px--)
                {
                    int i = pz * Width + px, v = d[i];
                    if (px < Width - 1) v = Min(v, d[i + 1] + 3);
                    if (pz < Depth - 1)
                    {
                        v = Min(v, d[i + Width] + 3);
                        if (px < Width - 1) v = Min(v, d[i + Width + 1] + 4);
                        if (px > 0) v = Min(v, d[i + Width - 1] + 4);
                    }
                    d[i] = v;
                }

            var map = new InfluenceMap(id);
            for (int i = 0; i < d.Length; i++) map._values[i] = (!any || d[i] >= Far) ? none : d[i] / 3.0;
            return map;
        }

        static int Min(int a, int b) { return a < b ? a : b; }
    }
}
