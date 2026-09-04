using System;
using System.Collections.Generic;
using Godless.Sim.Content;
using Xunit;

namespace Godless.Sim.Tests
{
    public class JsonTests
    {
        [Fact]
        public void ParsesTheShapesContentActuallyUses()
        {
            JsonValue v = JsonParser.Parse(
                "{\"type\":\"biome\",\"id\":\"temperate\",\"materials\":[\"oak\",\"granite\"]," +
                "\"rainfall\":800,\"coastal\":true,\"parent\":null}");

            Assert.Equal("biome", v["type"].AsString(null));
            Assert.Equal(2, v["materials"].Count);
            Assert.Equal("granite", v["materials"][1].AsString(null));
            Assert.Equal(800, v["rainfall"].AsInt32(0));
            Assert.True(v["coastal"].AsBool(false));
            Assert.True(v["parent"].IsNull);
        }

        [Fact]
        public void KeepsDocumentOrder_BecauseL2ForbidsUnorderedIteration()
        {
            JsonValue v = JsonParser.Parse("{\"z\":1,\"a\":2,\"m\":3}");
            Assert.Equal(new[] { "z", "a", "m" }, v.Keys);
        }

        [Fact]
        public void RoundTripsCanonically()
        {
            const string src = "{\"b\":[1,2,{\"c\":\"x\\ny\"}],\"a\":true}";
            string once = JsonParser.Parse(src).ToString();
            string twice = JsonParser.Parse(once).ToString();
            Assert.Equal(once, twice);
        }

        [Fact]
        public void AcceptsComments_BecauseContentCarriesDesignIntent()
        {
            JsonValue v = JsonParser.Parse("{ // why this biome is wet\n \"rainfall\": 800 /* mm */ }");
            Assert.Equal(800, v["rainfall"].AsInt32(0));
        }

        [Theory]
        [InlineData("{\"a\":1,}")]          // trailing comma
        [InlineData("{\"a\":1 \"b\":2}")]   // missing comma
        [InlineData("{a:1}")]               // unquoted key
        [InlineData("[1,2")]                // unterminated
        [InlineData("{\"a\":1}{\"b\":2}")]  // trailing content
        [InlineData("{\"a\":1,\"a\":2}")]   // duplicate member
        public void RejectsSloppyContentWithAPosition(string bad)
        {
            var e = Assert.Throws<JsonParseException>(() => JsonParser.Parse(bad));
            Assert.True(e.Line >= 1 && e.Column >= 1);
        }
    }

    public class ContentLoaderTests
    {
        static MemoryContentSource Source()
        {
            return new MemoryContentSource()
                .Add("base", "mod.json", "{\"name\":\"Godless\",\"version\":\"0.1.0\"}")
                .Add("base", "biomes/temperate.json",
                     "{\"type\":\"biome\",\"id\":\"temperate\",\"materials\":[\"oak\",\"granite\"],\"rainfall\":800}");
        }

        /// <summary>
        /// L5's tell, and half of G0's exit condition: deleting the base
        /// content must leave a game that boots with nothing to build. Not an
        /// error, not an empty-catch — zero mods, zero documents, no throw.
        /// </summary>
        [Fact]
        public void EmptyContentRoot_BootsWithNothingToBuild()
        {
            LoadResult result = ContentLoader.Load(new MemoryContentSource());

            Assert.Empty(result.LoadOrder);
            Assert.Equal(0, result.Database.DocumentCount);
            Assert.Empty(result.Database.Types());
        }

        [Fact]
        public void MissingContentDirectory_IsAlsoNotAnError()
        {
            LoadResult result = ContentLoader.Load(
                new DirectoryContentSource("/nonexistent/godless/content"));
            Assert.Equal(0, result.Database.DocumentCount);
        }

        [Fact]
        public void LoadsDocumentsByTypeAndId()
        {
            ContentDatabase db = ContentLoader.Load(Source()).Database;

            Assert.True(db.Contains("biome", "temperate"));
            Assert.Equal("base", db.OwnerOf("biome", "temperate"));
            Assert.Equal(800, db.Get("biome", "temperate")["rainfall"].AsInt32(0));
        }

        [Fact]
        public void TheBaseGameTakesNoSpecialPath()
        {
            // A mod named anything else loads identically. If "base" were
            // privileged anywhere, this would differ.
            var renamed = new MemoryContentSource()
                .Add("aardvark", "mod.json", "{\"name\":\"Godless\"}")
                .Add("aardvark", "biomes/temperate.json",
                     "{\"type\":\"biome\",\"id\":\"temperate\",\"rainfall\":800}");

            Assert.Equal(800, ContentLoader.Load(renamed).Database
                                 .Get("biome", "temperate")["rainfall"].AsInt32(0));
        }

