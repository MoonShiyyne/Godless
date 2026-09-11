using System.Collections.Generic;
using Godless.Sim.Core;

namespace Godless.Sim.World
{
    /// <summary>
    /// Where water goes on the island: every column's way down to the sea,
    /// how much rain gathers there, where it pools, and how far above the
    /// nearest water each column stands. S0B.
    ///
    /// Computed by priority flood from the sea inward (Barnes, Lehman and
    /// Mulla 2014): the sea is filled first, then each column is reached from
    /// the lowest wet frontier around it and drains back the way it was
    /// reached. A pit is filled to its spill height on the way, which is what
    /// makes a lake. The frontier is a bucket queue — heights are small
    /// integers — with first-in first-out inside a bucket and a fixed
    /// neighbour order, so flats drain the same way on every machine and the
    /// whole pass is linear in the number of columns.
    ///
    /// Heights follow IslandMap: a column of height h is solid below h, and
    /// a water level L means water up to, not including, L. The sea's level
    /// is SeaLevel + 1.
    /// </summary>
    public sealed class DrainageMap
    {
        public readonly int Width, Depth;

        /// <summary>The column each column drains into, or -1 at the sea.</summary>
        internal readonly int[] Downstream;

        /// <summary>Rain gathered at each column from everything upstream, in flow units.</summary>
        internal readonly long[] Flow;

        /// <summary>Surface water level after pits are filled: at least the ground, higher in a lake.</summary>
        internal readonly int[] Filled;

        /// <summary>Order the flood reached each column. Downstream always comes first.</summary>
        internal readonly int[] Reached;

        internal readonly int[] Ground;

        internal DrainageMap(int width, int depth, int[] ground)
        {
            Width = width;
            Depth = depth;
            Ground = ground;
            int n = width * depth;
            Downstream = new int[n];
            Flow = new long[n];
            Filled = new int[n];
            Reached = new int[n];
        }

        int Index(int x, int z) { return z * Width + x; }

        public int DownstreamOf(int x, int z) { return Downstream[Index(x, z)]; }
        public long FlowAt(int x, int z) { return Flow[Index(x, z)]; }
        public int FilledLevelAt(int x, int z) { return Filled[Index(x, z)]; }

        /// <summary>How deep a filled pit stands over the ground here. Zero where water runs off.</summary>
        public int PondDepthAt(int x, int z) { int i = Index(x, z); return Filled[i] - Ground[i]; }

        public ulong Digest()
        {
            var d = new Digest();
            for (int i = 0; i < Downstream.Length; i++) { d.Add(Downstream[i]); d.Add(Flow[i]); d.Add(Filled[i]); }
            return d.Value;
        }
    }

    public static class Drainage
    {
        // Fixed order: the four sides, then the four corners. Sides first so
        // that on a flat, water takes the straight way before the diagonal.
        static readonly int[] Dx = { 1, 0, -1, 0, 1, -1, -1, 1 };
        static readonly int[] Dz = { 0, 1, 0, -1, 1, 1, -1, -1 };

        /// <param name="ground">Column heights, row-major (z * width + x).</param>
        /// <param name="rain">Rain each column contributes, in flow units. Zero at sea.</param>
        /// <param name="seaLevel">Columns at or below this are sea, and where everything ends.</param>
        public static DrainageMap Compute(int width, int depth, int[] ground, int[] rain, int seaLevel)
        {
            int n = width * depth;
            var map = new DrainageMap(width, depth, ground);
            int maxLevel = seaLevel + 1;
            for (int i = 0; i < n; i++) if (ground[i] > maxLevel) maxLevel = ground[i];

            // Bucket queue: one FIFO per level.
            var buckets = new Queue<int>[maxLevel + 1];
            var seen = new bool[n];
            int lowest = maxLevel + 1;

            // The sea, and the rim of the world, are where water ends.
            for (int z = 0; z < depth; z++)
                for (int x = 0; x < width; x++)
                {
                    int i = z * width + x;
                    bool sea = ground[i] <= seaLevel;
                    bool rim = x == 0 || z == 0 || x == width - 1 || z == depth - 1;
                    if (!sea && !rim) continue;
                    int level = sea ? seaLevel + 1 : ground[i];
                    seen[i] = true;
                    map.Downstream[i] = -1;
                    map.Filled[i] = level;
                    Push(buckets, level, i, ref lowest);
                }

            int order = 0;
            while (true)
            {
                while (lowest < buckets.Length && (buckets[lowest] == null || buckets[lowest].Count == 0)) lowest++;
                if (lowest >= buckets.Length) break;

                int c = buckets[lowest].Dequeue();
                map.Reached[order++] = c;
                int cx = c % width, cz = c / width;
                for (int k = 0; k < 8; k++)
                {
                    int nx = cx + Dx[k], nz = cz + Dz[k];
                    if (nx < 0 || nz < 0 || nx >= width || nz >= depth) continue;
                    int ni = nz * width + nx;
                    if (seen[ni]) continue;
                    seen[ni] = true;
                    map.Downstream[ni] = c;
                    int level = ground[ni] > map.Filled[c] ? ground[ni] : map.Filled[c];
                    map.Filled[ni] = level;
                    Push(buckets, level, ni, ref lowest);
                }
            }

            // Rain runs downhill: upstream columns, reached last, hand their
            // flow to the column they drain into.
            for (int i = 0; i < n; i++) map.Flow[i] = rain[i];
            for (int k = n - 1; k >= 0; k--)
            {
                int c = map.Reached[k];
                int d = map.Downstream[c];
                if (d >= 0) map.Flow[d] += map.Flow[c];
            }
            return map;
        }

        static void Push(Queue<int>[] buckets, int level, int index, ref int lowest)
        {
            if (buckets[level] == null) buckets[level] = new Queue<int>();
            buckets[level].Enqueue(index);
            if (level < lowest) lowest = level;
        }
    }
}
