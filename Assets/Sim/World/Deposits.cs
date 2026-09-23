using System.Collections.Generic;
using Godless.Sim.Annals;
using Godless.Sim.Core;
using Godless.Sim.Deltas;
using Godless.Sim.Voxels;

namespace Godless.Sim.World
{
    /// <summary>
    /// Everything on the island that can be gathered, where it is, and how
    /// much of it is left. S2F.
    ///
    /// Until this, what a settlement could gather was a sum over the biomes
    /// around its fire, fixed at founding: a village could fell the same wood
    /// for a thousand years and never see it thin. Here the wood is trees, the
    /// trees are voxels, and felling one takes it out of the world with the
    /// reason on record. The nearest go first, so the clearing spreads out from
    /// the fire; the walk to the next one grows, so a tick of labour brings
    /// less; and a settlement that has cut everything in reach has to build in
    /// something else, which is the substitution Part 04 asks for ("timber
    /// exhausted within haul range") arriving by itself.
    ///
    /// The tell: a clearing grows around a village, pits open in the rubble
    /// field above it, and its later houses are made of what was still left.
    /// </summary>
    public sealed class DepositMap
    {
        public static readonly Symbol FelledKind = Symbol.For("deposit.worked");
        public static readonly Symbol RegrewKind = Symbol.For("deposit.regrew");

        readonly FeatureTable _kinds;

        // One entry per feature, in planting order (position, then kind).
        readonly List<int> _kind = new List<int>();
        readonly List<int> _x = new List<int>(), _y = new List<int>(), _z = new List<int>();
        readonly List<int> _size = new List<int>();
        readonly List<int> _voxels = new List<int>();     // yielding voxels when whole
        readonly List<int> _remaining = new List<int>();  // material units left
        readonly List<int> _dug = new List<int>();        // yielding voxels already taken out
        readonly List<bool> _standing = new List<bool>(); // trees and tufts: not yet cut down
        readonly List<long> _regrowAt = new List<long>(); // tick it grows back, or -1
        readonly List<int> _cause = new List<int>();      // record that cut it

        // Features by parcel: CSR over a stable order, built once when sealed.
        int[] _parcelStart;
        int[] _byParcel;

        // Pending regrowth, ordered by due tick then feature.
        readonly SortedSet<long> _due = new SortedSet<long>();

        // Ground dug away since the planning grid last looked (boulders, beds).
        bool _dirty;
        int _dx0, _dz0, _dx1, _dz1;

        internal DepositMap(FeatureTable kinds) { _kinds = kinds; }

        /// <summary>
        /// The columns whose ground a dig has changed since the last call, if
        /// any. The planning grid refreshes over them once a day rather than
        /// once a basket.
        /// </summary>
        public bool TakeDirty(out int x0, out int z0, out int x1, out int z1)
        {
            x0 = _dx0; z0 = _dz0; x1 = _dx1; z1 = _dz1;
            bool was = _dirty;
            _dirty = false;
            return was;
        }

        void MarkDirty(int x, int z, int reach)
        {
            if (!_dirty) { _dx0 = x - reach; _dz0 = z - reach; _dx1 = x + reach; _dz1 = z + reach; _dirty = true; return; }
            if (x - reach < _dx0) _dx0 = x - reach;
            if (z - reach < _dz0) _dz0 = z - reach;
            if (x + reach > _dx1) _dx1 = x + reach;
            if (z + reach > _dz1) _dz1 = z + reach;
        }

        public FeatureTable Kinds { get { return _kinds; } }
        public int Count { get { return _kind.Count; } }

        public FeatureKind KindOf(int feature) { return _kinds[_kind[feature]]; }
        public int X(int feature) { return _x[feature]; }
        public int Y(int feature) { return _y[feature]; }
        public int Z(int feature) { return _z[feature]; }
        public int Remaining(int feature) { return _remaining[feature]; }
        public bool Standing(int feature) { return _standing[feature]; }

        /// <summary>Whether there is anything left to take from it right now.</summary>
        public bool Available(int feature) { return _remaining[feature] > 0; }

        /// <summary>Units of a material left anywhere on the island. Tooling and invariants.</summary>
        public long Total(Symbol material)
        {
            long t = 0;
            for (int f = 0; f < _kind.Count; f++) if (_kinds[_kind[f]].Yields == material) t += _remaining[f];
            return t;
        }

