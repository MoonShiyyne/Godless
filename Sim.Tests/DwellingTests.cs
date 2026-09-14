using System.IO;
using Godless.Sim.Build;
using Godless.Sim.Content;
using Godless.Sim.Core;
using Godless.Sim.Culture;
using Godless.Sim.Drives;
using Godless.Sim.Harness;
using Godless.Sim.Settlements;
using Godless.Sim.World;
using Xunit;

namespace Godless.Sim.Tests
{
    /// <summary>
    /// Dwelling programs (S2O) and additions to homes (S2P).
    ///
    /// The tell: houses turn their doors to the fire or the sun, back into
    /// slopes and step out downhill, sized to the family they are for — and a
    /// crowded family adds a wing or a storey to the home it has, visibly
    /// younger than the walls it joins.
    /// </summary>
    public class DwellingTests
    {
        readonly Xunit.Abstractions.ITestOutputHelper _out;
        public DwellingTests(Xunit.Abstractions.ITestOutputHelper output) { _out = output; }

        static ContentDatabase Shipped()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Assets", "Content"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return ContentLoader.Load(new DirectoryContentSource(Path.Combine(dir.FullName, "Assets", "Content"))).Database;
        }

        static readonly ContentDatabase Content = Shipped();
        static readonly GeneTable Genes = GeneTable.FromContent(Content);
        static readonly GrammarTable Grammars = GrammarTable.FromContent(Content, Genes);
        static readonly Palette Palette = Palette.FromContent(Content);

        [Fact]
        public void TheWholeHouseAndItsPartsLoadAndStayApart()
        {
            Assert.Empty(Grammars.Problems);
            Assert.Equal("dwelling", Grammars.For("shelter").Name);
            Assert.Equal("wing", Grammars.PartFor("shelter", "wing").Name);
            Assert.Equal("storey", Grammars.PartFor("shelter", "storey").Name);
        }

        [Fact]
        public void TurningAHouseTurnsItsDoorAndNothingElse()
        {
            Blueprint plan = Grammars.For("shelter").Build(new Genome(Genes), Palette, 80, 80, 1150);
            int door = plan.DoorSide();
            Assert.True(door >= 0);
            for (int q = 1; q < 4; q++)
            {
                Blueprint turned = plan.Rotated(q);
                Assert.Equal(plan.Volume, turned.Volume);
                Assert.Equal(plan.Capacity, turned.Capacity);
                Assert.Equal((door + q) % 4, turned.DoorSide());
                if (q % 2 == 1) { Assert.Equal(plan.Width, turned.Depth); Assert.Equal(plan.Depth, turned.Width); }
            }
            Assert.Equal(plan.Digest(), plan.Rotated(4).Digest());
            Assert.Equal(plan.Digest(), plan.Rotated(1).Rotated(3).Digest());
        }

        [Fact]
        public void TheFamilyDecidesHowBigTheHouseIs()
        {
            Grammar g = Grammars.For("shelter");
            var genome = new Genome(Genes);
            Blueprint small = g.Build(genome, Palette, 80, 80, 1150, new System.Collections.Generic.Dictionary<string, double> { { "capacity", 3 } });
            Blueprint large = g.Build(genome, Palette, 80, 80, 1150, new System.Collections.Generic.Dictionary<string, double> { { "capacity", 12 } });
            Assert.Equal(3, small.Capacity);
            Assert.Equal(12, large.Capacity);
            Assert.True(large.Volume > small.Volume * 1.5, small.Volume + " voxels for three, " + large.Volume + " for twelve");
        }

        [Fact]
        public void AStoreyKnowsWhereTheRoofBegins()
        {
            Blueprint plan = Grammars.For("shelter").Build(new Genome(Genes), Palette, 80, 80, 1150);
            int roof = Construction.RoofBase(plan);
            Assert.True(roof > 0 && roof < plan.Height);
            Assert.True(Construction.Storeys(plan) >= 1);
        }

