using System.Collections.Generic;
using Godless.Sim.Voxels;

namespace Godless.Sim.World
{
    /// <summary>
    /// Puts the water on the island: rivers down the valleys, lakes in the
    /// basins, and a depth to water for every column. S0B.
    ///
    /// Six systems read water state that nothing produced until this — floods,
    /// drainage in site scoring, parting a river, a harbour silting, and a
    /// wall undermined by a flooded river. Part 11's abandonment causes are
    /// all water. The doc says the simulation already models water; this is
    /// the part that makes that true.
    ///
    /// The tell: rivers run from the high ground to the sea, lakes lie in the
    /// basins, and the flat land beside them is the land that floods — which
    /// is where a settlement that remembers a flood will refuse to build.
    ///
    /// Runs once, at the end of worldgen, and writes through SetRaw like the
    /// rest of worldgen: there is no history yet for it to be part of.
    /// Recomputing from the live terrain after a god edits it is cheap (the
    /// drainage pass is a few milliseconds) and is S2J's to call.
    /// </summary>
    public static class Hydrology
    {
        /// <summary>
        /// Flow at which water cuts a channel, in rain units: a column of
        /// biome rainfall R contributes R / 100. So about three hundred
        /// temperate columns of catchment make a stream.
        /// </summary>
        public const long RiverFlow = 2400;

        /// <summary>Flow past which a river is wide enough to take its flat banks too.</summary>
        public const long BroadRiverFlow = RiverFlow * 6;

        /// <summary>
        /// Deepest a lake stands over its basin floor. A basin deeper than
        /// this holds a lake at its bottom, below its spill point, the way a
        /// closed basin does — rather than drowning a highland plateau.
        /// </summary>
        public const int MaxLakeDepth = 3;

        public const int RainDivisor = 100;

        static readonly int[] Sx = { 1, 0, -1, 0 };
        static readonly int[] Sz = { 0, 1, 0, -1 };

        /// <summary>Computes drainage from the generated heights, carves it into the store, and records it on the map.</summary>
        public static DrainageMap Apply(IslandMap map, ChunkStore store, BiomeTable biomes, ushort water,
                                        WorldPreset preset = null)
        {
            const int W = ChunkStore.SizeX, D = ChunkStore.SizeZ;
            int n = W * D;
            int sea = map.SeaLevel;
            long riverFlow = preset != null ? preset.RiverFlow : RiverFlow;
            int maxLake = preset != null ? preset.MaxLakeDepth : MaxLakeDepth;

            var ground = new int[n];
            var rain = new int[n];
            for (int z = 0; z < D; z++)
                for (int x = 0; x < W; x++)
                {
                    int i = z * W + x;
                    ground[i] = map.HeightAt(x, z);
                    int b = map.BiomeAt(x, z);
                    if (ground[i] > sea && b >= 0) rain[i] = biomes.At(b).RainfallMm / RainDivisor;
                }

            DrainageMap drainage = Drainage.Compute(W, D, ground, rain, sea);
            var level = new int[n];      // water level per column; 0 = dry

            // Lakes: each depression is the connected set of columns filled to
            // one spill level. Fill it only to its floor plus MaxLakeDepth.
            var visited = new bool[n];
            var basin = new List<int>();
            var frontier = new Queue<int>();
            for (int start = 0; start < n; start++)
            {
                if (visited[start] || drainage.Filled[start] <= ground[start] || ground[start] <= sea) continue;

                int spill = drainage.Filled[start];
                basin.Clear();
                visited[start] = true;
                frontier.Enqueue(start);
                int floor = ground[start];
                while (frontier.Count > 0)
                {
                    int c = frontier.Dequeue();
                    basin.Add(c);
                    if (ground[c] < floor) floor = ground[c];
                    int cx = c % W, cz = c / W;
                    for (int k = 0; k < 4; k++)
                    {
                        int nx = cx + Sx[k], nz = cz + Sz[k];
                        if (nx < 0 || nz < 0 || nx >= W || nz >= D) continue;
                        int ni = nz * W + nx;
                        if (visited[ni] || drainage.Filled[ni] != spill || drainage.Filled[ni] <= ground[ni]) continue;
                        visited[ni] = true;
                        frontier.Enqueue(ni);
                    }
                }

                int surface = spill < floor + maxLake ? spill : floor + maxLake;
                foreach (int c in basin) if (ground[c] < surface) level[c] = surface;
            }

            // Rivers: channel columns that run off rather than pool. A dry
            // basin floor under a capped lake is not a channel; the river ends
            // in the lake and a new one leaves from the rim.
            var carve = new bool[n];
            for (int i = 0; i < n; i++)
            {
                if (ground[i] <= sea || level[i] > 0 || drainage.Filled[i] > ground[i]) continue;
                if (drainage.Flow[i] < riverFlow) continue;
                carve[i] = true;

                if (drainage.Flow[i] < riverFlow * 6) continue;
                int x = i % W, z = i / W;
                for (int k = 0; k < 4; k++)
                {
                    int nx = x + Sx[k], nz = z + Sz[k];
                    if (nx < 0 || nz < 0 || nx >= W || nz >= D) continue;
                    int ni = nz * W + nx;
                    if (ground[ni] == ground[i] && level[ni] == 0 && drainage.Filled[ni] == ground[ni]) carve[ni] = true;
                }
            }

            // Write the water. A river takes the top voxel of its column and
            // runs at the height the ground was; a lake fills up from the
            // floor. Nothing is ever placed above air.
            for (int i = 0; i < n; i++)
            {
                int x = i % W, z = i / W;
                if (carve[i])
                {
                    store.SetRaw(x, ground[i] - 1, z, water);
                    level[i] = ground[i];
                    ground[i] -= 1;
                }
                else if (level[i] > 0)
                {
                    for (int y = ground[i]; y < level[i]; y++) store.SetRaw(x, y, z, water);
                }
                else if (ground[i] <= sea)
                {
                    level[i] = sea + 1;
                }
            }

            // Depth to water: how far each column stands above the water it
            // drains to. Downstream is always reached first, so one pass in
            // flood order sees every column's outlet before the column.
            var reference = new int[n];
            var above = new int[n];
            for (int k = 0; k < n; k++)
            {
                int c = drainage.Reached[k];
                int d = drainage.Downstream[c];
                if (level[c] > 0) reference[c] = level[c];
                else if (d < 0) reference[c] = ground[c];
                else reference[c] = reference[d];

                int h = ground[c] - reference[c];
                above[c] = level[c] > 0 || h < 0 ? 0 : h;
            }

            map.SetWater(ground, level, above, carve);
            return drainage;
        }
    }
}
