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

        readonly VoxelWorld _voxels;
        readonly MaterialTable _materials;
        readonly VoxelTypes _types;
        readonly TileSet _tiles;
        readonly Palette _palette;
        readonly int _perTick;

        /// <param name="voxelsPerTick">Voxels one builder lays in a tick. A house is days of work.</param>
        readonly DepositMap _deposits;

        /// <summary>The island builders walk on, so a straight walk never goes out to sea (S2G). May be null.</summary>
        internal IslandMap _island;
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

        /// <summary>Voxels still to lay on everything commissioned that can actually be started.</summary>
        public static long Remaining(Settlement settlement)
        {
            long left = 0;
            foreach (Project p in settlement.Projects)
                if (!p.Complete && Ready(settlement, p)) left += Order(p).Count - p.Placed;
            return left;
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
            double left = 1.0 - project.Placed / (double)System.Math.Max(1, Order(project).Count);
            for (int m = 0; m < project.Built.Cost.Length; m++)
            {
                long still = (long)(project.Built.Cost[m] * left);
                if (still <= 0 || settlement.Stock.Of(m) >= still) continue;
                if (c.YieldPerLabourTick(m) <= 0.0) return false;
            }
            return true;
        }

        /// <summary>Something in the yard to use instead: the same class if there is any, else whatever there is most of. -1 if the yard is empty.</summary>
        int Substitute(Settlement settlement, int material)
        {
            int best = -1;
            long most = 0;
            for (int pass = 0; pass < 2 && best < 0; pass++)
                for (int m = 0; m < _materials.Count; m++)
                {
                    if (m == material) continue;
                    if (pass == 0 && _materials[m].Class != _materials[material].Class) continue;
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
            Project project = null;
            foreach (Project p in settlement.Projects)
            {
                if (p.Complete) continue;
                if (p.RethoughtOn != tick / _ticksPerDay && !Obtainable(settlement, p)) Rethink(settlement, p, rng, null, tick);
                if (Ready(settlement, p)) { project = p; break; }
            }
            if (project == null) return false;

            if (Walk(settlement, agent, grid, project)) return true;
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

            int laid = 0;
            while (laid < _perTick && project.Placed < order.Count)
            {
                int cell = order[project.Placed];
                Blueprint plan = project.Plan;
                int x = cell % plan.Width, z = (cell / plan.Width) % plan.Depth, y = cell / (plan.Width * plan.Depth);

                ushort type = project.Built.At(x, y, z);
                int material = _materials.IndexOf(_types.SymbolOf(type));
                if (material < 0) { project.Placed++; continue; }

                Int3 at = World(project, x, y, z);
                // Rethought today and still not to be had: a few voxels of a
                // hearth stone the land no longer holds do not hold up a house.
                // Use what the yard has, the same kind of thing if it can, and
                // failing that leave the voxel out and say so.
                if (settlement.Stock.Of(material) <= 0 && project.RethoughtOn == tick / _ticksPerDay
                    && !Obtainable(settlement, project))
                {
                    int instead = Substitute(settlement, material);
                    if (instead < 0)
                    {
                        project.Built.Note("left out a voxel of " + _materials[material].Name + ": none to be had");
                        project.Placed++;
                        continue;
                    }
                    type = _types.IdOf(_materials[instead].Voxel);
                    material = instead;
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

            ClearSite(settlement, project, tick);
            Groundworks(project, tick, annals);
        }

        /// <summary>
        /// Whatever grows or lies where the house will stand comes off first,
        /// into the yard (S2F): a house built in a wood starts as a clearing.
        /// </summary>
        void ClearSite(Settlement settlement, Project project, long tick)
        {
            if (_deposits == null) return;
            Int3 lo = World(project, 0, 0, 0);
            Int3 hi = World(project, project.Plan.Width - 1, 0, project.Plan.Depth - 1);
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
            settlement.ShelterCapacity += project.Plan.Capacity;

            RecordId done = annals.Write(tick, CompletedKind, settlement.Id, Centre(project), project.Begun,
                                         project.Built.TotalVoxels, project.Plan.Capacity,
                                         new[] { project.Intent.Kind.Id });

            // A family moves in (S2N): whoever had no roof, or the overflow of
            // whoever was most crowded.
            Households.MoveIn(settlement, project, project.Plan.Capacity, tick, annals, done);
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

                    int worldX = project.Site.ParcelX * ParcelGrid.Size + x;
                    int worldZ = project.Site.ParcelZ * ParcelGrid.Size + z;
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
            int originX = project.Site.ParcelX * ParcelGrid.Size - Grammar.Margin;
            int originZ = project.Site.ParcelZ * ParcelGrid.Size - Grammar.Margin;
            return new Int3(originX + x, project.Site.Ground + y, originZ + z);
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
