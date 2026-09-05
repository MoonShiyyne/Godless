using System.Collections.Generic;
using Godless.Sim.Annals;
using Godless.Sim.Content;
using Godless.Sim.Core;
using Godless.Sim.Deltas;
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
        public VoxelTypes VoxelTypes { get; private set; }

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

        public void Tick()
        {
            Clock.Advance();
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
