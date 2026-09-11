using System.Collections.Generic;
using Godless.Sim.Core;

namespace Godless.Sim.World
{
    /// <summary>The grid as a walker reads it: how high each parcel stands, how broken it is, and whether it is land.</summary>
    public readonly struct ParcelPathGrid
    {
        readonly ParcelGrid _grid;
        public ParcelPathGrid(ParcelGrid grid) { _grid = grid; }
        public double Height(int px, int pz) { return _grid.Height[px, pz]; }
        public double Slope(int px, int pz) { return _grid.Slope[px, pz]; }
        public bool IsLand(int px, int pz) { return _grid.IsLand(px, pz); }
    }

    /// <summary>
    /// Walking the island on the planning grid. S13.
    ///
    /// Twenty people, so this is A* over parcels and nothing cleverer: the
    /// flow fields the registry describes are for the thousands of agents of
    /// stratum 3, and their tell — "thousands of agents move without
    /// individual A* per frame" — cannot be observed at this scale. What can
    /// be observed is this one: people go round the water and along the
    /// gentle ground rather than straight over the ridge, and the way they go
    /// is where a road will later wear (S2K).
    ///
    /// Steps cost their slope, so a climb is dear and a flat is cheap, and
    /// diagonals cost their true length. The frontier is ordered by cost then
    /// by parcel, so the same two points always give the same path.
    /// </summary>
    public static class ParcelPath
    {
        /// <summary>Cost of one straight step on the flat. Diagonals cost the same times root two.</summary>
        public const int StepCost = 100;

        /// <summary>Cost added per voxel of climb from one parcel to the next, and per voxel of broken ground within one.</summary>
        public const int SlopeCost = 45;

        /// <summary>A climb between neighbouring parcels that people will not make. A wall is a wall however flat its top.</summary>
        public const int Impassable = 12;

        static readonly int[] Dx = { 1, 0, -1, 0, 1, 1, -1, -1 };
        static readonly int[] Dz = { 0, 1, 0, -1, 1, -1, 1, -1 };

        /// <summary>
        /// Parcels from start to goal inclusive, or an empty list when there
        /// is no way. Water and cliffs are refused; the goal itself is
        /// allowed to be either, so a path can end at the water's edge.
        /// </summary>
        public static List<int> Find(ParcelGrid grid, int fromX, int fromZ, int toX, int toZ)
        {
            var path = new List<int>();
            if (!ParcelGrid.InBounds(fromX, fromZ) || !ParcelGrid.InBounds(toX, toZ)) return path;

            var walkable = new ParcelPathGrid(grid);
            int start = fromZ * ParcelGrid.Width + fromX, goal = toZ * ParcelGrid.Width + toX;
            if (start == goal) { path.Add(start); return path; }

            int cells = ParcelGrid.Width * ParcelGrid.Depth;
            var cost = new int[cells];
            var from = new int[cells];
            var closed = new bool[cells];
            for (int i = 0; i < cells; i++) { cost[i] = int.MaxValue; from[i] = -1; }
            cost[start] = 0;

            // A binary heap keyed by (estimate, parcel): small, and ordered
            // the same way on every machine.
            var heap = new List<long>();
            Push(heap, Key(Estimate(start, goal), start));

            while (heap.Count > 0)
            {
                long top = Pop(heap);
                int at = (int)(top & 0xFFFFFFFFL);
                if (closed[at]) continue;
                closed[at] = true;
                if (at == goal) break;

                int ax = at % ParcelGrid.Width, az = at / ParcelGrid.Width;
                for (int k = 0; k < 8; k++)
                {
                    int nx = ax + Dx[k], nz = az + Dz[k];
                    if (!ParcelGrid.InBounds(nx, nz)) continue;
                    int next = nz * ParcelGrid.Width + nx;
                    if (closed[next]) continue;

                    int step = Step(walkable, ax, az, nx, nz, k >= 4, next == goal);
                    if (step < 0) continue;

                    int through = cost[at] + step;
                    if (through >= cost[next]) continue;
                    cost[next] = through;
                    from[next] = at;
                    Push(heap, Key(through + Estimate(next, goal), next));
                }
            }

            if (from[goal] < 0 && start != goal) return path;
            for (int at = goal; at >= 0; at = from[at]) path.Add(at);
            path.Reverse();
            return path;
        }