        /// <summary>Material units each feature started with, for anything measuring depletion.</summary>
        public int Initial(int feature) { return _voxels[feature] * _kinds[_kind[feature]].PerVoxel; }

        internal void Add(int kind, int x, int ground, int z, int size, int yieldingVoxels)
        {
            _kind.Add(kind);
            _x.Add(x); _y.Add(ground); _z.Add(z);
            _size.Add(size);
            _voxels.Add(yieldingVoxels);
            _remaining.Add(yieldingVoxels * _kinds[kind].PerVoxel);
            _dug.Add(0);
            _standing.Add(true);
            _regrowAt.Add(-1);
            _cause.Add(RecordId.None.Index);
        }

        internal void Seal()
        {
            int parcels = ParcelGrid.Width * ParcelGrid.Depth;
            _parcelStart = new int[parcels + 1];
            var parcelOf = new int[_kind.Count];
            for (int f = 0; f < _kind.Count; f++)
            {
                parcelOf[f] = (_z[f] / ParcelGrid.Size) * ParcelGrid.Width + _x[f] / ParcelGrid.Size;
                _parcelStart[parcelOf[f] + 1]++;
            }
            for (int p = 0; p < parcels; p++) _parcelStart[p + 1] += _parcelStart[p];
            _byParcel = new int[_kind.Count];
            var fill = (int[])_parcelStart.Clone();
            for (int f = 0; f < _kind.Count; f++) _byParcel[fill[parcelOf[f]]++] = f;
        }

        /// <summary>Features standing on a parcel, in planting order.</summary>
        public void InParcel(int px, int pz, List<int> into)
        {
            if (!ParcelGrid.InBounds(px, pz)) return;
            int p = pz * ParcelGrid.Width + px;
            for (int i = _parcelStart[p]; i < _parcelStart[p + 1]; i++) into.Add(_byParcel[i]);
        }

        /// <summary>
        /// Every feature within a radius of a point, nearest first (ties by
        /// feature), as (distance², feature). What a catchment is built from.
        /// </summary>
        public List<int> Within(int cx, int cz, int radius)
        {
            var found = new List<long>();
            long r2 = (long)radius * radius;
            int p0x = (cx - radius) / ParcelGrid.Size - 1, p1x = (cx + radius) / ParcelGrid.Size + 1;
            int p0z = (cz - radius) / ParcelGrid.Size - 1, p1z = (cz + radius) / ParcelGrid.Size + 1;
            var here = new List<int>();
            for (int pz = p0z; pz <= p1z; pz++)
                for (int px = p0x; px <= p1x; px++)
                {
                    here.Clear();
                    InParcel(px, pz, here);
                    foreach (int f in here)
                    {
                        long dx = _x[f] - cx, dz = _z[f] - cz;
                        long d2 = dx * dx + dz * dz;
                        if (d2 > r2) continue;
                        found.Add((d2 << 24) | (long)f);
                    }
                }
            found.Sort();
            var result = new List<int>(found.Count);
            foreach (long v in found) result.Add((int)(v & 0xFFFFFF));
            return result;
        }

        /// <summary>
        /// Takes up to <paramref name="units"/> of material from one feature,
        /// taking its voxels out of the world as it goes. A tree or a tuft
        /// comes down whole at the first cut; rock and earth come out a voxel
        /// at a time, from the top. Returns the units actually taken.
        /// </summary>
        public int Take(int feature, int units, VoxelWorld world, long tick, RecordId cause, int ticksPerDay)
        {
            if (units <= 0 || _remaining[feature] <= 0) return 0;
            FeatureKind kind = _kinds[_kind[feature]];

            if (kind.Shape == FeatureShape.Tree || kind.Shape == FeatureShape.Tuft)
            {
                if (_standing[feature]) CutDown(feature, kind, world, tick, cause);
            }
            else
            {
                // The voxels whose worth has now been carried off come out.
                int takenAfter = Initial(feature) - _remaining[feature] + System.Math.Min(units, _remaining[feature]);
                int voxelsGone = (takenAfter + kind.PerVoxel - 1) / kind.PerVoxel;
                if (voxelsGone > _dug[feature]) Dig(feature, kind, voxelsGone, world, tick, cause);
            }

            int got = System.Math.Min(units, _remaining[feature]);
            _remaining[feature] -= got;
            _cause[feature] = cause.Index;

            if (_remaining[feature] == 0 && kind.Renews && _regrowAt[feature] < 0)
            {
                long due = tick + (long)kind.RegrowDays * ticksPerDay;
                _regrowAt[feature] = due;
                _due.Add(DueKey(due, feature));
            }
            return got;
        }