        [Fact]
        public void LoadOrder_RespectsRequiresAndLoadAfter()
        {
            var src = new MemoryContentSource()
                .Add("zzz", "mod.json", "{\"requires\":[\"base\"]}")
                .Add("base", "mod.json", "{}")
                .Add("aaa", "mod.json", "{\"loadAfter\":[\"zzz\"]}");

            var ids = new List<string>();
            foreach (ModManifest m in ContentLoader.Load(src).LoadOrder) ids.Add(m.Id);

            Assert.True(ids.IndexOf("base") < ids.IndexOf("zzz"));
            Assert.True(ids.IndexOf("zzz") < ids.IndexOf("aaa"));
        }

        [Fact]
        public void LoadOrder_IgnoresAnAbsentSoftDependency()
        {
            var src = new MemoryContentSource().Add("a", "mod.json", "{\"loadAfter\":[\"never-installed\"]}");
            Assert.Single(ContentLoader.Load(src).LoadOrder);
        }

        [Fact]
        public void MissingHardDependency_IsAnError()
        {
            var src = new MemoryContentSource().Add("a", "mod.json", "{\"requires\":[\"absent\"]}");
            var e = Assert.Throws<ContentException>(() => ContentLoader.Load(src));
            Assert.Contains("absent", e.Message);
        }

        [Fact]
        public void DependencyCycle_IsReportedWithThePath()
        {
            var src = new MemoryContentSource()
                .Add("a", "mod.json", "{\"requires\":[\"b\"]}")
                .Add("b", "mod.json", "{\"requires\":[\"a\"]}");
            var e = Assert.Throws<ContentException>(() => ContentLoader.Load(src));
            Assert.Contains("cycle", e.Message);
        }

        [Fact]
        public void ManifestIdMustMatchItsFolder()
        {
            var src = new MemoryContentSource().Add("a", "mod.json", "{\"id\":\"b\"}");
            Assert.Throws<ContentException>(() => ContentLoader.Load(src));
        }

        [Fact]
        public void CodeMod_IsFlaggedForTheSave()
        {
            var src = new MemoryContentSource().Add("a", "mod.json", "{\"codeMod\":true}");
            Assert.True(ContentLoader.Load(src).ContainsCodeMod);
            Assert.False(ContentLoader.Load(Source()).ContainsCodeMod);
        }

        [Fact]
        public void FolderWithoutAManifest_IsSkippedWithAWarning()
        {
            var src = new MemoryContentSource().Add("junk", "notes.json", "{\"type\":\"x\",\"id\":\"y\"}");
            LoadResult r = ContentLoader.Load(src);
            Assert.Empty(r.LoadOrder);
            Assert.Single(r.Warnings);
        }

        [Fact]
        public void DocumentWithoutTypeOrId_IsAnErrorNamingTheFile()
        {
            var src = new MemoryContentSource()
                .Add("a", "mod.json", "{}")
                .Add("a", "broken.json", "{\"rainfall\":800}");
            var e = Assert.Throws<ContentException>(() => ContentLoader.Load(src));
            Assert.Contains("broken.json", e.Message);
        }

        [Fact]
        public void Digest_IsStableAcrossLoadsAndIndependentOfSourceOrder()
        {
            ulong a = ContentLoader.Load(Source()).Database.Digest();
            ulong b = ContentLoader.Load(Source()).Database.Digest();
            Assert.Equal(a, b);

            // Same content, documents added in a different sequence.
            var reordered = new MemoryContentSource()
                .Add("base", "biomes/temperate.json",
                     "{\"type\":\"biome\",\"id\":\"temperate\",\"materials\":[\"oak\",\"granite\"],\"rainfall\":800}")
                .Add("base", "mod.json", "{\"name\":\"Godless\",\"version\":\"0.1.0\"}");
            Assert.Equal(a, ContentLoader.Load(reordered).Database.Digest());
        }
    }

    public class ContentPatchTests
    {
        static MemoryContentSource WithPatch(string patchJson)
        {
            return new MemoryContentSource()
                .Add("base", "mod.json", "{}")
                .Add("base", "biomes/temperate.json",
                     "{\"type\":\"biome\",\"id\":\"temperate\",\"materials\":[\"oak\",\"granite\"],\"rainfall\":800}")
                .Add("highland", "mod.json", "{\"requires\":[\"base\"]}")
                .Add("highland", "patches/wetter.json", patchJson);
        }

