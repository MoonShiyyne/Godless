using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Godless.Sim.Chronicle;
using Godless.Sim.Content;
using Godless.Sim.Core;
using Godless.Sim.Harness;
using Godless.Sim.Life;
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
            System.Action<string, string, bool, string> Add = (milestone, text, met, measured) =>
            {
                goals.Add(new Goal { Milestone = milestone, Text = text, Met = met, Measured = measured });
            };

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
            Add("M0", "the world steps in 2 ms or less (room for 16x at 60 fps)", msPerStep <= 2.0,
                msPerStep.ToString("0.000", c) + " ms a step over " + years + " years, " + (world.Life != null ? world.Life.Creatures.Count : 0) + " creatures at the end");

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

            M1(content, mapName, seed, time, feed, Add);

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

        /// <summary>M1, Life: creatures at scale, bands that settle or die out, powers that land, a world that lives on its own.</summary>
        static void M1(ContentDatabase content, string mapName, ulong seed, TimeRules time, EventFeed feed,
                       System.Action<string, string, bool, string> add)
        {
            var c = CultureInfo.InvariantCulture;
            ParcelGrid grid; GodHand hand; int fx, fz; LifeSystem life;

            // A thousand creatures and more, stepped.
            SimWorld world = Make(content, mapName, seed, out grid, out hand, out fx, out fz, out life);
            world.Tick();
            int deer = life.Life.Species.IndexOf("deer");
            RngStream rng = world.Streams.Get("eval.crowd");
            for (int round = 0; world.Life.Creatures.Count < 1100 && round < 20; round++)
            {
                for (int k = 0; k < 60; k++)
                {
                    int x = rng.NextInt(40, 984), z = rng.NextInt(40, 984);
                    if (life.Passable(x, z)) world.Commands.Submit(new Spawn(life, deer, new Int3(x, 0, z), 6), world.Clock.Tick);
                }
                world.Tick();
            }
            int crowd = world.Life.Creatures.Count;
            var watch = Stopwatch.StartNew();
            for (int t = 0; t < 360; t++) world.Tick();
            double ms = watch.Elapsed.TotalMilliseconds / 360.0;
            add("M1", "1,000 creatures step in 1.5 ms or less", crowd >= 1000 && ms <= 1.5,
                crowd + " creatures, " + ms.ToString("0.000", c) + " ms a step");

            // A band set down settles or dies out within five minutes at 1x.
            int human = life.Life.Species.IndexOf("human");
            int resolved = 0, settledCount = 0, tried = 0;
            for (int k = 0; k < 5; k++)
            {
                SimWorld w = Make(content, mapName, seed + (ulong)k * 101, out grid, out hand, out fx, out fz, out life);
                w.Tick();
                RngStream r = w.Streams.Get("eval.band");
                int x = fx, z = fz;
                for (int a = 0; a < 50; a++)
                {
                    int tx = r.NextInt(60, 964), tz = r.NextInt(60, 964);
                    if (life.Passable(tx, tz)) { x = tx; z = tz; break; }
                }
                int before = life.Life.Bands.Count;
                w.Commands.Submit(new Spawn(life, human, new Int3(x, 0, z), 10), w.Clock.Tick);
                int steps = (int)(300 * time.TicksPerSecondAt1x);
                for (int t = 0; t < steps; t++) w.Tick();
                if (life.Life.Bands.Count <= before) continue;
                tried++;
                Band b = life.Life.Bands[before];
                if (b.Settled || b.Gone) resolved++;
                if (b.Settled) settledCount++;
            }
            add("M1", "a band set down anywhere settles or dies out within 5 minutes at 1x, 4 times in 5", tried > 0 && resolved * 5 >= tried * 4,
                resolved + " of " + tried + " resolved (" + settledCount + " settled)");

            // Every power lands the step after it is given, and does what it says.
            world = Make(content, mapName, seed, out grid, out hand, out fx, out fz, out life);
            for (int t = 0; t < 3; t++) world.Tick();
            var fails = new List<string>();
            int sheep = life.Life.Species.IndexOf("sheep");
            var at = new Int3(fx, 0, fz);
            int n0 = world.Life.Creatures.Count;
            world.Commands.Submit(new Spawn(life, sheep, at, 7), world.Clock.Tick); world.Tick();
            if (world.Life.Creatures.Count < n0 + 7) fails.Add("spawn");
            int near = Powers.Within(world.Life, fx, fz, 3).Count;
            world.Commands.Submit(new Smite(life, at, 3), world.Clock.Tick); world.Tick();
            if (near == 0 || Powers.Within(world.Life, fx, fz, 3).Count >= near) fails.Add("smite");
            world.Commands.Submit(new Spawn(life, sheep, at, 7), world.Clock.Tick); world.Tick();
            world.Commands.Submit(new Touch(life, at, true, 6), world.Clock.Tick); world.Tick();
            bool blessed = false;
            foreach (int i in Powers.Within(world.Life, fx, fz, 6)) if (world.Life.Creatures.Has(i, Creatures.Blessed)) blessed = true;
            if (!blessed) fails.Add("bless");
            world.Commands.Submit(new Touch(life, at, false, 6), world.Clock.Tick); world.Tick();
            bool cursed = false;
            foreach (int i in Powers.Within(world.Life, fx, fz, 6)) if (world.Life.Creatures.Has(i, Creatures.Cursed)) cursed = true;
            if (!cursed) fails.Add("curse");
            int p = (fz / ParcelGrid.Size) * ParcelGrid.Width + fx / ParcelGrid.Size;
            world.Commands.Submit(new Fire(life, grid, at, 12), world.Clock.Tick); world.Tick();
            if (world.Life.Grazing.Food[p] > 0.5) fails.Add("fire");
            world.Commands.Submit(new Rain(life, at, 40), world.Clock.Tick); world.Tick();
            if (world.Life.Grazing.RainMonths[p] <= 0) fails.Add("rain");
            int deltas = world.Voxels.Log.Count;
            world.Commands.Submit(new Water(grid, new Int3(fx + 30, 0, fz), 5), world.Clock.Tick); world.Tick(); world.Tick();
            if (world.Voxels.Log.Count <= deltas) fails.Add("water");
            add("M1", "every power takes effect the step after it is given (0.1 s at 1x)", fails.Count == 0,
                fails.Count == 0 ? "spawn, smite, bless, curse, fire, rain, water" : "failed: " + string.Join(", ", fails));

            string[] told = { "god.spawned", "god.smote", "god.blessed", "god.cursed", "god.fire", "god.rain", "god.water",
                              "life.band-founded", "life.band-moved", "life.band-settled", "life.band-gone" };
            var untold = new List<string>();
            foreach (string k in told) if (!feed.Tells(Symbol.For(k))) untold.Add(k);
            add("M1", "the player is told of every power and every band's fortunes", untold.Count == 0,
                untold.Count == 0 ? told.Length + " kinds told" : "untold: " + string.Join(", ", untold));

            // Left alone for twenty years, the world lives on.
            world = Make(content, mapName, seed, out grid, out hand, out fx, out fz, out life);
            world.Tick();
            int people0 = world.Life.CountOf(human);
            for (int t = 0; t < 20 * 360; t++) world.Tick();
            var counts = new List<string>();
            bool all = true;
            for (int s = 0; s < life.Life.Species.Count; s++)
            {
                int k = world.Life.CountOf(s);
                counts.Add(k + " " + life.Life.Species[s].Plural);
                if (k == 0) all = false;
            }
            add("M1", "left alone 20 years, every species lives on and people have grown", all && world.Life.CountOf(human) > people0,
                string.Join(", ", counts) + " (people began at " + people0 + ")");
        }

        static SimWorld Make(ContentDatabase content, string mapName, ulong seed, out ParcelGrid grid, out GodHand hand, out int fx, out int fz)
        {
            LifeSystem life;
            return Make(content, mapName, seed, out grid, out hand, out fx, out fz, out life);
        }

        static SimWorld Make(ContentDatabase content, string mapName, ulong seed, out ParcelGrid grid, out GodHand hand, out int fx, out int fz, out LifeSystem life)
        {
            WorldChoice choice = WorldChoice.Pick(content, mapName);
            var world = new SimWorld(seed, content, VoxelTypes.FromContent(content));
            world.Island = IslandGenerator.Generate(world.Voxels.Store, world.Streams, choice.Biomes, world.VoxelTypes, choice.Preset, choice.Features);
            world.BeginHistory();
            ConstraintFields fields;
            grid = Survey.Of(world, content, choice.Biomes, out fields);
            life = Genesis.AddSystems(world, content, grid, fields, choice.Biomes);
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
