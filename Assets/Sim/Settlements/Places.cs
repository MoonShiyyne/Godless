using System.Collections.Generic;
using Godless.Sim.Build;
using Godless.Sim.Core;
using Godless.Sim.Drives;
using Godless.Sim.Voxels;
using Godless.Sim.World;

namespace Godless.Sim.Settlements
{
    /// <summary>
    /// Where, for one person, each kind of place is. S2V.
    ///
    /// "Drink" is not a number going down: it is a walk to the nearest bit of
    /// river or shore and back. "Sleep" is their own bed if the family has one
    /// for them, a roof if not, the fire if there is no roof at all. The drive
    /// system asks this how far each is, to weigh the walk; the movement system
    /// asks it where to go.
    /// </summary>
    public static class Places
    {
        /// <summary>Voxels from the target at which a person counts as arrived.</summary>
        public const int Reach = 3;

        /// <summary>The target column for a person doing something at a kind of place. False when there is nowhere.</summary>
        public static bool Target(Settlement s, IslandMap island, Agent a, int index, ActionPlace place, bool night,
                                  out int gx, out int gz, out string where)
        {
            gx = a.X; gz = a.Z; where = "";
            switch (place)
            {
                case ActionPlace.Anywhere:
                case ActionPlace.Task:
                    return true;

                case ActionPlace.Fire:
                    Movement.AtFire(s, index, out gx, out gz);
                    return true;

                case ActionPlace.Wild:
                {
                    // The same spots the village's foragers use: a nearby tree or reed bed, by person and day.
                    int spot = s.Catchment != null && island != null && island.Deposits != null
                        ? s.Catchment.ForageSpot(a.Id.Hash, a.ForageDay) : -1;
                    if (spot >= 0) { gx = island.Deposits.X(spot); gz = island.Deposits.Z(spot); return true; }
                    Movement.AtFire(s, index + 16, out gx, out gz);
                    return true;
                }

                case ActionPlace.Store:
                    // At the fire until there is a granary (S2H).
                    Movement.AtFire(s, index + 8, out gx, out gz);
                    return true;

                case ActionPlace.Home:
                {
                    Household family = Households.Of(s, a);
                    if (family != null && family.Housed)
                    {
                        Project home = family.Home[0];
                        Int3 c = Construction.World(home, home.Plan.Width / 2, 0, home.Plan.Depth / 2);
                        gx = c.X; gz = c.Z; where = " at home";
                        return true;
                    }
                    Movement.AtFire(s, index, out gx, out gz);
                    where = " by the fire";
                    return true;
                }

                case ActionPlace.Bed:
                {
                    Household family = Households.Of(s, a);
                    if (a.ShelteredLastNight && family != null && family.Housed && !a.Crowded)
                    {
                        Furnishing.Bed bed;
                        if (BedOf(family, a, out bed)) { gx = bed.Centre.X; gz = bed.Centre.Z; where = " in bed"; return true; }
                        Project home = family.Home[0];
                        Int3 c = Construction.World(home, home.Plan.Width / 2, 0, home.Plan.Depth / 2);
                        gx = c.X; gz = c.Z; where = " at home";
                        return true;
                    }
                    if (a.ShelteredLastNight)
                    {
                        foreach (Project p in s.Projects)
                            if (p.Complete && p.Host == null)
                            {
                                Int3 c = Construction.World(p, p.Plan.Width / 2, 0, p.Plan.Depth / 2);
                                gx = c.X; gz = c.Z; where = " under someone else's roof";
                                return true;
                            }
                    }
                    Movement.AtFire(s, index, out gx, out gz);
                    where = " in the open";
                    return true;
                }

                case ActionPlace.Water:
                {
                    List<Int3> water = WaterPoints(s, island);
                    if (water.Count == 0) return false;
                    long best = long.MaxValue;
                    foreach (Int3 w in water)
                    {
                        long dx = w.X - a.X, dz = w.Z - a.Z;
                        long d = dx * dx + dz * dz;
                        if (d < best) { best = d; gx = w.X; gz = w.Z; }
                    }
                    return true;
                }

                case ActionPlace.People:
                {
                    // Whoever else is already talking, nearest first; the fire if nobody is.
                    long best = long.MaxValue;
                    bool found = false;
                    foreach (Agent other in s.People)
                    {
                        if (other == a || other.Activity < 0 || !other.Talking) continue;
                        long dx = other.X - a.X, dz = other.Z - a.Z;
                        long d = dx * dx + dz * dz;
                        if (d < best) { best = d; gx = other.X + 1; gz = other.Z; found = true; }
                    }
                    if (!found) Movement.AtFire(s, index + 4, out gx, out gz);
                    return true;
                }
            }
            return true;
        }

