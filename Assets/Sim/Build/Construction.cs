using System.Collections.Generic;
using Godless.Sim.Annals;
using Godless.Sim.Core;
using Godless.Sim.Deltas;
using Godless.Sim.Drives;
using Godless.Sim.Settlements;
using Godless.Sim.Voxels;
using Godless.Sim.World;

namespace Godless.Sim.Build
{
    /// <summary>
    /// Building, by hand, over days. S1A.
    ///
    /// Part 05's last stage: "agents haul real material and place real voxels
    /// over in-game days. Half-built structures are legitimate world states. A
    /// settlement that collapses mid-build leaves a skeleton, and skeletons are
    /// excellent storytelling."
    ///
    /// So nothing here places a building. A builder walks to the site (S13),
    /// lays a few voxels of it a tick from the bottom up, and takes each one
    /// out of the settlement's stock as it goes. Run out of oak and the wall
    /// stops at the height the oak reached, on record, until somebody gathers
    /// more.
    ///
    /// Every voxel goes through VoxelWorld.Set with the structure's record as
    /// its cause, so the delta log can say which house each change belongs to
    /// and the chain runs back through the intent to the nights in the open
    /// that asked for it (L3).
    /// </summary>
    public sealed class Construction
    {
        public static readonly Symbol BegunKind = Symbol.For("structure.begun");
        public static readonly Symbol CompletedKind = Symbol.For("structure.completed");
        static readonly Symbol PostRole = Symbol.For("role.post");
        static readonly Symbol WallRole = Symbol.For("role.wall");

        /// <summary>Whether a body column is the back wall's: the side opposite the door.</summary>
        static bool OnBack(Project project, int x, int z, int width, int depth)
        {
            if (project.DoorSide < 0) return false;
            switch ((project.DoorSide + 2) % 4)
            {
                case 0: return z == 0;
                case 1: return x == width - 1;
                case 2: return z == depth - 1;
                default: return x == 0;
            }
        }

        /// <summary>Natural ground a wall can be made of: anything solid that no settlement laid.</summary>
        bool IsHillside(ushort voxel)
        {
            if (voxel == VoxelTypes.AirId) return false;
            if (_ground == null) return false;
            return voxel < _ground.Length && _ground[voxel];
        }

        /// <summary>Where furniture and rubble go (S2R), and what they look like. Null leaves buildings unfurnished.</summary>
        public DetailLayer Details { get; set; }
        public DetailModelTable Models { get; set; }

        /// <summary>Which voxel ids are ground (TerrainBrush.SolidTable), for backing a house into a hill. Null disables it.</summary>
        public bool[] GroundTable { get { return _ground; } set { _ground = value; } }
        bool[] _ground;

        readonly VoxelWorld _voxels;
        readonly MaterialTable _materials;
        readonly VoxelTypes _types;
        readonly TileSet _tiles;
        readonly Palette _palette;
        readonly int _perTick;

        /// <param name="voxelsPerTick">Voxels one builder lays in a tick. A house is days of work.</param>
        readonly DepositMap _deposits;
        double _share = 1.0;

        /// <summary>The island builders walk on, so a straight walk never goes out to sea (S2G). May be null.</summary>
        public IslandMap Island { get { return _island; } set { _island = value; } }
        IslandMap _island;
        readonly int _ticksPerDay;

        public Construction(VoxelWorld voxels, MaterialTable materials, VoxelTypes types,
                            TileSet tiles, Palette palette, int voxelsPerTick = 4,
                            DepositMap deposits = null, int ticksPerDay = SimClock.DefaultTicksPerDay)
        {
            _deposits = deposits;
            _ticksPerDay = ticksPerDay;
            _voxels = voxels;
            _materials = materials;
            _types = types;
            _tiles = tiles;
            _palette = palette;
            _perTick = voxelsPerTick;
        }

        /// <summary>
        /// Share of a house's materials a settlement wants in hand before the
        /// first voxel goes down. Starting with an empty yard leaves a
        /// foundation nobody can build on and a plan made of nothing.
        /// </summary>
        public const double StartAt = 0.25;