        static JsonValue Temperate(string patchJson)
        {
            return ContentLoader.Load(WithPatch(patchJson)).Database.Get("biome", "temperate");
        }

        /// <summary>
        /// Part 22's actual lesson: a mod edits another mod's data by path,
        /// without shipping a copy of it.
        /// </summary>
        [Fact]
        public void OneModPatchesAnothersDocument()
        {
            JsonValue v = Temperate(
                "{\"$patch\":[{\"op\":\"replace\",\"target\":\"biome/temperate\",\"path\":\"rainfall\",\"value\":1600}]}");
            Assert.Equal(1600, v["rainfall"].AsInt32(0));
            Assert.Equal(2, v["materials"].Count); // untouched
        }

        [Fact]
        public void AddsAndRemovesMembers()
        {
            Assert.True(Temperate(
                "{\"$patch\":[{\"op\":\"add\",\"target\":\"biome/temperate\",\"path\":\"frost\",\"value\":true}]}")
                ["frost"].AsBool(false));

            Assert.True(Temperate(
                "{\"$patch\":[{\"op\":\"remove\",\"target\":\"biome/temperate\",\"path\":\"rainfall\"}]}")
                ["rainfall"].IsNull);
        }

        [Fact]
        public void EditsArraysByIndexAndAppendsWithDash()
        {
            Assert.Equal("slate", Temperate(
                "{\"$patch\":[{\"op\":\"replace\",\"target\":\"biome/temperate\",\"path\":\"materials/1\",\"value\":\"slate\"}]}")
                ["materials"][1].AsString(null));

            JsonValue appended = Temperate(
                "{\"$patch\":[{\"op\":\"add\",\"target\":\"biome/temperate\",\"path\":\"materials/-\",\"value\":\"thatch\"}]}");
            Assert.Equal(3, appended["materials"].Count);
            Assert.Equal("thatch", appended["materials"][2].AsString(null));

            Assert.Equal(1, Temperate(
                "{\"$patch\":[{\"op\":\"remove\",\"target\":\"biome/temperate\",\"path\":\"materials/0\"}]}")
                ["materials"].Count);
        }

        [Fact]
        public void PatchingSomethingAbsent_WarnsRatherThanCrashingTheLoad()
        {
            LoadResult r = ContentLoader.Load(WithPatch(
                "{\"$patch\":[{\"op\":\"replace\",\"target\":\"biome/nowhere\",\"path\":\"rainfall\",\"value\":1}]}"));
            Assert.Single(r.Warnings);
            Assert.Contains("biome/nowhere", r.Warnings[0]);
        }

        [Theory]
        // replace something that is not there, add something that already is,
        // remove something absent, index past the end — each names the mod.
        [InlineData("{\"$patch\":[{\"op\":\"replace\",\"target\":\"biome/temperate\",\"path\":\"nope\",\"value\":1}]}")]
        [InlineData("{\"$patch\":[{\"op\":\"add\",\"target\":\"biome/temperate\",\"path\":\"rainfall\",\"value\":1}]}")]
        [InlineData("{\"$patch\":[{\"op\":\"remove\",\"target\":\"biome/temperate\",\"path\":\"nope\"}]}")]
        [InlineData("{\"$patch\":[{\"op\":\"replace\",\"target\":\"biome/temperate\",\"path\":\"materials/9\",\"value\":\"x\"}]}")]
        public void AMisdirectedPatchFailsLoudlyAndNamesTheMod(string patch)
        {
            var e = Assert.Throws<ContentException>(() => ContentLoader.Load(WithPatch(patch)));
            Assert.Contains("highland", e.Message);
        }

        [Fact]
        public void PatchesApplyAfterEveryDocumentExists_SoOrderDoesNotTrapAuthors()
        {
            // "aaa" sorts first and patches a document defined by "zzz".
            var src = new MemoryContentSource()
                .Add("zzz", "mod.json", "{}")
                .Add("zzz", "b.json", "{\"type\":\"biome\",\"id\":\"tundra\",\"rainfall\":100}")
                .Add("aaa", "mod.json", "{}")
                .Add("aaa", "p.json",
                     "{\"$patch\":[{\"op\":\"replace\",\"target\":\"biome/tundra\",\"path\":\"rainfall\",\"value\":50}]}");

            LoadResult r = ContentLoader.Load(src);
            Assert.Empty(r.Warnings);
            Assert.Equal(50, r.Database.Get("biome", "tundra")["rainfall"].AsInt32(0));
        }
    }
}
