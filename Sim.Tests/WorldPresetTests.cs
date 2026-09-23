using System.Collections.Generic;
using Godless.Sim.Content;
using Godless.Sim.Core;
using Godless.Sim.Voxels;
using Godless.Sim.World;
using Xunit;

namespace Godless.Sim.Tests
{
    /// <summary>
    /// The maps, as content. S09.
    ///
    /// What these protect is not that the numbers are these numbers — a map is
    /// content and is meant to be edited — but that each map is still the kind
    /// of place its tell claims. A delta that grew a mountain, a desert that
    /// grew a forest, or a map whose biomes all offer the same two materials
    /// is a map that stopped being worth choosing, and none of those show up
    /// in a digest.
    /// </summary>
    public class WorldPresetTests
    {
        readonly Xunit.Abstractions.ITestOutputHelper _out;
        public WorldPresetTests(Xunit.Abstractions.ITestOutputHelper output) { _out = output; }

        static ContentDatabase RealContent()
        {
            var dir = new System.IO.DirectoryInfo(System.IO.Directory.GetCurrentDirectory());
            while (dir != null && !System.IO.Directory.Exists(System.IO.Path.Combine(dir.FullName, "Assets", "Content")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return ContentLoader.Load(new DirectoryContentSource(
                System.IO.Path.Combine(dir.FullName, "Assets", "Content"))).Database;
        }

        sealed class Made
        {
            public ChunkStore Store;
            public IslandMap Island;
            public WorldChoice Choice;
        }

        static readonly Dictionary<string, Made> Cache = new Dictionary<string, Made>();
        static readonly object Gate = new object();

        /// <summary>One island per map for the whole run: generation is a second each.</summary>
        static Made On(string map, ulong seed = 7UL)
        {
            string key = map + "@" + seed;
            lock (Gate)
            {
                if (Cache.TryGetValue(key, out Made hit)) return hit;
                Made made = Build(map, seed);
                Cache[key] = made;
                return made;
            }
        }

        static Made Build(string map, ulong seed)
        {
            ContentDatabase content = RealContent();
            WorldChoice choice = WorldChoice.Pick(content, map);
            VoxelTypes types = VoxelTypes.FromContent(content);
            var store = new ChunkStore();
            return new Made
            {
                Store = store,
                Choice = choice,
                Island = IslandGenerator.Generate(store, new StreamRegistry(seed), choice.Biomes, types, choice.Preset),
            };
        }

        // ── the documents ───────────────────────────────────────────────────

        [Fact]
        public void EveryMapContentDeclaresLoadsWithoutComplaint()
        {
            ContentDatabase content = RealContent();
            WorldTable maps = WorldTable.FromContent(content, BiomeTable.FromContent(content));

            Assert.Empty(maps.Problems);
            Assert.True(maps.Count >= 5, "the base game should offer several maps, not one");

            var names = new HashSet<string>();
            foreach (WorldPreset w in maps.All)
            {
                Assert.True(names.Add(w.Name), "two maps are both called " + w.Name);
                Assert.False(string.IsNullOrEmpty(w.Tell), w.Name + " has no tell (L4)");
                Assert.True(w.Relief > 0);
                Assert.True(w.Centres >= 1);
                Assert.True(w.Biomes.Count >= 2, w.Name + " admits fewer than two biomes");
            }
        }

        /// <summary>
        /// The limits the S09 and S0B invariants hold a map to are read from
        /// the map. Both failures this replaced came from a global number
        /// judging a place that had declared its own: the massif's four-deep
        /// tarns against Hydrology's three, the delta's flood plain against a
        /// hard-coded 0.9.
        /// </summary>
        [Fact]
        public void AMapsOwnLimitsComeFromItsDocument()
        {
            WorldTable maps = WorldTable.FromContent(RealContent());
            Assert.Equal(4, maps.Find("cold-massif").MaxLakeDepth);
            Assert.Equal(0.95, maps.Find("broad-delta").MaxDominantBiome);
            Assert.Equal(WorldPreset.Default().MaxDominantBiome, maps.Find("green-shore").MaxDominantBiome);
        }

        [Fact]
        public void MapsSortStablyAndAreFoundByName()
        {
            WorldTable a = WorldTable.FromContent(RealContent());
            WorldTable b = WorldTable.FromContent(RealContent());
            for (int i = 0; i < a.Count; i++) Assert.Equal(a[i].Name, b[i].Name);
            Assert.NotNull(a.Find("dry-reach"));
            Assert.Null(a.Find("no-such-map"));
        }

        [Fact]
        public void AnUnknownMapIsRefusedAndSaysWhatThereIs()
        {
            ContentException e = Assert.Throws<ContentException>(
                () => WorldChoice.Pick(RealContent(), "atlantis"));
            Assert.Contains("dry-reach", e.Message);
        }

        [Fact]
        public void NoMapMeansTheBuiltInIslandWithEveryBiome()
        {
            ContentDatabase content = RealContent();
            WorldChoice choice = WorldChoice.Pick(content, "");
            Assert.True(choice.Preset.IsDefault);
            Assert.Equal("", choice.Name);
            Assert.Equal(BiomeTable.FromContent(content).Count, choice.Biomes.Count);
        }

        [Fact]
        public void AMapAdmitsOnlyItsOwnBiomes()
        {
            WorldChoice dry = WorldChoice.Pick(RealContent(), "dry-reach");
            Assert.True(dry.Biomes.IndexOf(Symbol.For("biome.mesa")) >= 0);
            Assert.True(dry.Biomes.IndexOf(Symbol.For("biome.temperate")) < 0);

            Made made = On("dry-reach");
            for (int z = 0; z < ChunkStore.SizeZ; z += 37)
                for (int x = 0; x < ChunkStore.SizeX; x += 37)
                {
                    int b = made.Island.BiomeAt(x, z);
                    if (b < 0) continue;
                    Assert.True(b < dry.Biomes.Count);
                }
        }

        // ── determinism ─────────────────────────────────────────────────────

        [Fact]
        public void TheSameMapAndSeedGiveTheSameIsland()
        {
            Assert.Equal(Build("cold-massif", 11UL).Island.Digest(),
                         Build("cold-massif", 11UL).Island.Digest());
        }

        [Fact]
        public void DifferentMapsOnOneSeedAreDifferentWorlds()
        {
            var seen = new HashSet<ulong>();
            foreach (string map in new[] { "green-shore", "scattered-isles", "cold-massif", "broad-delta", "dry-reach" })
                Assert.True(seen.Add(On(map).Island.Digest()), map + " generated a world another map had already made");
        }

        // ── landform: each map is the kind of place it claims to be ─────────

        [Fact]
        public void TheDeltaIsFlatAndTheMassifIsNot()
        {
            int delta = ReliefOf(On("broad-delta"));
            int massif = ReliefOf(On("cold-massif"));
            _out.WriteLine("relief above sea: delta " + delta + ", massif " + massif);
            Assert.True(massif > delta * 3, "the massif should stand far above the delta");
        }

        [Fact]
        public void TheIslesAreSeveralSeparateLandMasses()
        {
            int isles = LandMasses(On("scattered-isles"));
            int shore = LandMasses(On("green-shore"));
            _out.WriteLine("land masses: isles " + isles + ", green shore " + shore);
            Assert.True(isles >= 4, "the scattered isles should be several islands, not one");
            Assert.True(isles > shore);
        }

        [Fact]
        public void TheDryReachRisesInSteps()
        {
            Made made = On("dry-reach");
            int step = made.Choice.Preset.Step;
            Assert.True(step > 1);

            // Terracing happens before erosion by water, so a river column may
            // sit a voxel off its bench. Everything else lands on one.
            int on = 0, off = 0;
            for (int z = 0; z < ChunkStore.SizeZ; z += 13)
                for (int x = 0; x < ChunkStore.SizeX; x += 13)
                {
                    int h = made.Island.HeightAt(x, z);
                    if (h <= made.Island.SeaLevel) continue;
                    if ((h - made.Island.SeaLevel) % step == 0) on++; else off++;
                }
            _out.WriteLine("columns on a bench: " + on + ", off it: " + off);
            Assert.True(on > off * 4, "a stepped map should put almost every column on a bench");
        }

        [Fact]
        public void EveryMapPutsLandInTheSeaAndSeaAroundTheLand()
        {
            foreach (string map in new[] { "green-shore", "scattered-isles", "cold-massif", "broad-delta", "dry-reach" })
            {
                Made made = On(map);
                int land = 0, total = 0;
                for (int z = 0; z < ChunkStore.SizeZ; z += 7)
                    for (int x = 0; x < ChunkStore.SizeX; x += 7)
                    {
                        total++;
                        if (made.Island.HeightAt(x, z) > made.Island.SeaLevel) land++;
                    }
                double fraction = land / (double)total;
                _out.WriteLine(map + ": " + (fraction * 100.0).ToString("0.0") + "% land");
                Assert.True(fraction > 0.01, map + " generated almost no land");
                Assert.True(fraction < 0.5, map + " left almost no sea");
            }
        }

        // ── materials: the point of having maps at all ──────────────────────

        [Fact]
        public void TheMapsDoNotAllBuildWithTheSameThings()
        {
            ContentDatabase content = RealContent();
            WorldTable maps = WorldTable.FromContent(content, BiomeTable.FromContent(content));

            var offered = new Dictionary<string, HashSet<string>>();
            foreach (WorldPreset w in maps.All)
            {
                var here = new HashSet<string>();
                foreach (Biome b in WorldChoice.Pick(content, w.Name).Biomes.All)
                    foreach (string m in b.MaterialNames) here.Add(m);
                offered[w.Name] = here;
                _out.WriteLine(w.Name + ": " + string.Join(", ", new List<string>(here).ToArray()));
                Assert.True(here.Count >= 3, w.Name + " offers almost nothing to build with");
            }

            // Every map must differ from every other in what it can supply.
            // Two maps with the same materials are one map with two names.
            foreach (var a in offered)
                foreach (var b in offered)
                {
                    if (a.Key == b.Key) continue;
                    Assert.False(a.Value.SetEquals(b.Value),
                        a.Key + " and " + b.Key + " offer exactly the same materials");
                }
        }

        [Fact]
        public void TheDryReachHasNoTimberAndTheDeltaHasNoStone()
        {
            ContentDatabase content = RealContent();
            Assert.DoesNotContain("timber", ClassesOn(content, "dry-reach"));
            Assert.Contains("earth", ClassesOn(content, "dry-reach"));
            Assert.DoesNotContain("stone", ClassesOn(content, "broad-delta"));
            Assert.Contains("timber", ClassesOn(content, "green-shore"));
        }

        static HashSet<string> ClassesOn(ContentDatabase content, string map)
        {
            var classes = new HashSet<string>();
            foreach (Biome b in WorldChoice.Pick(content, map).Biomes.All)
                foreach (string m in b.MaterialNames)
                    classes.Add(content.Get("voxel", m)["class"].AsString(""));
            return classes;
        }

        [Fact]
        public void NoMapLeavesAColumnWithoutABiome()
        {
            foreach (string map in new[] { "green-shore", "scattered-isles", "cold-massif", "broad-delta", "dry-reach" })
            {
                Made made = On(map);
                for (int z = 0; z < ChunkStore.SizeZ; z += 11)
                    for (int x = 0; x < ChunkStore.SizeX; x += 11)
                    {
                        if (made.Island.HeightAt(x, z) <= made.Island.SeaLevel) continue;
                        Assert.True(made.Island.BiomeAt(x, z) >= 0,
                            map + " left land at (" + x + ", " + z + ") with no biome");
                    }
            }
        }

        // ── helpers ─────────────────────────────────────────────────────────

        static int ReliefOf(Made made)
        {
            int top = 0;
            for (int z = 0; z < ChunkStore.SizeZ; z += 5)
                for (int x = 0; x < ChunkStore.SizeX; x += 5)
                {
                    int h = made.Island.HeightAt(x, z);
                    if (h > top) top = h;
                }
            return top - made.Island.SeaLevel;
        }

        /// <summary>Connected bodies of land big enough to settle, on a coarse grid.</summary>
        static int LandMasses(Made made)
        {
            const int Step = 8;
            int w = ChunkStore.SizeX / Step, d = ChunkStore.SizeZ / Step;
            var land = new bool[w * d];
            for (int j = 0; j < d; j++)
                for (int i = 0; i < w; i++)
                    land[j * w + i] = made.Island.HeightAt(i * Step, j * Step) > made.Island.SeaLevel;

            var seen = new bool[w * d];
            var queue = new Queue<int>();
            int[] dx = { 1, -1, 0, 0 }, dz = { 0, 0, 1, -1 };
            int masses = 0;

            for (int start = 0; start < land.Length; start++)
            {
                if (!land[start] || seen[start]) continue;
                seen[start] = true;
                queue.Enqueue(start);
                int size = 0;
                while (queue.Count > 0)
                {
                    int c = queue.Dequeue();
                    size++;
                    int cx = c % w, cz = c / w;
                    for (int k = 0; k < 4; k++)
                    {
                        int nx = cx + dx[k], nz = cz + dz[k];
                        if (nx < 0 || nz < 0 || nx >= w || nz >= d) continue;
                        int n = nz * w + nx;
                        if (!land[n] || seen[n]) continue;
                        seen[n] = true;
                        queue.Enqueue(n);
                    }
                }
                if (size >= 4) masses++;   // a single coarse cell is a rock, not an island
            }
            return masses;
        }
    }
}
