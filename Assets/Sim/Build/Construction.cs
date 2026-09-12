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
        public Construction(VoxelWorld voxels, MaterialTable materials, VoxelTypes types,
                            TileSet tiles, Palette palette, int voxelsPerTick = 4)
        {
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
                if (!p.Complete && Ready(settlement, p)) { project = p; break; }
            if (project == null) return false;

            if (Walk(agent, grid, project)) return true;
            Lay(settlement, project, tick, annals, rng);
            return true;
        }

        /// <summary>A step along the way there, if the builder is not there yet.</summary>
        static bool Walk(Agent agent, ParcelGrid grid, Project project)
        {
            int goalX = project.Site.ParcelX, goalZ = project.Site.ParcelZ;
            if (agent.ParcelX == goalX && agent.ParcelZ == goalZ) return false;

            List<int> path = ParcelPath.Find(grid, agent.ParcelX, agent.ParcelZ, goalX, goalZ);
            if (path.Count < 2)
            {
                // No way round: step straight at it rather than stand still.
                agent.ParcelX += System.Math.Sign(goalX - agent.ParcelX);
                agent.ParcelZ += System.Math.Sign(goalZ - agent.ParcelZ);
                return true;
            }
            int next = path[1];
            agent.ParcelX = next % ParcelGrid.Width;
            agent.ParcelZ = next / ParcelGrid.Width;
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
                if (!settlement.Stock.TryTake(material, 1, tick, settlement.Id, at, annals, project.Begun))
                {
                    // The wall stops at the height its material reached. A plan
                    // made of something the settlement never had is remade now,
                    // from what is actually in the yard.
                    if (project.Built.Compromises.Count > 0 && project.Placed == 0)
                        project.Built = Realizer.Realize(project.Plan, _tiles, _materials, settlement.Stock,
                                                         _palette, _types, rng, settlement.Catchment);
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

            Groundworks(project, tick, annals);
        }

        void Finish(Settlement settlement, Project project, long tick, Annalist annals)
        {
            project.Complete = true;
            settlement.ShelterCapacity += project.Plan.Capacity;

            RecordId done = annals.Write(tick, CompletedKind, settlement.Id, Centre(project), project.Begun,
                                         project.Built.TotalVoxels, project.Plan.Capacity,
                                         new[] { project.Intent.Kind.Id });
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