        /// <summary>Straight-line walk to a kind of place, in voxels; a large number where there is none.</summary>
        public static double Distance(Settlement s, IslandMap island, Agent a, int index, ActionPlace place, bool night)
        {
            int gx, gz;
            string where;
            if (!Target(s, island, a, index, place, night, out gx, out gz, out where)) return 100000.0;
            double dx = gx - a.X, dz = gz - a.Z;
            return SimMath.Sqrt(dx * dx + dz * dz);
        }

        /// <summary>
        /// Dry columns beside water within walking range of the fire, nearest
        /// first: river banks, lake shores, the beach. Found once per
        /// settlement; the water does not move (S2J will say otherwise).
        /// </summary>
        public static List<Int3> WaterPoints(Settlement s, IslandMap island)
        {
            if (s.WaterPointList != null) return s.WaterPointList;
            var found = new List<long>();
            if (island != null)
            {
                const int range = 96;
                for (int z = s.Hearth.Z - range; z <= s.Hearth.Z + range; z += 2)
                    for (int x = s.Hearth.X - range; x <= s.Hearth.X + range; x += 2)
                    {
                        if (x < 1 || z < 1 || x >= ChunkStore.SizeX - 1 || z >= ChunkStore.SizeZ - 1) continue;
                        if (!island.IsLand(x, z) || island.WaterLevelAt(x, z) > 0) continue;
                        bool beside = false;
                        for (int k = 0; k < 4 && !beside; k++)
                        {
                            int nx = x + (k == 0 ? 1 : k == 1 ? -1 : 0), nz = z + (k == 2 ? 1 : k == 3 ? -1 : 0);
                            if (!island.IsLand(nx, nz) || island.WaterLevelAt(nx, nz) > 0) beside = true;
                        }
                        if (!beside) continue;
                        long dx = x - s.Hearth.X, dz = z - s.Hearth.Z;
                        long d = dx * dx + dz * dz;
                        if (d > (long)range * range) continue;
                        found.Add((d << 24) | ((long)z << 12) | (long)x);
                    }
            }
            found.Sort();
            var list = new List<Int3>();
            for (int i = 0; i < found.Count && i < 256; i++)
                list.Add(new Int3((int)(found[i] & 0xFFF), 0, (int)((found[i] >> 12) & 0xFFF)));
            s.WaterPointList = list;
            return list;
        }

        /// <summary>The bed a person sleeps in: their place in the family's roll, among the family's beds.</summary>
        public static bool BedOf(Household family, Agent a, out Furnishing.Bed bed)
        {
            bed = default(Furnishing.Bed);
            int place = family.Members.IndexOf(a.Id.Hash);
            if (place < 0) return false;
            foreach (Project home in family.Homes)
            {
                if (place < home.Beds.Count) { bed = home.Beds[place]; return true; }
                place -= home.Beds.Count;
                foreach (Project added in home.Added)
                {
                    if (!added.Complete) continue;
                    if (place < added.Beds.Count) { bed = added.Beds[place]; return true; }
                    place -= added.Beds.Count;
                }
            }
            return false;
        }
    }
}
