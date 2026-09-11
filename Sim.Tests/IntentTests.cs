using System.IO;
using System.Linq;
using Godless.Sim.Annals;
using Godless.Sim.Build;
using Godless.Sim.Content;
using Godless.Sim.Core;
using Godless.Sim.Drives;
using Godless.Sim.Harness;
using Godless.Sim.Save;
using Godless.Sim.Settlements;
using Godless.Sim.Voxels;
using Godless.Sim.World;
using Xunit;

namespace Godless.Sim.Tests
{
    public class IntentTests
    {
        static ContentDatabase Shipped()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Assets", "Content"))) dir = dir.Parent;
            return ContentLoader.Load(new DirectoryContentSource(Path.Combine(dir.FullName, "Assets", "Content"))).Database;
        }

        static ContentDatabase With(params string[] docs)
        {
            var src = new MemoryContentSource().Add("base", "mod.json", "{}");
            for (int i = 0; i < docs.Length; i++) src.Add("base", "d" + i + ".json", docs[i]);
            return ContentLoader.Load(src).Database;
        }

        static readonly Sky Rain = new Sky(true, false);

        /// <summary>A settlement with drives and an intent bus, and a way to live its days.</summary>
        sealed class Town
        {
            public readonly DriveRules Rules;
            public readonly Annalist Annals = new Annalist();
            public readonly Settlement S;
            public readonly IntentBus Bus;
            public long Day;

            public Town(int people, int roofs)
            {
                ContentDatabase content = Shipped();
                Rules = DriveRules.FromContent(content);
                BiomeTable biomes = BiomeTable.FromContent(content);
                S = Settlement.Found("test", new Int3(101, 50, 99), biomes.At(biomes.IndexOf(Symbol.For("biome.temperate"))),
                                     people, Rules, 0, Annals, RecordId.None);
                S.ShelterCapacity = roofs;
                Bus = new IntentBus(IntentKindTable.FromContent(content, Rules.Needs), Rules.Needs.Count);
                S.AttachIntents(Bus);
            }

            public void Live(int days, System.Func<long, Sky> weather = null)
            {
                for (int i = 0; i < days; i++, Day++)
                {
                    Sky sky = weather == null ? (Day % 3 == 0 ? Rain : Sky.Fair) : weather(Day);
                    long dawn = Day * 4;
                    DriveSystem.Step(S, Rules, dawn, false, sky, Annals);
                    Bus.Evaluate(S, dawn, Annals);
                    DriveSystem.Step(S, Rules, dawn + 1, false, sky, Annals);
                    DriveSystem.Step(S, Rules, dawn + 2, false, sky, Annals);
                    DriveSystem.Step(S, Rules, dawn + 3, true, sky, Annals);
                }
            }

            public long Now { get { return Day * 4; } }
        }

        [Fact]
        public void TheShippedIntentKindLoads()
        {
            ContentDatabase content = Shipped();
            DriveRules rules = DriveRules.FromContent(content);
            IntentKindTable kinds = IntentKindTable.FromContent(content, rules.Needs);
            Assert.Empty(kinds.Problems);
            Assert.Equal(1, kinds.Count);
            Assert.Equal(rules.Needs.IndexOf("shelter"), kinds[0].Answers);
        }

        [Fact]
        public void RefusedIntentKindsSayWhy()
        {
            ContentDatabase content = With(
                "{\"type\":\"need\",\"id\":\"shelter\",\"tell\":\"x\"}",
                "{\"type\":\"intent\",\"id\":\"hut\",\"answers\":\"shelter\",\"threshold\":10}",
                "{\"type\":\"intent\",\"id\":\"altar\",\"answers\":\"reverence\",\"threshold\":10,\"tell\":\"x\"}",
                "{\"type\":\"intent\",\"id\":\"a-house\",\"answers\":\"shelter\",\"threshold\":10,\"tell\":\"x\"}",
                "{\"type\":\"intent\",\"id\":\"b-house\",\"answers\":\"shelter\",\"threshold\":10,\"tell\":\"x\"}");
            IntentKindTable kinds = IntentKindTable.FromContent(content, NeedTable.FromContent(content));

            Assert.Equal(1, kinds.Count);
            Assert.Contains(kinds.Problems, p => p.Contains("'hut'") && p.Contains("tell"));
            Assert.Contains(kinds.Problems, p => p.Contains("'altar'") && p.Contains("'reverence'"));
            Assert.Contains(kinds.Problems, p => p.Contains("already answers"));
        }

