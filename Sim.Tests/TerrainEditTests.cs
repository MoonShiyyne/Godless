using System.Collections.Generic;
using Godless.Sim.Annals;
using Godless.Sim.Core;
using Godless.Sim.Deltas;
using Godless.Sim.Voxels;
using Godless.Sim.World;
using Xunit;

namespace Godless.Sim.Tests
{
    public class VoxelRaycastTests
    {
        static bool Solid(ushort v) { return v != VoxelTypes.AirId; }

        [Fact]
        public void StraightDownHitsTheTopOfTheGroundAndReportsTheFaceItEntered()
        {
            var store = new ChunkStore();
            for (int y = 0; y <= 40; y++) store.SetRaw(100, y, 100, 1);

            Assert.True(VoxelRaycast.Cast(store, 100.5, 150.5, 100.5, 0, -1, 0, 500, Solid, out var hit));
            Assert.Equal(new Int3(100, 40, 100), hit.Voxel);
            Assert.Equal(new Int3(0, 1, 0), hit.Normal);   // entered through the top face
        }

        [Fact]
        public void ACameraFarAboveTheIslandStillHitsIt()
        {
            var store = new ChunkStore();
            store.SetRaw(256, 50, 256, 1);

            // From 300 voxels up and well outside the box on two axes.
            double ox = 256.5 - 200, oy = 350, oz = 256.5 - 200;
            double dx = 200, dy = 350 - 50.5, dz = 200;
            Assert.True(VoxelRaycast.Cast(store, ox, oy, oz, dx, dy * -1, dz, 2000, Solid, out var hit));
            Assert.Equal(new Int3(256, 50, 256), hit.Voxel);
        }

        [Fact]
        public void ARayThatMissesEverythingReturnsFalse()
        {
            var store = new ChunkStore();
            store.SetRaw(10, 10, 10, 1);
            Assert.False(VoxelRaycast.Cast(store, 100.5, 100.5, 100.5, 0, 1, 0, 1000, Solid, out _));
            Assert.False(VoxelRaycast.Cast(store, -50, 10.5, 10.5, -1, 0, 0, 1000, Solid, out _));
        }

        [Fact]
        public void StartingInsideRockHitsItWithNoFace()
        {
            var store = new ChunkStore();
            store.SetRaw(20, 20, 20, 1);
            Assert.True(VoxelRaycast.Cast(store, 20.5, 20.5, 20.5, 1, 0, 0, 10, Solid, out var hit));
            Assert.Equal(new Int3(20, 20, 20), hit.Voxel);
            Assert.Equal(Int3.Zero, hit.Normal);
        }

        /// <summary>
        /// The traversal must visit every cell the ray passes through, in
        /// order. The oracle intersects the ray with every solid voxel's box
        /// and takes the nearest entry.
        ///
        /// Getting the oracle right took three attempts, and the history is
        /// the useful part. A march in 0.001-voxel steps skips cells a ray only
        /// clips at a corner. An exact slab test demands a positive-length
        /// entry, but a ray passing exactly along an edge touches all four
        /// cells that meet there at one instant, and floating point decides
        /// which one the traversal steps into first — on seed "test.raycast"
        /// ray 2 crosses x = 49 and y = 34 within 1e-14 of each other. So the
        /// boxes are inflated by 1e-9, edge touches count on both sides, and
        /// entry distances are compared rather than cells.
        /// </summary>
        [Fact]
        public void AgreesWithAnExactIntersectionOracleOnRandomRays()
        {
            var store = new ChunkStore();
            var solids = new List<Int3>();
            var rng = new RngStream(StableHash.OfString("test.raycast"));
            for (int i = 0; i < 4000; i++)
            {
                var v = new Int3(20 + rng.NextInt(30), 20 + rng.NextInt(30), 20 + rng.NextInt(30));
                if (store.Get(v) == VoxelTypes.AirId) { store.SetRaw(v, 1); solids.Add(v); }
            }

            int compared = 0;
            for (int i = 0; i < 400; i++)
            {
                double ox = 5 + rng.NextInt(1000) / 10.0, oy = 5 + rng.NextInt(1000) / 10.0, oz = 5 + rng.NextInt(1000) / 10.0;
                double tx = 20 + rng.NextInt(300) / 10.0, ty = 20 + rng.NextInt(300) / 10.0, tz = 20 + rng.NextInt(300) / 10.0;
                double dx = tx - ox, dy = ty - oy, dz = tz - oz;
                double len = System.Math.Sqrt(dx * dx + dy * dy + dz * dz);
                if (len == 0) continue;

                bool fast = VoxelRaycast.Cast(store, ox, oy, oz, dx, dy, dz, 150, Solid, out var hit);

                double best = double.PositiveInfinity;
                foreach (Int3 v in solids)
                {
                    double t = Enter(ox, oy, oz, dx / len, dy / len, dz / len, v);
                    if (t >= 0 && t <= 150 && t < best) best = t;
                }
                bool exact = best <= 150;

                Assert.Equal(exact, fast);
                if (!fast) continue;

                double mine = Enter(ox, oy, oz, dx / len, dy / len, dz / len, hit.Voxel);
                Assert.True(System.Math.Abs(mine - best) < 1e-6,
                    "traversal stopped at " + hit.Voxel + " entered at " + mine + ", nearest solid is entered at " + best);
                compared++;
            }
            Assert.True(compared > 100, "only " + compared + " rays hit anything");
        }

