using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Godless.Sim.Content;
using Godless.Sim.Core;
using Godless.Sim.Harness;
using Godless.Sim.Voxels;
using Godless.Sim.World;

namespace Godless.Sim.Headless
{
    /// <summary>
    /// The headless runner. S08.
    ///
    /// This is what buys unattended iteration: a thousand simulated years in
    /// seconds, with no Editor, no GPU and no Unity licence in the loop. It
    /// exists because law L1 made it possible, and law L7 is unenforceable
    /// without it.
    ///
    /// Wall-clock timing and console IO live here rather than in Assets/Sim,
    /// which is why the law guard scans only the latter.
    /// </summary>
    public static class Program
    {
        public static int Main(string[] args)
        {
            var cli = new Args(args);
            string command = cli.Command ?? "help";

            switch (command)
            {
                case "run": return Run(cli);
                case "verify": return Verify(cli);
                case "content": return Content(cli);
                case "island": return Island(cli);
                case "help": Help(); return 0;
                default:
                    Console.Error.WriteLine("unknown command '" + command + "'");
                    Help();
                    return 2;
            }
        }

        // ── sim run ─────────────────────────────────────────────────────────

        static int Run(Args cli)
        {
            bool withIsland = cli.Text("island", "false") != "false";

            ulong first; int count;
            // Generating an island costs about half a second, so the default
            // batch shrinks when one is asked for. Two hundred seeds of an
            // empty world is a framework check; twenty seeds of a real island
            // is a content check.
            cli.Seeds(out first, out count, defaultCount: withIsland ? 20 : 200);
            int years = cli.Int("years", 300);

            BatchRunner runner;
            if (withIsland)
            {
                LoadResult content;
                try { content = ContentLoader.Load(new DirectoryContentSource(cli.Text("path", DefaultContentRoot()))); }
                catch (System.Exception e) { Console.Error.WriteLine("content error: " + e.Message); return 1; }

                BiomeTable biomes = BiomeTable.FromContent(content.Database);
                VoxelTypes types = VoxelTypes.FromContent(content.Database);
                if (biomes.Count == 0) { Console.Error.WriteLine("no biomes declared"); return 1; }

                runner = new BatchRunner(seed =>
                {
                    var world = new SimWorld(seed, content.Database, types);
                    world.Island = IslandGenerator.Generate(world.Voxels.Store, world.Streams, biomes, types);
                    return world;
                });
                runner.Collect(IslandInvariants.Collector(biomes));
                foreach (Invariant i in IslandInvariants.All()) runner.Assert(i);
            }
            else
            {
                runner = new BatchRunner(BatchRunner.EmptyWorld());
            }

            foreach (Invariant i in StandardInvariants.All()) runner.Assert(i);

            var watch = Stopwatch.StartNew();
            BatchReport report = runner.Run(first, count, years);
            watch.Stop();

            Console.Write(report.ToText());
            Console.WriteLine(Summary(report, watch, years));

            return report.AllPassed ? 0 : 1;
        }

        static string Summary(BatchReport report, Stopwatch watch, int years)
        {
            var c = CultureInfo.InvariantCulture;
            long simYears = (long)report.Runs.Count * years;
            double seconds = watch.Elapsed.TotalSeconds;
            string rate = seconds > 0.0
                ? ((long)(simYears / seconds)).ToString(c) + " sim-years/sec"
                : "instant";

            return "\n" + simYears.ToString(c) + " simulated years in "
                 + seconds.ToString("0.00", c) + "s (" + rate + ")"
                 + "\nmedian world " + report.Median("world.megabytes").ToString("0.00", c) + " MB, "
                 + report.Median("annals.count").ToString("0", c) + " annal records, "
                 + report.Median("deltas.count").ToString("0", c) + " voxel deltas"
                 + (report.AllPassed ? "" : "\n" + report.FailedCount.ToString(c) + " invariant(s) FAILED");
        }

        // ── sim verify ──────────────────────────────────────────────────────

        static int Verify(Args cli)
        {
            ulong first; int count;
            cli.Seeds(out first, out count, defaultCount: 20);
            int years = cli.Int("years", 100);

            var runner = new BatchRunner(BatchRunner.EmptyWorld());

            var watch = Stopwatch.StartNew();
            IReadOnlyList<ulong> broken = runner.FindNondeterministicSeeds(first, count, years);
            watch.Stop();

            var c = CultureInfo.InvariantCulture;
            if (broken.Count == 0)
            {
                Console.WriteLine("determinism ok — " + count.ToString(c) + " seeds x "
                    + years.ToString(c) + " years, run twice, byte-identical ("
                    + watch.Elapsed.TotalSeconds.ToString("0.00", c) + "s)");
                return 0;
            }

            Console.Error.WriteLine("DETERMINISM BROKEN on " + broken.Count.ToString(c) + " seed(s):");
            foreach (ulong seed in broken) Console.Error.WriteLine("  seed " + seed.ToString(c));
            Console.Error.WriteLine(
                "\nThe same seed produced two different histories. Look for a wall clock,\n" +
                "an unseeded generator, unordered iteration, or a stream keyed by load order.\n" +
                "Tools/check-laws.sh catches the common cases.");
            return 1;
        }

