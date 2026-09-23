using System.Collections.Generic;
using System.IO;
using Godless.Sim.Annals;
using Godless.Sim.Chronicle;
using Godless.Sim.Content;
using Godless.Sim.Core;
using Godless.Sim.Harness;
using Godless.Sim.Voxels;
using Godless.Sim.World;
using Xunit;

namespace Godless.Sim.Tests
{
    /// <summary>
    /// v2 M0: the clocks, the one door every god power goes through, and the
    /// feed that tells the player what happened.
    /// </summary>
    public class FoundationTests
    {
        static ContentDatabase Shipped()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Assets", "Content"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return ContentLoader.Load(new DirectoryContentSource(Path.Combine(dir.FullName, "Assets", "Content"))).Database;
        }

        static readonly ContentDatabase Content = Shipped();

        sealed class Sandbox
        {
            public SimWorld World;
            public ParcelGrid Grid;
            public GodHand Hand;
            public int X, Z;   // a flat dry column near the middle of the land
        }

        static Sandbox Make(ulong seed = 7)
        {
            WorldChoice choice = WorldChoice.Pick(Content, "green-shore");
            var world = new SimWorld(seed, Content, VoxelTypes.FromContent(Content));
            world.Island = TestIslands.Generate(world.Voxels.Store, world.Streams, choice.Biomes, world.VoxelTypes, choice.Preset, choice.Features);
            world.BeginHistory();
            ConstraintFields fields;
            ParcelGrid grid = Survey.Of(world, Content, choice.Biomes, out fields);
            world.Add(new GroundSystem(grid, fields, choice.Biomes)).Add(new DepositSystem(grid));
            int px, pz;
            Assert.True(Survey.FlattestNearWater(grid, world.Island, choice.Biomes, Symbol.None, out px, out pz));
            return new Sandbox
            {
                World = world, Grid = grid, Hand = new GodHand(world, grid),
                X = px * ParcelGrid.Size + 2, Z = pz * ParcelGrid.Size + 2,
            };
        }

        [Fact]
        public void TimeIsContentAndAYearIsHalfAMinuteAt1x()
        {
            TimeRules t = TimeRules.FromContent(Content);
            Assert.Equal(1, t.TicksPerDay);
            Assert.Equal(360, t.DaysPerYear);
            Assert.Equal(30, t.DaysPerMonth);
            Assert.InRange(t.SecondsPerYearAt1x, 20.0, 60.0);

            var clock = t.NewClock();
            int months = 0, years = 0;
            for (int i = 0; i < 720; i++)
            {
                clock.Advance();
                if (clock.IsFirstTickOfMonth) months++;
                if (clock.IsFirstTickOfYear) years++;
            }
            Assert.Equal(24, months);
            Assert.Equal(2, years);
            Assert.Equal(0, clock.Month);
            Assert.Equal(2, clock.Year);
        }

        /// <summary>A power submitted between steps lands at the start of the next, before any system, and every system that step sees it.</summary>
        [Fact]
        public void APowerLandsInTheNextStepAndTheGroundKnowsItThatStep()
        {
            Sandbox s = Make();
            for (int i = 0; i < 5; i++) s.World.Tick();
            int px = s.X / ParcelGrid.Size, pz = s.Z / ParcelGrid.Size;
            double before = s.Grid.Height[px, pz];

            long submitted = s.World.Clock.Tick;
            s.World.Commands.Submit(new RaiseGround(s.Hand, new Int3(s.X, 0, s.Z), 10, 12), submitted);
            Assert.Equal(1, s.World.Commands.Pending);
            s.World.Tick();
            s.World.Tick();   // the ground system reads marked ground at the start of the step after it was written

            CommandEntry e = s.World.Commands.Applied[0];
            Assert.Equal(submitted + 1, e.AppliedAt);
            Assert.Equal(GodHand.RaisedKind, s.World.Annals.Get(e.Record).Kind);
            Assert.Equal(e.AppliedAt, s.World.Annals.Get(e.Record).Tick);
            Assert.True(s.Grid.Height[px, pz] > before + 5, "the planning grid did not see the hill");
        }

