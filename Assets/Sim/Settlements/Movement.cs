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

        /// <summary>
        /// The way between two columns as the columns to pass through (S2W):
        /// nothing between on open ground, the parcels of the found path where
        /// a straight line would cross water or a cliff. For drawing a walk;
        /// nobody is moved by it.
        /// </summary>
        public static void Route(ParcelGrid grid, IslandMap island, int x0, int z0, int x1, int z1, List<Int3> via)
        {
            via.Clear();
            if (grid == null) return;
            int p0x = x0 / ParcelGrid.Size, p0z = z0 / ParcelGrid.Size, p1x = x1 / ParcelGrid.Size, p1z = z1 / ParcelGrid.Size;
            if (!ParcelGrid.InBounds(p0x, p0z) || !ParcelGrid.InBounds(p1x, p1z)) return;
            if ((p0x == p1x && p0z == p1z) || Clear(grid, p0x, p0z, p1x, p1z)) return;
            List<int> path = CachedPath(grid, p0z * ParcelGrid.Width + p0x, p1z * ParcelGrid.Width + p1x);
            for (int k = 1; k < path.Count - 1; k++)
            {
                int cx, cz;
                if (DryIn(island, path[k] % ParcelGrid.Width, path[k] / ParcelGrid.Width, out cx, out cz)) via.Add(new Int3(cx, 0, cz));
            }
        }

        /// <summary>Voxels from the fire the roofless camp: near enough for its light, clear of the fire itself (S2W).</summary>
        public const int CampNearest = 8, CampFarthest = 18;

        /// <summary>
        /// Where the i-th person without a roof keeps their place (S2W): round
        /// the fire at a distance, spread out, on dry land. The fire itself is
        /// kept for what is done at a fire.
        /// </summary>
        public static void Camp(Settlement s, IslandMap island, int i, out int x, out int z)
        {
            double angle = i * 2.399963229728653;
            int r = CampNearest + (i * 7) % (CampFarthest - CampNearest + 1);
            for (int attempt = 0; attempt < 8; attempt++)
            {
                x = s.Hearth.X + (int)SimMath.Round(SimMath.Cos(angle) * r);
                z = s.Hearth.Z + (int)SimMath.Round(SimMath.Sin(angle) * r);
                if (x >= 0 && z >= 0 && x < Voxels.ChunkStore.SizeX && z < Voxels.ChunkStore.SizeZ && (island == null || island.IsLand(x, z))) return;
                angle += 0.9;
            }
            AtFire(s, i, out x, out z);
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
        readonly PastimeTable _pastimes;
        CommonsRules _commons;

        /// <summary>Voxels from the hearth that count as being at the fire, for the record of where the day goes.</summary>
        public const int NearFire = 12;

        public MovementSystem(ParcelGrid grid, DriveRules rules, PastimeTable pastimes = null)
        {
            _grid = grid;
            _activities = rules.Activities;
            _needs = rules.Needs;
            _pastimes = pastimes ?? PastimeTable.None;
        }

        /// <summary>The commons rules (S2Z), so gatherings and work parties are drawn where they are held.</summary>
        public MovementSystem WithCommons(CommonsRules commons) { _commons = commons; return this; }

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
                // Arrived since the tick began (born, or come from another town): their tick starts where they are.
                if (a.StartTick != world.Clock.Tick) { a.StartX = a.X; a.StartZ = a.Z; a.StartTick = world.Clock.Tick; }
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

                // Errands move nobody any more (S2W): they are done on the way, and
                // drawn as the detour they are, from wherever the tick takes them.
                foreach (int e in a.ErrandPlaces)
                    if (_activities[e].At == ActionPlace.People) a.Talking = true;

                string workPose;
                if (act != null && act.Productive && s.Tasks != null
                    && Working(s, i, a, deposits, world.Island, world.Clock.TotalDays, world.Clock.Tick, out gx, out gz, out doing, out workPose))
                {
                    string verb = s.Tasks.KindOf(s.Tasks.CurrentTask(i)).Verb;
                    // A builder has already walked this tick (S1A does its own).
                    if (verb == "build" && a.Fetching < 0)
                    {
                        a.Doing = doing;
                        a.Pose = "work";
                        a.Arrived = true;
                        bool there = System.Math.Abs(a.X - a.GoalX) <= Places.Reach && System.Math.Abs(a.Z - a.GoalZ) <= Places.Reach;
                        DrawWork(s, world, a, i, "work", doing, there, verb);
                        continue;
                    }
                    Go(s, world, a, gx, gz);
                    a.Doing = (a.Arrived || workPose == "carry" ? doing : "going to work") + After(a);
                    a.Pose = a.Arrived ? workPose : (workPose == "carry" ? "carry" : "walk");
                    if (a.Arrived) { a.LastWorkX = a.X; a.LastWorkZ = a.Z; }
                    if (verb == "haul") DrawHaul(s, world, a, i);
                    else DrawWork(s, world, a, i, workPose == "carry" ? "work" : workPose, doing, a.Arrived, verb);
                    continue;
                }

                if (act == null || act.Productive)
                {
                    // Nothing wanted of them this tick (S2V): at home if they have
                    // one, else where they last worked, and the camp only for the
                    // newly founded and the roofless who have never worked (S2W).
                    string idle;
                    Household family = Households.Of(s, a);
                    if (family != null && family.Housed)
                    {
                        Project home = family.Home[0];
                        Int3 c = Construction.World(home, home.Plan.Width / 2, 0, home.Plan.Depth / 2);
                        gx = c.X + (i % 3) - 1; gz = c.Z + (i / 3 % 3) - 1; idle = "idle at home";
                    }
                    else if (a.LastWorkX >= 0) { gx = a.LastWorkX; gz = a.LastWorkZ; idle = "idle where they work"; }
                    else { Movement.Camp(s, world.Island, i, out gx, out gz); idle = "idle at the camp"; }
                    Go(s, world, a, gx, gz);
                    a.Doing = idle;
                    a.Pose = a.Arrived ? "stand" : "walk";
                    DrawOwn(s, world, a, i, "stand", idle, night, true);
                    continue;
                }

                // Somewhere of their own to go (S2V): the store, the water, their
                // bed, the others. What it does for them counts once they are there.
                string where;
                if (!Places.Target(s, world.Island, a, i, act.At, night, out gx, out gz, out where))
                {
                    Movement.Camp(s, world.Island, i, out gx, out gz);
                    where = "";
                }
                Go(s, world, a, gx, gz);

                if (a.Arrived)
                {
                    a.Doing = act.Doing + where + After(a);
                    a.Pose = act.Pose;
                    a.Talking = a.Talking || act.At == ActionPlace.People;
                }
                else
                {
                    a.Doing = "on the way: " + act.Doing + where;
                    a.Pose = "walk";
                }
                DrawOwn(s, world, a, i, act.Pose, act.Doing + where, night, false);
            }
            Company(s, needs, night);
        }

        // ── S2W: the tick drawn as it was spent ──────────────────────────────

        /// <summary>Voxels either way a worker moves about their work between spells of it.</summary>
        const int AboutTheWork = 2;

        /// <summary>Most loads drawn in one tick of hauling; a short haul makes more trips than anyone could follow.</summary>
        const int MostTripsShown = 6;

        /// <summary>Share of a tick spent taking up or putting down a load.</summary>
        const double Handling = 0.02;

        readonly List<Int3> _via = new List<Int3>();

        /// <summary>A walk along the ground from wherever the day has got to, round what cannot be crossed.</summary>
        void WalkTo(Agent a, SimWorld world, int x, int z, string pose, string doing, bool fills = false)
        {
            Itinerary day = a.Day;
            Movement.Route(_grid, world.Island, day.X, day.Z, x, z, _via);
            day.Walk(_via, x, z, pose, doing, fills);
        }

        /// <summary>Someone at work: out to it, the errands on the side, a pause, and back to it until the tick ends.</summary>
        void DrawWork(Settlement s, SimWorld world, Agent a, int i, string pose, string doing, bool arrived, string verb)
        {
            Itinerary day = a.Day;
            day.Begin(a.StartX, a.StartZ);
            Morning(s, world, a, i);
            if (!arrived)
            {
                WalkTo(a, world, a.X, a.Z, "walk", "on the way: " + doing, true);
                day.Finish();
                return;
            }

            WalkTo(a, world, a.X, a.Z, "walk", "on the way: " + doing);
            day.Fill(pose, doing);
            bool away = Errands(s, world, a, i);
            away |= AtCommons(s, world, a, i, false);
            if (away) WalkTo(a, world, a.X, a.Z, "walk", "back to " + doing);

            ulong who = a.Id.Hash;
            int hour = world.Clock.TickOfDay;
            bool pause = (hour == 1 || hour == 2) && StableHash.Combine(who, (ulong)world.Clock.Tick) % 3 == 0;
            // Moved about the work: a stand of reeds, a furrow, the other side of a wall.
            int spells = verb == "farm" || verb == "forage" || verb == "build" ? 3 : 2;
            for (int k = 1; k < spells; k++)
            {
                if (pause && k == 1) DoPastime(s, world, a, i, PastimeWhen.Midday, 3);
                ulong h = StableHash.Combine(who, (ulong)(world.Clock.Tick * 8 + k));
                int wx = a.X + ((int)(h % (2 * AboutTheWork + 1)) - AboutTheWork);
                int wz = a.Z + ((int)((h >> 8) % (2 * AboutTheWork + 1)) - AboutTheWork);
                if (world.Island != null && !world.Island.IsLand(Clamp(wx, Voxels.ChunkStore.SizeX), Clamp(wz, Voxels.ChunkStore.SizeZ)))
                { wx = a.X; wz = a.Z; }
                day.Walk(null, wx, wz, "walk", doing);
                day.Fill(pose, doing);
            }
            if (pause && spells < 2) DoPastime(s, world, a, i, PastimeWhen.Midday, 3);
            day.Walk(null, a.X, a.Z, "walk", doing);
            day.Fill(pose, doing);
            day.Finish();
        }

        /// <summary>A hauler's tick: loads taken up at the heap, carried, put down, and back for the next.</summary>
        void DrawHaul(Settlement s, SimWorld world, Agent a, int i)
        {
            Itinerary day = a.Day;
            day.Begin(a.StartX, a.StartZ);
            Morning(s, world, a, i);
            int fx = a.HaulFromX, fz = a.HaulFromZ, tx = a.HaulToX, tz = a.HaulToZ;
            string what = a.HaulWhat;
            if (Errands(s, world, a, i)) { }

            double dx = tx - fx, dz = tz - fz;
            double trip = 2.0 * SimMath.Sqrt(dx * dx + dz * dz) / DriveSystem.VoxelsWalkedPerTick + 2.0 * Handling;
            int trips = trip > 0.0 ? (int)(a.LabourShare * 0.9 / trip) : MostTripsShown;
            if (trips < 1) trips = 1;
            if (trips > MostTripsShown) trips = MostTripsShown;

            WalkTo(a, world, fx, fz, "walk", "on the way to the " + what);
            for (int k = 0; k < trips; k++)
            {
                day.Stay(Handling, "work", "loading " + what);
                WalkTo(a, world, tx, tz, "carry", "carrying " + what + " to " + a.HaulTo);
                day.Stay(Handling, "work", "putting down " + what);
                if (k < trips - 1) WalkTo(a, world, fx, fz, "walk", "back for more " + what);
            }
            bool atHeap = a.X == fx && a.Z == fz;
            WalkTo(a, world, a.X, a.Z, atHeap ? "walk" : "carry", atHeap ? "back for more " + what : "carrying " + what + " to " + a.HaulTo);
            day.Fill("work", atHeap ? "loading " + what : "putting down " + what);
            day.Finish();
        }

        /// <summary>
        /// A tick spent for themselves: the errands, the walk to where it is
        /// done and the time there — at night, the evening at home or by the
        /// camp and then bed; with nothing to do, a pastime or two.
        /// </summary>
        void DrawOwn(Settlement s, SimWorld world, Agent a, int i, string pose, string doing, bool night, bool idle)
        {
            Itinerary day = a.Day;
            day.Begin(a.StartX, a.StartZ);
            if (!night) { Morning(s, world, a, i); Errands(s, world, a, i); AtCommons(s, world, a, i, false); }
            if (!a.Arrived)
            {
                WalkTo(a, world, a.X, a.Z, "walk", "on the way: " + doing, true);
                day.Finish();
                return;
            }

            if (night)
            {
                // The evening at the fire (S2Z) when there is a gathering and they go; else at home.
                if (!AtCommons(s, world, a, i, true)) DoPastime(s, world, a, i, PastimeWhen.Evening, 5);
            }
            else if (idle)
            {
                DoPastime(s, world, a, i, PastimeWhen.Idle, 6);
                DoPastime(s, world, a, i, PastimeWhen.Idle, 7);
            }
            WalkTo(a, world, a.X, a.Z, "walk", "on the way: " + doing);
            day.Fill(pose, doing);
            day.Finish();
        }

        /// <summary>
        /// Time at the commons (S2Z): the evening's gathering for whoever goes,
        /// or an afternoon on the works for whoever is in today's party. True
        /// when they went.
        /// </summary>
        bool AtCommons(Settlement s, SimWorld world, Agent a, int i, bool evening)
        {
            Commons c = s.Commons;
            if (c == null || _commons == null || _commons.Stages.Count == 0) return false;
            int x, z;
            if (evening)
            {
                if (c.Tonight == null || c.Tonight.Day != world.Clock.TotalDays || !c.Attends(a)) return false;
                c.Spot(s, world.Island, _commons, i, true, out x, out z);
                string at = c.Tonight.Place;
                WalkTo(a, world, x, z, "walk", "on the way to " + at);
                a.Day.Stay(c.Tonight.Kind.Takes, c.Tonight.Kind.Pose, c.Tonight.Kind.Doing + " at " + at);
                return true;
            }
            if (world.Clock.TickOfDay != 2 || !c.InParty(a, world.Clock.TotalDays)) return false;
            int stage = System.Math.Min(c.Underway, _commons.Stages.Count - 1);
            if (stage < 0) return false;
            c.Spot(s, world.Island, _commons, i, false, out x, out z);
            WalkTo(a, world, x, z, "walk", "on the way to the fire");
            a.Day.Stay(0.35, "work", "making " + _commons.Stages[stage].Name);
            return true;
        }

        /// <summary>The first tick of a day starts where they slept, with whatever they do first thing.</summary>
        void Morning(Settlement s, SimWorld world, Agent a, int i)
        {
            if (world.Clock.TickOfDay != 0) return;
            DoPastime(s, world, a, i, PastimeWhen.Morning, 1);
        }

        /// <summary>Every errand of the tick that had somewhere to go, walked to and done. True when there were any.</summary>
        bool Errands(Settlement s, SimWorld world, Agent a, int i)
        {
            bool any = false;
            foreach (int e in a.ErrandPlaces)
            {
                Activity errand = _activities[e];
                int ex, ez;
                string at;
                if (!Places.Target(s, world.Island, a, i, errand.At, false, out ex, out ez, out at)) continue;
                Movement.OnLand(world.Island, ref ex, ref ez, a.X, a.Z);
                WalkTo(a, world, ex, ez, "walk", "on the way: " + errand.Doing + at);
                a.Day.Stay(errand.Takes * 0.5, errand.Pose, errand.Doing + at);
                any = true;
            }
            return any;
        }

        /// <summary>One pastime for a time of day, if content has one that fits them, done where it is done.</summary>
        void DoPastime(Settlement s, SimWorld world, Agent a, int i, PastimeWhen when, ulong salt)
        {
            Household family = Households.Of(s, a);
            bool housed = family != null && family.Housed;
            bool water = world.Island != null && Places.WaterPoints(s, world.Island).Count > 0;
            Pastime p = _pastimes.Pick(when, a.Id.Hash, world.Clock.TotalDays + world.Clock.TickOfDay * 7, salt, housed, water);
            if (p == null) return;
            Itinerary day = a.Day;
            int px = day.X, pz = day.Z;
            switch (p.At)
            {
                case PastimeAt.Here:
                    day.Stay(p.Takes, p.Pose, p.Doing);
                    return;
                case PastimeAt.Nearby:
                {
                    ulong h = StableHash.Combine(a.Id.Hash, (ulong)world.Clock.Tick + salt);
                    int nx = Clamp(px + (int)(h % 13) - 6, Voxels.ChunkStore.SizeX);
                    int nz = Clamp(pz + (int)((h >> 8) % 13) - 6, Voxels.ChunkStore.SizeZ);
                    if (world.Island != null && !world.Island.IsLand(nx, nz)) { day.Stay(p.Takes, "stand", p.Doing); return; }
                    day.Walk(null, nx, nz, p.Pose, p.Doing);
                    day.Stay(p.Takes * 0.5, p.Pose == "walk" ? "stand" : p.Pose, p.Doing);
                    day.Walk(null, px, pz, p.Pose, p.Doing);
                    return;
                }
                case PastimeAt.Home:
                {
                    Project home = family.Home[0];
                    Int3 c = Construction.World(home, home.Plan.Width / 2, 0, home.Plan.Depth / 2);
                    // In front of it rather than in its middle: by the door, or at the hearth inside.
                    int hx = c.X + (i % 3) - 1, hz = c.Z + (i / 3 % 3) - 1;
                    WalkTo(a, world, hx, hz, "walk", "going home");
                    day.Stay(p.Takes, p.Pose, p.Doing);
                    return;
                }
                case PastimeAt.Water:
                {
                    int wx, wz;
                    string ignored;
                    if (!Places.Target(s, world.Island, a, i, ActionPlace.Water, false, out wx, out wz, out ignored)) return;
                    WalkTo(a, world, wx, wz, "walk", "on the way: " + p.Doing);
                    day.Stay(p.Takes, p.Pose, p.Doing);
                    return;
                }
                case PastimeAt.Camp:
                {
                    int cx, cz;
                    Movement.Camp(s, world.Island, i, out cx, out cz);
                    WalkTo(a, world, cx, cz, "walk", "back to the camp");
                    day.Stay(p.Takes, p.Pose, p.Doing);
                    return;
                }
            }
        }

        static int Clamp(int v, int n) { return v < 0 ? 0 : (v >= n ? n - 1 : v); }

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
            // A builder fetching what the building waits on is drawn at the wood or the quarry.
            if (kind.Verb == "build" && a.Fetching < 0)
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

            int m = kind.Verb == "build" ? a.Fetching : s.Tasks.MaterialOf(task);
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
                Places.WildGround(s, island, a.Id.Hash, day, i, out gx, out gz);
                doing = "gathering " + material;
            }
            return true;
        }
    }
}
