using Godless.Sim.Core;
using Godless.Sim.Harness;

namespace Godless.Sim.World
{
    /// <summary>
    /// First thing every tick: ground the god's hand changed since the last
    /// one reaches everything that plans on it — the parcel grid (heights,
    /// slope, land, the way to water), the constraint fields read off it, and
    /// the deposits a hill buried or a cut planed away. S07, S10.
    ///
    /// Without it a hill raised beside a village was a picture: the grid still
    /// read the flat ground under it a year later, so houses were sited, fields
    /// laid and paths walked as if it were not there.
    ///
    /// Once a tick rather than once a stroke, because a held brush lands a
    /// dozen strokes a second and nothing reads the grid between ticks. What
    /// it does not do yet is let water find its new way (the water table,
    /// rivers, which columns are sea): that is drainage run again over the live
    /// ground, and S2J's.
    /// </summary>
    public sealed class GroundSystem : ISimSystem
    {
        public static readonly Symbol SystemId = Symbol.For("system.ground");

        readonly ParcelGrid _grid;
        readonly ConstraintFields _fields;
        readonly BiomeTable _biomes;

        public GroundSystem(ParcelGrid grid, ConstraintFields fields, BiomeTable biomes)
        {
            _grid = grid;
            _fields = fields;
            _biomes = biomes;
        }

        public Symbol Id { get { return SystemId; } }

        /// <summary>The grid it keeps up to date, for the harness to check it against the ground.</summary>
        public ParcelGrid Grid { get { return _grid; } }

        public void Tick(SimWorld world)
        {
            int x0, z0, x1, z1;
            if (_grid == null || !_grid.RefreshPending(world.Voxels.Store, out x0, out z0, out x1, out z1)) return;
            if (_fields != null && world.Island != null && _biomes != null)
                _fields.Refresh(world.Island, _grid, _biomes, x0, z0, x1, z1);
            if (world.Island != null && world.Island.Deposits != null)
                world.Island.Deposits.Recount(world.Voxels.Store, x0, z0, x1, z1);
        }
    }
}
