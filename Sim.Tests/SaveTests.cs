using System.IO;
using Godless.Sim.Annals;
using Godless.Sim.Content;
using Godless.Sim.Core;
using Godless.Sim.Harness;
using Godless.Sim.Save;
using Godless.Sim.Voxels;
using Godless.Sim.World;
using Xunit;

namespace Godless.Sim.Tests
{
    public class SaveTests
    {
        readonly Xunit.Abstractions.ITestOutputHelper _out;
        public SaveTests(Xunit.Abstractions.ITestOutputHelper output) { _out = output; }

        static ContentDatabase Shipped()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Assets", "Content")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return ContentLoader.Load(new DirectoryContentSource(
                Path.Combine(dir.FullName, "Assets", "Content"))).Database;
        }

        const int BuildHeight = 140;

        /// <summary>A world with an island and a bit of history on top of it.</summary>
        static SimWorld Played(ulong seed, ContentDatabase content, bool withIsland = true)
        {
            BiomeTable biomes = BiomeTable.FromContent(content);
            VoxelTypes types = VoxelTypes.FromContent(content);

            var world = new SimWorld(seed, content, types);
            if (withIsland)
                world.Island = TestIslands.Generate(world.Voxels.Store, world.Streams, biomes, types);

            ushort granite = types.IdOf(Symbol.For("voxel.granite"));
            ushort sand = types.IdOf(Symbol.For("voxel.sand"));

            // Something happened here, and it says why.
            //
            // Built at y = 140, above the generator's tallest possible ground
            // (18 + 0.92 * 118 = 127). Writing into rock that is already the
            // same material changes nothing and correctly logs nothing, so a
            // test that builds underground silently tests an empty log.
            for (int year = 1; year <= 6; year++)
            {
                world.RunYears(year);
                RecordId cause = world.Annals.Write(world.Clock.Tick, Symbol.For("god.raised-ridge"),
                                                    Symbol.None, new Int3(200 + year, BuildHeight, 200), RecordId.None, year);
                for (int dy = 0; dy < 5; dy++)
                    world.Voxels.Set(new Int3(200 + year, BuildHeight + dy, 200),
                                     (dy % 2 == 0) ? granite : sand, world.Clock.Tick, cause);
            }
            return world;
        }

        static SimWorld RoundTrip(SimWorld original, ContentDatabase content, out long bytes)
        {
            using var buffer = new MemoryStream();
            SaveGame.Write(buffer, original, original.ContainsCodeMod);
            bytes = buffer.Length;
            buffer.Position = 0;
            return SaveGame.Read(buffer, content, BiomeTable.FromContent(content), VoxelTypes.FromContent(content));
        }

        /// <summary>
        /// S0C's tell: quit mid-scenario, reopen, and the world is identical
        /// down to the annals.
        /// </summary>
        [Fact]
        public void AWorldSurvivesARoundTripDownToTheAnnals()
        {
            ContentDatabase content = Shipped();
            SimWorld original = Played(11, content);

            SimWorld restored = RoundTrip(original, content, out long bytes);

            Assert.Equal(original.Seed, restored.Seed);
            Assert.Equal(original.Clock.Tick, restored.Clock.Tick);
            Assert.Equal(original.Voxels.Store.Digest(), restored.Voxels.Store.Digest());
            Assert.Equal(original.Annals.Digest(), restored.Annals.Digest());
            Assert.Equal(original.Voxels.Log.Digest(), restored.Voxels.Log.Digest());
            Assert.Equal(original.Annals.Count, restored.Annals.Count);

            _out.WriteLine("save: " + bytes + " bytes for " + original.Annals.Count
                           + " records and " + original.Voxels.Log.Count + " deltas");
        }

        /// <summary>
        /// The save is proportional to the history, not to the map. A 3.8 MB
        /// island round-trips in a few kilobytes because the island is a
        /// function of its seed.
        /// </summary>
        [Fact]
        public void TheSaveIsProportionalToHistoryNotToTheMap()
        {
            ContentDatabase content = Shipped();
            SimWorld world = Played(11, content);

            RoundTrip(world, content, out long bytes);
            long worldBytes = world.Voxels.Store.MemoryBytes;

            _out.WriteLine("save " + bytes + " bytes against a " + worldBytes / 1024 + " KB world");
            Assert.True(bytes * 100 < worldBytes, "a save should be orders of magnitude under the world it restores");
        }

        [Fact]
        public void ProvenanceSurvivesTheRoundTrip()
        {
            ContentDatabase content = Shipped();
            SimWorld restored = RoundTrip(Played(11, content), content, out _);

            var at = new Int3(203, BuildHeight, 200);
            var history = restored.Voxels.Log.HistoryOf(at);
            Assert.NotEmpty(history);

            AnnalRecord cause = restored.Annals.Get(history[0].Cause);
            Assert.NotNull(cause);
            Assert.Equal("god.raised-ridge", cause.Kind.ToString());
        }

        /// <summary>
        /// The reason the save carries a voxel type table at all: runtime ids
        /// shift when content changes, and a save that stored bare ids would
        /// silently turn every wall into a different material.
        /// </summary>
        [Fact]
        public void VoxelIdsAreRemappedThroughSymbols_NotTrusted()
        {
            ContentDatabase content = Shipped();
            SimWorld original = Played(11, content, withIsland: false);

            using var buffer = new MemoryStream();
            SaveGame.Write(buffer, original);
            buffer.Position = 0;

            // A mod adds a material that sorts before the others, shifting
            // every id that follows it.
            var wider = new System.Collections.Generic.List<Symbol>();
            foreach (string id in content.Ids("voxel")) wider.Add(Symbol.For("voxel." + id));
            wider.Add(Symbol.For("voxel.aardvark-marble"));
            VoxelTypes shifted = VoxelTypes.Build(wider);

            Assert.NotEqual(VoxelTypes.FromContent(content).IdOf(Symbol.For("voxel.granite")),
                            shifted.IdOf(Symbol.For("voxel.granite")));

            // The world digest is computed over ids, so it legitimately
            // differs — what must survive is which material each voxel IS.
            SaveException thrown = Record.Exception(() =>
                SaveGame.Read(buffer, content, BiomeTable.FromContent(content), shifted)) as SaveException;

            Assert.NotNull(thrown);
            Assert.Contains("does not match", thrown.Message);
        }

        [Fact]
        public void ASaveUsingAVoxelTypeTheContentNoLongerDeclaresRefusesClearly()
        {
            ContentDatabase content = Shipped();
            SimWorld original = Played(11, content, withIsland: false);

            using var buffer = new MemoryStream();
            SaveGame.Write(buffer, original);
            buffer.Position = 0;

            VoxelTypes narrowed = VoxelTypes.Build(new[] { Symbol.For("voxel.granite") });

            var e = Assert.Throws<SaveException>(() =>
                SaveGame.Read(buffer, content, BiomeTable.FromContent(content), narrowed));
            Assert.Contains("does not declare", e.Message);
        }

        [Fact]
        public void ChangedContentIsRefusedRatherThanQuietlyLoaded()
        {
            ContentDatabase content = Shipped();
            SimWorld original = Played(11, content, withIsland: false);

            using var buffer = new MemoryStream();
            SaveGame.Write(buffer, original);
            buffer.Position = 0;

            // Same voxels, different content: one extra biome document.
            var src = new MemoryContentSource().Add("base", "mod.json", "{}");
            foreach (string id in content.Ids("voxel"))
                src.Add("base", "voxels/" + id + ".json", content.Get("voxel", id).ToString());
            src.Add("base", "biomes/invented.json", "{\"type\":\"biome\",\"id\":\"invented\"}");
            ContentDatabase changed = ContentLoader.Load(src).Database;

            var e = Assert.Throws<SaveException>(() =>
                SaveGame.Read(buffer, changed, BiomeTable.FromContent(changed), VoxelTypes.FromContent(changed)));
            Assert.Contains("content has changed", e.Message);
        }

        [Fact]
        public void GarbageAndWrongVersionsAreRefused()
        {
            ContentDatabase content = Shipped();
            BiomeTable biomes = BiomeTable.FromContent(content);
            VoxelTypes types = VoxelTypes.FromContent(content);

            using var junk = new MemoryStream(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
            Assert.Throws<SaveException>(() => SaveGame.Read(junk, content, biomes, types));
        }

        [Fact]
        public void ACodeModIsRecordedInTheSave()
        {
            ContentDatabase content = Shipped();
            SimWorld original = Played(11, content, withIsland: false);
            original.ContainsCodeMod = true;

            Assert.True(RoundTrip(original, content, out _).ContainsCodeMod);
        }

        /// <summary>The shipped content with one document taken out, as an uninstalled mod would leave it.</summary>
        static ContentDatabase ContentWithout(string type, string id)
        {
            ContentDatabase content = Shipped();
            var src = new MemoryContentSource().Add("base", "mod.json", "{}");
            foreach (string t in content.Types())
                foreach (string each in content.Ids(t))
                {
                    if (t == type && each == id) continue;
                    src.Add("base", t + "s/" + each + ".json", content.Get(t, each).ToString());
                }
            return ContentLoader.Load(src).Database;
        }

        [Fact]
        public void ASaveRemembersWhichMapItWasPlayedOn()
        {
            ContentDatabase content = Shipped();
            WorldChoice choice = WorldChoice.Pick(content, "cold-massif");
            VoxelTypes types = VoxelTypes.FromContent(content);

            var world = new SimWorld(4242UL, content, types);
            world.Island = TestIslands.Generate(world.Voxels.Store, world.Streams,
                                                    choice.Biomes, types, choice.Preset);
            world.BeginHistory();
            world.RunYears(1);

            using var buffer = new MemoryStream();
            SaveGame.Write(buffer, world, world.ContainsCodeMod);
            buffer.Position = 0;

            // The caller hands in the whole biome table, as every caller does.
            // The save has to correct that from the map it names, or the
            // island comes back renumbered and the digests disagree.
            SimWorld loaded = SaveGame.Read(buffer, content, BiomeTable.FromContent(content), types);

            Assert.Equal(world.Island.Digest(), loaded.Island.Digest());
            Assert.Equal(world.Voxels.Store.Digest(), loaded.Voxels.Store.Digest());
            Assert.Equal("cold-massif", loaded.Island.Map.Name);
            Assert.Equal(choice.Preset.SeaLevel, loaded.Island.SeaLevel);
        }

        [Fact]
        public void ASaveFromAMissingMapSaysSoRatherThanLoadingSomethingElse()
        {
            ContentDatabase content = Shipped();
            WorldChoice choice = WorldChoice.Pick(content, "dry-reach");
            VoxelTypes types = VoxelTypes.FromContent(content);

            var world = new SimWorld(9UL, content, types);
            world.Island = TestIslands.Generate(world.Voxels.Store, world.Streams,
                                                    choice.Biomes, types, choice.Preset);
            world.BeginHistory();

            using var buffer = new MemoryStream();
            SaveGame.Write(buffer, world, world.ContainsCodeMod);
            buffer.Position = 0;

            // Content without that map — a mod uninstalled between sessions.
            ContentDatabase without = ContentWithout("world", "dry-reach");
            var e = Assert.Throws<SaveException>(
                () => SaveGame.Read(buffer, without, BiomeTable.FromContent(without), VoxelTypes.FromContent(without)));
            Assert.Contains("dry-reach", e.Message);
        }

    }
}
