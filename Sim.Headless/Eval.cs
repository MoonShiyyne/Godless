using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Godless.Sim.Chronicle;
using Godless.Sim.Content;
using Godless.Sim.Core;
using Godless.Sim.Harness;
using Godless.Sim.Voxels;
using Godless.Sim.World;

namespace Godless.Sim.Headless
{
    /// <summary>
    /// `sim eval`: the game measured against what it is for (v2).
    ///
    /// The v1 behaviour check found a town that stopped building in year 8
    /// that 400 passing tests had not; this is that check made permanent. Each
    /// milestone adds goals — a measurable sentence and a threshold — and the
    /// command prints a scorecard and exits 1 on any miss, so it can gate a
    /// commit the way Tools/verify.sh does. Wall-clock timing lives here, not
    /// in the sim or the tests, which is where CLAUDE.md puts it.
    /// </summary>
    static class Eval
    {
        sealed class Goal
        {
            public string Milestone, Text, Measured;
            public bool Met;
        }

        public static int Run(string contentRoot, string mapName, ulong seed, int years)
        {
            var c = CultureInfo.InvariantCulture;
            ContentDatabase content = ContentLoader.Load(new DirectoryContentSource(contentRoot)).Database;
            var goals = new List<Goal>();
            void Add(string milestone, string text, bool met, string measured)
            {
                goals.Add(new Goal { Milestone = milestone, Text = text, Met = met, Measured = measured });
            }

            // Time: a year in a sitting.
            TimeRules time = TimeRules.FromContent(content);
            Add("M0", "a year passes in 20-60 real seconds at 1x", time.SecondsPerYearAt1x >= 20 && time.SecondsPerYearAt1x <= 60,
                time.SecondsPerYearAt1x.ToString("0.#", c) + " s");

            EventFeed feed = EventFeed.FromContent(content);
            Add("M0", "every feed line content declares loads", feed.Problems.Count == 0,
                feed.LineCount + " lines, " + feed.Problems.Count + " refused");

            // An empty world, stepped: the budget everything later spends.
            GodHand hand;
            ParcelGrid grid;
            int fx, fz;
            SimWorld world = Make(content, mapName, seed, out grid, out hand, out fx, out fz);
            int steps = world.Clock.TicksInYears(years) > int.MaxValue ? int.MaxValue : (int)world.Clock.TicksInYears(years);
            var watch = Stopwatch.StartNew();
            for (int i = 0; i < steps; i++) world.Tick();
            double msPerStep = watch.Elapsed.TotalMilliseconds / steps;
            Add("M0", "an empty world steps in 2 ms or less (room for 16x at 60 fps)", msPerStep <= 2.0,
                msPerStep.ToString("0.000", c) + " ms a step over " + years + " years");

            // A god power: submitted, landed, known to the ground, told to the player.
            feed.SkipTo(world.Annals);
            int px = fx / ParcelGrid.Size, pz = fz / ParcelGrid.Size;
            double before = grid.Height[px, pz];
            long submitted = world.Clock.Tick;
            world.Commands.Submit(new RaiseGround(hand, new Int3(fx, 0, fz), 10, 12), submitted);
            int until = 0;
            while (grid.Height[px, pz] <= before + 5 && until < 100) { world.Tick(); until++; }
            double seconds = until / time.TicksPerSecondAt1x;
            Add("M0", "a god power reaches the ground planning reads within 1 s at 1x", until < 100 && seconds <= 1.0,
                until + " steps, " + seconds.ToString("0.0#", c) + " s at 1x");
            List<FeedItem> told = feed.Read(world.Annals);
            Add("M0", "the player is told of it, with a place to go", told.Count == 1 && told[0].Place.X == fx && told[0].Place.Z == fz,
                told.Count + " feed line(s)" + (told.Count > 0 ? ": \"" + told[0].Text + "\"" : ""));

            // Replay: the same commands at the same steps give the same world.
            ulong a = Replay(content, mapName, seed), b = Replay(content, mapName, seed);
            Add("M0", "the same seed and the same god acts give the same world", a == b, a.ToString("x16", c));

            int missed = 0;
            Console.WriteLine("godless eval — " + mapName + ", seed " + seed.ToString(c) + "\n");
            foreach (Goal g in goals)
            {
                Console.WriteLine("  " + (g.Met ? "ok  " : "MISS") + " [" + g.Milestone + "] " + g.Text);
                Console.WriteLine("         " + g.Measured);
                if (!g.Met) missed++;
            }
            Console.WriteLine("\n" + (goals.Count - missed) + " of " + goals.Count + " goals met");
            return missed == 0 ? 0 : 1;
        }

        static SimWorld Make(ContentDatabase content, string mapName, ulong seed, out ParcelGrid grid, out GodHand hand, out int fx, out int fz)
        {
            WorldChoice choice = WorldChoice.Pick(content, mapName);
            var world = new SimWorld(seed, content, VoxelTypes.FromContent(content));
            world.Island = IslandGenerator.Generate(world.Voxels.Store, world.Streams, choice.Biomes, world.VoxelTypes, choice.Preset, choice.Features);
            world.BeginHistory();
            ConstraintFields fields;
            grid = Survey.Of(world, content, choice.Biomes, out fields);
            world.Add(new GroundSystem(grid, fields, choice.Biomes)).Add(new DepositSystem(grid));
            hand = new GodHand(world, grid, GroundPalette.From(world.Island, choice.Biomes, world.VoxelTypes));
            int px, pz;
            if (!Survey.FlattestNearWater(grid, world.Island, choice.Biomes, Symbol.None, out px, out pz)) { px = ParcelGrid.Width / 2; pz = ParcelGrid.Depth / 2; }
            fx = px * ParcelGrid.Size + 2;
            fz = pz * ParcelGrid.Size + 2;
            return world;
        }

        static ulong Replay(ContentDatabase content, string mapName, ulong seed)
        {
            ParcelGrid grid;
            GodHand hand;
            int fx, fz;
            SimWorld world = Make(content, mapName, seed, out grid, out hand, out fx, out fz);
            for (int t = 0; t < 120; t++)
            {
                if (t % 9 == 4) world.Commands.Submit(new RaiseGround(hand, new Int3(fx + t % 13, 0, fz), 7, 5), world.Clock.Tick);
                if (t % 13 == 6) world.Commands.Submit(new LowerGround(hand, new Int3(fx, 0, fz + t % 11), 5, 3), world.Clock.Tick);
                world.Tick();
            }
            return world.Annals.Digest() ^ world.Voxels.Store.Digest();
        }
    }
}
