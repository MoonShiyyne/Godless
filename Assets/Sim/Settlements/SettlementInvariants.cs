using System.Collections.Generic;
using Godless.Sim.Content;
using Godless.Sim.Core;
using Godless.Sim.Drives;
using Godless.Sim.Harness;
using Godless.Sim.Voxels;
using Godless.Sim.World;

namespace Godless.Sim.Settlements
{
    /// <summary>
    /// Stratum 2's claims about a living settlement, phrased over a batch of
    /// seeds (L7).
    ///
    /// The island batch has no people in it, and the systems stratum 2 adds
    /// are all about what people do to the land. So this batch founds one
    /// settlement per seed on a planted island — the same way the Editor and
    /// `sim settle` do — lets it live, and checks what it left behind.
    /// </summary>
    public static class SettlementInvariants
    {
        /// <summary>A planted island with one settlement on it, every stratum-1 and stratum-2 system running.</summary>
        /// <summary>How an island is made for a seed. Tests pass one that copies a shared island.</summary>
        public delegate IslandMap IslandMaker(ChunkStore into, StreamRegistry streams, BiomeTable biomes, VoxelTypes types,
                                              WorldPreset preset, FeatureTable features);

        public static WorldFactory Settled(ContentDatabase content, WorldChoice choice, int people = 20, IslandMaker make = null)
        {
            VoxelTypes types = VoxelTypes.FromContent(content);
            return seed =>
            {
                var world = new SimWorld(seed, content, types);
                world.Island = make != null
                    ? make(world.Voxels.Store, world.Streams, choice.Biomes, types, choice.Preset, choice.Features)
                    : IslandGenerator.Generate(world.Voxels.Store, world.Streams, choice.Biomes, types, choice.Preset, choice.Features);
                world.BeginHistory();

                ConstraintFields fields;
                ParcelGrid grid = Founding.Survey(world, content, choice.Biomes, out fields);
                int px, pz;
                if (!Founding.StandInSite(grid, world.Island, choice.Biomes, Symbol.None, out px, out pz)) return world;
                Founding.Begin(world, content, grid, choice.Biomes, "first", people, px, pz, null);
                Founding.AddSystems(world, content, grid, fields, choice.Biomes);
                return world;
            };
        }

        public static MetricCollector Collector()
        {
            return (world, into) =>
            {
                long people = 0, inSea = 0, outside = 0, houses = 0, parts = 0, orphaned = 0;
                IslandMap island = world.Island;
                foreach (Settlement s in world.Settlements)
                {
                    foreach (Agent a in s.People)
                    {
                        people++;
                        if (a.X < 0 || a.Z < 0 || a.X >= ChunkStore.SizeX || a.Z >= ChunkStore.SizeZ) { outside++; continue; }
                        if (island != null && !island.IsLand(a.X, a.Z)) inSea++;
                    }
                    foreach (Build.Project p in s.Projects)
                    {
                        if (p.Complete && p.Host == null) houses++;
                        if (p.Host != null)
                        {
                            parts++;
                            if (!p.Host.Complete || !s.Projects.Contains(p.Host) || !Contains(p.Host.Added, p)) orphaned++;
                        }
                    }
                }
                // S2S, S2T: nothing a building left behind hangs in the air.
                long floatingRubble = 0, floatingBeds = 0;
                foreach (Settlement s in world.Settlements)
                {
                    foreach (Build.RubbleCell c in s.Rubble)
                        if (world.Voxels.Get(c.At.X, c.At.Y - 1, c.At.Z) == VoxelTypes.AirId) floatingRubble++;
                    foreach (Build.Project p in s.Projects)
                        foreach (Build.Furnishing.Bed bed in p.Beds)
                            if (world.Voxels.Get(bed.Centre.X, bed.Centre.Y - 1, bed.Centre.Z) == VoxelTypes.AirId) floatingBeds++;
                }
                // S2V: every need a number a person can do something about, and
                // no need left at its worst for most of the settlement at once.
                long outOfRange = 0, worstPinned = 0;
                foreach (Settlement s in world.Settlements)
                {
                    if (s.People.Count == 0) continue;
                    int needs = s.People[0].Levels.Length;
                    for (int n = 0; n < needs; n++)
                    {
                        long pinned = 0;
                        foreach (Agent a in s.People)
                        {
                            double v = a.Levels[n];
                            if (!(v >= 0.0 && v <= 1.0)) outOfRange++;
                            if (v >= 0.999) pinned++;
                        }
                        long share = pinned * 100 / s.People.Count;
                        if (share > worstPinned) worstPinned = share;
                    }
                }
                into.Record("needs.out-of-range", outOfRange);
                into.Record("needs.worst-pinned-percent", worstPinned);

                into.Record("rubble.floating", floatingRubble);
                into.Record("beds.floating", floatingBeds);

                into.Record("settlements.count", world.Settlements.Count);
                into.Record("people.count", people);
                into.Record("people.in-sea", inSea);
                into.Record("people.outside", outside);
                into.Record("houses.standing", houses);
                into.Record("houses.additions", parts);
                into.Record("houses.orphaned-additions", orphaned);

                long overfull = 0, worked = 0;
                if (island != null && island.Deposits != null)
                    for (int f = 0; f < island.Deposits.Count; f++)
                    {
                        if (island.Deposits.Remaining(f) > island.Deposits.Initial(f)) overfull++;
                        if (island.Deposits.Remaining(f) < island.Deposits.Initial(f)) worked++;
                    }
                into.Record("deposits.overfull", overfull);
                into.Record("deposits.worked", worked);
            };
        }

        static bool Contains(IReadOnlyList<Build.Project> list, Build.Project p)
        {
            for (int i = 0; i < list.Count; i++) if (list[i] == p) return true;
            return false;
        }

        public static IReadOnlyList<Invariant> All()
        {
            return new List<Invariant>
            {
                // S2G. People go where their work and their needs put them, and
                // none of that is ever out in the sea or off the edge of the world.
                Invariant.PerRun("S2G", "nobody stands in the sea",
                    run => run.Metric("people.in-sea") == 0.0),
                Invariant.PerRun("S2G", "everybody is somewhere on the island",
                    run => run.Metric("people.outside") == 0.0),

                // S2V. Needs stay numbers between nothing and the worst, and people
                // act on them: no one need sits at its worst for most of a village.
                Invariant.PerRun("S2V", "every need level lies between 0 and 1",
                    run => run.Metric("needs.out-of-range") == 0.0),
                Invariant.PerRun("S2V", "no need is at its worst for most of a settlement",
                    run => run.Metric("needs.worst-pinned-percent") <= 50.0),

                // S2T. A fallen building's rubble lies on something; S2S, a bed stands on a floor.
                Invariant.PerRun("S2T", "every heap of rubble rests on something",
                    run => run.Metric("rubble.floating") == 0.0),
                Invariant.PerRun("S2S", "every bed stands on a floor",
                    run => run.Metric("beds.floating") == 0.0),

                // S2P. A wing or a storey is always part of a house that stands.
                Invariant.PerRun("S2P", "every wing and storey belongs to a standing house",
                    run => run.Metric("houses.orphaned-additions") == 0.0),

                // S2F. Growing back restores what was cut, never more.
                Invariant.PerRun("S2F", "no deposit holds more than it grew with",
                    run => run.Metric("deposits.overfull") == 0.0),

                // S2F. A settlement that lived and gathered has visibly used the land.
                Invariant.PerRun("S2F", "a settlement that lived has worked the land around it",
                    run => run.Metric("settlements.count") == 0.0 || run.Metric("deposits.worked") > 0.0),
            };
        }
    }
}