        // ── sim content ─────────────────────────────────────────────────────

        static int Content(Args cli)
        {
            string path = cli.Text("path", DefaultContentRoot());
            var c = CultureInfo.InvariantCulture;

            LoadResult result;
            try { result = ContentLoader.Load(new DirectoryContentSource(path)); }
            catch (ContentException e) { Console.Error.WriteLine("content error: " + e.Message); return 1; }
            catch (JsonParseException e) { Console.Error.WriteLine("content error: " + e.Message); return 1; }

            Console.WriteLine("content root: " + path);
            Console.WriteLine(result.LoadOrder.Count.ToString(c) + " mod(s), load order:");
            foreach (ModManifest mod in result.LoadOrder)
                Console.WriteLine("  " + mod.Id + " " + mod.Version + (mod.IsCodeMod ? "  [code mod]" : ""));

            Console.WriteLine("\n" + result.Database.DocumentCount.ToString(c) + " document(s):");
            foreach (string type in result.Database.Types())
            {
                IReadOnlyList<string> ids = result.Database.Ids(type);
                Console.WriteLine("  " + type + " (" + ids.Count.ToString(c) + "): " + string.Join(", ", ids));
            }

            foreach (string warning in result.Warnings) Console.WriteLine("\nwarning: " + warning);

            Console.WriteLine("\ndigest " + result.Database.Digest().ToString("x16", c));
            if (result.ContainsCodeMod)
                Console.WriteLine("a code mod is loaded — determinism is not guaranteed and the save records it");

            return 0;
        }

        // ── sim island ──────────────────────────────────────────────────────

        /// <summary>
        /// Draws the generated island as text.
        ///
        /// Not a toy: until S06 and S07 exist there is no renderer, and a
        /// world nobody can look at is a world whose bugs nobody can see. A
        /// coastline in the wrong place is obvious here and invisible in a
        /// digest.
        /// </summary>
        static int Island(Args cli)
        {
            var c = CultureInfo.InvariantCulture;
            ulong seed = (ulong)cli.Int("seed", 7);
            int width = cli.Int("width", 100);
            if (width < 16) width = 16;
            if (width > 400) width = 400;

            LoadResult content;
            try { content = ContentLoader.Load(new DirectoryContentSource(cli.Text("path", DefaultContentRoot()))); }
            catch (System.Exception e) { Console.Error.WriteLine("content error: " + e.Message); return 1; }

            BiomeTable biomes = BiomeTable.FromContent(content.Database);
            VoxelTypes types = VoxelTypes.FromContent(content.Database);
            if (biomes.Count == 0) { Console.Error.WriteLine("no biomes declared — nothing to generate"); return 1; }

            var store = new ChunkStore();
            var watch = Stopwatch.StartNew();
            IslandMap map = IslandGenerator.Generate(store, new StreamRegistry(seed), biomes, types);
            watch.Stop();

            // Terminal cells are about twice as tall as wide.
            int height = width / 2;
            int stepX = ChunkStore.SizeX / width;
            int stepZ = ChunkStore.SizeZ / height;
            if (stepX < 1) stepX = 1;
            if (stepZ < 1) stepZ = 1;

            var glyphs = new char[biomes.Count];
            for (int i = 0; i < biomes.Count; i++) glyphs[i] = GlyphFor(biomes.At(i).Id.ToString());

            var sb = new StringBuilder();
            for (int z = 0; z < ChunkStore.SizeZ; z += stepZ)
            {
                for (int x = 0; x < ChunkStore.SizeX; x += stepX)
                {
                    if (!map.IsLand(x, z)) { sb.Append(map.HeightAt(x, z) > IslandMap.SeaLevel - 6 ? '~' : ' '); continue; }
                    int b = map.BiomeAt(x, z);
                    sb.Append(b < 0 ? '?' : glyphs[b]);
                }
                sb.Append('\n');
            }
            Console.Write(sb.ToString());

            // Coverage, which is the number worth watching when tuning.
            var counts = new int[biomes.Count + 1];
            int land = 0;
            for (int z = 0; z < ChunkStore.SizeZ; z++)
                for (int x = 0; x < ChunkStore.SizeX; x++)
                {
                    if (!map.IsLand(x, z)) continue;
                    land++;
                    int b = map.BiomeAt(x, z);
                    counts[b < 0 ? biomes.Count : b]++;
                }

            long total = (long)ChunkStore.SizeX * ChunkStore.SizeZ;
            Console.WriteLine("\nseed " + seed.ToString(c) + "  ~ sea  ? unclaimed");
            for (int i = 0; i < biomes.Count; i++)
                Console.WriteLine("  " + glyphs[i] + "  " + biomes.At(i).Id
                    + "  " + Pct(counts[i], land) + " of land");
            if (counts[biomes.Count] > 0)
                Console.WriteLine("  ?  no biome accepted these columns  " + Pct(counts[biomes.Count], land)
                    + " of land  <- a gap in the selection windows");

            Console.WriteLine("\nland " + Pct(land, total) + " of the map, "
                + (store.MemoryBytes / 1024).ToString(c) + " KB across "
                + store.AllocatedChunks.ToString(c) + "/" + ChunkStore.ChunkCount.ToString(c)
                + " chunks, generated in " + watch.Elapsed.TotalSeconds.ToString("0.00", c) + "s");
            return 0;
        }