        /// <summary>
        /// Days a begun building waits on one material before the rest of it
        /// goes up in something of the same kind from the yard. Waiting is
        /// what a slow material costs; a wing that owes a hundred voxels of
        /// pine at a fifth of a voxel a tick is a season of waiting, beside a
        /// yard full of oak.
        /// </summary>
        public const int StandInAfterDays = 20;

        /// <summary>
        /// Voxels still to lay on everything commissioned that a builder can
        /// do something about now: lay it, or fetch what it waits on.
        /// </summary>
        public static long Remaining(Settlement settlement)
        {
            long left = 0;
            foreach (Project p in settlement.Projects)
                if (!p.Complete && (Workable(settlement, p) || Wants(settlement, p) >= 0)) left += Order(p).Count - p.Placed;
            return left;
        }

        /// <summary>
        /// What a builder with nothing to lay goes to fetch: what the first
        /// building waiting on the land is waiting for, begun ones first. -1
        /// when nothing waits on anything the land gives.
        /// </summary>
        public static int Fetch(Settlement settlement)
        {
            for (int pass = 0; pass < 2; pass++)
                foreach (Project p in settlement.Projects)
                {
                    if (p.Complete || p.Begun.Exists != (pass == 0)) continue;
                    int m = Wants(settlement, p);
                    if (m >= 0) return m;
                }
            return -1;
        }

        /// <summary>The material a ready building is waiting on that the land in reach gives, or -1.</summary>
        static int Wants(Settlement settlement, Project project)
        {
            if (project.Complete || project.Destroyed || project.Built == null || !Ready(settlement, project)) return -1;
            int m = NextMaterial(project);
            if (m < 0 || m >= settlement.Stock.Materials.Count || settlement.Stock.Of(m) > 0) return -1;
            return settlement.Catchment != null && settlement.Catchment.YieldPerLabourTick(m) > 0.0 ? m : -1;
        }

        /// <summary>The material the next voxel to lay is made of, or -1 when there is none.</summary>
        public static int NextMaterial(Project project)
        {
            if (project.Built == null) return -1;
            List<int> order = Order(project);
            return project.Placed < order.Count ? project.Built.MaterialAtCell(order[project.Placed]) : -1;
        }

        /// <summary>
        /// Whether a builder can do anything on it now: it is ready, and what
        /// its next voxel is made of is in the yard, or may be stood in for, or
        /// is gone from the land so the builder will make do. A building
        /// stalled on one material is left while there is other building to
        /// do. Before, every builder in the village walked to the first ready
        /// building and stood at its foot waiting for pine nobody fetched,
        /// while two houses with everything they needed were never begun.
        /// </summary>
        public static bool Workable(Settlement settlement, Project project)
        {
            if (project.Complete || project.Destroyed || project.Built == null || !Ready(settlement, project)) return false;
            // A plan made of something the settlement never had is remade from the yard when it is begun.
            if (!project.Begun.Exists && project.Built.Compromises.Count > 0) return true;
            int m = NextMaterial(project);
            if (m < 0 || m >= settlement.Stock.Materials.Count || settlement.Stock.Of(m) > 0) return true;
            if (project.StandsIn && Substitute(settlement, m, true) >= 0) return true;
            return !Obtainable(settlement, project);   // gone from the land: the builder makes do
        }

        /// <summary>
        /// Voxels of each material still to lay, counted from the cells not yet
        /// placed. Walls go up before roofs, so a share of the whole bill says
        /// the thatch is nearly paid for when none of it is on yet.
        /// </summary>
        public static long[] Owed(Project project)
        {
            if (project.OwedBuilt == project.Built && project.OwedPlaced == project.Placed && project.OwedCount != null)
                return project.OwedCount;
            var owed = new long[project.Built.Cost.Length];
            List<int> order = Order(project);
            for (int i = project.Placed; i < order.Count; i++)
            {
                int m = project.Built.MaterialAtCell(order[i]);
                if (m >= 0 && m < owed.Length) owed[m]++;
            }
            project.OwedCount = owed;
            project.OwedPlaced = project.Placed;
            project.OwedBuilt = project.Built;
            return owed;
        }

