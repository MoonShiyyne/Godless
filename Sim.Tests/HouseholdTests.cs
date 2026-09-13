using System.IO;
using Godless.Sim.Annals;
using Godless.Sim.Build;
using Godless.Sim.Content;
using Godless.Sim.Core;
using Godless.Sim.Culture;
using Godless.Sim.Drives;
using Godless.Sim.Harness;
using Godless.Sim.Settlements;
using Godless.Sim.Voxels;
using Godless.Sim.World;
using Xunit;

namespace Godless.Sim.Tests
{
    /// <summary>
    /// Families and growth. S2N.
    ///
    /// The tell: a village that eats well has more children the bigger it
    /// gets; a family too big for its roof is crowded at its own door and
    /// builds on; a family too big for one hearth splits and moves out.
    /// </summary>
    public class HouseholdTests
    {
        readonly Xunit.Abstractions.ITestOutputHelper _out;
        public HouseholdTests(Xunit.Abstractions.ITestOutputHelper output) { _out = output; }

        static ContentDatabase Shipped()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Assets", "Content"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return ContentLoader.Load(new DirectoryContentSource(Path.Combine(dir.FullName, "Assets", "Content"))).Database;
        }

        static readonly ContentDatabase Content = Shipped();

        /// <summary>A settlement with no island: fed, roofed, stocked with food, and only eating and being born.</summary>
        static SimWorld Fed(ulong seed, int people)
        {
            var world = new SimWorld(seed, Content, VoxelTypes.FromContent(Content));
            DriveRules rules = DriveRules.FromContent(Content);
            Settlement s = Settlement.Found("fed", new Int3(100, 50, 100), null, people, rules, 0, world.Annals, RecordId.None);
            s.Genome = new Genome(GeneTable.FromContent(Content));
            s.ShelterCapacity = 100000;
            s.Food = 1e9;
            Households.Found(s, HouseholdRules.FromContent(Content), 0, world.Annals);
            world.Settlements.Add(s);
            world.Add(new Subsistence(rules));
            return world;
        }

        [Fact]
        public void TheBiggerTheVillageTheMoreChildren()
        {
            SimWorld small = Fed(1, 20), large = Fed(1, 100);
            small.RunYears(1);
            large.RunYears(1);
            int a = small.Settlements[0].Born, b = large.Settlements[0].Born;
            _out.WriteLine("a year: " + a + " born to twenty, " + b + " to a hundred");
            Assert.True(a > 0);
            Assert.True(b > a * 3, "births did not scale with people: " + a + " against " + b);
        }

        [Fact]
        public void AFamilyTooBigForOneHearthSplitsAndSaysSo()
        {
            SimWorld world = Fed(2, 6);
            Settlement s = world.Settlements[0];
            HouseholdRules rules = s.HouseholdRules;
            Household first = s.Households[0];
            int limit = rules.SplitsAbove(s.Genome);

            int families = s.Households.Count;
            while (first.Size <= limit)
            {
                Agent parent = s.People[0];
                Agent child = s.Add(world.Streams);
                RecordId born = world.Annals.Write(0, Subsistence.BornKind, child.Id, s.Hearth, s.Founded);
                Households.Born(s, child, parent, 0, world.Annals, born);
                if (s.Households.Count > families) break;
            }

            Assert.Equal(families + 1, s.Households.Count);
            Household young = s.Households[s.Households.Count - 1];
            Assert.False(young.Housed);
            Assert.True(young.Size > 0);
            Assert.True(first.Size <= limit);

            int inFamilies = 0;
            foreach (Household h in s.Households) inFamilies += h.Size;
            Assert.Equal(s.People.Count, inFamilies);
            Assert.NotEmpty(world.Annals.OfKind(Households.FormedKind));
        }

        [Fact]
        public void SomeoneLostLeavesTheirFamilyAndAnEmptyFamilyIsGone()
        {
            SimWorld world = Fed(3, 3);
            Settlement s = world.Settlements[0];
            Assert.Single(s.Households);
            while (s.People.Count > 0) s.Remove(0, world.Streams);
            Assert.Empty(s.Households);
        }

        [Fact]
        public void AFinishedHouseTakesInAFamilyAndACrowdedFamilyFeelsItAtItsOwnDoor()
        {
            WorldChoice choice = WorldChoice.Pick(Content, "green-shore");
            SimWorld world = SettlementInvariants.Settled(Content, choice, 20, TestIslands.Generate)(7);
            Settlement s = world.Settlements[0];

            for (int day = 0; day < 500 && !AnyHoused(s); day++)
                for (int t = 0; t < world.Clock.TicksPerDay; t++) world.Tick();
            Assert.True(AnyHoused(s), "no family had a roof within 500 days");
            Assert.NotEmpty(world.Annals.OfKind(Households.MovedInKind));

            // Crowd one housed family well past its beds, then let a night pass.
            Household home = null;
            foreach (Household h in s.Households) if (h.Housed) { home = h; break; }
            Households.Settle(s);
            int over = home.Beds - home.Size + 3;
            for (int i = 0; i < over; i++)
            {
                Agent child = s.Add(world.Streams);
                Households.Join(s, child, home);
            }
            do world.Tick(); while (world.Clock.TickOfDay != world.Clock.TicksPerDay - 1);

            Households.Settle(s);
            Assert.True(home.Crowded);
            int crowdedHere = 0, crowdedElsewhere = 0;
            foreach (Agent a in s.People)
            {
                if (!a.Crowded) continue;
                if (a.Household == home.Number) crowdedHere++; else crowdedElsewhere++;
            }
            _out.WriteLine(crowdedHere + " crowded in the full family, " + crowdedElsewhere + " elsewhere");
            Assert.True(crowdedHere > 0);
            Assert.True(crowdedHere >= crowdedElsewhere);
        }

        static bool AnyHoused(Settlement s)
        {
            foreach (Household h in s.Households) if (h.Housed) return true;
            return false;
        }
    }
}
