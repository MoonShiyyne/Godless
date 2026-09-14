using System.Collections.Generic;
using Godless.Sim.Build;
using Godless.Sim.Collective;
using Godless.Sim.Core;
using Godless.Sim.Drives;
using Godless.Sim.Harness;
using Godless.Sim.World;

namespace Godless.Sim.Settlements
{
    /// <summary>
    /// Where everybody is, and where they are going. S2G.
    ///
    /// Until this a person was a row of numbers at the hearth, and only a
    /// builder ever moved. Now each one has a place, and the place follows
    /// from the same two things that already decide what they do: their own
    /// needs (the drives: the fire when they are cold, a roof or the open at
    /// night) and the settlement's calls on them (the task board: the tree
    /// they are felling, the reed bed, the house going up). Nothing here
    /// decides anything new. It puts the decisions somewhere a stranger can
    /// see them.
    ///
    /// The tell: at dawn the village walks out to the edge of its clearing,
    /// the builders climb onto the walls, and at dusk everyone who has no
    /// roof lies down round the fire.
    /// </summary>
    public static class Movement
    {
        /// <summary>
        /// Parcels a person covers in a tick. A tick is six hours, and the
        /// village's whole range is a morning's walk: at three parcels a tick
        /// (S2G's first number) a gatherer called back to the fire was still
        /// on the way when the next tick asked again, and never arrived (S2V).
        /// </summary>
        public const int StrideParcels = 64;

        /// <summary>
        /// A tick's walk toward a column. Follows a path over the planning grid
        /// once one is known, straight at the goal when there is none, and
        /// counts every parcel crossed as foot traffic. True once there.
        /// </summary>
        public static bool Toward(Agent a, ParcelGrid grid, int goalX, int goalZ, InfluenceMap traffic = null,
                                  IslandMap island = null)
        {
            a.GoalX = goalX;
            a.GoalZ = goalZ;
            if (a.X == goalX && a.Z == goalZ) return true;

            int goalParcel = (goalZ / ParcelGrid.Size) * ParcelGrid.Width + goalX / ParcelGrid.Size;
            int here = a.ParcelZ * ParcelGrid.Width + a.ParcelX;
            int stride = StrideParcels * ParcelGrid.Size;

            if (here == goalParcel || grid == null)
            {
                Step(a, goalX, goalZ, stride, island);
                return a.X == goalX && a.Z == goalZ;
            }

            // Open ground is walked straight. Only a line that crosses water
            // or a cliff is worth a path search, and most walks — fire to
            // wood, wood to fire — cross neither. Searching every one was most
            // of a settlement's running cost.
            if (Clear(grid, a.ParcelX, a.ParcelZ, goalX / ParcelGrid.Size, goalZ / ParcelGrid.Size))
            {
                int bx = a.X, bz = a.Z;
                Step(a, goalX, goalZ, stride, island);
                if (traffic != null) Wear(traffic, bx, bz, a.X, a.Z);
                a.Path = null;
                return a.X == goalX && a.Z == goalZ;
            }

            if (a.Path == null || a.PathGoal != goalParcel || a.PathStep >= a.Path.Count || a.Path[a.PathStep] != here)
            {
                a.Path = CachedPath(grid, here, goalParcel);
                a.PathGoal = goalParcel;
                a.PathStep = 0;
            }

            if (a.Path.Count < 2)
            {
                // No way round: straight at it rather than standing still,
                // though never out into the sea.
                Step(a, goalX, goalZ, stride, island);
                return a.X == goalX && a.Z == goalZ;
            }

            int last = a.Path.Count - 1;
            int to = a.PathStep + StrideParcels > last ? last : a.PathStep + StrideParcels;
            for (int i = a.PathStep + 1; i <= to && traffic != null; i++)
            {
                int p = a.Path[i];
                traffic[p % ParcelGrid.Width, p / ParcelGrid.Width] += 1.0;
            }
            a.PathStep = to;
            if (to == last) { a.X = goalX; a.Z = goalZ; return true; }
            int next = a.Path[to];
            int cx, cz;
            if (DryIn(island, next % ParcelGrid.Width, next / ParcelGrid.Width, out cx, out cz)) { a.X = cx; a.Z = cz; }
            return false;
        }

