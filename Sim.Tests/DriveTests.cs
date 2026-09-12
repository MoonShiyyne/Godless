using System.IO;
using Godless.Sim.Annals;
using Godless.Sim.Content;
using Godless.Sim.Core;
using Godless.Sim.Drives;
using Godless.Sim.Harness;
using Godless.Sim.Settlements;
using Godless.Sim.Voxels;
using Godless.Sim.World;
using Xunit;

namespace Godless.Sim.Tests
{
    public class DriveTests
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

        static Biome Temperate(ContentDatabase content)
        {
            BiomeTable biomes = BiomeTable.FromContent(content);
            return biomes.At(biomes.IndexOf(Symbol.For("biome.temperate")));
        }

        static readonly Sky Rain = new Sky(true, false);

        // A day is ticks 0-2 and the night is tick 3, as in the running sim.
        static void Day(Settlement s, DriveRules rules, long day, Sky sky, Annalist annals)
        {
            for (int t = 0; t < 3; t++) DriveSystem.Step(s, rules, day * 4 + t, false, sky, annals);
        }

        static void Night(Settlement s, DriveRules rules, long day, Sky sky, Annalist annals)
        {
            DriveSystem.Step(s, rules, day * 4 + 3, true, sky, annals);
        }

        [Fact]
        public void TheShippedNeedsAndActivitiesLoadClean()
        {
            DriveRules rules = DriveRules.FromContent(Shipped());
            Assert.Empty(rules.Problems());
            Assert.Equal(4, rules.Needs.Count);
            foreach (Need n in rules.Needs.All) Assert.False(string.IsNullOrWhiteSpace(n.Tell), n.Name);
            Assert.True(rules.Activities.IndexOf("work") >= 0);
        }

        /// <summary>Part 03, enforced by the content: no activity builds anything.</summary>
        [Fact]
        public void NoActivityIsBuilding()
        {
            DriveRules rules = DriveRules.FromContent(Shipped());
            int shelter = rules.Needs.IndexOf("shelter");
            foreach (Activity a in rules.Activities.All)
                Assert.Equal(0.0, a.ReliefFor(shelter));
        }

        [Fact]
        public void AConditionNothingProducesIsRefusedByName()
        {
            DriveRules rules = DriveRules.FromContent(With(
                "{\"type\":\"need\",\"id\":\"awe\",\"tell\":\"x\",\"rises\":{\"moonlight\":0.1}}",
                "{\"type\":\"need\",\"id\":\"quiet\",\"rises\":{}}",
                "{\"type\":\"activity\",\"id\":\"pray\",\"relieves\":{\"awe\":0.2}}",
                "{\"type\":\"activity\",\"id\":\"dance\",\"requires\":[\"festival\"]}"));

            Assert.Equal(0, rules.Needs.Count);
            Assert.Equal(0, rules.Activities.Count);
            Assert.Contains(rules.Problems(), p => p.Contains("'moonlight'"));
            Assert.Contains(rules.Problems(), p => p.Contains("'quiet'") && p.Contains("tell"));
            Assert.Contains(rules.Problems(), p => p.Contains("'pray'") && p.Contains("'awe'"));
            Assert.Contains(rules.Problems(), p => p.Contains("'festival'"));
        }

        [Fact]
        public void TheMostUrgentNeedWinsAndTiesAreStable()
        {
            DriveRules rules = DriveRules.FromContent(With(
                "{\"type\":\"need\",\"id\":\"a\",\"tell\":\"x\"}",
                "{\"type\":\"need\",\"id\":\"b\",\"tell\":\"x\"}",
                "{\"type\":\"activity\",\"id\":\"do-a\",\"relieves\":{\"a\":0.1}}",
                "{\"type\":\"activity\",\"id\":\"do-b\",\"relieves\":{\"b\":0.1}}",
                "{\"type\":\"activity\",\"id\":\"idle\",\"base\":0.2}"));
            var agent = new Agent(Symbol.For("test.agent"), 0, rules.Needs);
            int a = rules.Needs.IndexOf("a"), b = rules.Needs.IndexOf("b");

            agent.SetLevel(a, 0.9, RecordId.None);
            agent.SetLevel(b, 0.5, RecordId.None);
            Assert.Equal(rules.Activities.IndexOf("do-a"), DriveSystem.Choose(agent, rules.Needs, rules.Activities, 0UL));

            agent.SetLevel(a, 0.1, RecordId.None);
            agent.SetLevel(b, 0.1, RecordId.None);
            Assert.Equal(rules.Activities.IndexOf("idle"), DriveSystem.Choose(agent, rules.Needs, rules.Activities, 0UL));

            // Equal utility: the earlier activity in stable-hash order.
            agent.SetLevel(a, 0.7, RecordId.None);
            agent.SetLevel(b, 0.7, RecordId.None);
            int first = System.Math.Min(rules.Activities.IndexOf("do-a"), rules.Activities.IndexOf("do-b"));
            Assert.Equal(first, DriveSystem.Choose(agent, rules.Needs, rules.Activities, 0UL));
        }