        void CutDown(int feature, FeatureKind kind, VoxelWorld world, long tick, RecordId cause)
        {
            _standing[feature] = false;
            var cells = new List<Int3>();
            Cells(kind, _x[feature], _y[feature], _z[feature], _size[feature], cells, yielding: false);
            foreach (Int3 at in cells)
            {
                if (!ChunkStore.InBounds(at.X, at.Y, at.Z)) continue;
                ushort here = world.Get(at);
                if (here == kind.Voxel || (kind.Crown != VoxelTypes.AirId && here == kind.Crown))
                    world.Set(at, VoxelTypes.AirId, tick, cause);
            }
        }

        void Dig(int feature, FeatureKind kind, int voxelsGone, VoxelWorld world, long tick, RecordId cause)
        {
            var cells = new List<Int3>();
            Cells(kind, _x[feature], _y[feature], _z[feature], _size[feature], cells, yielding: true);

            // The ones already taken are air now, so the next still standing
            // in dig order are the next to go.
            int toTake = voxelsGone - _dug[feature];
            foreach (Int3 at in cells)
            {
                if (toTake <= 0) break;
                if (!ChunkStore.InBounds(at.X, at.Y, at.Z) || world.Get(at) != kind.Voxel) continue;
                world.Set(at, VoxelTypes.AirId, tick, cause);
                toTake--;
            }
            _dug[feature] = voxelsGone;
            MarkDirty(_x[feature], _z[feature], _size[feature]);
        }

        /// <summary>
        /// Clears every feature whose footprint falls in a rectangle of columns
        /// — the ground a house is about to stand on — and returns what came
        /// off it, per kind. A wood built over is timber in the yard, not a
        /// crown sticking out of a roof.
        /// </summary>
        public int[] Clear(int x0, int z0, int x1, int z1, VoxelWorld world, long tick, RecordId cause, int ticksPerDay)
        {
            var got = new int[_kinds.Count];
            var here = new List<int>();
            for (int pz = z0 / ParcelGrid.Size - 1; pz <= z1 / ParcelGrid.Size + 1; pz++)
                for (int px = x0 / ParcelGrid.Size - 1; px <= x1 / ParcelGrid.Size + 1; px++)
                {
                    here.Clear();
                    InParcel(px, pz, here);
                    foreach (int f in here)
                    {
                        if (_x[f] < x0 || _x[f] > x1 || _z[f] < z0 || _z[f] > z1 || _remaining[f] <= 0) continue;
                        FeatureKind kind = _kinds[_kind[f]];
                        got[_kind[f]] += Take(f, _remaining[f], world, tick, cause, ticksPerDay);

                        // Built over, it does not come back.
                        if (_regrowAt[f] >= 0) { _due.Remove(DueKey(_regrowAt[f], f)); _regrowAt[f] = -1; }
                    }
                }
            return got;
        }