        // A village walks the same few routes all day — fire to field, field to
        // store — so a found path is kept on the grid. A house going up changes
        // a parcel or two, not the way across the island, so paths outlive that
        // many refreshes before they are all found again. The lists are only read.
        const int PathsOutliveRefreshes = 200;
        const int MostPathsKept = 50000;

        static List<int> CachedPath(ParcelGrid grid, int from, int to)
        {
            if (grid.Version - grid.PathsVersion >= PathsOutliveRefreshes || grid.Paths.Count >= MostPathsKept)
            {
                grid.Paths.Clear();
                grid.PathsVersion = grid.Version;
            }
            long key = (long)from * (ParcelGrid.Width * ParcelGrid.Depth) + to;
            List<int> path;
            if (grid.Paths.TryGetValue(key, out path)) return path;
            path = ParcelPath.Find(grid, from % ParcelGrid.Width, from / ParcelGrid.Width, to % ParcelGrid.Width, to / ParcelGrid.Width);
            grid.Paths[key] = path;
            return path;
        }

        /// <summary>Whether a straight walk between two parcels stays on land and never climbs a wall.</summary>
        static bool Clear(ParcelGrid grid, int x0, int z0, int x1, int z1)
        {
            int dx = System.Math.Abs(x1 - x0), dz = System.Math.Abs(z1 - z0);
            int sx = x0 < x1 ? 1 : -1, sz = z0 < z1 ? 1 : -1;
            int err = dx - dz, x = x0, z = z0;
            double last = grid.Height[x0, z0];
            while (x != x1 || z != z1)
            {
                int e2 = 2 * err;
                if (e2 > -dz) { err -= dz; x += sx; }
                if (e2 < dx) { err += dx; z += sz; }
                if (!ParcelGrid.InBounds(x, z)) return false;
                bool goal = x == x1 && z == z1;
                if (!goal && (!grid.IsLand(x, z) || grid.Slope[x, z] >= ParcelPath.Impassable)) return false;
                double h = grid.Height[x, z];
                if (System.Math.Abs(h - last) >= ParcelPath.Impassable) return false;
                last = h;
            }
            return true;
        }

        /// <summary>Foot traffic along a straight stride, a parcel at a time.</summary>
        static void Wear(InfluenceMap traffic, int x0, int z0, int x1, int z1)
        {
            int p0x = x0 / ParcelGrid.Size, p0z = z0 / ParcelGrid.Size;
            int p1x = x1 / ParcelGrid.Size, p1z = z1 / ParcelGrid.Size;
            int steps = System.Math.Max(System.Math.Abs(p1x - p0x), System.Math.Abs(p1z - p0z));
            for (int i = 1; i <= steps; i++)
            {
                int px = p0x + (p1x - p0x) * i / steps, pz = p0z + (p1z - p0z) * i / steps;
                if (ParcelGrid.InBounds(px, pz)) traffic[px, pz] += 1.0;
            }
        }

        static void Step(Agent a, int goalX, int goalZ, int stride, IslandMap island)
        {
            int dx = goalX - a.X, dz = goalZ - a.Z;
            int nx = a.X + (System.Math.Abs(dx) <= stride ? dx : System.Math.Sign(dx) * stride);
            int nz = a.Z + (System.Math.Abs(dz) <= stride ? dz : System.Math.Sign(dz) * stride);
            if (island != null && !island.IsLand(Clamp(nx, Voxels.ChunkStore.SizeX), Clamp(nz, Voxels.ChunkStore.SizeZ))) return;
            a.X = nx;
            a.Z = nz;
        }

