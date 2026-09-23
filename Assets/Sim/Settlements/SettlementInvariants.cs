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
                        if (p.Complete && p.Host == null && p.IsHome) houses++;
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

                // S2X: a heap holds something, and one drawn is drawn by a detail that exists.
                long badHeaps = 0;
                foreach (Settlement s in world.Settlements)
                    foreach (Pile p in s.Piles)
                        if (!(p.Amount >= 0.0) || (p.Detail >= 0 && world.Details.Get(p.Detail) == null)) badHeaps++;
                into.Record("heaps.broken", badHeaps);

                // S2I: every plot stands on ground its own farm claims, its soil a share
                // of full, and every plant drawn on it exists. S2H: food is never below nothing.
                long badPlots = 0, negativeFood = 0;
                foreach (Settlement s in world.Settlements)
                {
                    if (!(s.Food >= 0.0)) negativeFood++;
                    foreach (Farm f in s.Farms)
                        foreach (Plot p in f.Plots)
                        {
                            if (s.ClaimOn(p.ParcelX, p.ParcelZ) != f.Record || !(p.Fertility >= 0.0 && p.Fertility <= 1.0)) badPlots++;
                            foreach (int id in p.Details) if (world.Details.Get(id) == null) { badPlots++; break; }
                        }
                }
                into.Record("plots.broken", badPlots);

                // S2W: every drawn tick is whole — it runs from the start of the tick to its
                // end, each stretch picks up where the last left off, and it ends where they stand.
                long brokenDays = 0;
                foreach (Settlement s in world.Settlements)
                    foreach (Agent a in s.People)
                    {
                        IReadOnlyList<Leg> legs = a.Day.Legs;
                        if (legs.Count == 0) continue;
                        bool broken = legs[0].Start != 0.0 || legs[legs.Count - 1].End != 1.0
                                      || legs[legs.Count - 1].ToX != a.X || legs[legs.Count - 1].ToZ != a.Z;
                        for (int k = 1; k < legs.Count && !broken; k++)
                            if (legs[k].Start != legs[k - 1].End || legs[k].FromX != legs[k - 1].ToX || legs[k].FromZ != legs[k - 1].ToZ
                                || legs[k].End < legs[k].Start) broken = true;
                        if (broken) brokenDays++;
                    }
                into.Record("days.broken", brokenDays);

                // S2Z: the fire and its seats are drawn by details that exist, its stage is
                // one content has, and the ground kept round it is claimed by nothing else.
                long brokenCommons = 0;
                foreach (Settlement s in world.Settlements)
                {
                    Commons c = s.Commons;
                    if (c == null) continue;
                    if (c.Stage < 0 || c.FireDetail >= 0 && world.Details.Get(c.FireDetail) == null) brokenCommons++;
                    foreach (int seat in c.Seats) if (world.Details.Get(seat) == null) { brokenCommons++; break; }
                    if (!c.Kept.Exists) brokenCommons++;
                }
                into.Record("commons.broken", brokenCommons);

                // S2Y: no town has claimed ground inside another's border, and no two fires stand too close.
                long trespass = 0, crowdedFires = 0;
                foreach (Settlement s in world.Settlements)
                {
                    if (s.Borders == null) continue;
                    foreach (int parcel in s.Claims)
                        if (s.Borders.BelongsToAnother(s, parcel % World.ParcelGrid.Width, parcel / World.ParcelGrid.Width)
                            && s.Borders.Owner(parcel % World.ParcelGrid.Width, parcel / World.ParcelGrid.Width).ClaimOn(parcel % World.ParcelGrid.Width, parcel / World.ParcelGrid.Width).Exists)
                            trespass++;
                }
                TownRules towns = TownRules.FromContent(world.Content);
                for (int i = 0; i < world.Settlements.Count; i++)
                    for (int j = i + 1; j < world.Settlements.Count; j++)
                    {
                        Settlement a = world.Settlements[i], b = world.Settlements[j];
                        double dx = a.HearthParcelX - b.HearthParcelX, dz = a.HearthParcelZ - b.HearthParcelZ;
                        if (towns != null && dx * dx + dz * dz < (double)towns.NearestTownParcels * towns.NearestTownParcels) crowdedFires++;
                    }
                into.Record("towns.trespass", trespass);
                into.Record("towns.fires-too-close", crowdedFires);
                into.Record("food.negative", negativeFood);

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

                // Last, because it changes the world it measures.
                GodStroke(world, into);
            };
        }

        /// <summary>
        /// S07, S10: the god raises a hill beside the first fire of a lived-in
        /// world. The next tick's planning grid must read the ground the brush
        /// left, parcel for parcel, and the clock must have moved only by ticks
        /// the systems ran — the brush used to take a tick of its own per stroke.
        /// </summary>
        static void GodStroke(SimWorld world, RunResult into)
        {
            GroundSystem ground = null;
            foreach (ISimSystem system in world.Systems)
                if (system is GroundSystem g) { ground = g; break; }
            if (ground == null || ground.Grid == null || world.Settlements.Count == 0) return;

            ParcelGrid grid = ground.Grid;
            Settlement first = world.Settlements[0];
            int px = -1, pz = -1;
            int[] offsets = { 6, 0, -6, 0, 0, 6, 0, -6 };
            for (int i = 0; i < offsets.Length && px < 0; i += 2)
            {
                int x = first.HearthParcelX + offsets[i], z = first.HearthParcelZ + offsets[i + 1];
                if (ParcelGrid.InBounds(x, z) && grid.IsLand(x, z)) { px = x; pz = z; }
            }
            if (px < 0) return;

            var hand = new GodHand(world, grid);
            int cx = px * ParcelGrid.Size + ParcelGrid.Size / 2, cz = pz * ParcelGrid.Size + ParcelGrid.Size / 2;
            const int radius = 6;
            long clock = world.Clock.Tick, ran = world.TicksRun;
            hand.Raise(new Int3(cx, grid.GroundAt(cx, cz), cz), radius, 10);

            // What the grid should read, taken from the ground as the brush left it.
            int p0x = (cx - radius) / ParcelGrid.Size, p1x = (cx + radius) / ParcelGrid.Size;
            int p0z = (cz - radius) / ParcelGrid.Size, p1z = (cz + radius) / ParcelGrid.Size;
            var expected = new double[(p1x - p0x + 1) * (p1z - p0z + 1)];
            for (int z = p0z; z <= p1z; z++)
                for (int x = p0x; x <= p1x; x++)
                {
                    int sum = 0;
                    for (int dz = 0; dz < ParcelGrid.Size; dz++)
                        for (int dx = 0; dx < ParcelGrid.Size; dx++)
                            sum += world.Voxels.Store.TopMatching(x * ParcelGrid.Size + dx, z * ParcelGrid.Size + dz, hand.Solid);
                    expected[(z - p0z) * (p1x - p0x + 1) + x - p0x] = sum / (double)(ParcelGrid.Size * ParcelGrid.Size);
                }

            world.Tick();

            long stale = 0;
            for (int z = p0z; z <= p1z; z++)
                for (int x = p0x; x <= p1x; x++)
                    if (grid.Height[x, z] != expected[(z - p0z) * (p1x - p0x + 1) + x - p0x]) stale++;
            into.Record("god.stale-parcels", stale);
            into.Record("god.ticks-not-run", (world.Clock.Tick - clock) - (world.TicksRun - ran));
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
                // S07, S10. The god's hand reaches the ground people plan on, in
                // the tick after it lands, and takes no tick of its own.
                Invariant.PerRun("S10", "a hill the god raises is on the planning grid by the next tick",
                    run => run.Metric("god.stale-parcels") == 0.0),
                Invariant.PerRun("S07", "a stroke of the brush moves the clock by no tick the systems did not run",
                    run => run.Metric("god.ticks-not-run") == 0.0),

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

                // S2W. The tick drawn is the tick spent, from where they stood to where they stand.
                Invariant.PerRun("S2W", "every person's drawn tick is whole and ends where they stand",
                    run => run.Metric("days.broken") == 0.0),

                // S2Z. The first fire is kept, and what it has become is drawn.
                Invariant.PerRun("S2Z", "every town's commons is kept, and its fire and seats are drawn by details that exist",
                    run => run.Metric("commons.broken") == 0.0),

                // S2Y. Towns keep to their own ground and their own distance.
                Invariant.PerRun("S2Y", "no two towns claim the same parcel",
                    run => run.Metric("towns.trespass") == 0.0),
                Invariant.PerRun("S2Y", "no new town's fire stands nearer another than content allows",
                    run => run.Metric("towns.fires-too-close") == 0.0),

                // S2I. A field is its farm's ground, and what grows on it is drawn.
                Invariant.PerRun("S2I", "every plot lies on its own farm's claim, with soil between none and full, drawn by details that exist",
                    run => run.Metric("plots.broken") == 0.0),
                // S2H. Rot and eating never take a store below empty.
                Invariant.PerRun("S2H", "no settlement holds less than no food",
                    run => run.Metric("food.negative") == 0.0),

                // S2X. What waits to be carried is a real heap, drawn where it lies.
                Invariant.PerRun("S2X", "every heap holds something and is drawn by a detail that exists",
                    run => run.Metric("heaps.broken") == 0.0),

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