        /// <summary>
        /// Whether everything still to lay can ever be had: in the yard, or
        /// still growing or lying within reach. Only an island with real
        /// deposits can say no (S2F).
        /// </summary>
        public static bool Obtainable(Settlement settlement, Project project)
        {
            Catchment c = settlement.Catchment;
            if (c == null || !c.HasDeposits || project.Built == null) return true;
            long[] owed = Owed(project);
            for (int m = 0; m < project.Built.Cost.Length; m++)
            {
                long still = owed[m];
                if (still <= 0 || settlement.Stock.Of(m) + (long)Hauling.Piled(settlement, m) >= still) continue;
                if (c.YieldPerLabourTick(m) <= 0.0) return false;
            }
            return true;
        }

        /// <summary>
        /// Something in the yard to use instead: the same class if there is
        /// any, else (unless <paramref name="sameKind"/>) whatever there is
        /// most of. -1 if there is nothing.
        /// </summary>
        static int Substitute(Settlement settlement, int material, bool sameKind)
        {
            MaterialTable materials = settlement.Stock.Materials;
            int best = -1;
            long most = 0;
            for (int pass = 0; pass < (sameKind ? 1 : 2) && best < 0; pass++)
                for (int m = 0; m < materials.Count; m++)
                {
                    if (m == material) continue;
                    if (pass == 0 && materials[m].Class != materials[material].Class) continue;
                    long held = settlement.Stock.Of(m);
                    if (held > most) { most = held; best = m; }
                }
            return best;
        }

        /// <summary>
        /// Remakes the materials of what is not yet built from what can be had
        /// now. What stands keeps what it was made of.
        /// </summary>
        void Rethink(Settlement settlement, Project project, RngStream rng, string ranOut, long tick)
        {
            // Once a day at most: realizing is the WFC solve, and a plan that
            // is still unobtainable after one is not going to change by the
            // next builder's tick.
            project.RethoughtOn = tick / _ticksPerDay;
            Structure fresh = Realizer.Realize(project.Plan, _tiles, _materials, settlement.Stock, _palette, _types,
                                               rng, settlement.Catchment);
            if (ranOut != null) fresh.Note("finished in what was left after the " + ranOut + " in reach ran out");
            project.Built = fresh;
        }

        /// <summary>
        /// Once a day, every unfinished project whose materials can no longer be
        /// had is remade from what can (S2F). Without this only a builder's tick
        /// remade one, and a project not ready to start never got a builder:
        /// a house planned in slate after the slate ran out waited for ever.
        /// </summary>
        public void Review(Settlement settlement, long tick, RngStream rng)
        {
            long day = tick / _ticksPerDay;
            foreach (Project p in settlement.Projects)
            {
                if (p.Complete || p.Destroyed || p.Built == null) continue;
                if (p.RethoughtOn != day && !Obtainable(settlement, p)) Rethink(settlement, p, rng, null, tick);

                // How long a begun building has waited on what its next voxel
                // is made of. Long enough, and the same kind of thing will do.
                int next = p.Begun.Exists ? NextMaterial(p) : -1;
                if (next < 0 || settlement.Stock.Of(next) > 0) { p.WaitingSince = -1; p.StandsIn = false; continue; }
                if (p.WaitingSince < 0) p.WaitingSince = day;
                p.StandsIn = day - p.WaitingSince >= StandInAfterDays;
            }
        }

        /// <summary>Whether there is enough in the yard to be worth starting, or it is started already.</summary>
        public static bool Ready(Settlement settlement, Project project)
        {
            if (project.Begun.Exists) return true;
            long want = 0, have = 0;
            for (int m = 0; m < project.Built.Cost.Length; m++)
            {
                want += project.Built.Cost[m];
                long held = settlement.Stock.Of(m);
                have += held < project.Built.Cost[m] ? held : project.Built.Cost[m];
            }
            return want == 0 || have >= want * StartAt;
        }