        static int Clamp(int v, int n) { return v < 0 ? 0 : (v >= n ? n - 1 : v); }

        /// <summary>A parcel's centre, or failing that its first dry column in grid order. False when all of it is sea.</summary>
        static bool DryIn(IslandMap island, int px, int pz, out int x, out int z)
        {
            x = px * ParcelGrid.Size + ParcelGrid.Size / 2;
            z = pz * ParcelGrid.Size + ParcelGrid.Size / 2;
            if (island == null || island.IsLand(x, z)) return true;
            for (int dz = 0; dz < ParcelGrid.Size; dz++)
                for (int dx = 0; dx < ParcelGrid.Size; dx++)
                    if (island.IsLand(px * ParcelGrid.Size + dx, pz * ParcelGrid.Size + dz))
                    { x = px * ParcelGrid.Size + dx; z = pz * ParcelGrid.Size + dz; return true; }
            return false;
        }

        /// <summary>
        /// The goal if it is dry land, otherwise the fallback: a place by the
        /// reeds is the reed bed itself, not the water a voxel past it.
        /// </summary>
        public static void OnLand(IslandMap island, ref int x, ref int z, int fallbackX, int fallbackZ)
        {
            x = Clamp(x, Voxels.ChunkStore.SizeX);
            z = Clamp(z, Voxels.ChunkStore.SizeZ);
            if (island == null || island.IsLand(x, z)) return;
            x = Clamp(fallbackX, Voxels.ChunkStore.SizeX);
            z = Clamp(fallbackZ, Voxels.ChunkStore.SizeZ);
        }

        /// <summary>A place round the fire for the i-th person: rings of eight, three voxels apart.</summary>
        public static void AtFire(Settlement s, int i, out int x, out int z)
        {
            int ring = 1 + i / 8, slot = i % 8;
            int r = 2 + ring * 2;
            int[] ox = { r, r, 0, -r, -r, -r, 0, r };
            int[] oz = { 0, r, r, r, 0, -r, -r, -r };
            x = s.Hearth.X + ox[slot];
            z = s.Hearth.Z + oz[slot];
        }
    }

    /// <summary>Moves every settlement's people toward what their drives and their tasks have them doing. S2G.</summary>
    public sealed class MovementSystem : ISimSystem
    {
        public static readonly Symbol SystemId = Symbol.For("system.movement");

        readonly ParcelGrid _grid;
        readonly ActivityTable _activities;

        readonly NeedTable _needs;

        /// <summary>Voxels from the hearth that count as being at the fire, for the record of where the day goes.</summary>
        public const int NearFire = 12;

        public MovementSystem(ParcelGrid grid, DriveRules rules)
        {
            _grid = grid;
            _activities = rules.Activities;
            _needs = rules.Needs;
        }

        public Symbol Id { get { return SystemId; } }

        public void Tick(SimWorld world)
        {
            bool night = world.Clock.TickOfDay == world.Clock.TicksPerDay - 1;
            foreach (Settlement s in world.Settlements) Place(s, world, night);
        }