        const double Touch = 1e-9;

        /// <summary>
        /// Entry distance of a ray into a voxel box inflated by Touch, or -1
        /// if it misses. Inflated so a ray along an edge counts as touching it.
        /// </summary>
        static double Enter(double ox, double oy, double oz, double dx, double dy, double dz, Int3 v)
        {
            double tmin = 0, tmax = double.PositiveInfinity;
            if (!Slab(ox, dx, v.X, ref tmin, ref tmax)) return -1;
            if (!Slab(oy, dy, v.Y, ref tmin, ref tmax)) return -1;
            if (!Slab(oz, dz, v.Z, ref tmin, ref tmax)) return -1;
            return tmin;
        }

        static bool Slab(double o, double d, int lo, ref double tmin, ref double tmax)
        {
            if (d == 0) return o >= lo - Touch && o <= lo + 1 + Touch;
            double t0 = (lo - Touch - o) / d, t1 = (lo + 1 + Touch - o) / d;
            if (t0 > t1) { double s = t0; t0 = t1; t1 = s; }
            if (t0 > tmin) tmin = t0;
            if (t1 < tmax) tmax = t1;
            return tmin <= tmax;
        }
    }

    public class TerrainBrushTests
    {
        const ushort Stone = 1, Water = 2;
        static readonly bool[] SolidTable = { false, true, false };

        static VoxelWorld FlatWorld(int height)
        {
            var store = new ChunkStore();
            for (int x = 60; x < 140; x++)
                for (int z = 60; z < 140; z++)
                    for (int y = 0; y <= height; y++) store.SetRaw(x, y, z, Stone);
            var world = new VoxelWorld(store, new DeltaLog());
            world.EndTick(0);   // baseline
            return world;
        }

        [Fact]
        public void FalloffIsADomeThatReachesZeroAtTheRadius()
        {
            Assert.Equal(8, TerrainBrush.Falloff(0, 5, 8));
            Assert.Equal(0, TerrainBrush.Falloff(25, 5, 8));   // on the rim
            Assert.Equal(0, TerrainBrush.Falloff(26, 5, 8));   // outside
            Assert.True(TerrainBrush.Falloff(9, 5, 8) < 8 && TerrainBrush.Falloff(9, 5, 8) > 0);
        }

        [Fact]
        public void RaisingBuildsAHillCentredOnTheColumn()
        {
            VoxelWorld world = FlatWorld(40);
            var changed = new List<Int3>();
            TerrainBrush.Raise(world, SolidTable, 100, 100, 6, 10, Stone, 1, RecordId.None, changed);

            Assert.Equal(50, TerrainBrush.TopSolid(world.Store, SolidTable, 100, 100));   // peak
            int shoulder = TerrainBrush.TopSolid(world.Store, SolidTable, 104, 100);
            Assert.InRange(shoulder, 41, 49);                                              // slope
            Assert.Equal(40, TerrainBrush.TopSolid(world.Store, SolidTable, 107, 100));    // untouched
            Assert.Equal(changed.Count, world.Log.Count);
        }

        [Fact]
        public void EveryVoxelTheBrushTouchesNamesTheStrokeThatMovedIt()
        {
            var annals = new Annalist();
            VoxelWorld world = FlatWorld(40);
            RecordId stroke = annals.Write(1, Symbol.For("god.raised-ground"), RecordId.None);

            TerrainBrush.Raise(world, SolidTable, 100, 100, 4, 6, Stone, 1, stroke, null);
            foreach (VoxelDelta d in world.Log.All()) Assert.Equal(stroke, d.Cause);
        }

        [Fact]
        public void LoweringBelowTheWaterlineMakesABayNotAPit()
        {
            VoxelWorld world = FlatWorld(40);
            TerrainBrush.Lower(world, SolidTable, 100, 100, 5, 12, Water, 35, 1, RecordId.None, null);

            // Dug from 40 down to 29 at the centre: above 35 is air, 35 and below is water.
            Assert.Equal(VoxelTypes.AirId, world.Get(100, 38, 100));
            Assert.Equal(Water, world.Get(100, 35, 100));
            Assert.Equal(Water, world.Get(100, 29, 100));
            Assert.Equal(Stone, world.Get(100, 28, 100));
        }