        /// <summary>
        /// One builder's tick: walk toward the site, or lay a few voxels of
        /// it. Returns false when there is nothing to build.
        /// </summary>
        public bool Work(Settlement settlement, Agent agent, ParcelGrid grid, long tick, Annalist annals, RngStream rng)
        {
            // What is begun is finished before anything new is started, and a
            // building waiting on one material is passed over for one that has
            // what it needs, in the order they were commissioned.
            Project project = null;
            for (int pass = 0; pass < 2 && project == null; pass++)
                foreach (Project p in settlement.Projects)
                {
                    if (p.Complete || p.Begun.Exists != (pass == 0)) continue;
                    if (p.RethoughtOn != tick / _ticksPerDay && !Obtainable(settlement, p)) Rethink(settlement, p, rng, null, tick);
                    if (Workable(settlement, p)) { project = p; break; }
                }
            if (project == null) return false;

            if (Walk(settlement, agent, grid, project)) return true;
            _share = agent.LabourShare;
            Lay(settlement, project, tick, annals, rng);
            return true;
        }

        /// <summary>
        /// A tick's walk toward the site, if the builder is not there yet. The
        /// same walk everyone takes (S2G): a path once, then strides along it.
        /// </summary>
        bool Walk(Settlement settlement, Agent agent, ParcelGrid grid, Project project)
        {
            if (agent.ParcelX == project.Site.ParcelX && agent.ParcelZ == project.Site.ParcelZ) return false;
            int gx = project.Site.ParcelX * ParcelGrid.Size + ParcelGrid.Size / 2 + (agent.Index % 3) - 1;
            int gz = project.Site.ParcelZ * ParcelGrid.Size + ParcelGrid.Size / 2 + (agent.Index / 3 % 3) - 1;
            Movement.Toward(agent, grid, gx, gz, settlement.Traffic, _island);
            return true;
        }

        void Lay(Settlement settlement, Project project, long tick, Annalist annals, RngStream rng)
        {
            List<int> order = Order(project);
            if (!project.Begun.Exists) Begin(settlement, project, tick, annals, rng);

            // A builder who stopped to eat and drink lays a little less (S2V).
            int perTick = System.Math.Max(1, (int)SimMath.Round(_perTick * _share));
            int laid = 0;
            while (laid < perTick && project.Placed < order.Count)
            {
                int cell = order[project.Placed];
                Blueprint plan = project.Plan;
                int x = cell % plan.Width, z = (cell / plan.Width) % plan.Depth, y = cell / (plan.Width * plan.Depth);

                ushort type = project.Built.At(x, y, z);
                int material = _materials.IndexOf(_types.SymbolOf(type));
                if (material < 0) { project.Placed++; continue; }

                Int3 at = World(project, x, y, z);

                // The hill already stands where this piece of back wall goes (S2O).
                if (project.EarthBacked && plan.At(x, y, z) == WallRole
                    && OnBack(project, x - Grammar.Margin, z - Grammar.Margin, plan.Width - 2 * Grammar.Margin, plan.Depth - 2 * Grammar.Margin)
                    && IsHillside(_voxels.Get(at)))
                {
                    project.Placed++;
                    continue;
                }
                // Rethought today and still not to be had: a few voxels of a
                // hearth stone the land no longer holds do not hold up a house.
                // Use what the yard has, the same kind of thing if it can, and
                // failing that leave the voxel out and say so.
                if (settlement.Stock.Of(material) <= 0)
                {
                    bool gone = project.RethoughtOn == tick / _ticksPerDay && !Obtainable(settlement, project);
                    if (gone || project.StandsIn)
                    {
                        // Waited on too long, it is finished in the same kind
                        // of thing: the wall shows where the pine gave out.
                        int instead = Substitute(settlement, material, !gone);
                        if (instead < 0 && !gone) return;
                        if (instead < 0)
                        {
                            project.Built.Note("left out a voxel of " + _materials[material].Name + ": none to be had");
                            project.Placed++;
                            continue;
                        }
                        if (!gone) NoteOnce(project.Built, "finished in " + _materials[instead].Name + " after waiting on "
                                                           + _materials[material].Name);
                        type = _types.IdOf(_materials[instead].Voxel);
                        material = instead;
                    }
                }

                if (!settlement.Stock.TryTake(material, 1, tick, settlement.Id, at, annals, project.Begun))
                {
                    // The wall stops at the height its material reached. A plan
                    // made of something the settlement never had is remade now,
                    // from what is actually in the yard.
                    if (project.Built.Compromises.Count > 0 && project.Placed == 0)
                        project.Built = Realizer.Realize(project.Plan, _tiles, _materials, settlement.Stock,
                                                         _palette, _types, rng, settlement.Catchment);

                    // And a material that is gone from the land for good is
                    // not waited for (S2F): the rest of the building goes up in
                    // whatever there is, so the wall itself records the day
                    // the sand ran out.
                    else if (project.RethoughtOn != tick / _ticksPerDay && !Obtainable(settlement, project))
                        Rethink(settlement, project, rng, _materials[material].Name, tick);
                    return;
                }

                _voxels.Set(at, type, tick, project.Begun);
                project.Placed++;
                laid++;

                // A post at the bottom of the building carries on down to the
                // ground it stands on, which under stilts is wherever the
                // hillside happens to be.
                if (y == 0 && project.Plan.At(x, y, z) == PostRole)
                    for (int drop = 1; drop <= PostReach(project, x, z); drop++)
                        _voxels.Set(new Int3(at.X, at.Y - drop, at.Z), type, tick, project.Begun);
            }

            if (project.Placed >= order.Count) Finish(settlement, project, tick, annals);
        }