        void Place(Settlement s, SimWorld world, bool night)
        {
            IReadOnlyList<Agent> people = s.People;
            DepositMap deposits = world.Island != null ? world.Island.Deposits : null;
            if (s.Traffic == null) s.Traffic = new InfluenceMap(Settlement.TrafficField);
            bool families = s.Households.Count > 0;
            NeedTable needs = _needs;
            Places.AllotBeds(s);

            for (int i = 0; i < people.Count; i++)
            {
                Agent a = people[i];
                if (!night)
                {
                    s.DaylightTicks++;
                    if (System.Math.Abs(a.X - s.Hearth.X) <= NearFire && System.Math.Abs(a.Z - s.Hearth.Z) <= NearFire) s.DaylightAtFire++;
                }
                Activity act = a.Activity >= 0 ? _activities[a.Activity] : null;
                a.Talking = false;
                a.ForageDay = world.Clock.TotalDays;
                int gx, gz;
                string doing;

                // The main activity does what it does whenever its place is within
                // the walk the rest of the tick allows (S2V) — wherever the person is
                // drawn. Checking arrival instead let an errand, which is drawn at its
                // own place, swallow the thing the tick was for.
                if (families && act != null && !act.Productive && act.At != ActionPlace.Anywhere && act.At != ActionPlace.Task)
                {
                    int tx, tz;
                    string ignored;
                    if (Places.Target(s, world.Island, a, i, act.At, night, out tx, out tz, out ignored))
                    {
                        double dx = tx - a.X, dz = tz - a.Z;
                        double walk = SimMath.Sqrt(dx * dx + dz * dz);
                        if (night || walk <= DriveSystem.VoxelsWalkedPerTick * a.LabourShare) Relieve(s, a, act, needs);
                    }
                }

                // An errand with somewhere to go is where they are seen this tick —
                // kneeling at the water, sitting by the store — unless they are
                // building, which needs them on the site.
                bool building = s.Tasks != null && s.Tasks.CurrentTask(i) >= 0 && s.Tasks.KindOf(s.Tasks.CurrentTask(i)).Verb == "build";
                // Someone at work is mostly seen at work: their errands show a tick
                // in four, staggered person by person, and the rest of the time
                // they are only the "(after ...)" on what they are doing.
                bool working = act != null && act.Productive;
                bool seenOnErrand = !working || (world.Clock.Tick + i) % 4 == 0;
                if (a.ErrandActivity >= 0 && !building && !night && seenOnErrand)
                {
                    Activity errand = _activities[a.ErrandActivity];
                    string at;
                    if (Places.Target(s, world.Island, a, i, errand.At, night, out gx, out gz, out at))
                    {
                        Go(s, world, a, gx, gz);
                        string then = act != null && act.Productive ? ", then back to work" : "";
                        a.Doing = (a.Arrived ? errand.Doing : "on the way: " + errand.Doing) + at + then;
                        a.Pose = a.Arrived ? errand.Pose : "walk";
                        a.Talking = a.Arrived && errand.At == ActionPlace.People;
                        continue;
                    }
                }

                string workPose;
                if (act != null && act.Productive && s.Tasks != null
                    && Working(s, i, a, deposits, world.Island, world.Clock.TotalDays, world.Clock.Tick, out gx, out gz, out doing, out workPose))
                {
                    // A builder has already walked this tick (S1A does its own).
                    if (s.Tasks.CurrentTask(i) >= 0 && s.Tasks.KindOf(s.Tasks.CurrentTask(i)).Verb == "build")
                    {
                        a.Doing = doing;
                        a.Pose = "work";
                        a.Arrived = true;
                        continue;
                    }
                    Go(s, world, a, gx, gz);
                    a.Doing = (a.Arrived || workPose == "carry" ? doing : "going to work") + After(a);
                    a.Pose = a.Arrived ? workPose : (workPose == "carry" ? "carry" : "walk");
                    if (a.Arrived) { a.LastWorkX = a.X; a.LastWorkZ = a.Z; }
                    continue;
                }

                if (act == null || act.Productive)
                {
                    // Nothing wanted of them this tick (S2V): at home if they have
                    // one, else where they last worked, and the fire only for the
                    // newly founded and the roofless who have never worked.
                    string idle;
                    Household family = Households.Of(s, a);
                    if (family != null && family.Housed)
                    {
                        Project home = family.Home[0];
                        Int3 c = Construction.World(home, home.Plan.Width / 2, 0, home.Plan.Depth / 2);
                        gx = c.X + (i % 3) - 1; gz = c.Z + (i / 3 % 3) - 1; idle = "idle at home";
                    }
                    else if (a.LastWorkX >= 0) { gx = a.LastWorkX; gz = a.LastWorkZ; idle = "idle where they work"; }
                    else { Movement.AtFire(s, i, out gx, out gz); idle = "idle at the fire"; }
                    Go(s, world, a, gx, gz);
                    a.Doing = idle;
                    a.Pose = a.Arrived ? "stand" : "walk";
                    continue;
                }

                // Somewhere of their own to go (S2V): the store, the water, their
                // bed, the others. What it does for them counts once they are there.
                string where;
                if (!Places.Target(s, world.Island, a, i, act.At, night, out gx, out gz, out where))
                {
                    Movement.AtFire(s, i, out gx, out gz);
                    where = "";
                }
                Go(s, world, a, gx, gz);

                if (a.Arrived)
                {
                    a.Doing = act.Doing + where + After(a);
                    a.Pose = act.Pose;
                    a.Talking = act.At == ActionPlace.People;
                }
                else
                {
                    a.Doing = "on the way: " + act.Doing + where;
                    a.Pose = night ? "walk" : "walk";
                }
            }
            Company(s, needs, night);
        }