        /// <summary>
        /// Reads back what is left of every feature near a rectangle of
        /// columns after something other than gathering changed the ground
        /// there: the god's brush burying a wood under a hill or planing a
        /// boulder off (S07). A feature's voxel that is no longer its material
        /// is gone from it — a tree whose trunk is buried is gone whole, and
        /// rock or earth loses what was taken off it as if it had been dug.
        /// Nothing is scheduled to grow back where it was buried; Regrow would
        /// refuse the ground anyway. Returns how many features lost anything.
        /// </summary>
        public int Recount(ChunkStore store, int x0, int z0, int x1, int z1)
        {
            int lost = 0;
            var here = new List<int>();
            var cells = new List<Int3>();
            const int widest = 16;  // no feature reaches further than this from where it stands (a tree's size is its height)
            for (int pz = (z0 - widest) / ParcelGrid.Size - 1; pz <= (z1 + widest) / ParcelGrid.Size + 1; pz++)
                for (int px = (x0 - widest) / ParcelGrid.Size - 1; px <= (x1 + widest) / ParcelGrid.Size + 1; px++)
                {
                    here.Clear();
                    InParcel(px, pz, here);
                    foreach (int f in here)
                    {
                        if (_remaining[f] <= 0) continue;
                        int reach = _size[f];
                        if (_x[f] + reach < x0 || _x[f] - reach > x1 || _z[f] + reach < z0 || _z[f] - reach > z1) continue;
                        FeatureKind kind = _kinds[_kind[f]];
                        bool plant = kind.Shape == FeatureShape.Tree || kind.Shape == FeatureShape.Tuft;
                        if (plant && !_standing[f]) continue;   // already felled: what is left lies in the yard's reckoning

                        cells.Clear();
                        Cells(kind, _x[f], _y[f], _z[f], _size[f], cells, yielding: true);
                        int present = 0;
                        foreach (Int3 at in cells)
                            if (ChunkStore.InBounds(at.X, at.Y, at.Z) && store.Get(at) == kind.Voxel) present++;

                        int left = present * kind.PerVoxel;
                        if (left >= _remaining[f]) continue;
                        _remaining[f] = left;
                        if (plant && present == 0) _standing[f] = false;
                        if (!plant && _voxels[f] - present > _dug[f]) _dug[f] = _voxels[f] - present;
                        lost++;
                    }
                }
            return lost;
        }

        /// <summary>
        /// Grows back whatever is due by this tick, into air only — a house
        /// built over a felled wood stays a house. Each regrowth names the cut
        /// that made room for it. Returns how many came back.
        /// </summary>
        public int Regrow(VoxelWorld world, long tick)
        {
            int grew = 0;
            while (_due.Count > 0)
            {
                long first = _due.Min;
                long due = first >> 24;
                if (due > tick) break;
                _due.Remove(first);
                int f = (int)(first & 0xFFFFFF);
                if (_regrowAt[f] != due) continue;

                FeatureKind kind = _kinds[_kind[f]];
                var cells = new List<Int3>();
                Cells(kind, _x[f], _y[f], _z[f], _size[f], cells, yielding: false);

                // Only where the ground it grew from is still there and open.
                ushort under = world.Get(_x[f], _y[f] - 1, _z[f]);
                if (under == VoxelTypes.AirId || world.Get(_x[f], _y[f], _z[f]) != VoxelTypes.AirId)
                {
                    _regrowAt[f] = -1;   // built over or dug away: it does not come back
                    continue;
                }

                var cause = new RecordId(_cause[f]);
                int yielding = 0;
                foreach (Int3 at in cells)
                {
                    if (!ChunkStore.InBounds(at.X, at.Y, at.Z) || world.Get(at) != VoxelTypes.AirId) continue;
                    ushort v = VoxelAt(kind, _x[f], _y[f], _z[f], _size[f], at);
                    world.Set(at, v, tick, cause);
                    if (v == kind.Voxel) yielding++;
                }
                _voxels[f] = yielding;
                _remaining[f] = yielding * kind.PerVoxel;
                _standing[f] = true;
                _dug[f] = 0;
                _regrowAt[f] = -1;
                grew++;
            }
            return grew;
        }

        static long DueKey(long due, int feature) { return (due << 24) | (long)feature; }

