using System.Collections.Generic;
using Godless.Sim.Content;
using Godless.Sim.Harness;
using Godless.Sim.Voxels;
using Godless.Sim.World;

namespace Godless.Sim.Life
{
    /// <summary>
    /// Life's invariants for the batch harness (v2 M1, law L7), and the world
    /// they run on: a real island with every system the Editor runs.
    /// </summary>
    public static class LifeInvariants
    {
        public static WorldFactory Living(ContentDatabase content, WorldChoice choice)
        {
            VoxelTypes types = VoxelTypes.FromContent(content);
            return seed =>
            {
                var world = new SimWorld(seed, content, types);
                world.Island = IslandGenerator.Generate(world.Voxels.Store, world.Streams, choice.Biomes, types, choice.Preset, choice.Features);
                world.BeginHistory();
                ConstraintFields fields;
                ParcelGrid grid = Survey.Of(world, content, choice.Biomes, out fields);
                Genesis.AddSystems(world, content, grid, fields, choice.Biomes);
                return world;
            };
        }

        public static MetricCollector Collector()
        {
            return (world, into) =>
            {
                Living life = world.Life;
                if (life == null) return;
                Creatures c = life.Creatures;
                long outside = 0, badLevels = 0, alive = 0, strays = 0, leaderless = 0;
                var members = new Dictionary<int, int>();
                for (int i = 0; i < c.Length; i++)
                {
                    if (!c.Alive[i]) continue;
                    alive++;
                    if (c.X[i] < 0 || c.Z[i] < 0 || c.X[i] >= ChunkStore.SizeX || c.Z[i] >= ChunkStore.SizeZ
                        || double.IsNaN(c.X[i]) || double.IsNaN(c.Z[i])) outside++;
                    if (!(c.Hunger[i] >= 0.0 && c.Hunger[i] <= 1.0) || double.IsNaN(c.Health[i])) badLevels++;
                    if (c.Group[i] >= 0)
                    {
                        int n; members.TryGetValue(c.Group[i], out n); members[c.Group[i]] = n + 1;
                        Group g = life.GroupOf(c.Group[i]);
                        if (g == null || g.Gone || g.Species != c.Species[i]) strays++;
                    }
                }
                long miscounted = alive == c.Count ? 0 : 1;
                foreach (Group g in life.Groups)
                {
                    int n; members.TryGetValue(g.Number, out n);
                    if (n != g.Members) miscounted++;
                    if (g.Gone) continue;
                    // A live group is led by one of its own, and a herd of one is no herd.
                    if (g.Members > 0 && (g.Leader < 0 || !c.Alive[g.Leader] || c.Group[g.Leader] != g.Number)) leaderless++;
                    if (!g.IsBand && g.Members < 2) leaderless++;
                }
                into.Record("life.outside", outside);
                into.Record("life.bad-levels", badLevels);
                into.Record("life.miscounted", miscounted);
                into.Record("life.strays", strays);
                into.Record("life.leaderless", leaderless);
                into.Record("life.alive", alive);
                int native = 0, living = 0;
                for (int s = 0; s < life.Species.Count && s < life.Native.Length; s++)
                {
                    if (!life.Native[s] || life.Species[s].Person) continue;   // people may die out; that is history, not a bug
                    native++;
                    if (life.CountOf(s) > 0) living++;
                }
                into.Record("life.species-alive", living);
                into.Record("life.species", native);
            };
        }

        public static IReadOnlyList<Invariant> All()
        {
            return new List<Invariant>
            {
                Invariant.PerRun("M1", "every creature stands inside the world, at a real place",
                    run => run.Metric("life.outside") == 0.0),
                Invariant.PerRun("M1", "every hunger and health is a number, hunger between 0 and 1",
                    run => run.Metric("life.bad-levels") == 0.0),
                Invariant.PerRun("M1", "the living are counted right, and every group knows its members",
                    run => run.Metric("life.miscounted") == 0.0),
                Invariant.PerRun("M1", "every creature in a group is in a live group of its own kind",
                    run => run.Metric("life.strays") == 0.0),
                Invariant.PerRun("M1", "every live group is led by one of its members, and no herd is one animal",
                    run => run.Metric("life.leaderless") == 0.0),
                Invariant.PerRun("M1", "the wild is never empty: every animal native to the map is alive somewhere",
                    run => run.Metric("life.species-alive") == run.Metric("life.species")),
            };
        }
    }
}