        /// <summary>
        /// Company out and about (S2V): everyone awake is placed, so whoever is
        /// near whom is known — the woodcutters by the same stand of trees, the
        /// hands in neighbouring plots, the queue at the store. Each person near
        /// others has the needs that ease in company eased, and knows who the
        /// nearest was.
        /// </summary>
        public static void Company(Settlement s, NeedTable needs, bool night)
        {
            IReadOnlyList<Agent> people = s.People;
            int within = 4;
            bool any = false;
            for (int n = 0; n < needs.Count; n++)
                if (needs[n].NearRelief > 0.0) { any = true; within = System.Math.Max(within, needs[n].NearWithin); }
            if (night || !any)
            {
                foreach (Agent a in people) { a.Beside = 0; a.BesideWhom = -1; }
                return;
            }

            // Everyone by cell of the reach, in person order; looked up, never walked (L2).
            var cells = new Dictionary<long, List<int>>();
            for (int i = 0; i < people.Count; i++)
            {
                long key = CellKey(people[i].X / within, people[i].Z / within);
                List<int> list;
                if (!cells.TryGetValue(key, out list)) { list = new List<int>(); cells[key] = list; }
                list.Add(i);
            }

            for (int i = 0; i < people.Count; i++)
            {
                Agent a = people[i];
                int cx = a.X / within, cz = a.Z / within, count = 0, nearest = -1, nearestD = int.MaxValue;
                for (int dz = -1; dz <= 1; dz++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        List<int> list;
                        if (!cells.TryGetValue(CellKey(cx + dx, cz + dz), out list)) continue;
                        foreach (int j in list)
                        {
                            if (j == i) continue;
                            int d = System.Math.Max(System.Math.Abs(people[j].X - a.X), System.Math.Abs(people[j].Z - a.Z));
                            if (d > within) continue;
                            count++;
                            if (d < nearestD || (d == nearestD && j < nearest)) { nearestD = d; nearest = j; }
                        }
                    }
                a.Beside = count;
                a.BesideWhom = nearest;
                if (count == 0) continue;
                for (int n = 0; n < needs.Count; n++)
                {
                    Need need = needs[n];
                    if (need.NearRelief <= 0.0) continue;
                    int near = System.Math.Min(count, need.NearMost);
                    int reach = need.NearWithin;
                    if (reach < within && nearestD > reach) continue;
                    a.Levels[n] = SimMath.Clamp01(a.Levels[n] - need.NearRelief * near);
                }
            }
        }

        static long CellKey(int cx, int cz) { return ((long)cx << 32) ^ (uint)cz; }

