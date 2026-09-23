using System.Collections.Generic;
using Godless.Sim.Annals;
using Godless.Sim.Core;
using Godless.Sim.Harness;
using Godless.Sim.Voxels;

namespace Godless.Sim.World
{
    /// <summary>
    /// The god's verbs on the ground: raise it and lower it. S07's brush, in
    /// the sim so the Editor, the harness and the tests all act the same way.
    /// v2 routes every god power through the command queue; this stays the
    /// voxel half of the terrain powers.
    ///
    /// The god acts in the present tick, never in a tick of its own. A stroke
    /// used to advance the clock before it wrote, so a held brush took a dozen
    /// ticks a second in which nobody ate, worked or built. Now every act lands
    /// on the tick the world is at, after that tick's systems, and whatever
    /// plans on the ground reads it at the start of the next
    /// (<see cref="GroundSystem"/>).
    ///
    /// The one exception is a present already written into history — the
    /// untouched island at tick 0, or a tick that ended in a snapshot — where
    /// a change could not be recorded. There the world first takes its next
    /// tick, whole, every system running, and the act lands in that one.
    ///
    /// Every voxel an act changes cites its god.* record (L3).
    /// </summary>
    public sealed class GodHand
    {
        public static readonly Symbol RaisedKind = Symbol.For("god.raised-ground");
        public static readonly Symbol LoweredKind = Symbol.For("god.lowered-ground");

        readonly SimWorld _world;
        readonly ParcelGrid _grid;
        readonly GroundPalette _ground;
        readonly ushort _stone, _water;

        /// <param name="grid">The planning grid the brush marks for refreshing; null in a world nobody plans on.</param>
        /// <param name="ground">What raised ground is made of by place and height; null raises bare granite.</param>
        public GodHand(SimWorld world, ParcelGrid grid, GroundPalette ground = null)
        {
            _world = world;
            _grid = grid;
            _ground = ground;
            Solid = TerrainBrush.SolidTable(world.Content, world.VoxelTypes);
            world.VoxelTypes.TryGetId(Symbol.For("voxel.granite"), out _stone);
            world.VoxelTypes.TryGetId(Symbol.For("voxel.water"), out _water);
        }

        /// <summary>Which voxel ids are ground, as the brush and the grid read it.</summary>
        public bool[] Solid { get; private set; }

        /// <summary>The water voxel, or air in content without one.</summary>
        public ushort Water { get { return _water; } }

        /// <summary>
        /// The tick an act lands on: the present, unless history has already
        /// closed it, in which case the world runs its next tick first.
        /// </summary>
        public long Present()
        {
            return _world.Present();
        }

        /// <summary>Raises a dome of ground round a column. Returns the stroke's record.</summary>
        public RecordId Raise(Int3 at, int radius, int strength, List<Int3> changed = null)
        {
            long tick = Present();
            RecordId stroke = _world.Annals.Write(tick, RaisedKind, Symbol.None, at, RecordId.None, radius, strength);
            TerrainBrush.Raise(_world.Voxels, Solid, at.X, at.Z, radius, strength, _stone, tick, stroke, changed, _ground);
            Touched(at, radius);
            return stroke;
        }

        /// <summary>Lowers the ground round a column; below the sea it fills with water. Returns the stroke's record.</summary>
        public RecordId Lower(Int3 at, int radius, int depth, List<Int3> changed = null)
        {
            long tick = Present();
            RecordId stroke = _world.Annals.Write(tick, LoweredKind, Symbol.None, at, RecordId.None, radius, depth);
            int sea = _world.Island != null ? _world.Island.SeaLevel : IslandMap.DefaultSeaLevel;
            TerrainBrush.Lower(_world.Voxels, Solid, at.X, at.Z, radius, depth, _water, sea, tick, stroke, changed);
            Touched(at, radius);
            return stroke;
        }

        void Touched(Int3 at, int radius)
        {
            if (_grid != null) _grid.MarkDirty(at.X - radius, at.Z - radius, at.X + radius, at.Z + radius);
        }
    }
}