        static void NoteOnce(Structure built, string compromise)
        {
            foreach (string c in built.Compromises) if (c == compromise) return;
            built.Note(compromise);
        }

        /// <summary>
        /// The first voxel. The materials are settled here rather than at
        /// commissioning when the settlement had nothing: a house is a record
        /// of the conditions it was begun in, and an empty yard is not one.
        /// </summary>
        void Begin(Settlement settlement, Project project, long tick, Annalist annals, RngStream rng)
        {
            if (project.Built.Compromises.Count > 0)
                project.Built = Realizer.Realize(project.Plan, _tiles, _materials, settlement.Stock, _palette, _types, rng, settlement.Catchment);

            project.Begun = annals.Write(tick, BegunKind, settlement.Id, Centre(project), project.Site.Record,
                                         project.Built.TotalVoxels, project.Plan.Capacity,
                                         new[] { project.Intent.Kind.Id });

            if (project.PartKind == "storey") Unroof(settlement, project, tick);
            else
            {
                ClearSite(settlement, project, tick);
                Groundworks(project, tick, annals);
            }
        }

        /// <summary>
        /// A storey goes on where the roof was (S2P): the host's roof and gables
        /// come off first, into the yard, citing the storey that took them.
        /// </summary>
        void Unroof(Settlement settlement, Project storey, long tick)
        {
            Project host = storey.Beneath ?? storey.Host;
            if (host == null || !host.Complete) return;
            int roofY = RoofBase(host.Plan);
            if (roofY < 0) return;
            for (int y = roofY; y < host.Plan.Height; y++)
                for (int z = 0; z < host.Plan.Depth; z++)
                    for (int x = 0; x < host.Plan.Width; x++)
                    {
                        ushort type = host.Built.At(x, y, z);
                        if (type == VoxelTypes.AirId) continue;
                        Int3 at = World(host, x, y, z);
                        if (_voxels.Get(at) != type) continue;
                        _voxels.Set(at, VoxelTypes.AirId, tick, storey.Begun);
                        int m = _materials.IndexOf(_types.SymbolOf(type));
                        if (m >= 0) settlement.Stock.Add(m, 1);
                    }
        }

