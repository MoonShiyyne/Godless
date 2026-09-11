using Godless.Sim.Annals;
using Godless.Sim.Core;

namespace Godless.Sim.Settlements
{
    /// <summary>
    /// What a settlement holds to build with, in whole voxels. S11.
    ///
    /// Part 05: realization is constrained "to materials the settlement
    /// physically holds in stock" — so the same grammar with a different stock
    /// is a different building. That makes this ledger the thing that turns
    /// geography into architecture, and it is deliberately dumb: gathered in,
    /// taken out, never negative, never taken in part.
    ///
    /// The one thing it records is running short: the first time something is
    /// wanted and not there, with whatever wanted it as the cause. That is the
    /// moment the tell turns on — "a settlement that runs out of timber
    /// visibly stops building with timber" — and the chronicle can name it.
    /// A run of shortages is one record; a successful take ends it.
    /// </summary>
    public sealed class MaterialStock
    {
        public static readonly Symbol ShortKind = Symbol.For("stock.short");

        readonly MaterialTable _materials;
        readonly long[] _voxels;
        readonly double[] _carry;
        readonly bool[] _short;

        public MaterialStock(MaterialTable materials)
        {
            _materials = materials;
            _voxels = new long[materials.Count];
            _carry = new double[materials.Count];
            _short = new bool[materials.Count];
        }

        public MaterialTable Materials { get { return _materials; } }

        public long Of(int material) { return _voxels[material]; }
        public bool Has(int material, long voxels = 1) { return _voxels[material] >= voxels; }

        /// <summary>Whether the last attempt to take this material found too little.</summary>
        public bool IsShort(int material) { return _short[material]; }

        /// <summary>
        /// Labour turned into material at this catchment's rate. Fractions
        /// carry over, so five ticks at 0.4 is two voxels, not zero. Returns
        /// the whole voxels added.
        /// </summary>
        public long Gather(int material, double labourTicks, Catchment catchment)
        {
            if (labourTicks < 0.0) throw new System.ArgumentOutOfRangeException(nameof(labourTicks));
            double got = _carry[material] + labourTicks * catchment.YieldPerLabourTick(material);
            long whole = (long)got;
            _carry[material] = got - whole;
            _voxels[material] += whole;
            return whole;
        }

        /// <summary>Material arriving whole — founding supplies, salvage, trade.</summary>
        public void Add(int material, long voxels)
        {
            if (voxels < 0) throw new System.ArgumentOutOfRangeException(nameof(voxels));
            _voxels[material] += voxels;
        }

        /// <summary>
        /// Takes all of it or none of it. A take that finds too little records
        /// the shortage, once per run of shortages, caused by whatever wanted
        /// the material — an intent, a structure under construction.
        /// </summary>
        public bool TryTake(int material, long voxels, long tick, Symbol settlement, Int3 place,
                            Annalist annals, RecordId cause)
        {
            if (voxels < 0) throw new System.ArgumentOutOfRangeException(nameof(voxels));
            if (_voxels[material] >= voxels)
            {
                _voxels[material] -= voxels;
                _short[material] = false;
                return true;
            }

            if (!_short[material])
            {
                _short[material] = true;
                annals.Write(tick, ShortKind, settlement, place, cause, voxels, _voxels[material],
                             new[] { _materials[material].Voxel });
            }
            return false;
        }

        public ulong Digest()
        {
            var d = new Digest();
            for (int m = 0; m < _voxels.Length; m++)
            {
                d.Add(_materials[m].Voxel.Hash);
                d.Add(_voxels[m]);
                d.Add(System.BitConverter.DoubleToInt64Bits(_carry[m]));
                d.Add(_short[m] ? 1 : 0);
            }
            return d.Value;
        }
    }
}
