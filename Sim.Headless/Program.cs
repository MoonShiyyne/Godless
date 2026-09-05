using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Godless.Sim.Content;
using Godless.Sim.Core;
using Godless.Sim.Harness;
using Godless.Sim.Voxels;

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
            ulong first; int count;
            cli.Seeds(out first, out count);
            int years = cli.Int("years", 300);

            var runner = new BatchRunner(BatchRunner.EmptyWorld());
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

  sim run      [--seeds A..B] [--years N]   batch run, checking every invariant
  sim verify   [--seeds A..B] [--years N]   run each seed twice, compare byte for byte
  sim content  [--path P]                   load Assets/Content and report what it holds

Defaults: run 0..200 x 300 years, verify 0..20 x 100 years.
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