        /// <summary>
        /// S12's tell, verbatim: an agent who slept in the rain behaves
        /// differently tomorrow. Two identical people, one roof, one wet
        /// night; the next day is fair for both.
        /// </summary>
        [Fact]
        public void AnAgentWhoSleptInTheRainBehavesDifferentlyTomorrow()
        {
            ContentDatabase content = Shipped();
            DriveRules rules = DriveRules.FromContent(content);
            var annals = new Annalist();
            Settlement s = Settlement.Found("test", new Int3(100, 50, 100), Temperate(content), 2, rules, 0, annals, RecordId.None);
            s.ShelterCapacity = 1;

            Night(s, rules, 0, Rain, annals);
            Agent dry = s.People[0], wet = s.People[1];
            Assert.True(dry.ShelteredLastNight, "the tie for the one roof goes to founding order");
            Assert.False(wet.ShelteredLastNight);

            int warm = rules.Activities.IndexOf("warm");
            bool wetWarmed = false, dryWarmed = false;
            for (int t = 0; t < 3; t++)
            {
                DriveSystem.Step(s, rules, 4 + t, false, Sky.Fair, annals);
                wetWarmed |= wet.Activity == warm;
                dryWarmed |= dry.Activity == warm;
            }

            Assert.True(wetWarmed, "whoever slept in the rain spends part of the morning at the fire");
            Assert.False(dryWarmed);
            Assert.True(wet.ProductiveTicks < dry.ProductiveTicks,
                        "and does less work: " + wet.ProductiveTicks + " against " + dry.ProductiveTicks);

            // And the wet night is on record, naming the rain, and the agent's
            // warmth remembers which night it was.
            int warmth = rules.Needs.IndexOf("warmth");
            AnnalRecord night = annals.Get(wet.CauseOf(warmth));
            Assert.Equal(DriveSystem.ExposedKind, night.Kind);
            Assert.Equal(1, night.ValueA);
            Assert.Contains(Conditions.Rain, night.Participants);
            Assert.Equal(s.Founded, night.Cause);
        }

        /// <summary>
        /// Agents never decide to build; they generate pressure. And every
        /// unit of shelter pressure names the record behind it (L3).
        /// </summary>
        [Fact]
        public void PeopleWithNoRoofGenerateShelterPressureThatNamesItsCause()
        {
            ContentDatabase content = Shipped();
            DriveRules rules = DriveRules.FromContent(content);
            var annals = new Annalist();
            Settlement s = Settlement.Found("test", new Int3(100, 50, 100), Temperate(content), 20, rules, 0, annals, RecordId.None);
            var tally = (PressureTally)s.Pressure;
            int shelter = rules.Needs.IndexOf("shelter");

            for (long d = 0; d < 20; d++)
            {
                Sky sky = d % 3 == 0 ? Rain : Sky.Fair;
                Day(s, rules, d, sky, annals);
                Night(s, rules, d, sky, annals);
            }

            Assert.True(tally.Total(shelter) > 0.0);
            Assert.Equal(tally.Total(shelter), tally.Caused(shelter));

            // Give them roofs and the pressure stops growing.
            s.ShelterCapacity = 20;
            for (long d = 20; d < 30; d++) { Day(s, rules, d, Sky.Fair, annals); Night(s, rules, d, Sky.Fair, annals); }
            double settled = tally.Total(shelter);
            for (long d = 30; d < 40; d++) { Day(s, rules, d, Sky.Fair, annals); Night(s, rules, d, Sky.Fair, annals); }
            Assert.Equal(settled, tally.Total(shelter));
        }

        [Fact]
        public void NightsInTheOpenAreRecordedBySpellNotByNight()
        {
            ContentDatabase content = Shipped();
            DriveRules rules = DriveRules.FromContent(content);
            var annals = new Annalist();
            Settlement s = Settlement.Found("test", new Int3(100, 50, 100), Temperate(content), 5, rules, 0, annals, RecordId.None);

            for (long d = 0; d < 30; d++) Night(s, rules, d, Sky.Fair, annals);
            Assert.Single(annals.OfKind(DriveSystem.ExposedKind));

            Night(s, rules, 30, Rain, annals);
            Night(s, rules, 31, Rain, annals);
            Night(s, rules, 32, Sky.Fair, annals);
            Assert.Equal(3, annals.OfKind(DriveSystem.ExposedKind).Count);

            s.ShelterCapacity = 2;
            Night(s, rules, 33, Sky.Fair, annals);
            AnnalRecord fewer = annals.OfKind(DriveSystem.ExposedKind)[3];
            Assert.Equal(3, fewer.ValueA);
            Assert.Equal(2, fewer.ValueB);

            s.ShelterCapacity = 5;
            for (long d = 34; d < 40; d++) Night(s, rules, d, Rain, annals);
            Assert.Equal(4, annals.OfKind(DriveSystem.ExposedKind).Count);
            Assert.False(s.CurrentExposure.Exists);
        }

