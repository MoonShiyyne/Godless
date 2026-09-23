using Godless.Sim.Content;
using Godless.Sim.Core;
using Godless.Sim.Harness;
using Godless.Sim.Voxels;

namespace Godless.Sim.World
{
    /// <summary>
    /// The planning grid and the constraint fields an island needs before
    /// anything can plan on it: what is ground, what is water, how steep, how
    /// damp, how exposed. Once per world, then kept current by the
    /// <see cref="GroundSystem"/> as the ground changes.
    /// </summary>
    public static class Survey
    {
        public static ParcelGrid Of(SimWorld world, ContentDatabase content, BiomeTable biomes, out ConstraintFields fields)
        {
            VoxelTypes types = world.VoxelTypes;
            bool[] solid = TerrainBrush.SolidTable(content, types);
            var wet = new bool[types.Count];
            ushort water;
            if (types.TryGetId(Symbol.For("voxel.water"), out water)) wet[water] = true;

            ParcelGrid grid = ParcelGrid.Build(world.Voxels.Store, solid, wet);
            fields = ConstraintFields.Compute(world.Island, grid, biomes);
            return grid;
        }

        /// <summary>
        /// The flattest dry parcel within four of water, in the wanted biome
        /// if one is named and anywhere if not. False when the island has
        /// nowhere that qualifies. A stand-in for "where people would settle"
        /// in tools that need one place to measure.
        /// </summary>
        public static bool FlattestNearWater(ParcelGrid grid, IslandMap island, BiomeTable biomes, Symbol biome,
                                       out int parcelX, out int parcelZ)
        {
            parcelX = parcelZ = -1;
            double flattest = double.MaxValue;
            for (int pz = 0; pz < ParcelGrid.Depth; pz++)
                for (int px = 0; px < ParcelGrid.Width; px++)
                {
                    if (!grid.IsLand(px, pz) || grid.WetColumns(px, pz) > 0) continue;
                    double water = grid.WaterDistance[px, pz];
                    if (water < 1.0 || water > 4.0) continue;
                    if (!biome.IsNone)
                    {
                        int b = island.BiomeAt(px * ParcelGrid.Size + 2, pz * ParcelGrid.Size + 2);
                        if (b < 0 || biomes.At(b).Id != biome) continue;
                    }
                    if (grid.Slope[px, pz] >= flattest) continue;
                    flattest = grid.Slope[px, pz];
                    parcelX = px;
                    parcelZ = pz;
                }
            return parcelX >= 0;
        }
    }
}