        /// <summary>
        /// A feature's voxels. With <paramref name="yielding"/>, only the ones
        /// that are material, in the order they are dug (top down); otherwise
        /// every voxel it occupies.
        /// </summary>
        internal static void Cells(FeatureKind kind, int x, int ground, int z, int size, List<Int3> into, bool yielding)
        {
            switch (kind.Shape)
            {
                case FeatureShape.Tuft:
                    for (int y = size - 1; y >= 0; y--) into.Add(new Int3(x, ground + y, z));
                    break;

                case FeatureShape.Tree:
                    for (int y = size - 1; y >= 0; y--) into.Add(new Int3(x, ground + y, z));
                    if (yielding) break;
                    Crown(kind, x, ground, z, size, into);
                    break;

                case FeatureShape.Boulder:
                    for (int y = size; y >= 0; y--)
                        for (int dz = -size; dz <= size; dz++)
                            for (int dx = -size; dx <= size; dx++)
                            {
                                // A low dome: a radius-high hump that thins to its rim.
                                int d2 = dx * dx + dz * dz;
                                if (d2 > size * size) continue;
                                int h = size - (d2 * size) / (size * size + 1);
                                if (y >= h) continue;
                                into.Add(new Int3(x + dx, ground + y, z + dz));
                            }
                    break;

                case FeatureShape.Bed:
                    for (int depth = 1; depth <= 2; depth++)
                        for (int dz = -size; dz <= size; dz++)
                            for (int dx = -size; dx <= size; dx++)
                            {
                                if (dx * dx + dz * dz > size * size) continue;
                                if (depth == 2 && dx * dx + dz * dz > (size - 1) * (size - 1)) continue;
                                into.Add(new Int3(x + dx, ground - depth, z + dz));
                            }
                    break;
            }
        }

        /// <summary>
        /// A crown, top down. Round crowns are a ball about the top of the
        /// trunk; conical ones widen from a point above the trunk to the full
        /// radius a third of the way up it.
        /// </summary>
        static void Crown(FeatureKind kind, int x, int ground, int z, int size, List<Int3> into)
        {
            int r = kind.CrownRadius;
            int top = ground + size - 1;   // highest trunk voxel
            if (kind.ConeCrown)
            {
                int tip = top + 1, baseY = ground + size / 3;
                int span = tip - baseY;
                for (int y = tip; y >= baseY; y--)
                {
                    int rr = span <= 0 ? r : (r * (tip - y) + span / 2) / span;
                    for (int dz = -rr; dz <= rr; dz++)
                        for (int dx = -rr; dx <= rr; dx++)
                        {
                            if (dx == 0 && dz == 0 && y <= top) continue;
                            if (dx * dx + dz * dz > rr * rr + (rr > 0 ? 1 : 0)) continue;
                            into.Add(new Int3(x + dx, y, z + dz));
                        }
                }
                return;
            }
            for (int dy = r; dy >= -r; dy--)
                for (int dz = -r; dz <= r; dz++)
                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (dx == 0 && dz == 0 && dy <= 0) continue;
                        if (dx * dx + dy * dy + dz * dz > r * r + 1) continue;
                        into.Add(new Int3(x + dx, top + dy, z + dz));
                    }
        }

        /// <summary>What a feature is made of at one of its cells.</summary>
        internal static ushort VoxelAt(FeatureKind kind, int x, int ground, int z, int size, Int3 at)
        {
            if (kind.Shape != FeatureShape.Tree) return kind.Voxel;
            return at.X == x && at.Z == z && at.Y < ground + size ? kind.Voxel : kind.Crown;
        }

        /// <summary>A deep copy, including what has been cut and what is due back.</summary>
        public DepositMap Clone()
        {
            var c = new DepositMap(_kinds);
            c._kind.AddRange(_kind); c._x.AddRange(_x); c._y.AddRange(_y); c._z.AddRange(_z);
            c._size.AddRange(_size); c._voxels.AddRange(_voxels); c._remaining.AddRange(_remaining);
            c._dug.AddRange(_dug); c._standing.AddRange(_standing); c._regrowAt.AddRange(_regrowAt);
            c._cause.AddRange(_cause);
            foreach (long due in _due) c._due.Add(due);
            c._parcelStart = _parcelStart;   // never changes after sealing
            c._byParcel = _byParcel;
            c._dirty = _dirty; c._dx0 = _dx0; c._dz0 = _dz0; c._dx1 = _dx1; c._dz1 = _dz1;
            return c;
        }

        public ulong Digest()
        {
            var d = new Digest();
            for (int f = 0; f < _kind.Count; f++)
            {
                d.Add(_kind[f]); d.Add(_x[f]); d.Add(_y[f]); d.Add(_z[f]); d.Add(_size[f]);
                d.Add(_remaining[f]); d.Add(_dug[f]); d.Add(_standing[f] ? 1 : 0); d.Add(_regrowAt[f]);
            }
            return d.Value;
        }
    }
}
