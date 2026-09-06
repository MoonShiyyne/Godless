using System.Collections.Generic;
using Godless.Sim.Harness;
using Godless.Sim.Voxels;

namespace Godless.Sim.World
{
    /// <summary>
    /// What the island generator claims, phrased over a batch of seeds. S09.
    ///
    /// These are the first invariants in the project that could actually fail
    /// for an interesting reason: an island is a tuned artifact, and "the
    /// coastline drifted off the edge on one seed in two hundred" is exactly
    /// the class of bug a single play session never finds.
    /// </summary>
    public static class IslandInvariants
    {
        public static MetricCollector Collector(BiomeTable biomes)
        {
            return (world, into) =>
            {
                IslandMap map = world.Island;
                if (map == null) return;

                long land = 0, unclaimed = 0, edgeLand = 0;
                for (int z = 0; z < ChunkStore.SizeZ; z++)
                    for (int x = 0; x < ChunkStore.SizeX; x++)
                    {
                        if (!map.IsLand(x, z)) continue;
                        land++;
                        if (map.BiomeAt(x, z) < 0) unclaimed++;
                        if (x == 0 || z == 0 || x == ChunkStore.SizeX - 1 || z == ChunkStore.SizeZ - 1) edgeLand++;
                    }

                long total = (long)ChunkStore.SizeX * ChunkStore.SizeZ;
                into.Record("island.land-fraction", (double)land / total);
                into.Record("island.unclaimed-columns", unclaimed);
                into.Record("island.edge-land-columns", edgeLand);

                var present = new SortedSet<int>();
                for (int z = 0; z < ChunkStore.SizeZ; z += 4)
                    for (int x = 0; x < ChunkStore.SizeX; x += 4)
                        if (map.IsLand(x, z)) present.Add(map.BiomeAt(x, z));
                into.Record("island.biomes-present", present.Count);
            };
        }

        public static IReadOnlyList<Invariant> All()
        {
            return new List<Invariant>
            {
                // The edge of the world is sea, not a cliff where the array
                // ended. One seed in two hundred pushing land to the border
                // would be invisible in play and obvious here.
                Invariant.PerRun("S09", "the island never touches the edge of the world",
                    run => run.Metric("island.edge-land-columns") == 0.0),

                // Every land column belongs to a biome. A gap in the selection
                // windows silently falls back to bare stone, which reads as a
                // terrain bug rather than as the content bug it is.
                Invariant.PerRun("S09", "every land column is claimed by a biome",
                    run => run.Metric("island.unclaimed-columns") == 0.0),

                // An island, not a continent and not a reef.
                Invariant.PerRun("S09", "land covers between 8% and 75% of the map",
                    run => run.Metric("island.land-fraction") >= 0.08
                        && run.Metric("island.land-fraction") <= 0.75),

                // G1's first test is "change the biome and see whether the
                // architecture changes". One biome swallowing the island
                // leaves nothing to change.
                Invariant.PerRun("S09", "at least three biomes appear on land",
                    run => run.Metric("island.biomes-present") >= 3.0),
            };
        }
    }
}
