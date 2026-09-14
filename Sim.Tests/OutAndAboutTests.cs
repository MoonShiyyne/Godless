using System.IO;
using Godless.Sim.Annals;
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
    /// Life away from the fire (S2V): company had working beside others, the
    /// day spent where the work and the home are, houses that go further out
    /// when the ground by the fire is taken, and a reach for materials that
    /// widens when the near ones are worked out.
    /// </summary>
    public class OutAndAboutTests
    {
        readonly Xunit.Abstractions.ITestOutputHelper _out;
        public OutAndAboutTests(Xunit.Abstractions.ITestOutputHelper output) { _out = output; }

        static ContentDatabase Shipped()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Assets", "Content"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return ContentLoader.Load(new DirectoryContentSource(Path.Combine(dir.FullName, "Assets", "Content"))).Database;
        }

        static readonly ContentDatabase Content = Shipped();

        [Fact]
        public void PeopleWorkingSideBySideKeepEachOtherCompany()
        {
            DriveRules rules = DriveRules.FromContent(Content);
            var annals = new Annalist();
            BiomeTable biomes = BiomeTable.FromContent(Content);
            Settlement s = Settlement.Found("test", new Int3(400, 50, 400), biomes.At(0), 4, rules, 0, annals, RecordId.None);
            int company = rules.Needs.IndexOf("company");
            Assert.True(rules.Needs[company].NearRelief > 0.0, "company is not eased by being near others");

            for (int i = 0; i < 4; i++) s.People[i].SetLevel(company, 0.5, RecordId.None);
            s.People[3].PlaceAt(s.HearthParcelX + 20, s.HearthParcelZ);   // off on their own

            MovementSystem.Company(s, rules.Needs, false);
            for (int i = 0; i < 3; i++)
            {
                Assert.Equal(2, s.People[i].Beside);
                Assert.True(s.People[i].BesideWhom >= 0 && s.People[i].BesideWhom < 3);
                Assert.True(s.People[i].Level(company) < 0.5, "three together, and company did not ease");
            }
            Assert.Equal(0, s.People[3].Beside);
            Assert.Equal(-1, s.People[3].BesideWhom);
            Assert.Equal(0.5, s.People[3].Level(company));

            // Asleep, nobody keeps anyone company.
            double before = s.People[0].Level(company);
            MovementSystem.Company(s, rules.Needs, true);
            Assert.Equal(0, s.People[0].Beside);
            Assert.Equal(before, s.People[0].Level(company));
        }

        static SimWorld Settled(string map, out Settlement s)
        {
            WorldChoice choice = WorldChoice.Pick(Content, map);
            SimWorld world = SettlementInvariants.Settled(Content, choice, 20, TestIslands.Generate)(7);
            s = world.Settlements[0];
            return world;
        }

        /// <summary>A day in a lived-in village is spent at work and at home, not about the fire.</summary>
        [Fact]
        public void TheDayIsSpentAwayFromTheFire()
        {
            Settlement s;
            SimWorld world = Settled("green-shore", out s);
            for (int day = 0; day < 400; day++)
                for (int t = 0; t < world.Clock.TicksPerDay; t++) world.Tick();
            double share = (double)s.DaylightAtFire / s.DaylightTicks;
            _out.WriteLine((share * 100).ToString("0.0") + "% of daylight at the fire, " + s.People.Count + " people");
            Assert.True(share < 0.25, (share * 100).ToString("0") + "% of the day spent within a stone's throw of the fire");

            // And the housed are idle at home.
            foreach (Agent a in s.People)
            {
                Household family = Households.Of(s, a);
                if (family != null && family.Housed) Assert.NotEqual("idle at the fire", a.Doing);
            }
        }

        /// <summary>With the ground round the fire taken, a house is sited further out rather than not at all.</summary>
        [Fact]
        public void AHouseGoesFurtherOutWhenTheNearGroundIsTaken()
        {
            Settlement s;
            SimWorld world = Settled("green-shore", out s);
            WorldChoice choice = WorldChoice.Pick(Content, "green-shore");
            ConstraintFields fields;
            ParcelGrid grid = Founding.Survey(world, Content, choice.Biomes, out fields);
            GeneTable genes = GeneTable.FromContent(Content);
            IntentKindTable kinds = IntentKindTable.FromContent(Content, DriveRules.FromContent(Content).Needs);
            SitingRule rule = SitingTable.FromContent(Content, genes, kinds).For("shelter");
            Blueprint plan = GrammarTable.FromContent(Content, genes, kinds).For("shelter")
                .Build(s.Genome, Palette.FromContent(Content), 80, 80, 1150);

            // Everything within the first search radius claimed but a way out from the fire.
            int hx = s.HearthParcelX, hz = s.HearthParcelZ, r = rule.SearchRadius;
            for (int pz = hz - r - 2; pz <= hz + r + 2; pz++)
                for (int px = hx - r - 2; px <= hx + r + 2; px++)
                    if ((px != hx || pz < hz) && !s.IsClaimed(px, pz)) s.ClaimParcel(px, pz, s.Founded);

            s.Intents.Add(DriveRules.FromContent(Content).Needs.IndexOf("shelter"), hx, hz, 1000.0, s.Founded);
            s.Intents.Evaluate(s, world.Clock.Tick, world.Annals);
            BuildIntent intent = null;
            foreach (BuildIntent i in s.Intents.Intents) if (i.Kind.Purpose == IntentPurpose.Home) intent = i;
            Assert.NotNull(intent);

            var sites = SiteScorer.Candidates(s, intent, plan, rule, grid, fields, s.Genome, 4);
            Assert.NotEmpty(sites);
            Site site = sites[0];
            int away = System.Math.Max(System.Math.Abs(site.ParcelX - hx), System.Math.Abs(site.ParcelZ - hz));
            _out.WriteLine("sited " + away + " parcels from the fire, first radius " + r);
            Assert.True(away > r, "the site is inside ground that was all claimed");
        }

        /// <summary>A material worked out of reach sends people further for it.</summary>
        [Fact]
        public void TheReachForMaterialsWidensWhenTheNearOnesAreWorkedOut()
        {
            Settlement s;
            SimWorld world = Settled("green-shore", out s);
            for (int t = 0; t < world.Clock.TicksPerDay; t++) world.Tick();
            Catchment c = s.Catchment;
            DepositMap deposits = world.Island.Deposits;
            int oak = s.Stock.Materials.IndexOf("oak");
            Assert.True(oak >= 0);
            int reach = c.Reach;

            // Fell every oak in reach.
            foreach (int f in deposits.Within(s.Hearth.X, s.Hearth.Z, reach))
                if (s.Stock.Materials.IndexOf(deposits.KindOf(f).Yields) == oak && deposits.Available(f))
                    deposits.Take(f, deposits.Remaining(f), world.Voxels, world.Clock.Tick, s.Founded, world.Clock.TicksPerDay);
            c.Refresh();
            _out.WriteLine("reach " + reach + " -> " + c.Reach + ", oak now yields " + c.YieldPerLabourTick(oak).ToString("0.00"));
            Assert.True(c.Reach > reach, "the reach did not widen with the oak in it gone");
            Assert.True(c.Reach <= reach * Catchment.MostReachTimes);
        }
    }
}