        /// <summary>A tick of an activity's effect: its relief, the meal it eats, the food it brings in.</summary>
        static void Relieve(Settlement s, Agent a, Activity act, NeedTable needs)
        {
            if (act.UsesFood > 0.0 && s.Food < act.UsesFood) return;

            // What is gathered is what relieves: foraging bare ground fills nobody.
            double share = 1.0;
            if (act.GathersFood > 0.0)
            {
                double asked = act.GathersFood * a.LabourShare;
                double found = s.Catchment != null && asked > 0.0 ? s.Catchment.Forage(asked) : 0.0;
                s.Food += found;
                share = asked > 0.0 ? SimMath.Clamp01(found / asked) : 0.0;
            }
            for (int n = 0; n < needs.Count; n++)
                if (act.ReliefFor(n) != 0.0) a.Levels[n] = SimMath.Clamp01(a.Levels[n] - act.ReliefFor(n) * share);
            if (act.UsesFood > 0.0) s.Food -= act.UsesFood;
        }

        /// <summary>The errands done on the side this tick, as a trailing phrase.</summary>
        static string After(Agent a) { return a.Errands.Length == 0 ? "" : " (after " + a.Errands + ")"; }

        void Go(Settlement s, SimWorld world, Agent a, int gx, int gz)
        {
            Movement.OnLand(world.Island, ref gx, ref gz, s.Hearth.X, s.Hearth.Z);
            Movement.Toward(a, _grid, gx, gz, s.Traffic, world.Island);
            a.Arrived = System.Math.Abs(a.X - gx) <= Places.Reach && System.Math.Abs(a.Z - gz) <= Places.Reach;
        }

        /// <summary>Where a worker's task puts them, and what to call it.</summary>
        static bool Working(Settlement s, int i, Agent a, DepositMap deposits, IslandMap island, long day, long tick,
                            out int gx, out int gz, out string doing, out string pose)
        {
            gx = s.Hearth.X; gz = s.Hearth.Z; doing = ""; pose = "work";
            int task = s.Tasks.CurrentTask(i);
            if (task < 0) return false;

            TaskKind kind = s.Tasks.KindOf(task);

            // S2X: a tick at the heap, a tick at where it goes, turn about —
            // the loads in between are too many to draw one by one.
            // S2I: in the plot, spread out a little so a field's hands are not one figure.
            if (kind.Verb == "farm")
            {
                gx = a.FieldX + (i % 3) - 1;
                gz = a.FieldZ + (i / 3 % 3) - 1;
                doing = a.FieldWork;
                return a.FieldX >= 0;
            }

            if (kind.Verb == "haul")
            {
                if ((tick + i) % 2 == 0) { gx = a.HaulFromX; gz = a.HaulFromZ; doing = "loading " + a.HaulWhat; }
                else { gx = a.HaulToX; gz = a.HaulToZ; doing = "carrying " + a.HaulWhat + " to " + a.HaulTo; pose = "carry"; }
                return true;
            }
            if (kind.Verb == "build")
            {
                doing = "building";
                return true;
            }

            if (kind.Verb == "forage")
            {
                int spot = s.Catchment != null ? s.Catchment.ForageSpot(a.Id.Hash, day) : -1;
                if (spot >= 0 && deposits != null) { gx = deposits.X(spot); gz = deposits.Z(spot); }
                else Places.WildGround(s, island, a.Id.Hash, day, i, out gx, out gz);
                doing = "foraging";
                return true;
            }

            int m = s.Tasks.MaterialOf(task);
            string material = m >= 0 ? s.Stock.Materials[m].Name : "material";
            if (deposits != null && a.WorkingAt >= 0)
            {
                gx = deposits.X(a.WorkingAt) + 1;
                gz = deposits.Z(a.WorkingAt);
                FeatureShape shape = deposits.KindOf(a.WorkingAt).Shape;
                doing = (shape == FeatureShape.Tree ? "felling " : shape == FeatureShape.Tuft ? "cutting "
                        : shape == FeatureShape.Boulder ? "breaking " : "digging ") + material;
            }
            else
            {
                Movement.AtFire(s, i + 16, out gx, out gz);
                doing = "gathering " + material;
            }
            return true;
        }
    }
}