        [Fact]
        public void RoofsGoToWhoeverNeedsOneMost()
        {
            ContentDatabase content = Shipped();
            DriveRules rules = DriveRules.FromContent(content);
            var annals = new Annalist();
            Settlement s = Settlement.Found("test", new Int3(100, 50, 100), Temperate(content), 3, rules, 0, annals, RecordId.None);
            s.ShelterCapacity = 1;
            s.People[2].SetLevel(rules.Needs.IndexOf("shelter"), 0.8, RecordId.None);

            Night(s, rules, 0, Sky.Fair, annals);
            Assert.True(s.People[2].ShelteredLastNight);
            Assert.False(s.People[0].ShelteredLastNight);

            // Nobody owns the bed. The one who started worst off keeps it
            // while the others catch up — a crowded roof presses on whoever
            // has it too (S1E) — and then it changes hands. Once everyone is
            // equally desperate the need saturates and who has it stops
            // meaning anything, which is the honest end of a village with
            // three people and one bed.
            var roofed = new int[3];
            for (long d = 1; d <= 24; d++)
            {
                Day(s, rules, d, Sky.Fair, annals);
                Night(s, rules, d, Sky.Fair, annals);
                for (int i = 0; i < 3; i++) if (s.People[i].ShelteredLastNight) roofed[i]++;
            }
            int slept = 0;
            foreach (int nights in roofed) if (nights > 0) slept++;
            Assert.True(slept > 1, "the same person had the bed every night");
            Assert.True(roofed[2] > 0, "the one who needed it most never got it");
        }

        [Fact]
        public void WeatherIsStatelessAndFollowsTheClimate()
        {
            ContentDatabase content = Shipped();
            BiomeTable biomes = BiomeTable.FromContent(content);
            Biome wet = biomes.At(biomes.IndexOf(Symbol.For("biome.flood-plain")));
            Biome dry = biomes.At(biomes.IndexOf(Symbol.For("biome.highland")));
            var streams = new StreamRegistry(42);

            Sky first = Weather.On(streams, wet, 100, 360);
            for (long d = 0; d < 50; d++) Weather.On(streams, wet, d, 360);
            Sky again = Weather.On(new StreamRegistry(42), wet, 100, 360);
            Assert.Equal(first.Rain, again.Rain);
            Assert.Equal(first.Cold, again.Cold);
            Assert.Equal(first.Rain, Weather.On(streams, wet, 100, 360).Rain);

            int wetDays = 0, dryDays = 0, highlandWinterCold = 0, highlandSummerCold = 0, plainWinterCold = 0;
            const int years = 20;
            for (long d = 0; d < 360L * years; d++)
            {
                if (Weather.On(streams, wet, d, 360).Rain) wetDays++;
                Sky high = Weather.On(streams, dry, d, 360);
                if (high.Rain) dryDays++;
                int season = (int)(d % 360) / 90;
                if (season == 3 && high.Cold) highlandWinterCold++;
                if (season == 1 && high.Cold) highlandSummerCold++;
                if (season == 3 && Weather.On(streams, wet, d, 360).Cold) plainWinterCold++;
            }

            Assert.InRange(wetDays / (360.0 * years), Weather.RainPerMille(wet, 360) / 1000.0 - 0.03, Weather.RainPerMille(wet, 360) / 1000.0 + 0.03);
            Assert.InRange(dryDays / (360.0 * years), Weather.RainPerMille(dry, 360) / 1000.0 - 0.03, Weather.RainPerMille(dry, 360) / 1000.0 + 0.03);
            Assert.True(wetDays > dryDays * 2, "the flood plain is the rainy one");
            Assert.True(highlandWinterCold > 90 * years / 2, "a severity-5 winter is mostly cold days");
            Assert.Equal(0, highlandSummerCold);
            Assert.Equal(0, plainWinterCold);
        }

        static SimWorld SettledWorld(ulong seed, ContentDatabase content)
        {
            var world = new SimWorld(seed, content, VoxelTypes.Build(new Symbol[0]));
            DriveRules rules = DriveRules.FromContent(content);
            BiomeTable biomes = BiomeTable.FromContent(content);
            Biome biome = biomes.Count > 0 ? biomes.At(0) : null;
            world.Settlements.Add(Settlement.Found("first", new Int3(256, 50, 256), biome, 20, rules, 0, world.Annals, RecordId.None));
            world.Add(new DriveSystem(rules));
            return world;
        }

        [Fact]
        public void TheSameSeedLivesTheSameYears()
        {
            ContentDatabase content = Shipped();
            SimWorld a = SettledWorld(7, content), b = SettledWorld(7, content), c = SettledWorld(8, content);
            a.RunYears(2); b.RunYears(2); c.RunYears(2);

            Assert.Equal(a.Annals.Digest(), b.Annals.Digest());
            Assert.Equal(a.Settlements[0].Digest(), b.Settlements[0].Digest());
            Assert.NotEqual(a.Settlements[0].Digest(), c.Settlements[0].Digest());
        }

        [Fact]
        public void WithNoContentPeopleExistAndDoNothing()
        {
            SimWorld world = SettledWorld(1, new ContentDatabase());
            world.RunYears(1);
            foreach (Agent a in world.Settlements[0].People) Assert.Equal(-1, a.Activity);
        }
    }
}
