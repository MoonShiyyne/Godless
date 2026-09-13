using System.Collections.Generic;
using Godless.Sim.Annals;
using Godless.Sim.Content;
using Godless.Sim.Core;
using Godless.Sim.Deltas;
using Godless.Sim.Voxels;

namespace Godless.Sim.World
{
    /// <summary>
    /// Raises and lowers the ground around a column. S07's terrain editing,
    /// and the shape Part 07's first god verb will take: "raise a ridge".
    ///
    /// Works on columns rather than voxels — find each column's highest solid
    /// voxel, then add or remove from the top — because that is what raising
    /// a hill means, and because it cannot leave a floating voxel or a hole
    /// in the middle of solid rock.
    ///
    /// Every change goes through VoxelWorld.Set with a cause, so the brush has
    /// no way to change the world without recording why (L3). Integer
    /// arithmetic throughout: the same stroke gives the same hill everywhere.
    /// </summary>
    public static class TerrainBrush
    {
        /// <summary>Which runtime voxel ids are solid, read from content.</summary>
        public static bool[] SolidTable(ContentDatabase content, VoxelTypes types)
        {
            var solid = new bool[types.Count];
            foreach (string name in content.Ids("voxel"))
            {
                ushort id;
                if (types.TryGetId(Symbol.For("voxel." + name), out id))
                    solid[id] = content.Get("voxel", name)["solid"].AsBool(true);
            }
            return solid;
        }

        /// <summary>Highest solid voxel in a column, or -1 if there is none.</summary>
        public static int TopSolid(ChunkStore store, bool[] solid, int x, int z)
        {
            return store.TopMatching(x, z, solid);
        }

        /// <summary>
        /// How much a column at squared distance d2 from the centre moves, for a
        /// brush of the given radius and peak: a dome, height * (1 - d^2/r^2),
        /// rounded. Integer, so it is identical on every machine.
        /// </summary>
        public static int Falloff(int d2, int radius, int peak)
        {
            int r2 = radius * radius;
            if (r2 <= 0 || d2 > r2) return 0;
            return (peak * (r2 - d2) + r2 / 2) / r2;
        }

        /// <param name="ground">
        /// Where the new ground's material comes from. Null keeps
        /// <paramref name="material"/> for every voxel, which is what a test
        /// wanting one known type asks for; a palette instead gives each
        /// column the surface and rock that belong at the height it reaches,
        /// so a raised hill wears the biome it grows into.
        /// </param>
        public static int Raise(VoxelWorld world, bool[] solid, int centreX, int centreZ,
                                int radius, int peak, ushort material, long tick, RecordId cause,
                                List<Int3> changed, GroundPalette ground = null)
        {
            int count = 0;
            for (int x = centreX - radius; x <= centreX + radius; x++)
                for (int z = centreZ - radius; z <= centreZ + radius; z++)
                {
                    if (!ChunkStore.InBounds(x, 0, z)) continue;
                    int dx = x - centreX, dz = z - centreZ;
                    int amount = Falloff(dx * dx + dz * dz, radius, peak);
                    if (amount <= 0) continue;

                    int top = TopSolid(world.Store, solid, x, z);
                    int limit = System.Math.Min(top + amount, ChunkStore.SizeY - 2);

                    // The column's new top is decided before anything is
                    // placed, so the whole stroke agrees with itself: the
                    // soil cap sits under the final surface, not under
                    // whatever height the loop happened to have reached.
                    for (int y = top + 1; y <= limit; y++)
                    {
                        var at = new Int3(x, y, z);
                        ushort type = ground != null ? ground.At(x, y, z, limit) : material;
                        if (world.Set(at, type, tick, cause)) { count++; if (changed != null) changed.Add(at); }
                    }
                }
            return count;
        }

        /// <summary>
        /// Removes ground from the top of each column. What was dug out below
        /// the waterline fills with water, so lowering a coast makes a bay
        /// rather than a dry pit beside the sea. The bottom layer is never
        /// removed: the island has a floor.
        /// </summary>
        public static int Lower(VoxelWorld world, bool[] solid, int centreX, int centreZ,
                                int radius, int depth, ushort water, int seaLevel, long tick, RecordId cause,
                                List<Int3> changed)
        {
            int count = 0;
            for (int x = centreX - radius; x <= centreX + radius; x++)
                for (int z = centreZ - radius; z <= centreZ + radius; z++)
                {
                    if (!ChunkStore.InBounds(x, 0, z)) continue;
                    int dx = x - centreX, dz = z - centreZ;
                    int amount = Falloff(dx * dx + dz * dz, radius, depth);
                    if (amount <= 0) continue;

                    int top = TopSolid(world.Store, solid, x, z);
                    int floor = System.Math.Max(top - amount + 1, 1);
                    for (int y = top; y >= floor; y--)
                    {
                        var at = new Int3(x, y, z);
                        ushort fill = y <= seaLevel ? water : VoxelTypes.AirId;
                        if (world.Set(at, fill, tick, cause)) { count++; if (changed != null) changed.Add(at); }
                    }
                }
            return count;
        }
    }
}
