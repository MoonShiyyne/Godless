using System.Collections.Generic;
using Godless.Sim.Annals;
using Godless.Sim.Content;
using Godless.Sim.Core;
using Godless.Sim.Deltas;
using Godless.Sim.Voxels;
using Godless.Sim.World;
using Xunit;

namespace Godless.Sim.Tests
{
    /// <summary>
    /// What the god's brush is made of.
    ///
    /// The tell: push a hill up out of a beach and it stops being sand
    /// somewhere on the way up — the new ground wears whatever the biome
    /// table says belongs at that height. Before this the brush laid grey
    /// stone everywhere, which told the player nothing about the world.
    /// </summary>
    public class GroundPaletteTests
    {
        readonly Xunit.Abstractions.ITestOutputHelper _out;
        public GroundPaletteTests(Xunit.Abstractions.ITestOutputHelper output) { _out = output; }

        static ContentDatabase RealContent()
        {
            var dir = new System.IO.DirectoryInfo(System.IO.Directory.GetCurrentDirectory());
            while (dir != null && !System.IO.Directory.Exists(System.IO.Path.Combine(dir.FullName, "Assets", "Content")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return ContentLoader.Load(new DirectoryContentSource(
                System.IO.Path.Combine(dir.FullName, "Assets", "Content"))).Database;
        }

        sealed class Isle
        {
            public VoxelWorld World;
            public IslandMap Island;
            public GroundPalette Ground;
            public VoxelTypes Types;
            public bool[] Solid;
        }

        static Isle Generated;
        static readonly object Gate = new object();

        static Isle Island()
        {
            lock (Gate)
            {
                if (Generated != null) return Generated;

                ContentDatabase content = RealContent();
                WorldChoice choice = WorldChoice.Pick(content, "green-shore");
                VoxelTypes types = VoxelTypes.FromContent(content);
                var world = new VoxelWorld(new ChunkStore(), new DeltaLog());
                IslandMap island = IslandGenerator.Generate(world.Store, new StreamRegistry(7UL),
                                                            choice.Biomes, types, choice.Preset);
                Generated = new Isle
                {
                    World = world,
                    Island = island,
                    Types = types,
                    Ground = GroundPalette.From(island, choice.Biomes, types),
                    Solid = TerrainBrush.SolidTable(content, types),
                };
                return Generated;
            }
        }

        [Fact]
        public void RaisedGroundWearsTheBiomeItGrowsInto()
        {
            Isle isle = Island();

            // A low column with dry-enough air, raised far past the highland
            // threshold. What ends up on top must be that height's surface,
            // not the sand it started as.
            int x = -1, z = -1;
            for (int cz = 64; cz < ChunkStore.SizeZ - 64 && x < 0; cz += 3)
                for (int cx = 64; cx < ChunkStore.SizeX - 64; cx += 3)
                {
                    int h = isle.Island.HeightAt(cx, cz);
                    if (h <= isle.Island.SeaLevel || h > isle.Island.SeaLevel + 6) continue;
                    x = cx; z = cz; break;
                }
            Assert.True(x >= 0, "no low coastal column on this island");

            int before = TerrainBrush.TopSolid(isle.World.Store, isle.Solid, x, z);
            ushort wasOnTop = isle.World.Get(x, before, z);

            int lift = 100 - before;
            Assert.True(lift > 0);
            TerrainBrush.Raise(isle.World, isle.Solid, x, z, 3, lift, isle.Types.IdOf(Symbol.For("voxel.granite")),
                               1, RecordId.None, null, isle.Ground);

            int after = TerrainBrush.TopSolid(isle.World.Store, isle.Solid, x, z);
            ushort nowOnTop = isle.World.Get(x, after, z);
            _out.WriteLine("column (" + x + ", " + z + ") " + before + " -> " + after
                           + ": " + isle.Types.SymbolOf(wasOnTop) + " became " + isle.Types.SymbolOf(nowOnTop));

            Assert.True(after > before + 40);
            Assert.NotEqual(wasOnTop, nowOnTop);

            // Whatever content calls it, the top of a hill that high is the
            // surface of a biome whose window contains that height.
            Assert.Equal(SurfaceAt(after, x, z), isle.Types.SymbolOf(nowOnTop));
        }

        [Fact]
        public void TheRockUnderTheSurfaceIsNotTheSurface()
        {
            Isle isle = Island();
            int x = 300, z = 300;
            int top = TerrainBrush.TopSolid(isle.World.Store, isle.Solid, x, z);
            if (top <= isle.Island.SeaLevel) return;   // that column is sea on this seed; nothing to assert

            ushort surface = isle.Ground.At(x, top, z, top);
            ushort deep = isle.Ground.At(x, top - GroundPalette.SoilDepth - 1, z, top);
            _out.WriteLine(isle.Types.SymbolOf(surface) + " over " + isle.Types.SymbolOf(deep));

            // The two may legitimately match — highland is granite over
            // granite — so this asserts the shape of the rule, not a
            // particular biome's choice.
            Assert.Equal(surface, isle.Ground.At(x, top - 1, z, top));
            Assert.Equal(deep, isle.Ground.At(x, 5, z, top));
        }

        [Fact]
        public void GroundPushedUpFromUnderTheSeaIsSeabedUntilItIsLand()
        {
            Isle isle = Island();
            int sea = isle.Island.SeaLevel;

            // A column whose new top is still under water gets seabed; the
            // same column raised past the waterline gets a land surface.
            ushort under = isle.Ground.At(400, sea - 2, 400, sea - 1);
            ushort over = isle.Ground.At(400, sea + 9, 400, sea + 10);
            _out.WriteLine("under the sea: " + isle.Types.SymbolOf(under) + ", above it: " + isle.Types.SymbolOf(over));
            Assert.Equal(Symbol.For("voxel.sand"), isle.Types.SymbolOf(under));
            Assert.NotEqual(under, over);
        }

        [Fact]
        public void WithoutAPaletteTheBrushStillPlacesWhatItWasGiven()
        {
            // The old behaviour is still available, because a test that wants
            // one known voxel type should be able to ask for one.
            Isle isle = Island();
            ushort slate = isle.Types.IdOf(Symbol.For("voxel.slate"));
            var world = new VoxelWorld(new ChunkStore(), new DeltaLog());
            for (int y = 0; y <= 40; y++) world.Store.SetRaw(600, y, 600, slate);
            world.EndTick(0);

            TerrainBrush.Raise(world, isle.Solid, 600, 600, 3, 5, slate, 1, RecordId.None, null);
            Assert.Equal(slate, world.Get(600, 45, 600));
        }

        static Symbol SurfaceAt(int height, int x, int z)
        {
            WorldChoice choice = WorldChoice.Pick(RealContent(), "green-shore");
            Isle isle = Island();
            bool exact;
            Biome b = choice.Biomes.Select(height, isle.Island.MoistureAt(x, z), out exact);
            Assert.NotNull(b);
            return b.Surface;
        }
    }
}
