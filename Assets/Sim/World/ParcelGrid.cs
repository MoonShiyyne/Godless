using Godless.Sim.Core;
using Godless.Sim.Voxels;

namespace Godless.Sim.World
{
    /// <summary>
    /// The island at planning resolution: 128 x 128 parcels of 4 x 4 columns.
    /// S10.
    ///
    /// Part 23's two resolutions. The fine voxel grid is for terrain and
    /// construction; this coarse grid is for planning, influence maps and
    /// pathing. Agents plan coarse and build fine, which is what keeps a
    /// settlement's layout coherent at settlement scale and keeps the
    /// planning cost survivable — 16k cells instead of 262k columns.
    ///
    /// It reads the live world, so it follows the terrain as the god edits
    /// it: Refresh recomputes only the columns and parcels an edit touched,
    /// and the water-distance field, which is global but costs two sweeps.
    /// </summary>
    public sealed class ParcelGrid
    {
        public const int Size = 4;                            // columns per parcel edge
        public const int Width = ChunkStore.SizeX / Size;     // 128
        public const int Depth = ChunkStore.SizeZ / Size;     // 128

        public static readonly Symbol HeightField = Symbol.For("field.height");
        public static readonly Symbol SlopeField = Symbol.For("field.slope");
        public static readonly Symbol LandField = Symbol.For("field.land");
        public static readonly Symbol WaterDistanceField = Symbol.For("field.water-distance");

        readonly bool[] _solid;
        readonly bool[] _water;

        // Per column: the top solid voxel, and whether water stands on it.
        readonly short[] _ground = new short[ChunkStore.SizeX * ChunkStore.SizeZ];
        readonly bool[] _wet = new bool[ChunkStore.SizeX * ChunkStore.SizeZ];

        // Per parcel.
        readonly short[] _min = new short[Width * Depth];
        readonly short[] _max = new short[Width * Depth];
        readonly byte[] _wetColumns = new byte[Width * Depth];

        public InfluenceMap Height { get; private set; }
        public InfluenceMap Slope { get; private set; }
        public InfluenceMap Land { get; private set; }
        public InfluenceMap WaterDistance { get; private set; }

        /// <summary>Increments on every refresh, so a consumer can tell its cache is stale.</summary>
        public int Version { get; private set; }

        // Paths walked on this grid, by their two ends (S2V), and the version
        // they were found at. Kept on the grid so each world has its own and
        // what one world walks never changes another's.
        internal readonly System.Collections.Generic.Dictionary<long, System.Collections.Generic.List<int>> Paths =
            new System.Collections.Generic.Dictionary<long, System.Collections.Generic.List<int>>();
        internal int PathsVersion;

        ParcelGrid(bool[] solid, bool[] water)
        {
            _solid = solid;
            _water = water;
            Height = new InfluenceMap(HeightField);
            Slope = new InfluenceMap(SlopeField);
            Land = new InfluenceMap(LandField);
        }

        /// <param name="solid">Which voxel ids are ground (TerrainBrush.SolidTable).</param>
        /// <param name="water">Which voxel ids are water standing on the ground.</param>
        public static ParcelGrid Build(ChunkStore store, bool[] solid, bool[] water)
        {
            var grid = new ParcelGrid(solid, water);
            grid.Refresh(store, 0, 0, ChunkStore.SizeX - 1, ChunkStore.SizeZ - 1);
            return grid;
        }