        /// <summary>
        /// What a step from one parcel to the next costs, or -1 where people
        /// will not go. The climb between the two is what stops a walker: a
        /// wall of rock is impassable however flat its top, and only the goal
        /// may be water, so a path can end at the water's edge.
        /// </summary>
        static int Step(ParcelPathGrid grid, int fromX, int fromZ, int px, int pz, bool diagonal, bool isGoal)
        {
            double climb = grid.Height(px, pz) - grid.Height(fromX, fromZ);
            if (climb < 0.0) climb = -climb;
            if (climb >= Impassable) return -1;
            if (!isGoal)
            {
                if (!grid.IsLand(px, pz)) return -1;
                if (grid.Slope(px, pz) >= Impassable) return -1;
            }
            int flat = diagonal ? 141 : StepCost;
            return flat + (int)(climb * SlopeCost) + (int)(grid.Slope(px, pz) * SlopeCost);
        }

        static int Estimate(int at, int goal)
        {
            int ax = at % ParcelGrid.Width, az = at / ParcelGrid.Width;
            int gx = goal % ParcelGrid.Width, gz = goal / ParcelGrid.Width;
            int dx = ax > gx ? ax - gx : gx - ax, dz = az > gz ? az - gz : gz - az;
            int straight = dx > dz ? dx - dz : dz - dx;
            int diagonal = dx < dz ? dx : dz;
            return straight * StepCost + diagonal * 141;
        }

        /// <summary>Cost in the high bits, parcel in the low: one long orders the frontier.</summary>
        static long Key(int estimate, int parcel) { return ((long)estimate << 32) | (uint)parcel; }

        static void Push(List<long> heap, long value)
        {
            heap.Add(value);
            int i = heap.Count - 1;
            while (i > 0)
            {
                int parent = (i - 1) / 2;
                if (heap[parent] <= heap[i]) break;
                long t = heap[parent]; heap[parent] = heap[i]; heap[i] = t;
                i = parent;
            }
        }

        static long Pop(List<long> heap)
        {
            long top = heap[0];
            heap[0] = heap[heap.Count - 1];
            heap.RemoveAt(heap.Count - 1);
            int i = 0;
            while (true)
            {
                int left = 2 * i + 1, right = left + 1, smallest = i;
                if (left < heap.Count && heap[left] < heap[smallest]) smallest = left;
                if (right < heap.Count && heap[right] < heap[smallest]) smallest = right;
                if (smallest == i) return top;
                long t = heap[smallest]; heap[smallest] = heap[i]; heap[i] = t;
                i = smallest;
            }
        }

        /// <summary>What a path costs to walk, for comparing two ways round.</summary>
        public static int CostOf(ParcelGrid grid, IReadOnlyList<int> path)
        {
            var g = new ParcelPathGrid(grid);
            int total = 0;
            for (int i = 1; i < path.Count; i++)
            {
                int px = path[i] % ParcelGrid.Width, pz = path[i] / ParcelGrid.Width;
                int qx = path[i - 1] % ParcelGrid.Width, qz = path[i - 1] / ParcelGrid.Width;
                bool diagonal = px != qx && pz != qz;
                double climb = g.Height(px, pz) - g.Height(qx, qz);
                if (climb < 0.0) climb = -climb;
                total += (diagonal ? 141 : StepCost) + (int)(climb * SlopeCost) + (int)(g.Slope(px, pz) * SlopeCost);
            }
            return total;
        }
    }
}
