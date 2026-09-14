using System.Collections.Generic;
using System.IO;
using Godless.Sim.Content;
using Godless.Sim.Core;
using Godless.Sim.Drives;
using Godless.Sim.Harness;
using Godless.Sim.Settlements;
using Godless.Sim.World;
using Xunit;

namespace Godless.Sim.Tests
{
    /// <summary>
    /// Individual needs and the actions people take for them (S2V).
    ///
    /// The tell: one person kneels at the water's edge while another eats by
    /// the store and a third walks off to their own bed — each for their own
    /// reason, and a hungry one on bare ground goes hungry.
    /// </summary>
    public class NeedsTests
    {
        readonly Xunit.Abstractions.ITestOutputHelper _out;
        public NeedsTests(Xunit.Abstractions.ITestOutputHelper output) { _out = output; }

        static ContentDatabase Shipped()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Assets", "Content"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return ContentLoader.Load(new DirectoryContentSource(Path.Combine(dir.FullName, "Assets", "Content"))).Database;
        }

        static readonly ContentDatabase Content = Shipped();

        static SimWorld Settled(ulong seed)
        {
            WorldChoice choice = WorldChoice.Pick(Content, "green-shore");
            return SettlementInvariants.Settled(Content, choice, 20, TestIslands.Generate)(seed);
        }

        /// <summary>Every need but shelter, which only building meets, has something a person can go and do about it.</summary>
        [Fact]
        public void EveryNeedHasAnActionThatMeetsIt()
        {
            DriveRules rules = DriveRules.FromContent(Content);
            int shelter = rules.Needs.IndexOf("shelter");
            for (int n = 0; n < rules.Needs.Count; n++)
            {
                if (n == shelter) continue;
                bool met = false;
                foreach (Activity a in rules.Activities.All) if (a.ReliefFor(n) > 0.0) met = true;
                Assert.True(met, rules.Needs[n].Name + " has no action that relieves it");
            }
            Assert.Equal(ActionPlace.Water, rules.Activities[rules.Activities.IndexOf("drink")].At);
            Assert.Equal(ActionPlace.Store, rules.Activities[rules.Activities.IndexOf("eat")].At);
            Assert.Equal(ActionPlace.Bed, rules.Activities[rules.Activities.IndexOf("sleep")].At);
        }

        /// <summary>Every pose an activity asks for has a figure to draw it, dressed by the view.</summary>
        [Fact]
        public void EveryPoseHasAFigure()
        {
            DriveRules rules = DriveRules.FromContent(Content);
            Voxels.DetailModelTable models = Voxels.DetailModelTable.FromContent(Content);
            Assert.Empty(models.Problems);
            var poses = new HashSet<string> { "stand", "work", "lie" };
            foreach (Activity a in rules.Activities.All) poses.Add(a.Pose);
            foreach (string pose in poses)
            {
                Voxels.DetailModel figure = models.Find("person-" + pose);
                Assert.True(figure != null, "no person-" + pose + " model for the '" + pose + "' pose");
                foreach (string slot in new[] { "clothes", "skin", "hair" }) Assert.Contains(slot, figure.Slots);
            }
            Assert.NotNull(models.Find("person-walk-a"));
            Assert.NotNull(models.Find("person-walk-b"));
        }

        /// <summary>People are seen doing their errands where those errands are done, and it keeps them well.</summary>
        [Fact]
        public void PeopleDrinkAtTheWaterAndEatAtTheStore()
        {
            SimWorld world = Settled(7);
            Settlement s = world.Settlements[0];
            List<Int3> water = Places.WaterPoints(s, world.Island);
            Assert.NotEmpty(water);

            int drinking = 0, eating = 0;
            for (int day = 0; day < 60; day++)
                for (int t = 0; t < world.Clock.TicksPerDay; t++)
                {
                    world.Tick();
                    foreach (Agent a in s.People)
                    {
                        if (!a.Arrived) continue;
                        if (a.Doing.StartsWith("drinking"))
                        {
                            int near = int.MaxValue;
                            foreach (Int3 w in water)
                                near = System.Math.Min(near, System.Math.Max(System.Math.Abs(w.X - a.X), System.Math.Abs(w.Z - a.Z)));
                            Assert.True(near <= Places.Reach + 1, a.Doing + " " + near + " voxels from any water");
                            drinking++;
                        }
                        if (a.Doing.StartsWith("eating")) eating++;
                    }
                }
            _out.WriteLine(drinking + " seen drinking, " + eating + " seen eating");
            Assert.True(drinking > 0, "nobody was seen drinking in sixty days");
            Assert.True(eating > 0, "nobody was seen eating in sixty days");

            DriveRules rules = DriveRules.FromContent(Content);
            foreach (string need in new[] { "thirst", "hunger", "rest" })
            {
                int n = rules.Needs.IndexOf(need);
                double sum = 0.0;
                foreach (Agent a in s.People) sum += a.Level(n);
                double mean = sum / s.People.Count;
                _out.WriteLine(need + " " + mean.ToString("0.00"));
                Assert.True(mean < rules.Needs[n].Threshold, need + " averages " + mean.ToString("0.00") + " after sixty days of looking after it");
            }
        }