        /// <summary>
        /// S14's tell: every commissioned structure carries the events that
        /// produced it, from the moment it is requested. Twenty people and no
        /// roofs; the first shelter intent names the nights that asked for it,
        /// and the chain runs back to the founding.
        /// </summary>
        [Fact]
        public void AnIntentCarriesTheNightsThatAskedForIt()
        {
            var town = new Town(20, 0);
            town.Live(10);

            BuildIntent first = town.Bus.Intents[0];
            AnnalRecord raised = town.Annals.Get(first.Record);

            Assert.Equal(IntentBus.RaisedKind, raised.Kind);
            Assert.Equal(town.S.Id, raised.Subject);
            Assert.Equal(first.Kind.Id, raised.Participants[0]);
            Assert.InRange(first.RaisedTick, 4, 4 * 7);

            // The causes are the recorded nights in the open, strongest first,
            // and the record holds the same list the intent does.
            Assert.Equal(first.Causes[0], raised.Cause);
            Assert.Equal(first.Causes.Skip(1), raised.Contributors);
            Assert.True(first.Causes.Count > 1, "more than one spell of nights contributed");
            foreach (RecordId c in first.Causes)
                Assert.Equal(DriveSystem.ExposedKind, town.Annals.Get(c).Kind);

            // Walkable both ways: back to the founding, and forward from any
            // contributing night to the intent it helped raise.
            var chain = town.Annals.CausalChain(first.Record);
            Assert.Equal(Settlement.FoundedKind, chain[chain.Count - 1].Kind);
            Assert.Contains(town.Annals.Consequences(first.Causes[first.Causes.Count - 1]), r => r.Id == first.Record);

            // It leans toward where the people are sleeping.
            Assert.Equal(town.S.HearthParcelX, first.ParcelX);
            Assert.Equal(town.S.HearthParcelZ, first.ParcelZ);
        }

        [Fact]
        public void PressureBeyondTheOpenLimitMakesIntentsHeavierNotMoreNumerous()
        {
            var town = new Town(20, 0);
            town.Live(20);
            int max = town.Bus.Kinds[0].MaxOpen;
            Assert.Equal(max, town.Bus.Intents.Count);

            BuildIntent newest = town.Bus.Intents[max - 1];
            double before = newest.Weight;
            town.Live(10);
            Assert.Equal(max, town.Bus.Intents.Count);
            Assert.True(newest.Weight > before, "the unanswered one grows more urgent");
            Assert.Equal(0.0, town.Bus.Pending(0));
        }

        [Fact]
        public void BuildingOneLetsTheNextBeAskedFor()
        {
            var town = new Town(20, 0);
            town.Live(20);
            BuildIntent first = town.Bus.Intents[0];

            // Stand-in for S1A: a finished structure, caused by the intent.
            RecordId built = town.Annals.Write(town.Now, Symbol.For("structure.completed"), Symbol.For("structure.1"),
                                               first.Place, first.Record);
            RecordId resolved = town.Bus.Resolve(first, town.Now, town.Annals, built);

            AnnalRecord r = town.Annals.Get(resolved);
            Assert.Equal(first.Record, r.Cause);
            Assert.Equal(new[] { built }, r.Contributors);
            Assert.Equal(IntentStatus.Resolved, first.Status);
            Assert.Equal(1, town.Bus.OutstandingCount(0));

            town.Live(10);
            Assert.Equal(3, town.Bus.Intents.Count);
        }