        /// <summary>
        /// Cuts a doorway, two voxels wide and four high, through the host's
        /// wall and the wing's own where they meet, at the wing's floor and
        /// in the middle of the wall they share.
        /// </summary>
        void OpenDoorway(Project wing, long tick, RecordId cause)
        {
            Project host = wing.Host;
            int side = wing.DoorSide;          // the wing's door looks away from the house
            if (side < 0) return;
            int dx, dz;
            Blueprint.SideStep(side, out dx, out dz);

            int hostW = host.Plan.Width - 2 * Grammar.Margin, hostD = host.Plan.Depth - 2 * Grammar.Margin;
            Int3 h0 = World(host, Grammar.Margin, 0, Grammar.Margin);
            int wingW = wing.Plan.Width - 2 * Grammar.Margin, wingD = wing.Plan.Depth - 2 * Grammar.Margin;
            Int3 w0 = World(wing, Grammar.Margin, 0, Grammar.Margin);

            int lo, hi;
            wing.Plan.Span(Symbol.For("role.floor"), out lo, out hi);
            int floorY = w0.Y + (lo < 0 ? 0 : lo) + 1;

            // The two walls: the host's on this side, and the wing's back.
            int hostWall, wingWall;
            if (dx != 0)
            {
                hostWall = dx > 0 ? h0.X + hostW - 1 : h0.X;
                wingWall = dx > 0 ? w0.X : w0.X + wingW - 1;
            }
            else
            {
                hostWall = dz > 0 ? h0.Z + hostD - 1 : h0.Z;
                wingWall = dz > 0 ? w0.Z : w0.Z + wingD - 1;
            }
            int along = dx != 0 ? w0.Z + wingD / 2 - 1 : w0.X + wingW / 2 - 1;

            for (int y = floorY; y < floorY + 4; y++)
                for (int a = along; a < along + 2; a++)
                {
                    foreach (int wall in new[] { hostWall, wingWall })
                    {
                        Int3 at = dx != 0 ? new Int3(wall, y, a) : new Int3(a, y, wall);
                        if (!ChunkStore.InBounds(at.X, at.Y, at.Z)) continue;
                        ushort here = _voxels.Get(at);
                        if (here == VoxelTypes.AirId) continue;
                        if (_materials.IndexOf(_types.SymbolOf(here)) < 0) continue;   // only building material, never the hill
                        _voxels.Set(at, VoxelTypes.AirId, tick, cause);
                    }
                }
        }

        /// <summary>The lowest course of a blueprint's roof, or -1 when it has none.</summary>
        public static int RoofBase(Blueprint plan)
        {
            int lo, hi;
            plan.Span(Symbol.For("role.roof"), out lo, out hi);
            return lo;
        }

        /// <summary>Storeys in a blueprint: the distinct courses holding a floor.</summary>
        public static int Storeys(Blueprint plan)
        {
            Symbol floor = Symbol.For("role.floor");
            int n = 0;
            for (int y = 0; y < plan.Height; y++)
            {
                bool any = false;
                for (int z = 0; z < plan.Depth && !any; z++)
                    for (int x = 0; x < plan.Width && !any; x++)
                        if (plan.At(x, y, z) == floor) any = true;
                if (any) n++;
            }
            return n;
        }

        /// <summary>
        /// Whatever grows or lies where the house will stand comes off first,
        /// into the yard (S2F): a house built in a wood starts as a clearing.
        /// </summary>
        void ClearSite(Settlement settlement, Project project, long tick)
        {
            Int3 lo = World(project, 0, 0, 0);
            Int3 hi = World(project, project.Plan.Width - 1, 0, project.Plan.Depth - 1);

            // Rubble on the ground goes into the yard first (S2T): the old house
            // becomes the new one's stock.
            if (settlement.Rubble.Count > 0)
                Collapse.ClearRubble(_voxels, Details, settlement, lo.X - 1, lo.Z - 1, hi.X + 1, hi.Z + 1, tick);

            if (_deposits == null) return;
            int[] got = _deposits.Clear(lo.X - 1, lo.Z - 1, hi.X + 1, hi.Z + 1, _voxels, tick, project.Begun, _ticksPerDay);
            for (int k = 0; k < got.Length; k++)
            {
                if (got[k] <= 0) continue;
                int m = _materials.IndexOf(_deposits.Kinds[k].Yields);
                if (m >= 0) settlement.Stock.Add(m, got[k]);
            }
        }