        [Fact]
        public void TheIslandKeepsItsFloor()
        {
            VoxelWorld world = FlatWorld(3);
            TerrainBrush.Lower(world, SolidTable, 100, 100, 3, 50, Water, 0, 1, RecordId.None, null);
            Assert.Equal(Stone, world.Get(100, 0, 100));
        }

        /// <summary>
        /// S07's tell, on the sim side: raise a hill, then scrub back to
        /// before you raised it and find the ground as it was.
        /// </summary>
        [Fact]
        public void RaiseAHillThenScrubBackToBeforeYouRaisedIt()
        {
            VoxelWorld world = FlatWorld(40);
            ulong before = world.Store.Digest();

            TerrainBrush.Raise(world, SolidTable, 100, 100, 6, 10, Stone, 5, RecordId.None, null);
            world.EndTick(5);
            Assert.NotEqual(before, world.Store.Digest());

            Assert.Equal(before, world.AsOf(4).Digest());
        }
    }

    public class HistoryViewTests
    {
        static VoxelWorld RandomHistory(out long present)
        {
            var store = new ChunkStore();
            for (int x = 0; x < 96; x++)
                for (int z = 0; z < 96; z++)
                    for (int y = 0; y < 30; y++) store.SetRaw(x, y, z, 1);

            var world = new VoxelWorld(store, new DeltaLog(snapshotInterval: 7));
            world.EndTick(0);
            bool[] solid = { false, true, true, false };
            var rng = new RngStream(StableHash.OfString("test.history"));

            long tick = 0;
            for (int stroke = 0; stroke < 40; stroke++)
            {
                tick++;
                int cx = 8 + rng.NextInt(80), cz = 8 + rng.NextInt(80);
                if (rng.NextBool())
                    TerrainBrush.Raise(world, solid, cx, cz, 2 + rng.NextInt(6), 2 + rng.NextInt(8), (ushort)(1 + rng.NextInt(2)), tick, RecordId.None, null);
                else
                    TerrainBrush.Lower(world, solid, cx, cz, 2 + rng.NextInt(6), 2 + rng.NextInt(8), 3, 20, tick, RecordId.None, null);
                world.EndTick(tick);
            }
            present = tick;
            return world;
        }

        [Fact]
        public void SeekingAnywhereMatchesAFullReconstruction()
        {
            VoxelWorld world = RandomHistory(out long present);
            var view = new HistoryView(world.Store, world.Log, present);
            var rng = new RngStream(StableHash.OfString("test.seeks"));

            // Random jumps in both directions, including repeats and the ends.
            long[] targets = new long[30];
            for (int i = 0; i < targets.Length; i++) targets[i] = rng.NextInt((int)present + 1);
            targets[5] = 0; targets[12] = present; targets[13] = present; targets[20] = 0;

            foreach (long t in targets)
            {
                view.Seek(t, null);
                Assert.Equal(world.Log.Reconstruct(t).Digest(), view.Store.Digest());
            }
        }

        [Fact]
        public void ScrubbingNeverTouchesTheLiveWorld()
        {
            VoxelWorld world = RandomHistory(out long present);
            ulong live = world.Store.Digest();

            var view = new HistoryView(world.Store, world.Log, present);
            view.Seek(0, null);
            view.Seek(present / 2, null);

            Assert.Equal(live, world.Store.Digest());
            Assert.NotEqual(live, view.Store.Digest());
        }

        /// <summary>
        /// The reported voxels are what the renderer redraws, so they must
        /// cover every chunk that actually differs. A missed chunk is a stale
        /// piece of the past left on screen.
        /// </summary>
        [Fact]
        public void TheReportedChangesCoverEveryChunkThatDiffers()
        {
            VoxelWorld world = RandomHistory(out long present);
            var view = new HistoryView(world.Store, world.Log, present);

            ChunkStore before = view.Store.Clone();
            var changed = new List<Int3>();
            view.Seek(present / 3, changed);

            var reported = new HashSet<int>();
            foreach (Int3 v in changed) reported.Add(ChunkStore.ChunkIndex(v.X >> 5, v.Y >> 5, v.Z >> 5));

            for (int cy = 0; cy < ChunkStore.ChunksY; cy++)
                for (int cz = 0; cz < ChunkStore.ChunksZ; cz++)
                    for (int cx = 0; cx < ChunkStore.ChunksX; cx++)
                    {
                        bool differs = false;
                        for (int y = 0; y < 32 && !differs; y++)
                            for (int z = 0; z < 32 && !differs; z++)
                                for (int x = 0; x < 32 && !differs; x++)
                                {
                                    int wx = cx * 32 + x, wy = cy * 32 + y, wz = cz * 32 + z;
                                    if (before.Get(wx, wy, wz) != view.Store.Get(wx, wy, wz)) differs = true;
                                }
                        if (differs)
                            Assert.Contains(ChunkStore.ChunkIndex(cx, cy, cz), reported);
                    }
        }
    }
}