        /// <summary>
        /// Needs come round about once a week, so what one person does can be
        /// followed: counted over eight weeks, people eat and drink every five
        /// to twelve days, not every day.
        /// </summary>
        [Fact]
        public void PeopleEatAndDrinkAboutOnceAWeek()
        {
            SimWorld world = Settled(7);
            Settlement s = world.Settlements[0];
            for (int t = 0; t < 14 * world.Clock.TicksPerDay; t++) world.Tick();   // settle past the founding levels

            var meals = new Dictionary<ulong, int>();
            var drinks = new Dictionary<ulong, int>();
            const int days = 56;
            for (int t = 0; t < days * world.Clock.TicksPerDay; t++)
            {
                world.Tick();
                foreach (Agent a in s.People)
                {
                    if (a.Errands.Contains("eating")) meals[a.Id.Hash] = (meals.TryGetValue(a.Id.Hash, out int m) ? m : 0) + 1;
                    if (a.Errands.Contains("drinking")) drinks[a.Id.Hash] = (drinks.TryGetValue(a.Id.Hash, out int d) ? d : 0) + 1;
                }
            }

            int people = s.People.Count;
            int eaten = 0, drunk = 0;
            foreach (int n in meals.Values) eaten += n;
            foreach (int n in drinks.Values) drunk += n;
            double daysPerMeal = (double)days * people / System.Math.Max(1, eaten);
            double daysPerDrink = (double)days * people / System.Math.Max(1, drunk);
            _out.WriteLine(people + " people: a meal every " + daysPerMeal.ToString("0.0") + " days, a drink every " + daysPerDrink.ToString("0.0"));
            Assert.InRange(daysPerDrink, 5.0, 12.0);
            // Foragers eat some of what they find, so meals from the store can come less often than drinks.
            Assert.InRange(daysPerMeal, 5.0, 20.0);
        }

        /// <summary>An errand that eats takes from the store, and an empty store feeds nobody.</summary>
        [Fact]
        public void AnEmptyStoreFeedsNobody()
        {
            SimWorld world = Settled(7);
            Settlement s = world.Settlements[0];
            for (int day = 0; day < 5; day++)
                for (int t = 0; t < world.Clock.TicksPerDay; t++) world.Tick();
            while (world.Clock.TickOfDay != 0) world.Tick();

            // Two weeks with the store kept empty: a meal is a week's worth, and
            // nobody eats one that is not there.
            for (int t = 0; t < 14 * world.Clock.TicksPerDay; t++)
            {
                s.Food = 0.0;
                world.Tick();
                foreach (Agent a in s.People)
                {
                    Assert.DoesNotContain("eating", a.Errands);
                    Assert.False(a.Doing.StartsWith("eating"), a.Doing);
                }
            }

            // Hungry by now, and the store full again: they come and eat.
            s.Food = 1000.0;
            int ate = 0;
            for (int t = 0; t < 8; t++)
            {
                world.Tick();
                foreach (Agent a in s.People) if (a.Errands.Contains("eating")) ate++;
            }
            Assert.True(ate > 0, "nobody ate from a full store in two days after two hungry weeks");
            Assert.True(s.Food < 1000.0);
        }

        /// <summary>A bed sleeps one: owners and guests in spare beds never double up.</summary>
        [Fact]
        public void NoTwoSleepersShareABed()
        {
            SimWorld world = Settled(7);
            Settlement s = world.Settlements[0];
            int nights = 0, guests = 0;
            for (int day = 0; day < 600; day++)
                for (int t = 0; t < world.Clock.TicksPerDay; t++)
                {
                    world.Tick();
                    if (world.Clock.TickOfDay != world.Clock.TicksPerDay - 1) continue;
                    var taken = new HashSet<int>();
                    bool any = false;
                    foreach (Agent a in s.People)
                    {
                        Build.Furnishing.Bed bed;
                        if (!Places.SleepsIn(s, a, out bed)) continue;
                        Assert.True(taken.Add(bed.Instance), "two people in bed " + bed.Instance + " on day " + day);
                        any = true;
                        if (a.Doing.Contains("spare bed")) guests++;
                    }
                    if (any) nights++;
                }
            _out.WriteLine(nights + " nights checked, " + guests + " guest-nights in spare beds");
            Assert.True(nights > 0, "nobody slept in a bed in 600 days");
        }

        /// <summary>Each person sleeps in their own bed once the family has one.</summary>
        [Fact]
        public void TheHousedSleepInTheirOwnBeds()
        {
            SimWorld world = Settled(7);
            Settlement s = world.Settlements[0];
            int checkedSleepers = 0;
            for (int day = 0; day < 500 && checkedSleepers == 0; day++)
            {
                for (int t = 0; t < world.Clock.TicksPerDay; t++)
                {
                    world.Tick();
                    if (world.Clock.TickOfDay != world.Clock.TicksPerDay - 1) continue;
                    foreach (Agent a in s.People)
                        {
                            Build.Furnishing.Bed bed;
                            if (!a.Arrived || !a.Doing.StartsWith("asleep in bed") || !Places.SleepsIn(s, a, out bed)) continue;
                            Assert.True(System.Math.Abs(a.X - bed.Centre.X) <= Places.Reach && System.Math.Abs(a.Z - bed.Centre.Z) <= Places.Reach,
                                        a.Doing + " at " + a.X + "," + a.Z + ", bed at " + bed.Centre.X + "," + bed.Centre.Z);
                            checkedSleepers++;
                        }
                }
            }
            _out.WriteLine(checkedSleepers + " sleepers checked");
            Assert.True(checkedSleepers > 0, "nobody slept in a bed of their own in 500 days");
        }
    }
}