        void Finish(Settlement settlement, Project project, long tick, Annalist annals)
        {
            project.Complete = true;
            if (project.IsHome) settlement.ShelterCapacity += project.Plan.Capacity;

            RecordId done = annals.Write(tick, CompletedKind, settlement.Id, Centre(project), project.Begun,
                                         project.Built.TotalVoxels, project.Plan.Capacity,
                                         new[] { project.Intent.Kind.Id });

            // A wing is a room of the house, not a shed beside it: a doorway goes
            // through the wall the two share (S2P).
            if (project.PartKind == "wing" && project.Host != null) OpenDoorway(project, tick, done);

            if (project.IsHome)
            {
                // A bed for each sleeping place (S2S).
                Furnishing.Furnish(project, Details, Models, _types, _materials, tick, done);

                // A wing or a storey (S2P) is more room in a home that stands: its
                // beds are the host family's. A house of its own takes a family in
                // (S2N): whoever had no roof, or the overflow of whoever was most crowded.
                if (project.Host == null)
                    Households.MoveIn(settlement, project, project.Plan.Capacity, tick, annals, done);
            }
            if (project.Intent.Outstanding) settlement.Intents.Resolve(project.Intent, tick, annals, done);
        }

        /// <summary>
        /// Cut and fill before anything is laid (S16). Under stilts nothing is
        /// moved: that is the whole point of stilts. The spoil is whatever the
        /// surface was, so a terrace cut into grassy ground reads as grass and
        /// one cut into sand reads as sand.
        /// </summary>
        void Groundworks(Project project, long tick, Annalist annals)
        {
            GroundPlan ground = project.Ground;
            if (ground == null || ground.Strategy == GroundStrategy.Stilt) return;

            int moved = 0;
            for (int z = 0; z < ground.Depth; z++)
                for (int x = 0; x < ground.Width; x++)
                {
                    int target = ground.LevelAt(x, z), was = ground.GroundAt(x, z);
                    if (target < 0 || was < 0 || target == was) continue;

                    // Backed into the slope (S2O): the hillside under the back
                    // wall is not dug away. It stands as that wall.
                    if (project.EarthBacked && target < was && OnBack(project, x, z, ground.Width, ground.Depth)) continue;

                    int worldX = project.Site.ParcelX * ParcelGrid.Size + project.OffsetX + x;
                    int worldZ = project.Site.ParcelZ * ParcelGrid.Size + project.OffsetZ + z;
                    ushort surface = _voxels.Store.Get(worldX, was - 1, worldZ);

                    for (int y = System.Math.Min(target, was); y < System.Math.Max(target, was); y++)
                    {
                        var at = new Int3(worldX, y, worldZ);
                        _voxels.Set(at, target > was ? surface : VoxelTypes.AirId, tick, project.Begun);
                        moved++;
                    }
                }
            ground.Moved = moved;
        }

        static Int3 Centre(Project project)
        {
            Blueprint plan = project.Plan;
            return World(project, plan.Width / 2, 0, plan.Depth / 2);
        }

        /// <summary>Where a blueprint cell stands in the world, once the site is under it.</summary>
        public static Int3 World(Project project, int x, int y, int z)
        {
            int originX = project.Site.ParcelX * ParcelGrid.Size - Grammar.Margin + project.OffsetX;
            int originZ = project.Site.ParcelZ * ParcelGrid.Size - Grammar.Margin + project.OffsetZ;
            return new Int3(originX + x, project.Site.Ground + project.OffsetY + y, originZ + z);
        }

        /// <summary>
        /// A post stands on the ground, however far down that is (S16). Under
        /// stilts the blueprint's posts are as long as the genome made them;
        /// this is what carries them the rest of the way.
        /// </summary>
        public static int PostReach(Project project, int x, int z)
        {
            GroundPlan ground = project.Ground;
            if (ground == null || ground.Strategy != GroundStrategy.Stilt) return 0;
            int under = ground.GroundAt(x - Grammar.Margin, z - Grammar.Margin);
            return under < 0 ? 0 : project.Site.Ground - under;
        }

        /// <summary>
        /// The cells to lay, bottom up and in a fixed order, so a half-built
        /// house is a house to the height it has reached rather than a
        /// scattering of walls.
        /// </summary>
        internal static List<int> Order(Project project)
        {
            if (project.Order != null) return project.Order;

            Blueprint plan = project.Plan;
            var order = new List<int>();
            for (int y = 0; y < plan.Height; y++)
                for (int z = 0; z < plan.Depth; z++)
                    for (int x = 0; x < plan.Width; x++)
                        if (project.Built.At(x, y, z) != VoxelTypes.AirId)
                            order.Add((y * plan.Depth + z) * plan.Width + x);
            project.Order = order;
            return order;
        }
    }
}
