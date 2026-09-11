using System.Collections.Generic;
using System.IO;
using Godless.Sim.Content;
using Godless.Sim.Core;
using Godless.Sim.Voxels;
using Godless.Sim.World;
using Xunit;

namespace Godless.Sim.Tests
{
    public class PathTests
    {
        const ushort Stone = 1, Water = 2;
        static readonly bool[] Solid = { false, true, false };
        static readonly bool[] Wet = { false, false, true };

        /// <summary>A plain at height h, with whatever the caller draws on it.</summary>
        static ChunkStore Plain(int h, System.Action<ChunkStore> draw = null)
        {
            var store = new ChunkStore();
            for (int x = 0; x < 256; x++)
                for (int z = 0; z < 256; z++)
                    for (int y = 0; y <= h; y++) store.SetRaw(x, y, z, Stone);
            draw?.Invoke(store);
            return store;
        }

        static ParcelGrid Grid(ChunkStore store) { return ParcelGrid.Build(store, Solid, Wet); }

        [Fact]
        public void OnOpenGroundThePathIsTheStraightOne()
        {
            ParcelGrid grid = Grid(Plain(20));
            List<int> path = ParcelPath.Find(grid, 4, 4, 20, 4);
            Assert.Equal(17, path.Count);
            Assert.Equal(4 * ParcelGrid.Width + 4, path[0]);
            Assert.Equal(4 * ParcelGrid.Width + 20, path[path.Count - 1]);
        }

        /// <summary>S13's tell: people go round the water rather than through it.</summary>
        [Fact]
        public void PeopleGoRoundTheWater()
        {
            // A lake across the direct way, open ground above and below it.
            ChunkStore store = Plain(20, s =>
            {
                for (int x = 40; x < 60; x++)
                    for (int z = 8; z < 40; z++) s.SetRaw(x, 21, z, Water);
            });
            ParcelGrid grid = Grid(store);

            List<int> path = ParcelPath.Find(grid, 4, 4, 20, 4);
            Assert.NotEmpty(path);
            foreach (int cell in path)
            {
                int px = cell % ParcelGrid.Width, pz = cell / ParcelGrid.Width;
                Assert.Equal(0, grid.WetColumns(px, pz));
            }
            // Diagonals mean the way round can have as many steps as the way
            // through, so it is the cost that tells, not the count.
            int round = ParcelPath.CostOf(grid, path);
            int straight = ParcelPath.CostOf(Grid(Plain(20)), ParcelPath.Find(Grid(Plain(20)), 4, 4, 20, 4));
            Assert.True(round > straight, "round " + round + " against straight " + straight);
        }

        /// <summary>And up the gentle ground rather than over the ridge.</summary>
        [Fact]
        public void PeopleWalkRoundARidgeRatherThanOverIt()
        {
            ChunkStore store = Plain(20, s =>
            {
                // A wall of rock 10 voxels high across the way, with a gap in it.
                for (int x = 40; x < 44; x++)
                    for (int z = 0; z < 40; z++)
                    {
                        if (z >= 24 && z < 28) continue;   // the pass
                        for (int y = 21; y <= 30; y++) s.SetRaw(x, y, z, Stone);
                    }
            });
            ParcelGrid grid = Grid(store);

            List<int> path = ParcelPath.Find(grid, 2, 2, 20, 2);
            Assert.NotEmpty(path);
            bool throughThePass = false;
            foreach (int cell in path)
            {
                int px = cell % ParcelGrid.Width, pz = cell / ParcelGrid.Width;
                if (px >= 10 && px <= 10 && pz >= 6 && pz <= 6) throughThePass = true;
                Assert.True(grid.Slope[px, pz] < ParcelPath.Impassable, "walked over a cliff at " + px + "," + pz);
            }
            Assert.True(throughThePass, "did not use the pass");
        }

        [Fact]
        public void NoWayMeansNoPath()
        {
            ChunkStore store = Plain(20, s =>
            {
                for (int x = 40; x < 44; x++)
                    for (int z = 0; z < 256; z++)
                        for (int y = 21; y <= 40; y++) s.SetRaw(x, y, z, Stone);
            });
            ParcelGrid grid = Grid(store);
            Assert.Empty(ParcelPath.Find(grid, 2, 2, 20, 2));
        }

        [Fact]
        public void APathCanEndAtTheWatersEdge()
        {
            ChunkStore store = Plain(20, s =>
            {
                for (int x = 80; x < 120; x++)
                    for (int z = 0; z < 120; z++) s.SetRaw(x, 21, z, Water);
            });
            ParcelGrid grid = Grid(store);
            // Parcel 20 is the first one the water covers; a walker can reach
            // its edge but not stand a parcel deeper in.
            List<int> path = ParcelPath.Find(grid, 4, 4, 20, 4);
            Assert.NotEmpty(path);
            Assert.Equal(4 * ParcelGrid.Width + 20, path[path.Count - 1]);
            Assert.Empty(ParcelPath.Find(grid, 4, 4, 22, 4));
        }

        [Fact]
        public void TheSameTwoPlacesAlwaysGiveTheSameWay()
        {
            ParcelGrid grid = Grid(Plain(20, s =>
            {
                for (int x = 30; x < 50; x++)
                    for (int z = 10; z < 30; z++) s.SetRaw(x, 21, z, Water);
            }));
            Assert.Equal(ParcelPath.Find(grid, 2, 2, 24, 12), ParcelPath.Find(grid, 2, 2, 24, 12));
            Assert.True(ParcelPath.CostOf(grid, ParcelPath.Find(grid, 2, 2, 24, 12)) > 0);
        }
    }
}
