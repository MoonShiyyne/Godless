namespace Godless.Sim.Voxels
{
    /// <summary>
    /// Finds the first voxel a ray enters. Amanatides and Woo's grid
    /// traversal: step to whichever cell boundary the ray crosses next, so
    /// every cell on the ray is visited exactly once and none are skipped.
    ///
    /// A ray passing exactly along an edge touches every cell meeting there
    /// at one instant; which of them it reports is decided by rounding. They
    /// are all the same distance away, so for picking it makes no difference,
    /// but it is a touch rather than an entry and tests should treat it so.
    ///
    /// In the sim rather than in Unity because picking is a question about
    /// the voxel grid, not about colliders. It needs no physics scene, gives
    /// the same answer headless as in the Editor, and uses only the four
    /// basic operations and Floor.
    /// </summary>
    public static class VoxelRaycast
    {
        public struct Hit
        {
            /// <summary>The voxel the ray stopped in.</summary>
            public Core.Int3 Voxel;

            /// <summary>
            /// The face it entered through, as a unit step outward — so
            /// Voxel + Normal is the empty cell in front of that face. Zero if
            /// the ray started inside a hit voxel.
            /// </summary>
            public Core.Int3 Normal;

            public double Distance;
        }

        public static bool Cast(ChunkStore store, double ox, double oy, double oz,
                                double dx, double dy, double dz, double maxDistance,
                                System.Func<ushort, bool> stopsAt, out Hit hit)
        {
            hit = default(Hit);

            double length = System.Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (length <= 0.0) return false;
            dx /= length; dy /= length; dz /= length;

            // Clip to the island's box, so a camera far above it starts
            // marching where the world begins instead of through empty air.
            double tEnter = 0.0, tExit = maxDistance;
            int enterAxis = -1;
            if (!Slab(ox, dx, ChunkStore.SizeX, 0, ref tEnter, ref tExit, ref enterAxis)) return false;
            if (!Slab(oy, dy, ChunkStore.SizeY, 1, ref tEnter, ref tExit, ref enterAxis)) return false;
            if (!Slab(oz, dz, ChunkStore.SizeZ, 2, ref tEnter, ref tExit, ref enterAxis)) return false;

            // Nudge just inside so the first cell is the one actually entered.
            double t = tEnter + 1e-9;
            double px = ox + dx * t, py = oy + dy * t, pz = oz + dz * t;

            int x = Cell(px, ChunkStore.SizeX), y = Cell(py, ChunkStore.SizeY), z = Cell(pz, ChunkStore.SizeZ);
            int stepX = dx > 0 ? 1 : -1, stepY = dy > 0 ? 1 : -1, stepZ = dz > 0 ? 1 : -1;

            double tDeltaX = dx != 0 ? System.Math.Abs(1.0 / dx) : double.PositiveInfinity;
            double tDeltaY = dy != 0 ? System.Math.Abs(1.0 / dy) : double.PositiveInfinity;
            double tDeltaZ = dz != 0 ? System.Math.Abs(1.0 / dz) : double.PositiveInfinity;

            double tMaxX = dx != 0 ? t + ((stepX > 0 ? x + 1 - px : px - x) * tDeltaX) : double.PositiveInfinity;
            double tMaxY = dy != 0 ? t + ((stepY > 0 ? y + 1 - py : py - y) * tDeltaY) : double.PositiveInfinity;
            double tMaxZ = dz != 0 ? t + ((stepZ > 0 ? z + 1 - pz : pz - z) * tDeltaZ) : double.PositiveInfinity;

            // The face we came in through: the slab that set tEnter, or none
            // if the ray began inside the box.
            int nx = 0, ny = 0, nz = 0;
            if (tEnter > 0.0)
            {
                if (enterAxis == 0) nx = -stepX;
                else if (enterAxis == 1) ny = -stepY;
                else if (enterAxis == 2) nz = -stepZ;
            }

            double traveled = t;
            while (traveled <= tExit)
            {
                if (!ChunkStore.InBounds(x, y, z)) return false;

                if (stopsAt(store.Get(x, y, z)))
                {
                    hit.Voxel = new Core.Int3(x, y, z);
                    hit.Normal = new Core.Int3(nx, ny, nz);
                    hit.Distance = traveled;
                    return true;
                }

                if (tMaxX < tMaxY && tMaxX < tMaxZ)
                {
                    x += stepX; traveled = tMaxX; tMaxX += tDeltaX;
                    nx = -stepX; ny = 0; nz = 0;
                }
                else if (tMaxY < tMaxZ)
                {
                    y += stepY; traveled = tMaxY; tMaxY += tDeltaY;
                    nx = 0; ny = -stepY; nz = 0;
                }
                else
                {
                    z += stepZ; traveled = tMaxZ; tMaxZ += tDeltaZ;
                    nx = 0; ny = 0; nz = -stepZ;
                }
            }
            return false;
        }

        static bool Slab(double origin, double dir, double size, int axis,
                         ref double tEnter, ref double tExit, ref int enterAxis)
        {
            if (dir == 0.0) return origin >= 0.0 && origin < size;

            double t0 = (0.0 - origin) / dir;
            double t1 = (size - origin) / dir;
            if (t0 > t1) { double s = t0; t0 = t1; t1 = s; }

            if (t0 > tEnter) { tEnter = t0; enterAxis = axis; }
            if (t1 < tExit) tExit = t1;
            return tEnter <= tExit;
        }

        static int Cell(double p, int size)
        {
            int c = (int)System.Math.Floor(p);
            if (c < 0) c = 0;
            if (c >= size) c = size - 1;
            return c;
        }
    }
}
