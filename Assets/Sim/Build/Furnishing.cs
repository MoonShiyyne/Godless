using System.Collections.Generic;
using Godless.Sim.Annals;
using Godless.Sim.Core;
using Godless.Sim.Settlements;
using Godless.Sim.Voxels;

namespace Godless.Sim.Build
{
    /// <summary>
    /// A bed for everyone a building sleeps. S2S.
    ///
    /// Capacity was a number the grammar wrote down; now it is furniture. When
    /// a house, a wing or a storey is finished, its beds are laid on its floors
    /// — clear of the hearth and of the way in, long-ways where they fit and
    /// turned where that fits better — in the building's own floor timber. A
    /// room the program sized for six has six beds in it, and each person has
    /// one to sleep in. When the building comes down (S2T) they come down too.
    /// </summary>
    public static class Furnishing
    {
        public static readonly Symbol BedModel = Symbol.For("model.bed");
        static readonly Symbol Floor = Symbol.For("role.floor");
        static readonly Symbol Door = Symbol.For("role.door");

        /// <summary>A bed laid in a building: the detail instance, and the world voxel its middle stands over.</summary>
        public struct Bed
        {
            public int Instance;
            public Int3 Centre;
        }

        /// <summary>
        /// Lays as many beds as the building sleeps, on its floors, and records
        /// them on the project. Returns how many fitted; a building with fewer
        /// beds than sleeping places notes it.
        /// </summary>
        public static int Furnish(Project project, DetailLayer details, DetailModelTable models, VoxelTypes types,
                                  Settlements.MaterialTable materials, long tick, RecordId cause)
        {
            DetailModel bed = models != null ? models.Find(BedModel) : null;
            if (bed == null || details == null) return 0;
            int wanted = project.Plan.Capacity;
            if (wanted <= 0) return 0;

            // Frame in the floor's timber, or the walls' where the floor is not timber.
            ushort frame = VoxelTypes.AirId;
            int floorMaterial = project.Built.MaterialFor(Floor);
            if (floorMaterial < 0) floorMaterial = project.Built.MaterialFor(Symbol.For("role.wall"));
            if (floorMaterial >= 0) frame = types.IdOf(materials[floorMaterial].Voxel);

            Blueprint plan = project.Plan;
            int bw = bed.SizeX / DetailModelTable.CellsPerVoxel, bd = bed.SizeZ / DetailModelTable.CellsPerVoxel;
            var taken = new bool[plan.Width * plan.Height * plan.Depth];
            int laid = 0;

            // Floors from the bottom: every course that holds a floor slab with room above it.
            for (int y = 0; y < plan.Height - 1 && laid < wanted; y++)
            {
                for (int turn = 0; turn < 2 && laid < wanted; turn++)
                {
                    int sx = turn == 0 ? bw : bd, sz = turn == 0 ? bd : bw;
                    for (int z = 0; z + sz <= plan.Depth && laid < wanted; z++)
                        for (int x = 0; x + sx <= plan.Width && laid < wanted; x++)
                        {
                            if (!Fits(plan, taken, x, y, z, sx, sz)) continue;
                            for (int dz = 0; dz < sz; dz++)
                                for (int dx = 0; dx < sx; dx++)
                                {
                                    taken[Index(plan, x + dx, y + 1, z + dz)] = true;
                                    // An aisle beside each bed, so a room is not wall-to-wall mattress.
                                    if (x + dx + 1 < plan.Width) taken[Index(plan, x + dx + 1, y + 1, z + dz)] = true;
                                }

                            Int3 corner = Construction.World(project, x, y + 1, z);
                            int id = details.Place(bed, corner.X * DetailModelTable.CellsPerVoxel, corner.Y * DetailModelTable.CellsPerVoxel,
                                                   corner.Z * DetailModelTable.CellsPerVoxel, turn, new[] { frame }, tick, cause);
                            project.BedList.Add(new Bed { Instance = id, Centre = Construction.World(project, x + sx / 2, y + 1, z + sz / 2) });
                            laid++;
                        }
                }
            }

            if (laid < wanted)
                project.Built.Note("room for " + laid + " beds of the " + wanted + " it sleeps; the rest sleep on the floor");
            return laid;
        }

        static int Index(Blueprint plan, int x, int y, int z) { return (y * plan.Depth + z) * plan.Width + x; }

        /// <summary>A bed's footprint: floor under every cell, nothing built above, clear of any door and anything already laid.</summary>
        static bool Fits(Blueprint plan, bool[] taken, int x, int y, int z, int sx, int sz)
        {
            for (int dz = 0; dz < sz; dz++)
                for (int dx = 0; dx < sx; dx++)
                {
                    int cx = x + dx, cz = z + dz;
                    if (plan.At(cx, y, cz) != Floor) return false;
                    if (!plan.At(cx, y + 1, cz).IsNone) return false;
                    if (taken[Index(plan, cx, y + 1, cz)]) return false;
                    // Headroom: a bed is not laid under the roof's lowest course.
                    if (y + 2 < plan.Height && !plan.At(cx, y + 2, cz).IsNone) return false;
                }
            // Keep the way in clear: no bed within a voxel of a door.
            for (int dz = -1; dz <= sz; dz++)
                for (int dx = -1; dx <= sx; dx++)
                    for (int dy = 1; dy <= 2; dy++)
                        if (plan.At(x + dx, y + dy, z + dz) == Door) return false;
            return true;
        }
    }
}
