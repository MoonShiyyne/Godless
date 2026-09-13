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

                // The map's own answer to "how much of this should be land",
                // carried alongside the measurement so the assertion can
                // compare the two without knowing which map this is.
                WorldPreset preset = map.Map ?? WorldPreset.Default();
                into.Record("island.land-least", preset.MinLand);
                into.Record("island.land-most", preset.MaxLand);

                into.Record("island.unclaimed-columns", unclaimed);
                into.Record("island.edge-land-columns", edgeLand);

                var present = new SortedDictionary<int, long>();
                long sampled = 0;
                for (int z = 0; z < ChunkStore.SizeZ; z += 4)
                    for (int x = 0; x < ChunkStore.SizeX; x += 4)
                    {
                        if (!map.IsLand(x, z)) continue;
                        int b = map.BiomeAt(x, z);
                        long had; present.TryGetValue(b, out had);
                        present[b] = had + 1;
                        sampled++;
                    }
                into.Record("island.biomes-present", present.Count);

                long largest = 0;
                foreach (var pair in present) if (pair.Value > largest) largest = pair.Value;
                into.Record("island.dominant-biome-fraction", sampled > 0 ? (double)largest / sampled : 1.0);

                CollectWater(world, map, into);
                CollectDeposits(world, map, into);
            };
        }

        /// <summary>
        /// S2F's claims about a planted island: every deposit stands on ground
        /// and out of the water, and nothing a biome promises is missing from
        /// it. A bare island records nothing and passes trivially.
        /// </summary>
        static void CollectDeposits(SimWorld world, IslandMap map, RunResult into)
        {
            DepositMap deposits = map.Deposits;
            if (deposits == null) return;

            bool[] ground = TerrainBrush.SolidTable(world.Content, world.VoxelTypes);
            long misplaced = 0;
            for (int f = 0; f < deposits.Count; f++)
            {
                int x = deposits.X(f), y = deposits.Y(f), z = deposits.Z(f);
                if (map.WaterLevelAt(x, z) > 0) { misplaced++; continue; }
                if (deposits.KindOf(f).Shape == FeatureShape.Bed) continue;   // a bed is the ground
                if (!IsSolid(ground, world.Voxels.Store.Get(x, y - 1, z))) misplaced++;
            }
            into.Record("deposits.count", deposits.Count);
            into.Record("deposits.misplaced", misplaced);
            into.Record("deposits.unpromised", deposits.Kinds.Problems.Count);
        }

        /// <summary>
        /// S0B's claims, checked against the voxels rather than the map, so a
        /// carving bug shows up as the thing a player would see: a river that
        /// never reaches the sea, or water hanging over air.
        /// </summary>
        static void CollectWater(SimWorld world, IslandMap map, RunResult into)
        {
            ChunkStore store = world.Voxels.Store;
            bool[] solid = TerrainBrush.SolidTable(world.Content, world.VoxelTypes);
            ushort water;
            bool hasWater = world.VoxelTypes.TryGetId(Core.Symbol.For("voxel.water"), out water);

            long rivers = 0, lakes = 0, mouths = 0, misplaced = 0, overdeep = 0;
            for (int z = 0; z < ChunkStore.SizeZ; z++)
                for (int x = 0; x < ChunkStore.SizeX; x++)
                {
                    int level = map.WaterLevelAt(x, z);
                    if (!map.IsLand(x, z) || level == 0) continue;

                    int h = map.HeightAt(x, z);
                    if (map.IsRiver(x, z))
                    {
                        rivers++;
                        if (TouchesSea(map, x, z)) mouths++;
                    }
                    else
                    {
                        lakes++;
                        if (level - h > Hydrology.MaxLakeDepth) overdeep++;
                    }

                    bool ok = hasWater && h > 0 && IsSolid(solid, store.Get(x, h - 1, z));
                    for (int y = h; y < level && ok; y++) ok = store.Get(x, y, z) == water;
                    ushort above = store.Get(x, level, z);
                    if (above == water || IsSolid(solid, above)) ok = false;
                    if (!ok) misplaced++;
                }

            into.Record("water.river-columns", rivers);
            into.Record("water.lake-columns", lakes);
            into.Record("water.river-mouths", mouths);
            into.Record("water.misplaced-columns", misplaced);
            into.Record("water.overdeep-lake-columns", overdeep);
        }

        static bool IsSolid(bool[] table, ushort id) { return id < table.Length && table[id]; }

        static bool TouchesSea(IslandMap map, int x, int z)
        {
            for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx, nz = z + dz;
                    if (nx < 0 || nz < 0 || nx >= ChunkStore.SizeX || nz >= ChunkStore.SizeZ) continue;
                    if (!map.IsLand(nx, nz)) return true;
                }
            return false;
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

                // An island, not a continent and not a reef — where how much
                // of each is a property of the map, not of the generator. A
                // delta is mostly water on purpose and a massif is not, so the
                // bounds come from the world document (L5) and the default
                // island keeps the built-in ones.
                Invariant.PerRun("S09", "land covers as much of the map as the map asks for",
                    run => run.Metric("island.land-fraction") >= run.Metric("island.land-least")
                        && run.Metric("island.land-fraction") <= run.Metric("island.land-most")),

                // G1's first test is "change the biome and see whether the
                // architecture changes". One biome swallowing the island
                // leaves nothing to change.
                //
                // Counting biomes was the wrong way to say that once maps
                // became content: a map that deliberately admits three cannot
                // be held to showing three, and a map showing four of which
                // one covers everything passes while being exactly the
                // failure this is for. So: more than one, and none of them
                // the whole island.
                Invariant.PerRun("S09", "more than one biome appears on land",
                    run => run.Metric("island.biomes-present") >= 2.0),

                Invariant.PerRun("S09", "no single biome swallows the island",
                    run => run.Metric("island.dominant-biome-fraction") <= 0.9),

                // S2F. A tree in a river, or a boulder hanging over a hole, is a
                // deposit nobody can reach and a picture nobody believes.
                Invariant.PerRun("S2F", "every deposit stands on dry ground",
                    run => run.Metric("deposits.misplaced") == 0.0),

                // And a biome that promises a material has it lying there, or
                // its settlements plan houses in something that does not exist.
                Invariant.PerRun("S2F", "every material a biome promises is grown or laid in it",
                    run => run.Metric("deposits.unpromised") == 0.0),

                // S0B. Floods, drainage in site scoring and every water verb
                // read this. An island with no river reaching the sea has no
                // crossing to settle at and nothing to flood.
                Invariant.PerRun("S0B", "at least one river reaches the sea",
                    run => run.Metric("water.river-mouths") >= 1.0),

                // Water sits on ground and nothing sits on water. Checked
                // against the voxels, so a carving bug fails here instead of
                // hanging a sheet of water in the air for a player to find.
                Invariant.PerRun("S0B", "every column of water rests on ground, under open air",
                    run => run.Metric("water.misplaced-columns") == 0.0),

                // Lakes stay in their basins: a closed basin holds a lake at
                // its bottom rather than drowning a highland plateau.
                Invariant.PerRun("S0B", "no lake stands deeper than its cap",
                    run => run.Metric("water.overdeep-lake-columns") == 0.0),
            };
        }
    }
}