        /// <summary>Commands land in the order they were given, and the same commands at the same steps give the same world (L2).</summary>
        [Fact]
        public void TheSameCommandsAtTheSameStepsGiveTheSameWorld()
        {
            ulong Run()
            {
                Sandbox s = Make(11);
                for (int t = 0; t < 60; t++)
                {
                    if (t % 7 == 3) s.World.Commands.Submit(new RaiseGround(s.Hand, new Int3(s.X + t, 0, s.Z), 6, 4), s.World.Clock.Tick);
                    if (t % 11 == 5) s.World.Commands.Submit(new LowerGround(s.Hand, new Int3(s.X, 0, s.Z + t), 5, 2), s.World.Clock.Tick);
                    s.World.Tick();
                }
                var order = new List<long>();
                foreach (CommandEntry e in s.World.Commands.Applied) order.Add(e.SubmittedAt);
                for (int i = 1; i < order.Count; i++) Assert.True(order[i] >= order[i - 1]);
                return s.World.Annals.Digest() ^ s.World.Voxels.Store.Digest();
            }
            Assert.Equal(Run(), Run());
        }

        /// <summary>Landing while paused: an act on the untouched island takes the world's next whole step, and lands once.</summary>
        [Fact]
        public void LandingWhilePausedOnAClosedPresentLandsOnce()
        {
            Sandbox s = Make();
            Assert.Equal(0, s.World.Clock.Tick);
            s.World.Commands.Submit(new RaiseGround(s.Hand, new Int3(s.X, 0, s.Z), 6, 8), 0);
            Assert.Equal(1, s.World.Commands.ApplyNow(s.World));
            Assert.Equal(1, s.World.Clock.Tick);
            Assert.Single(s.World.Commands.Applied);
            Assert.Equal(0, s.World.Commands.Pending);
            Assert.Single(s.World.Annals.OfKind(GodHand.RaisedKind));
        }

        [Fact]
        public void TheFeedTellsThePlayerWhatTheGodDidAndWhere()
        {
            EventFeed feed = EventFeed.FromContent(Content);
            Assert.Empty(feed.Problems);
            Assert.True(feed.Tells(GodHand.RaisedKind));
            Assert.True(feed.Tells(GodHand.LoweredKind));

            Sandbox s = Make();
            feed.SkipTo(s.World.Annals);
            s.World.Commands.Submit(new RaiseGround(s.Hand, new Int3(s.X, 0, s.Z), 6, 4), s.World.Clock.Tick);
            s.World.Tick();
            List<FeedItem> items = feed.Read(s.World.Annals);
            Assert.Single(items);
            Assert.Equal(s.X, items[0].Place.X);
            Assert.Equal(s.Z, items[0].Place.Z);
            Assert.False(string.IsNullOrEmpty(items[0].Text));
            Assert.Empty(feed.Read(s.World.Annals));   // read once
        }

        [Theory]
        [InlineData("{\"type\":\"feed\",\"id\":\"x\",\"text\":\"hi\"}", "names no record kind")]
        [InlineData("{\"type\":\"feed\",\"id\":\"x\",\"kind\":\"a.b\",\"text\":\" \"}", "has no text")]
        [InlineData("{\"type\":\"feed\",\"id\":\"x\",\"kind\":\"a.b\",\"text\":\"hi\",\"importance\":7}", "must be 1, 2 or 3")]
        public void ABadFeedLineIsRefusedAndSaysWhy(string doc, string expected)
        {
            var src = new MemoryContentSource().Add("base", "mod.json", "{}").Add("base", "f.json", doc);
            EventFeed feed = EventFeed.FromContent(ContentLoader.Load(src).Database);
            Assert.Single(feed.Problems);
            Assert.Contains(expected, feed.Problems[0]);
            Assert.Equal(0, feed.LineCount);
        }
    }
}
