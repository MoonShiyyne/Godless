using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Godless.Sim.Content;
using Godless.Sim.Annals;
using Godless.Sim.Build;
using Godless.Sim.Core;
using Godless.Sim.Drives;
using Activity = Godless.Sim.Drives.Activity;
using Godless.Sim.Harness;
using Godless.Sim.Settlements;
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
                case "parcels": return Parcels(cli);
                case "settle": return Settle(cli);
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

            // The palette rule is checked where a content author will look
            // for it, rather than only in a test they will never run.
            Palette palette = Palette.FromContent(result.Database);
            IReadOnlyList<string> problems = palette.Violations();
            if (problems.Count > 0)
            {
                Console.WriteLine("\npalette (S0A) — " + problems.Count.ToString(c) + " problem(s):");
                foreach (string problem in problems) Console.WriteLine("  " + problem);
            }
            else if (palette.Materials.Count > 0)
            {
                Console.WriteLine("\npalette ok — " + palette.Materials.Count.ToString(c)
                    + " materials, all at least " + palette.MinValueSeparation.ToString(c)
                    + " apart in value; storey " + palette.FloorHeightMetres.ToString("0.0", c) + " m");
            }

            // The gene rule, where a modder will see it (S17).
            Godless.Sim.Culture.GeneTable genes = Godless.Sim.Culture.GeneTable.FromContent(result.Database);
            if (genes.Problems.Count > 0)
            {
                Console.WriteLine("\ngenes (S17) — " + genes.Problems.Count.ToString(c) + " refused:");
                foreach (string problem in genes.Problems) Console.WriteLine("  " + problem);
            }
            if (genes.Count > 0)
            {
                Console.WriteLine("\n" + genes.Count.ToString(c) + " gene(s), each with its tell:");
                foreach (Godless.Sim.Culture.Gene g in genes.All)
                    Console.WriteLine("  " + g.Name.PadRight(16) + g.Tell);
            }

            // Building materials (S11): every biome's offer must be gatherable.
            MaterialTable mats = MaterialTable.FromContent(result.Database, BiomeTable.FromContent(result.Database));
            if (mats.Problems.Count > 0)
            {
                Console.WriteLine("\nmaterials (S11) — " + mats.Problems.Count.ToString(c) + " problem(s):");
                foreach (string problem in mats.Problems) Console.WriteLine("  " + problem);
            }
            else if (mats.Count > 0)
            {
                var names = new List<string>();
                foreach (BuildingMaterial m in mats.All) names.Add(m.Name);
                Console.WriteLine("\n" + mats.Count.ToString(c) + " building material(s), gathered within "
                    + mats.HaulRangeVoxels.ToString(c) + " voxels of a hearth: " + string.Join(", ", names));
            }

            // Needs and activities (S12), with anything refused and why.
            DriveRules drives = DriveRules.FromContent(result.Database);
            IReadOnlyList<string> refused = drives.Problems();
            if (refused.Count > 0)
            {
                Console.WriteLine("\ndrives (S12) — " + refused.Count.ToString(c) + " refused:");
                foreach (string problem in refused) Console.WriteLine("  " + problem);
            }
            if (drives.Needs.Count > 0)
            {
                Console.WriteLine("\n" + drives.Needs.Count.ToString(c) + " need(s), each with what a stranger sees when it goes unmet:");
                foreach (Need n in drives.Needs.All) Console.WriteLine("  " + n.Name.PadRight(16) + n.Tell);
                var acts = new List<string>();
                foreach (Activity a in drives.Activities.All) acts.Add(a.Name + (a.Productive ? "*" : ""));
                Console.WriteLine("  activities: " + string.Join(", ", acts) + "   (* productive)");
            }

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
                    if (AnyWater(map, x, z, stepX, stepZ, out bool lake)) { sb.Append(lake ? 'o' : '='); continue; }
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
            // S0B: the water on the land.
            int riverCols = 0, lakeCols = 0, floodCols = 0;
            for (int z = 0; z < ChunkStore.SizeZ; z++)
                for (int x = 0; x < ChunkStore.SizeX; x++)
                {
                    if (map.IsRiver(x, z)) riverCols++;
                    else if (map.IsLake(x, z)) lakeCols++;
                    else if (map.IsLand(x, z) && map.WaterLevelAt(x, z) == 0 && map.HeightAboveWaterAt(x, z) <= 1) floodCols++;
                }

            Console.WriteLine("\nseed " + seed.ToString(c) + "  ~ sea  = river  o lake  ? unclaimed");
            for (int i = 0; i < biomes.Count; i++)
                Console.WriteLine("  " + glyphs[i] + "  " + biomes.At(i).Id
                    + "  " + Pct(counts[i], land) + " of land");
            if (counts[biomes.Count] > 0)
                Console.WriteLine("  ?  no biome accepted these columns  " + Pct(counts[biomes.Count], land)
                    + " of land  <- a gap in the selection windows");

            Console.WriteLine("\nwater (S0B): " + riverCols.ToString(c) + " river columns, " + lakeCols.ToString(c)
                + " lake columns; " + Pct(floodCols, land) + " of land stands within a voxel of its water and floods first");
            Console.WriteLine("land " + Pct(land, total) + " of the map, "
                + (store.MemoryBytes / 1024).ToString(c) + " KB across "
                + store.AllocatedChunks.ToString(c) + "/" + ChunkStore.ChunkCount.ToString(c)
                + " chunks, generated in " + watch.Elapsed.TotalSeconds.ToString("0.00", c) + "s");
            return 0;
        }

        // ── sim parcels ─────────────────────────────────────────────────────

        /// <summary>
        /// Draws one of S10's planning fields over the island. The fields are
        /// what settlements will read when choosing where to build, so this is
        /// the first view of the island as a settlement will see it.
        /// </summary>
        static int Parcels(Args cli)
        {
            var c = CultureInfo.InvariantCulture;
            ulong seed = (ulong)cli.Int("seed", 7);
            string field = cli.Text("field", "water-distance");

            LoadResult content;
            try { content = ContentLoader.Load(new DirectoryContentSource(cli.Text("path", DefaultContentRoot()))); }
            catch (System.Exception e) { Console.Error.WriteLine("content error: " + e.Message); return 1; }

            VoxelTypes types = VoxelTypes.FromContent(content.Database);
            BiomeTable biomes = BiomeTable.FromContent(content.Database);
            var store = new ChunkStore();
            IslandGenerator.Generate(store, new StreamRegistry(seed), biomes, types);

            bool[] solid = TerrainBrush.SolidTable(content.Database, types);
            var wet = new bool[types.Count];
            ushort water;
            if (types.TryGetId(Symbol.For("voxel.water"), out water)) wet[water] = true;

            var watch = Stopwatch.StartNew();
            ParcelGrid grid = ParcelGrid.Build(store, solid, wet);
            watch.Stop();
            double first = watch.Elapsed.TotalMilliseconds;
            watch.Restart();
            ParcelGrid.Build(store, solid, wet);
            watch.Stop();

            InfluenceMap map;
            switch (field)
            {
                case "height": map = grid.Height; break;
                case "slope": map = grid.Slope; break;
                case "water-distance": map = grid.WaterDistance; break;
                default: Console.Error.WriteLine("unknown field '" + field + "' — try height, slope or water-distance"); return 2;
            }

            // Ramp over land only; the sea is drawn as blank so the coast reads.
            const string ramp = " .:-=+*#%@";
            double lo = double.MaxValue, hi = double.MinValue;
            for (int pz = 0; pz < ParcelGrid.Depth; pz++)
                for (int px = 0; px < ParcelGrid.Width; px++)
                    if (grid.IsLand(px, pz)) { lo = Math.Min(lo, map[px, pz]); hi = Math.Max(hi, map[px, pz]); }

            var sb = new StringBuilder();
            for (int pz = 0; pz < ParcelGrid.Depth; pz += 2)
            {
                for (int px = 0; px < ParcelGrid.Width; px++)
                {
                    if (!grid.IsLand(px, pz)) { sb.Append(' '); continue; }
                    double t = hi > lo ? (map[px, pz] - lo) / (hi - lo) : 0.0;
                    sb.Append(ramp[1 + (int)Math.Min(ramp.Length - 2, t * (ramp.Length - 1))]);
                }
                sb.Append('\n');
            }
            Console.Write(sb.ToString());
            Console.WriteLine("\nfield." + field + " over land: " + lo.ToString("0.#", c) + " (.) to "
                + hi.ToString("0.#", c) + " (@), sea blank");
            Console.WriteLine("128 x 128 parcels of 4 x 4 columns, built in " + first.ToString("0", c)
                + " ms cold, " + watch.Elapsed.TotalMilliseconds.ToString("0", c) + " ms warm");
            return 0;
        }

        // ── sim settle ──────────────────────────────────────────────────────

        /// <summary>
        /// Founds one settlement on a real island and prints its days: the
        /// weather, who slept in the open, what people did with their time and
        /// what pressure it left. S12's tell, readable without a renderer.
        /// </summary>
        static int Settle(Args cli)
        {
            var c = CultureInfo.InvariantCulture;
            ulong seed = (ulong)cli.Int("seed", 7);
            int days = cli.Int("days", 30);
            int people = cli.Int("people", 20);
            int roofs = cli.Int("roofs", 0);
            string wantBiome = cli.Text("biome", "temperate");

            LoadResult content;
            try { content = ContentLoader.Load(new DirectoryContentSource(cli.Text("path", DefaultContentRoot()))); }
            catch (System.Exception e) { Console.Error.WriteLine("content error: " + e.Message); return 1; }

            ContentDatabase db = content.Database;
            VoxelTypes types = VoxelTypes.FromContent(db);
            BiomeTable biomes = BiomeTable.FromContent(db);
            DriveRules rules = DriveRules.FromContent(db);
            if (biomes.Count == 0) { Console.Error.WriteLine("no biomes declared — nowhere to settle"); return 1; }

            var world = new SimWorld(seed, db, types);
            IslandMap island = IslandGenerator.Generate(world.Voxels.Store, world.Streams, biomes, types);
            world.Island = island;
            world.BeginHistory();

            bool[] solid = TerrainBrush.SolidTable(db, types);
            var wet = new bool[types.Count];
            ushort water;
            if (types.TryGetId(Symbol.For("voxel.water"), out water)) wet[water] = true;
            ParcelGrid grid = ParcelGrid.Build(world.Voxels.Store, solid, wet);

            int px, pz;
            if (!StandInSite(grid, island, biomes, Symbol.For("biome." + wantBiome), out px, out pz)
                && !StandInSite(grid, island, biomes, Symbol.None, out px, out pz))
            { Console.Error.WriteLine("no dry, flat parcel near water on this island"); return 1; }

            int hx = px * ParcelGrid.Size + 2, hz = pz * ParcelGrid.Size + 2;
            var hearth = new Int3(hx, grid.GroundAt(hx, hz) + 1, hz);
            int b = island.BiomeAt(hx, hz);
            Biome biome = b >= 0 ? biomes.At(b) : null;

            Settlement s = Settlement.Found("first", hearth, biome, people, rules, 0, world.Annals, RecordId.None);
            s.ShelterCapacity = roofs;
            IntentKindTable kinds = IntentKindTable.FromContent(db, rules.Needs);
            var bus = new IntentBus(kinds, rules.Needs.Count);
            s.AttachIntents(bus);
            PressureTally tally = bus.Tally;
            world.Settlements.Add(s);
            world.Add(new DriveSystem(rules)).Add(new IntentSystem());

            Console.WriteLine("seed " + seed.ToString(c) + ": " + people.ToString(c) + " people found a settlement at parcel ("
                + px.ToString(c) + ", " + pz.ToString(c) + ") in " + (biome == null ? "no biome" : biome.Id.ToString())
                + ", with " + roofs.ToString(c) + " roof(s)");
            Console.WriteLine("site is a stand-in: the flattest dry parcel within four of water. S15 and S30 replace it.");

            // S11: what the land within hauling range offers to build with.
            MaterialTable materials = MaterialTable.FromContent(db, biomes);
            s.Catchment = Catchment.Survey(island, biomes, materials, hx, hz);
            s.Stock = new MaterialStock(materials);
            var offered = new List<string>();
            for (int m = 0; m < materials.Count; m++)
                if (s.Catchment.Offers(m))
                    offered.Add(materials[m].Name + " " + (s.Catchment.YieldPerLabourTick(m) / materials[m].PerLabourTick * 100).ToString("0", c) + "%");
            Console.WriteLine("within " + materials.HaulRangeVoxels.ToString(c) + " voxels the land offers, at this share of full yield: "
                + (offered.Count == 0 ? "nothing" : string.Join(", ", offered)) + "\n");

            var header = new StringBuilder("  day  weather     in open ");
            foreach (Activity a in rules.Activities.All) if (a.Name != "sleep") header.Append(a.Name.PadLeft(11));
            header.Append("   pressure:");
            foreach (Need n in rules.Needs.All) header.Append(n.Name.PadLeft(9));
            header.Append("   asks for");
            Console.WriteLine(header.ToString());

            int lastIntents = 0;
            var lastTicks = new long[rules.Activities.Count];
            var lastPressure = new double[rules.Needs.Count];
            for (int d = 0; d < days; d++)
            {
                // One row is dawn to night of one day, so an intent raised at
                // dawn lands on the row of the day it was raised.
                do world.Tick(); while (world.Clock.TickOfDay != world.Clock.TicksPerDay - 1);

                long day = world.Clock.TotalDays;
                Sky sky = Weather.On(world.Streams, biome, day, world.Clock.DaysPerYear);
                int inOpen = 0;
                foreach (Agent a in s.People) if (!a.ShelteredLastNight) inOpen++;

                var line = new StringBuilder();
                line.Append(day.ToString(c).PadLeft(5)).Append("  ").Append(sky.ToString().PadRight(10))
                    .Append((inOpen.ToString(c) + "/" + people.ToString(c)).PadLeft(8)).Append(' ');
                for (int i = 0; i < rules.Activities.Count; i++)
                {
                    long ticks = s.ActivityTicks[i] - lastTicks[i];
                    lastTicks[i] = s.ActivityTicks[i];
                    if (rules.Activities[i].Name != "sleep") line.Append(ticks.ToString(c).PadLeft(11));
                }
                line.Append("            ");
                for (int n = 0; n < rules.Needs.Count; n++)
                {
                    double p = tally.Total(n) - lastPressure[n];
                    lastPressure[n] = tally.Total(n);
                    line.Append(p.ToString("0.0", c).PadLeft(9));
                }
                line.Append("   ");
                for (; lastIntents < bus.Intents.Count; lastIntents++) line.Append(bus.Intents[lastIntents].Kind.Name + " ");
                Console.WriteLine(line.ToString());
            }

            IReadOnlyList<AnnalRecord> spells = world.Annals.OfKind(DriveSystem.ExposedKind);
            Console.WriteLine("\n" + spells.Count.ToString(c) + " spell(s) of nights in the open on record; every unit of pressure names its cause:");
            for (int n = 0; n < rules.Needs.Count; n++)
            {
                double total = tally.Total(n);
                if (total <= 0.0) continue;
                Console.WriteLine("  " + rules.Needs[n].Name.PadRight(10) + total.ToString("0.0", c).PadLeft(9)
                    + "   " + Pct((long)(tally.Caused(n) * 1000), (long)(total * 1000)) + " traced to a record");
            }
            // S14's tell: every intent names the records that produced it.
            if (bus.Intents.Count > 0)
                Console.WriteLine("\n" + bus.Intents.Count.ToString(c) + " build intent(s), each with the records that asked for it:");
            foreach (BuildIntent i in bus.Intents)
            {
                Console.WriteLine("  " + i.Record + "  day " + (i.RaisedTick / world.Clock.TicksPerDay).ToString(c) + "  "
                    + i.Kind.Name + " near parcel (" + i.ParcelX.ToString(c) + ", " + i.ParcelZ.ToString(c) + "), weight "
                    + i.Weight.ToString("0", c) + ", budget " + i.BudgetVoxels.ToString(c) + " voxels, " + i.Status.ToString().ToLowerInvariant());
                foreach (RecordId cause in i.Causes)
                {
                    AnnalRecord r = world.Annals.Get(cause);
                    string weather = r.Participants.Count == 0 ? "dry" : string.Join(" and ", r.Participants).Replace("condition.", "");
                    Console.WriteLine("      because " + r.Id + ": from day " + (r.Tick / world.Clock.TicksPerDay).ToString(c) + ", "
                        + (r.Kind == DriveSystem.ExposedKind
                            ? r.ValueA.ToString(c) + " slept in the open, " + weather
                            : r.Kind.ToString()));
                }
            }
            Console.WriteLine("\nactivity ticks are agent-ticks: " + people.ToString(c) + " people x 3 daylight ticks a day.");
            return 0;
        }

        /// <summary>
        /// The flattest land parcel in the biome within four parcels of water,
        /// ties to the lowest index. A stand-in for S15 site scoring and S30
        /// founding, so the drives have somewhere real to happen.
        /// </summary>
        static bool StandInSite(ParcelGrid grid, IslandMap island, BiomeTable biomes, Symbol biome, out int bestX, out int bestZ)
        {
            bestX = bestZ = -1;
            double bestSlope = double.MaxValue;
            for (int pz = 0; pz < ParcelGrid.Depth; pz++)
                for (int px = 0; px < ParcelGrid.Width; px++)
                {
                    if (!grid.IsLand(px, pz) || grid.WetColumns(px, pz) > 0) continue;
                    double wd = grid.WaterDistance[px, pz];
                    if (wd < 1.0 || wd > 4.0) continue;
                    if (!biome.IsNone)
                    {
                        int b = island.BiomeAt(px * ParcelGrid.Size + 2, pz * ParcelGrid.Size + 2);
                        if (b < 0 || biomes.At(b).Id != biome) continue;
                    }
                    if (grid.Slope[px, pz] < bestSlope) { bestSlope = grid.Slope[px, pz]; bestX = px; bestZ = pz; }
                }
            return bestX >= 0;
        }

        /// <summary>Whether any column in a drawn cell holds river or lake water. Rivers are one column wide; sampling would miss them.</summary>
        static bool AnyWater(IslandMap map, int x0, int z0, int w, int h, out bool lake)
        {
            bool river = false;
            lake = false;
            for (int z = z0; z < z0 + h && z < ChunkStore.SizeZ; z++)
                for (int x = x0; x < x0 + w && x < ChunkStore.SizeX; x++)
                {
                    if (map.IsRiver(x, z)) river = true;
                    else if (map.IsLake(x, z)) lake = true;
                }
            if (lake) return true;
            return river;
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
  sim parcels  [--seed N] [--field F]       draw a planning field: height, slope, water-distance
  sim settle   [--seed N] [--days D] [--people P] [--roofs R] [--biome B]
                                            found a settlement and print its days (S12, S14)

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
