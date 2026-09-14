using System.Collections.Generic;
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
    /// A town's first fire and what it becomes (S2Z).
    ///
    /// The tell: the same spot in the middle of the village at every age of it,
    /// a ring of stones, then a ring of logs, then a paved square, then a hall
    /// with the old fire for its hearth, and the whole village standing there
    /// the evening after a death.
    /// </summary>
    public class CommonsTests
    {
        readonly Xunit.Abstractions.ITestOutputHelper _out;
        public CommonsTests(Xunit.Abstractions.ITestOutputHelper output) { _out = output; }

        static ContentDatabase Shipped()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Assets", "Content"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return ContentLoader.Load(new DirectoryContentSource(Path.Combine(dir.FullName, "Assets", "Content"))).Database;
        }

        static readonly ContentDatabase Content = Shipped();

        static SimWorld Settled(ulong seed, int people)
        {
            WorldChoice choice = WorldChoice.Pick(Content, "green-shore");
            SimWorld world = SettlementInvariants.Settled(Content, choice, people, TestIslands.Generate)(seed);
            Assert.NotEmpty(world.Settlements);
            return world;
        }

        [Fact]
        public void TheCommonsContentAllLoads()
        {
            CommonsRules rules = CommonsRules.FromContent(Content, DriveRules.FromContent(Content).Needs);
            Assert.NotNull(rules);
            Assert.Empty(rules.Problems);
            Assert.True(rules.Stages.Count >= 3);
            Assert.NotEmpty(rules.Gatherings);
            DetailModelTable models = DetailModelTable.FromContent(Content);
            Assert.Empty(models.Problems);
            Assert.NotNull(models.Find(rules.Stages[0].Fire));
            foreach (KeyValuePair<Symbol, string> seat in rules.SeatModels) Assert.NotNull(models.Find(seat.Value));

            // The last stage is commissioned as a building, and content can build both kinds of it, each with a way in.
            var kinds = IntentKindTable.FromContent(Content, DriveRules.FromContent(Content).Needs);
            Assert.Empty(kinds.Problems);
            var genes = GeneTable.FromContent(Content);
            GrammarTable grammars = GrammarTable.FromContent(Content, genes);
            Assert.Empty(grammars.Problems);
            foreach (string name in new[] { "hall", "stoa" })
            {
                Assert.Contains(kinds.All, k => k.Name == name && k.Purpose == IntentPurpose.Commons);
                Blueprint plan = grammars.For(name).Build(new Genome(genes), Palette.FromContent(Content), 64, 64, 2600);
                Assert.True(plan.Count(Symbol.For("role.door")) > 0, name + " has no way in");
                Assert.True(plan.Width - 2 * Grammar.Margin >= 12, name + " is " + plan.Width + " wide");
                _out.WriteLine(name + ": " + plan.Width + " x " + plan.Depth + " x " + plan.Height + ", " + plan.Volume + " cells");
            }
        }

        /// <summary>The day the fire is lit it is drawn, and ground is kept round it that nobody builds on and everybody walks across.</summary>
        [Fact]
        public void TheFireIsDrawnAndItsGroundKeptFromTheDayItIsLit()
        {
            SimWorld world = Settled(7, 20);
            Settlement s = world.Settlements[0];
            world.Tick();
            Commons c = s.Commons;
            Assert.NotNull(c);
            Assert.Equal(0, c.Stage);
            Assert.True(c.Fire >= 0, "the fire is not drawn");
            DetailInstance fire = world.Details.Get(c.Fire);
            Assert.NotNull(fire);
            Assert.True(System.Math.Abs(fire.VoxelX - s.Hearth.X) <= 1 && System.Math.Abs(fire.VoxelZ - s.Hearth.Z) <= 1);
            Assert.True(s.IsCommons(s.HearthParcelX, s.HearthParcelZ));
            Assert.True(s.IsCommons(s.HearthParcelX + 1, s.HearthParcelZ) || !s.IsClaimed(s.HearthParcelX + 1, s.HearthParcelZ));
            Assert.Equal(Commons.KeptKind, world.Annals.Get(c.Kept).Kind);

            // Ground kept round it, not only the fire's own parcel.
            int kept = 0;
            for (int dz = -3; dz <= 3; dz++)
                for (int dx = -3; dx <= 3; dx++)
                    if (s.IsCommons(s.HearthParcelX + dx, s.HearthParcelZ + dz)) kept++;
            _out.WriteLine(kept + " parcels kept round the fire");
            Assert.True(kept >= 9, "only " + kept + " parcels kept round the fire");
        }

        /// <summary>Some evenings people go to the fire, and whoever goes is seen walking there and sitting round it.</summary>
        [Fact]
        public void SomeEveningsPeopleGatherAtTheFireAndAreSeenThere()
        {
            SimWorld world = Settled(7, 20);
            Settlement s = world.Settlements[0];
            int gatherings = 0, seenThere = 0;
            for (int t = 0; t < 90 * world.Clock.TicksPerDay; t++)
            {
                world.Tick();
                Commons c = s.Commons;
                if (c.Tonight == null || world.Clock.TickOfDay != world.Clock.TicksPerDay - 1) continue;
                gatherings++;
                Assert.Equal(Commons.GatheredKind, world.Annals.Get(c.Tonight.Record).Kind);
                Assert.True(world.Annals.Get(c.Tonight.Record).Cause.Exists);
                foreach (Agent a in s.People)
                    foreach (Leg leg in a.Day.Legs)
                    {
                        if (leg.Moves || !leg.Doing.StartsWith(c.Tonight.Kind.Doing)) continue;
                        Assert.True(c.Attends(a), "someone who did not go is seen at the gathering");
                        double d = System.Math.Sqrt((leg.ToX - s.Hearth.X) * (leg.ToX - s.Hearth.X) + (leg.ToZ - s.Hearth.Z) * (leg.ToZ - s.Hearth.Z));
                        Assert.True(d <= 30.0, leg.Doing + " " + d.ToString("0") + " voxels from the fire");
                        seenThere++;
                    }
            }
            _out.WriteLine(gatherings + " gatherings, " + seenThere + " people seen at them");
            Assert.True(gatherings > 0, "nobody gathered at the fire in ninety days");
            Assert.True(seenThere > 0, "a gathering nobody was seen at");
        }

        /// <summary>A town too big for its campfire lays logs round it, from its own timber, and the gatherings move onto them.</summary>
        [Fact]
        public void GatheringsTooBigForTheCampfireGrowItIntoAFireCircle()
        {
            SimWorld world = Settled(7, 70);
            Settlement s = world.Settlements[0];
            CommonsRules rules = CommonsRules.FromContent(Content, DriveRules.FromContent(Content).Needs);
            world.Tick();
            int timber = -1;
            for (int m = 0; m < s.Stock.Materials.Count; m++)
                if (s.Stock.Materials[m].Class == Symbol.For("class.timber")) { timber = m; break; }
            Assert.True(timber >= 0);
            s.Stock.Add(timber, 400);

            int day = 0;
            for (; day < 150 && s.Commons.Stage < 1; day++)
                for (int t = 0; t < world.Clock.TicksPerDay; t++) world.Tick();
            _out.WriteLine("stage " + s.Commons.Stage + " after " + day + " days, " + s.Commons.GatheringsHeld + " gatherings, " + s.Commons.Seats.Count + " seats");
            Assert.True(s.Commons.Stage >= 1, "seventy people never outgrew a campfire for sixteen");
            Assert.Equal(rules.Stages[1].Seats, s.Commons.Seats.Count);
            foreach (int seat in s.Commons.Seats)
            {
                DetailInstance d = world.Details.Get(seat);
                Assert.NotNull(d);
                AnnalRecord works = world.Annals.Get(d.Cause);
                Assert.Equal(Commons.WorksKind, works.Kind);
                // The works were begun because a gathering was too big.
                Assert.Equal(Commons.GatheredKind, world.Annals.Get(works.Cause).Kind);
            }
        }
    }
}