        /// <summary>
        /// Recomputes every column and parcel in an inclusive rectangle of world
        /// columns, then the water-distance field. Call after an edit with the
        /// edit's footprint; the rectangle is widened to whole parcels.
        /// </summary>
        public void Refresh(ChunkStore store, int x0, int z0, int x1, int z1)
        {
            int p0x = Clamp(x0 / Size, Width), p1x = Clamp(x1 / Size, Width);
            int p0z = Clamp(z0 / Size, Depth), p1z = Clamp(z1 / Size, Depth);

            for (int pz = p0z; pz <= p1z; pz++)
                for (int px = p0x; px <= p1x; px++)
                {
                    short lo = short.MaxValue, hi = short.MinValue;
                    int wet = 0, sum = 0;

                    for (int dz = 0; dz < Size; dz++)
                        for (int dx = 0; dx < Size; dx++)
                        {
                            int x = px * Size + dx, z = pz * Size + dz;
                            int top = store.TopMatching(x, z, _solid);
                            bool isWet = top + 1 < ChunkStore.SizeY
                                         && Matches(_water, store.Get(x, top + 1, z));

                            int c = z * ChunkStore.SizeX + x;
                            _ground[c] = (short)top;
                            _wet[c] = isWet;

                            if (top < lo) lo = (short)top;
                            if (top > hi) hi = (short)top;
                            sum += top;
                            if (isWet) wet++;
                        }

                    int p = pz * Width + px;
                    _min[p] = lo;
                    _max[p] = hi;
                    _wetColumns[p] = (byte)wet;

                    Height[px, pz] = sum / (double)(Size * Size);
                    Slope[px, pz] = hi - lo;
                    Land[px, pz] = wet * 2 < Size * Size ? 1.0 : 0.0;
                }

            // Global, but two integer sweeps over 16k cells.
            WaterDistance = InfluenceMap.DistanceTo(WaterDistanceField,
                (px, pz) => _wetColumns[pz * Width + px] > 0, Width + Depth);
            Version++;
        }

        // Ground changed since the last refresh by something that does not
        // refresh the grid itself (the god's brush): columns, inclusive.
        bool _pending;
        int _px0, _pz0, _px1, _pz1;

        /// <summary>Whether ground has changed that the grid has not read yet.</summary>
        public bool HasPending { get { return _pending; } }

        /// <summary>
        /// Notes a rectangle of columns whose ground changed, to be read at the
        /// start of the next tick (<see cref="GroundSystem"/>) rather than once
        /// per stroke: a held brush lands a dozen strokes a second.
        /// </summary>
        public void MarkDirty(int x0, int z0, int x1, int z1)
        {
            if (!_pending) { _px0 = x0; _pz0 = z0; _px1 = x1; _pz1 = z1; _pending = true; return; }
            if (x0 < _px0) _px0 = x0;
            if (z0 < _pz0) _pz0 = z0;
            if (x1 > _px1) _px1 = x1;
            if (z1 > _pz1) _pz1 = z1;
        }

        /// <summary>
        /// Refreshes whatever <see cref="MarkDirty"/> noted, and hands back the
        /// columns it covered (clamped to the world) so what is derived from
        /// the grid can follow. False when nothing was pending.
        /// </summary>
        public bool RefreshPending(ChunkStore store, out int x0, out int z0, out int x1, out int z1)
        {
            x0 = System.Math.Max(0, _px0); z0 = System.Math.Max(0, _pz0);
            x1 = System.Math.Min(ChunkStore.SizeX - 1, _px1); z1 = System.Math.Min(ChunkStore.SizeZ - 1, _pz1);
            if (!_pending) return false;
            _pending = false;
            if (x0 > x1 || z0 > z1) return false;
            Refresh(store, x0, z0, x1, z1);
            return true;
        }

        static bool Matches(bool[] table, ushort id) { return id < table.Length && table[id]; }
        static int Clamp(int v, int n) { return v < 0 ? 0 : (v >= n ? n - 1 : v); }

        public static bool InBounds(int px, int pz) { return InfluenceMap.InBounds(px, pz); }

        /// <summary>The parcel holding a world column.</summary>
        public static int ParcelOf(int worldColumn) { return worldColumn / Size; }

        public int GroundAt(int x, int z) { return _ground[z * ChunkStore.SizeX + x]; }
        public bool IsWetColumn(int x, int z) { return _wet[z * ChunkStore.SizeX + x]; }

        public int MinGround(int px, int pz) { return _min[pz * Width + px]; }
        public int MaxGround(int px, int pz) { return _max[pz * Width + px]; }
        public int WetColumns(int px, int pz) { return _wetColumns[pz * Width + px]; }
        public bool IsLand(int px, int pz) { return Land[px, pz] > 0.5; }

        public ulong Digest()
        {
            var d = new Digest();
            for (int i = 0; i < _min.Length; i++) { d.Add(_min[i]); d.Add(_max[i]); d.Add(_wetColumns[i]); }
            d.Add(Height.Digest()); d.Add(Slope.Digest()); d.Add(Land.Digest()); d.Add(WaterDistance.Digest());
            return d.Value;
        }
    }
}
