using System.Collections.Generic;
using Godless.Sim.Annals;
using Godless.Sim.Content;
using Godless.Sim.Core;
using Godless.Sim.Deltas;
using Godless.Sim.Settlements;
using Godless.Sim.Voxels;

namespace Godless.Sim.Harness
{
    /// <summary>
    /// Something that runs every tick. Systems never reach for each other;
    /// they read and write the world, which is what keeps the tick order the
    /// only coupling between them.
    /// </summary>
    public interface ISimSystem
    {
        Symbol Id { get; }
        void Tick(SimWorld world);
    }

    /// <summary>
    /// One simulation: a clock, its RNG streams, its content, its voxels and
    /// its record, plus the systems that advance them.
    ///
    /// This is what the headless harness constructs a few hundred of. It has
    /// no Unity in it and no IO in it, so a thousand simulated years cost
    /// what the arithmetic costs.
    /// </summary>
    public sealed class SimWorld
    {
        readonly List<ISimSystem> _systems = new List<ISimSystem>();

        public SimWorld(ulong seed, ContentDatabase content, VoxelTypes voxelTypes,
                        long snapshotInterval = DeltaLog.DefaultSnapshotInterval)
        {
            Seed = seed;
            Content = content;
            VoxelTypes = voxelTypes;
            Clock = SimClock.Default();
            Streams = new StreamRegistry(seed);
            Annals = new Annalist();
            Voxels = new VoxelWorld(new ChunkStore(), new DeltaLog(snapshotInterval));
        }

        public ulong Seed { get; private set; }
        public SimClock Clock { get; private set; }
        public StreamRegistry Streams { get; private set; }
        public Annalist Annals { get; private set; }
        public VoxelWorld Voxels { get; private set; }
        public ContentDatabase Content { get; private set; }

        /// <summary>Things drawn in detail cells — furniture, rubble — and why each is there (S2R).</summary>
        public DetailLayer Details { get; } = new DetailLayer();
        public VoxelTypes VoxelTypes { get; private set; }

        /// <summary>
        /// The generated island, or null in a world that never made one. Set
        /// once at construction time by whoever generated it, so that nothing
        /// downstream has to re-derive terrain it could just read.
        /// </summary>
        public World.IslandMap Island { get; set; }

        /// <summary>
        /// The settlements on the island, in founding order. Stratum 1 has one.
        /// Systems iterate this list, so its order is tick order.
        /// </summary>
        public List<Settlement> Settlements { get; } = new List<Settlements.Settlement>();

        /// <summary>
        /// Part 22: a code mod voids the determinism guarantee, so the save
        /// records that it was present rather than letting a later bug report
        /// look like a sim failure.
        /// </summary>
        public bool ContainsCodeMod { get; set; }

        /// <summary>
        /// Systems tick in registration order, which is a design decision and
        /// therefore explicit. Nothing here discovers systems by reflection —
        /// discovery order is load order, and L2 has already been paid for
        /// once on that lesson.
        /// </summary>
        public SimWorld Add(ISimSystem system)
        {
            _systems.Add(system);
            return this;
        }

        public IReadOnlyList<ISimSystem> Systems { get { return _systems; } }

        /// <summary>
        /// Call once worldgen is finished: snapshots the world as it stands,
        /// which is the baseline every later reconstruction replays from. A
        /// world with no baseline can be played but not scrubbed back — the
        /// timeline would rebuild tick 0 from an empty map.
        /// </summary>
        public void BeginHistory()
        {
            Voxels.Log.Snapshot(Clock.Tick, Voxels.Store);
        }

        /// <summary>
        /// Ticks this world has run through its systems. The clock may only
        /// move here, so on a world begun at tick 0 the two agree; a tick the
        /// clock counted that no system ran is a tick of nobody eating,
        /// working or building (the old god's brush took a dozen a second).
        /// </summary>
        public long TicksRun { get; private set; }

        public void Tick()
        {
            Clock.Advance();
            TicksRun++;
            for (int i = 0; i < _systems.Count; i++) _systems[i].Tick(this);
            Voxels.EndTick(Clock.Tick);
        }

        public void RunYears(int years)
        {
            long target = Clock.TicksInYears(years);
            while (Clock.Tick < target) Tick();
        }
    }
}