        [Fact]
        public void ShippedPreferencesLoadAndWeighByTheGenome()
        {
            SitingTable siting = SitingTable.FromContent(Content, Genes);
            Assert.Empty(siting.Problems);
            SitingRule rule = siting.For("shelter");
            var low = new Genome(Genes);
            var high = new Genome(Genes);
            high.Mutate(Symbol.For("gene.communal_ratio"), 1.0, 0, Symbol.None, Annals.RecordId.None, new Annals.Annalist());
            low.Mutate(Symbol.For("gene.communal_ratio"), 0.0, 0, Symbol.None, Annals.RecordId.None, new Annals.Annalist());
            Assert.True(rule.Weight("doorToFire", high) > rule.Weight("doorToFire", low));
            Assert.True(rule.Weight("wing", high) > rule.Weight("apart", high));
        }

        /// <summary>A lived-in village: every house knows which way it faces and why it is the way it is.</summary>
        [Fact]
        public void EveryHouseFacesSomewhereAndSaysWhy()
        {
            WorldChoice choice = WorldChoice.Pick(Content, "green-shore");
            SimWorld world = SettlementInvariants.Settled(Content, choice, 20, TestIslands.Generate)(7);
            Settlement s = world.Settlements[0];
            for (int day = 0; day < 400 && s.Projects.Count < 3; day++)
                for (int t = 0; t < world.Clock.TicksPerDay; t++) world.Tick();

            Assert.True(s.Projects.Count >= 2, "only " + s.Projects.Count + " houses planned in 400 days");
            foreach (Project p in s.Projects)
            {
                _out.WriteLine(p.Site.Record + ": " + string.Join("; ", p.Reasons));
                Assert.True(p.DoorSide >= 0);
                Assert.NotEmpty(p.Reasons);
                Assert.Equal(p.DoorSide, p.Plan.DoorSide());
            }
        }

        /// <summary>
        /// A family crowded past its beds adds to its own home rather than
        /// splitting — and the new room's beds are the family's once it stands.
        /// </summary>
        [Fact]
        public void ACrowdedFamilyAddsToTheHomeItHas()
        {
            WorldChoice choice = WorldChoice.Pick(Content, "green-shore");
            SimWorld world = SettlementInvariants.Settled(Content, choice, 20, TestIslands.Generate)(7);
            Settlement s = world.Settlements[0];

            Household family = null;
            for (int day = 0; day < 500 && family == null; day++)
            {
                for (int t = 0; t < world.Clock.TicksPerDay; t++) world.Tick();
                foreach (Household h in s.Households) if (h.Housed && h.Home[0].Complete) { family = h; break; }
            }
            Assert.True(family != null, "no family had a finished home within 500 days");

            // Crowd it by two: too few to send off to a house of their own, so
            // the only answer the program allows is room added to this home.
            Households.Settle(s);
            Project home = family.Home[0];
            int before = Households.CapacityOf(home);
            for (int i = 0; i < 2; i++) Households.Join(s, s.Add(world.Streams), family);

            Project added = null;
            for (int day = 0; day < 600 && added == null; day++)
            {
                for (int t = 0; t < world.Clock.TicksPerDay; t++) world.Tick();
                foreach (Project p in s.Projects) if (p.Host == home) { added = p; break; }
            }
            Assert.True(added != null, "the crowded family added nothing to its home in 600 days");
            _out.WriteLine(added.PartKind + ": " + string.Join("; ", added.Reasons));
            Assert.Contains(added.PartKind, new[] { "wing", "storey" });
            Assert.Contains(added, home.Added);

            for (int day = 0; day < 400 && !added.Complete; day++)
                for (int t = 0; t < world.Clock.TicksPerDay; t++) world.Tick();
            if (added.Complete) Assert.True(Households.CapacityOf(home) > before);
        }
    }
}