        static string Pct(long part, long whole)
        {
            if (whole <= 0) return "0.0%";
            return (100.0 * part / whole).ToString("0.0", CultureInfo.InvariantCulture) + "%";
        }

        static char GlyphFor(string biomeId)
        {
            if (biomeId.EndsWith("shore", StringComparison.Ordinal)) return '.';
            if (biomeId.EndsWith("flood-plain", StringComparison.Ordinal)) return ',';
            if (biomeId.EndsWith("temperate", StringComparison.Ordinal)) return 'n';
            if (biomeId.EndsWith("highland", StringComparison.Ordinal)) return '^';
            return '#';
        }

        static string DefaultContentRoot()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, "Assets", "Content");
                if (Directory.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
            return Path.Combine("Assets", "Content");
        }

        // ── help ────────────────────────────────────────────────────────────

        static void Help()
        {
            Console.WriteLine(
@"godless sim harness

  sim run      [--seeds A..B] [--years N] [--island]
                                            batch run, checking every invariant
  sim verify   [--seeds A..B] [--years N]   run each seed twice, compare byte for byte
  sim content  [--path P]                   load Assets/Content and report what it holds
  sim island   [--seed N] [--width W]       generate an island and draw it

Defaults: run 0..200 x 300 years (0..20 with --island), verify 0..20 x 100 years.
--island generates real terrain from Assets/Content for every seed, which is
what makes the S09 invariants meaningful and costs about half a second each.
Exit code is 0 when everything passed and 1 when something did not.

Invariants are declared per system and owned by a registry id (law L7). The
list grows as systems land; `sim run` prints every one it checked.");
        }

        // ── argument parsing ────────────────────────────────────────────────

        sealed class Args
        {
            readonly Dictionary<string, string> _options =
                new Dictionary<string, string>(StringComparer.Ordinal);

            public string Command { get; private set; }

            public Args(string[] argv)
            {
                for (int i = 0; i < argv.Length; i++)
                {
                    string a = argv[i];
                    if (a.StartsWith("--", StringComparison.Ordinal))
                    {
                        string key = a.Substring(2);
                        string value = (i + 1 < argv.Length && !argv[i + 1].StartsWith("--", StringComparison.Ordinal))
                            ? argv[++i] : "true";
                        _options[key] = value;
                    }
                    else if (Command == null) Command = a;
                }
            }

            public string Text(string key, string fallback)
            {
                string v;
                return _options.TryGetValue(key, out v) ? v : fallback;
            }

            public int Int(string key, int fallback)
            {
                string v;
                int parsed;
                if (_options.TryGetValue(key, out v) &&
                    int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                    return parsed;
                return fallback;
            }

            /// <summary>Parses "0..200" or a bare count.</summary>
            public void Seeds(out ulong first, out int count, int defaultCount = 200)
            {
                first = 0UL;
                count = defaultCount;

                string spec;
                if (!_options.TryGetValue("seeds", out spec)) return;

                int dots = spec.IndexOf("..", StringComparison.Ordinal);
                if (dots < 0)
                {
                    int only;
                    if (int.TryParse(spec, NumberStyles.Integer, CultureInfo.InvariantCulture, out only))
                    { first = (ulong)only; count = 1; }
                    return;
                }

                ulong lo; ulong hi;
                if (ulong.TryParse(spec.Substring(0, dots), NumberStyles.Integer, CultureInfo.InvariantCulture, out lo) &&
                    ulong.TryParse(spec.Substring(dots + 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out hi) &&
                    hi >= lo)
                {
                    first = lo;
                    count = (int)(hi - lo);
                    if (count == 0) count = 1;
                }
            }
        }
    }
}
