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
    /// A person's tick drawn as it was spent (S2W): the walk out, the work,
    /// the errands on the way, the small things between.
    ///
    /// The tell: a line of people walking out of the village in the morning,
    /// one stopping at the river on the way, and coming back in the evening.
    /// </summary>
    public class DayTests
    {
        readonly Xunit.Abstractions.ITestOutputHelper _out;
        public DayTests(Xunit.Abstractions.ITestOutputHelper output) { _out = output; }

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
            SimWorld world = SettlementInvariants.Settled(Content, choice, 20, TestIslands.Generate)(seed);
            Assert.NotEmpty(world.Settlements);
            return world;
        }

        [Fact]
        public void PastimesAllLoad()
        {
            PastimeTable table = PastimeTable.FromContent(Content);
            Assert.Empty(table.Problems);
            Assert.NotEmpty(table.All);
            Voxels.DetailModelTable models = Voxels.DetailModelTable.FromContent(Content);
            foreach (Pastime p in table.All)
                Assert.True(models.Find("person-" + (p.Pose == "walk" ? "walk-a" : p.Pose)) != null, p.Name + " asks for a pose nobody can be drawn in");
            foreach (PastimeWhen when in new[] { PastimeWhen.Morning, PastimeWhen.Midday, PastimeWhen.Evening, PastimeWhen.Idle })
                Assert.Contains(table.All, p => p.Fits(when));
        }

        /// <summary>Every tick is whole: it starts where they stood, each stretch starts where the last ended, and it ends where they stand.</summary>
        [Fact]
        public void EveryTickStartsWhereTheyStoodAndEndsWhereTheyStand()
        {
            SimWorld world = Settled(7);
            Settlement s = world.Settlements[0];
            var before = new Dictionary<ulong, Int3>();
            long legs = 0, walks = 0;

            for (int t = 0; t < 120 * world.Clock.TicksPerDay; t++)
            {
                before.Clear();
                foreach (Agent a in s.People) before[a.Id.Hash] = new Int3(a.X, 0, a.Z);
                world.Tick();
                foreach (Agent a in s.People)
                {
                    IReadOnlyList<Leg> day = a.Day.Legs;
                    Assert.NotEmpty(day);
                    Int3 was;
                    if (before.TryGetValue(a.Id.Hash, out was))
                        Assert.True(day[0].FromX == was.X && day[0].FromZ == was.Z, "person " + a.Index + "'s tick starts somewhere they were not");
                    Assert.Equal(0.0, day[0].Start);
                    Assert.Equal(1.0, day[day.Count - 1].End);
                    Assert.True(day[day.Count - 1].ToX == a.X && day[day.Count - 1].ToZ == a.Z,
                                "person " + a.Index + "'s tick ends at " + day[day.Count - 1].ToX + "," + day[day.Count - 1].ToZ + " but they stand at " + a.X + "," + a.Z);
                    for (int k = 0; k < day.Count; k++)
                    {
                        Leg leg = day[k];
                        Assert.True(leg.End >= leg.Start, "a stretch that ends before it starts");
                        Assert.False(string.IsNullOrEmpty(leg.Pose));
                        if (k > 0)
                        {
                            Assert.Equal(day[k - 1].End, leg.Start);
                            Assert.True(day[k - 1].ToX == leg.FromX && day[k - 1].ToZ == leg.FromZ, "a stretch starts where the last did not end: " + leg.Doing);
                        }
                        legs++;
                        if (leg.Moves) walks++;
                    }
                }
            }
            _out.WriteLine(legs + " stretches, " + walks + " of them walks");
            Assert.True(walks > legs / 10, "hardly anyone walked anywhere");
        }

        /// <summary>People walk out to what they do and take the time to do the small things: different ones, over a season.</summary>
        [Fact]
        public void AVillageDoesMoreThanWalkToWorkAndBack()
        {
            SimWorld world = Settled(7);
            Settlement s = world.Settlements[0];
            var pastimes = new HashSet<string>();
            foreach (Pastime p in PastimeTable.FromContent(Content).All) pastimes.Add(p.Doing);
            var seen = new SortedDictionary<string, int>();
            int walkingOut = 0;

            for (int t = 0; t < 200 * world.Clock.TicksPerDay; t++)
            {
                world.Tick();
                foreach (Agent a in s.People)
                    foreach (Leg leg in a.Day.Legs)
                    {
                        if (pastimes.Contains(leg.Doing)) { int n; seen.TryGetValue(leg.Doing, out n); seen[leg.Doing] = n + 1; }
                        if (leg.Moves && leg.Doing.StartsWith("on the way: felling")) walkingOut++;
                    }
            }
            foreach (KeyValuePair<string, int> kv in seen) _out.WriteLine(kv.Key + ": " + kv.Value);
            _out.WriteLine(walkingOut + " walks out to a tree");
            Assert.True(seen.Count >= 6, "only " + seen.Count + " different pastimes in two hundred days");
            Assert.True(walkingOut > 0, "nobody was seen walking out to fell a tree");
        }

        /// <summary>The same seed spends every tick the same way (L2): the drawn day is part of the world, not the machine.</summary>
        [Fact]
        public void TheSameSeedSpendsTheSameDay()
        {
            SimWorld one = Settled(11), two = Settled(11);
            for (int t = 0; t < 40 * one.Clock.TicksPerDay; t++) { one.Tick(); two.Tick(); }
            Settlement a = one.Settlements[0], b = two.Settlements[0];
            Assert.Equal(a.People.Count, b.People.Count);
            for (int i = 0; i < a.People.Count; i++)
            {
                IReadOnlyList<Leg> x = a.People[i].Day.Legs, y = b.People[i].Day.Legs;
                Assert.Equal(x.Count, y.Count);
                for (int k = 0; k < x.Count; k++)
                {
                    Assert.Equal(x[k].Start, y[k].Start);
                    Assert.Equal(x[k].ToX, y[k].ToX);
                    Assert.Equal(x[k].Doing, y[k].Doing);
                }
            }
        }
    }
}