        [Fact]
        public void AnIntentsLifeGoesOneWay()
        {
            var town = new Town(20, 0);
            town.Live(20);
            BuildIntent a = town.Bus.Intents[0], b = town.Bus.Intents[1];

            town.Bus.Claim(a, town.Now, town.Annals, RecordId.None);
            Assert.Throws<System.InvalidOperationException>(() => town.Bus.Claim(a, town.Now, town.Annals, RecordId.None));
            town.Bus.Resolve(a, town.Now, town.Annals, RecordId.None);
            Assert.Throws<System.InvalidOperationException>(() => town.Bus.Resolve(a, town.Now, town.Annals, RecordId.None));
            Assert.Throws<System.InvalidOperationException>(() => town.Bus.Abandon(a, town.Now, town.Annals, RecordId.None));

            town.Bus.Abandon(b, town.Now, town.Annals, RecordId.None);
            Assert.Equal(0, town.Bus.OutstandingCount(0));

            var other = new Town(20, 0);
            other.Live(20);
            Assert.Throws<System.ArgumentException>(() => town.Bus.Claim(other.Bus.Intents[0], town.Now, town.Annals, RecordId.None));
        }

        [Fact]
        public void PressureThatNeverReachesTheThresholdDecaysAway()
        {
            var town = new Town(20, 20);
            int shelter = town.Rules.Needs.IndexOf("shelter");
            town.Bus.Add(shelter, 25, 25, 30.0, town.S.Founded);
            Assert.Equal(30.0, town.Bus.Pending(0));

            town.Live(400, d => Sky.Fair);   // twenty half-lives
            Assert.Empty(town.Bus.Intents);
            Assert.True(town.Bus.Pending(0) < 1e-3, "pending " + town.Bus.Pending(0));
        }

        [Fact]
        public void PressureNoRecordExplainsIsCausedByTheFounding()
        {
            var town = new Town(1, 1);
            int shelter = town.Rules.Needs.IndexOf("shelter");
            town.Bus.Add(shelter, 25, 26, 100.0, RecordId.None);
            town.Bus.Evaluate(town.S, 4, town.Annals);

            BuildIntent i = town.Bus.Intents.Single();
            Assert.Equal(new[] { town.S.Founded }, i.Causes);
            Assert.Equal(25, i.ParcelX);
            Assert.Equal(26, i.ParcelZ);
        }

        static SimWorld Settled(ulong seed)
        {
            ContentDatabase content = Shipped();
            var world = new SimWorld(seed, content, VoxelTypes.FromContent(content));
            DriveRules rules = DriveRules.FromContent(content);
            BiomeTable biomes = BiomeTable.FromContent(content);
            Settlement s = Settlement.Found("first", new Int3(256, 50, 256), biomes.At(biomes.IndexOf(Symbol.For("biome.temperate"))),
                                            20, rules, 0, world.Annals, RecordId.None);
            s.AttachIntents(new IntentBus(IntentKindTable.FromContent(content, rules.Needs), rules.Needs.Count));
            world.Settlements.Add(s);
            world.Add(new DriveSystem(rules)).Add(new IntentSystem());
            return world;
        }

        [Fact]
        public void TheSameSeedAsksForTheSameBuildings()
        {
            SimWorld a = Settled(3), b = Settled(3);
            a.RunYears(1); b.RunYears(1);
            Assert.Equal(a.Annals.Digest(), b.Annals.Digest());
            Assert.Equal(a.Settlements[0].Intents.Digest(), b.Settlements[0].Intents.Digest());
            Assert.NotEmpty(a.Settlements[0].Intents.Intents);
        }

        /// <summary>The save carries contributors, so a loaded intent still knows every night that asked for it.</summary>
        [Fact]
        public void ASaveKeepsEveryCauseOfAnIntent()
        {
            SimWorld world = Settled(5);
            world.RunYears(1);
            using var buffer = new MemoryStream();
            SaveGame.Write(buffer, world);
            buffer.Position = 0;
            SimWorld loaded = SaveGame.Read(buffer, world.Content, BiomeTable.FromContent(world.Content), world.VoxelTypes);

            AnnalRecord original = world.Annals.OfKind(IntentBus.RaisedKind)[0];
            AnnalRecord restored = loaded.Annals.Get(original.Id);
            Assert.NotEmpty(original.Contributors);
            Assert.Equal(original.Contributors, restored.Contributors);
            Assert.Equal(world.Annals.Digest(), loaded.Annals.Digest());
        }
    }
}
