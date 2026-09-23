using Godless.Sim.Core;
using Godless.Sim.Harness;

namespace Godless.Sim.World
{
    /// <summary>
    /// Once a day: what was cut grows back where it can, and the planning
    /// grid catches up with the ground that was dug away. S2F. Whoever keeps a
    /// catchment re-reads it themselves.
    ///
    /// Daily rather than per basket because nothing downstream reads finer:
    /// siting, the grid and the task board's demand all think in days.
    /// </summary>
    public sealed class DepositSystem : ISimSystem
    {
        public static readonly Symbol SystemId = Symbol.For("system.deposits");

        readonly ParcelGrid _grid;

        public DepositSystem(ParcelGrid grid = null) { _grid = grid; }

        public Symbol Id { get { return SystemId; } }

        public void Tick(SimWorld world)
        {
            if (!world.Clock.IsFirstTickOfDay || world.Island == null || world.Island.Deposits == null) return;
            DepositMap deposits = world.Island.Deposits;

            deposits.Regrow(world.Voxels, world.Clock.Tick);

            int x0, z0, x1, z1;
            if (_grid != null && deposits.TakeDirty(out x0, out z0, out x1, out z1))
                _grid.Refresh(world.Voxels.Store, x0 < 0 ? 0 : x0, z0 < 0 ? 0 : z0,
                              x1 >= Voxels.ChunkStore.SizeX ? Voxels.ChunkStore.SizeX - 1 : x1,
                              z1 >= Voxels.ChunkStore.SizeZ ? Voxels.ChunkStore.SizeZ - 1 : z1);

        }
    }
}
