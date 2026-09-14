using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Godless.Sim.Content;
using Godless.Sim.Annals;
using Godless.Sim.Build;
using Godless.Sim.Collective;
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
                case "blueprint": return BlueprintCmd(cli);
                case "separate": return Separate(cli);
                case "maps": return Maps(cli);
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
            bool bare = cli.Text("bare", "false") != "false";

            ulong first; int count;
            // Generating an island costs about half a second, so the default
            // batch shrinks when one is asked for. Two hundred seeds of an
            // empty world is a framework check; twenty seeds of a real island
            // is a content check.
            bool settled = cli.Text("settle", "false") != "false";
            cli.Seeds(out first, out count, defaultCount: settled ? 10 : withIsland ? 20 : 200);
            int years = cli.Int("years", settled ? 5 : 300);

            BatchRunner runner;
            if (cli.Text("settle", "false") != "false")
            {
                // Stratum 2's batch: one settlement per seed on a planted island.
                LoadResult content;
                try { content = ContentLoader.Load(new DirectoryContentSource(cli.Text("path", DefaultContentRoot()))); }
                catch (System.Exception e) { Console.Error.WriteLine("content error: " + e.Message); return 1; }
                WorldChoice map;
                try { map = WorldChoice.Pick(content.Database, cli.Text("map", "")); }
                catch (System.Exception e) { Console.Error.WriteLine(e.Message); return 1; }

                runner = new BatchRunner(SettlementInvariants.Settled(content.Database, map, cli.Int("people", 20)));
                runner.Collect(SettlementInvariants.Collector());
                foreach (Invariant i in SettlementInvariants.All()) runner.Assert(i);
            }
            else if (withIsland)
            {
                LoadResult content;
                try { content = ContentLoader.Load(new DirectoryContentSource(cli.Text("path", DefaultContentRoot()))); }
                catch (System.Exception e) { Console.Error.WriteLine("content error: " + e.Message); return 1; }

                WorldChoice map;
                try { map = WorldChoice.Pick(content.Database, cli.Text("map", "")); }
                catch (System.Exception e) { Console.Error.WriteLine(e.Message); return 1; }
                BiomeTable biomes = map.Biomes;
                VoxelTypes types = VoxelTypes.FromContent(content.Database);
                if (biomes.Count == 0) { Console.Error.WriteLine("no biomes declared"); return 1; }

                runner = new BatchRunner(seed =>
                {
                    var world = new SimWorld(seed, content.Database, types);
                    world.Island = IslandGenerator.Generate(world.Voxels.Store, world.Streams, biomes, types, map.Preset,
                                                            bare ? null : map.Features);
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

            // The tileset (S1D), and whether it can answer every grammar role.
            MaterialTable tileMaterials = MaterialTable.FromContent(result.Database, BiomeTable.FromContent(result.Database));
            TileSet tiles = TileSet.FromContent(result.Database, tileMaterials);
            foreach (string problem in tiles.Problems) Console.WriteLine("\ntileset (S1D) problem: " + problem);
            if (tiles.Count > 0)
            {
                Console.WriteLine("\ntileset: " + tiles.Count.ToString(c) + " role(s), at most " + tiles.MaxMaterials.ToString(c)
                    + " materials a building, roof and wall at least " + tiles.MinRoofWallContrast.ToString(c) + " apart in value");
                foreach (RoleTile t in tiles.Roles)
                {
                    var classes = new List<string>();
                    foreach (Symbol cl in t.Classes) classes.Add(cl.ToString().Replace("class.", ""));
                    Console.WriteLine("  " + t.Name.PadRight(10) + (t.Open ? "an opening" : string.Join(" then ", classes)));
                }
            }

            // Grammars (S18): every name and rule checked at load.
            GrammarTable grammarTable = GrammarTable.FromContent(result.Database, genes);
            foreach (string problem in grammarTable.Problems) Console.WriteLine("\ngrammar (S18) refused: " + problem);
            foreach (Grammar g in grammarTable.All)
            {
                Console.WriteLine("\ngrammar '" + g.Name + "' builds " + g.Builds + ": " + g.Tell);
                foreach (string gap in tiles.Answers(g)) Console.WriteLine("  gap: " + gap);
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

            WorldChoice choice;
            try { choice = WorldChoice.Pick(content.Database, cli.Text("map", "")); }
            catch (System.Exception e) { Console.Error.WriteLine(e.Message); return 1; }
            BiomeTable biomes = choice.Biomes;
            VoxelTypes types = VoxelTypes.FromContent(content.Database);
            if (biomes.Count == 0) { Console.Error.WriteLine("no biomes declared — nothing to generate"); return 1; }

            var store = new ChunkStore();
            var watch = Stopwatch.StartNew();
            IslandMap map = IslandGenerator.Generate(store, new StreamRegistry(seed), biomes, types, choice.Preset,
                                                    cli.Text("bare", "false") != "false" ? null : choice.Features);
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
                    if (!map.IsLand(x, z)) { sb.Append(map.HeightAt(x, z) > map.SeaLevel - 6 ? '~' : ' '); continue; }
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

            if (map.Deposits != null)
            {
                // S2F: what grows and lies on it, per kind, and what it is worth.
                var perKind = new long[map.Deposits.Kinds.Count];
                var units = new long[map.Deposits.Kinds.Count];
                for (int f = 0; f < map.Deposits.Count; f++)
                {
                    int k = map.Deposits.Kinds.IndexOf(map.Deposits.KindOf(f).Id);
                    perKind[k]++;
                    units[k] += map.Deposits.Remaining(f);
                }
                Console.WriteLine("\ndeposits (S2F):");
                for (int k = 0; k < perKind.Length; k++)
                {
                    FeatureKind kind = map.Deposits.Kinds[k];
                    Console.WriteLine("  " + kind.Name.PadRight(9) + perKind[k].ToString(c).PadLeft(7) + " "
                        + kind.Shape.ToString().ToLowerInvariant() + (perKind[k] == 1 ? "" : "s") + ", "
                        + units[k].ToString(c) + " voxels of " + kind.Yields.ToString().Replace("voxel.", "")
                        + (kind.Renews ? ", back in " + kind.RegrowDays.ToString(c) + " days" : ", never back"));
                }
            }
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
            WorldChoice choice;
            try { choice = WorldChoice.Pick(content.Database, cli.Text("map", "")); }
            catch (System.Exception e) { Console.Error.WriteLine(e.Message); return 1; }
            BiomeTable biomes = choice.Biomes;
            var store = new ChunkStore();
            IslandMap island = IslandGenerator.Generate(store, new StreamRegistry(seed), biomes, types, choice.Preset,
                                                       cli.Text("bare", "false") != "false" ? null : choice.Features);

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

            ConstraintFields fields = ConstraintFields.Compute(island, grid, biomes);
            InfluenceMap map = fields.Find(Symbol.For("field." + field));
            if (map == null)
                switch (field)
                {
                    case "height": map = grid.Height; break;
                    case "slope": map = grid.Slope; break;
                    case "water-distance": map = grid.WaterDistance; break;
                    default:
                        Console.Error.WriteLine("unknown field '" + field + "' — try height, slope, water-distance, "
                                                + "sun, snow-load, damp, exposure or flood-risk");
                        return 2;
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
            Console.WriteLine(ParcelGrid.Width.ToString(c) + " x " + ParcelGrid.Depth.ToString(c)
                + " parcels of " + ParcelGrid.Size.ToString(c) + " x " + ParcelGrid.Size.ToString(c)
                + " columns, built in " + first.ToString("0", c)
                + " ms cold, " + watch.Elapsed.TotalMilliseconds.ToString("0", c) + " ms warm");
            return 0;
        }

        // ── sim blueprint ───────────────────────────────────────────────────

        /// <summary>
        /// Runs a grammar for a genome and draws the result from the front and
        /// the side. Any gene can be set by name: --roof_pitch 0.9. Genes not
        /// named stay at their defaults, so moving one number and looking is
        /// the whole of the gene rule's test, done by hand.
        /// </summary>
        static int BlueprintCmd(Args cli)
        {
            var c = CultureInfo.InvariantCulture;
            LoadResult content;
            try { content = ContentLoader.Load(new DirectoryContentSource(cli.Text("path", DefaultContentRoot()))); }
            catch (System.Exception e) { Console.Error.WriteLine("content error: " + e.Message); return 1; }

            ContentDatabase db = content.Database;
            var genes = Godless.Sim.Culture.GeneTable.FromContent(db);
            GrammarTable grammars = GrammarTable.FromContent(db, genes);
            foreach (string problem in grammars.Problems) Console.WriteLine("refused: " + problem);
            string name = cli.Text("grammar", "dwelling");
            Grammar grammar = null;
            foreach (Grammar g in grammars.All) if (g.Name == name) grammar = g;
            if (grammar == null) { Console.Error.WriteLine("no grammar called '" + name + "'"); return 1; }

            var genome = new Godless.Sim.Culture.Genome(genes);
            var set = new List<string>();
            foreach (Godless.Sim.Culture.Gene g in genes.All)
            {
                string v = cli.Text(g.Name, null);
                double value;
                if (v == null || !double.TryParse(v, NumberStyles.Float, c, out value)) continue;
                genome.Mutate(g.Id, value, 0, Symbol.None, RecordId.None, new Annalist());
                set.Add(g.Name + " " + genome[g.Id].ToString("0.##", c));
            }

            int lot = cli.Int("lot", 60);
            Palette palette = Palette.FromContent(db);
            Blueprint bp = grammar.Build(genome, palette, lot, lot, 650);

            Console.WriteLine("grammar " + grammar.Name + (set.Count == 0 ? ", every gene at its default" : ", " + string.Join(", ", set)));
            Console.WriteLine("\nfront (looking north)" + new string(' ', Math.Max(1, bp.Width - 20)) + "   side (looking east)");
            for (int y = bp.Height - 1; y >= 0; y--)
            {
                var row = new StringBuilder();
                for (int x = 0; x < bp.Width; x++) row.Append(Glyph(bp, x, y, 0, 0, 1));
                row.Append("   ");
                for (int z = bp.Depth - 1; z >= 0; z--) row.Append(Glyph(bp, 0, y, z, 1, 0));
                Console.WriteLine(row.ToString().TrimEnd());
            }
            Console.WriteLine(new string('~', bp.Width) + "   " + new string('~', bp.Depth));

            Godless.Sim.Core.Symbol Role(string r) { return Symbol.For("role." + r); }
            bp.Span(Role("roof"), out int roofLo, out int roofHi);
            bp.Span(Role("floor"), out int floorLo, out _);
            int walls = bp.Count(Role("wall")), openings = bp.Count(Role("window")) + bp.Count(Role("door"));
            Console.WriteLine("\nsleeps " + bp.Capacity.ToString(c) + ", " + bp.Volume.ToString(c) + " voxels, " + bp.OccupiedHeight.ToString(c)
                + " high, footprint " + bp.Footprint(Role("floor")).ToString(c) + " columns, roof rises " + (roofHi - roofLo).ToString(c)
                + ", floor raised " + floorLo.ToString(c) + ", " + Pct(openings, walls + openings) + " of the walls open");
            Console.WriteLine("# wall  o window  D door  ^ roof  | post  = plinth  _ floor  * hearth");

            // S19: the same house, built from what a settlement holds.
            string held = cli.Text("stock", null);
            if (held == null) return 0;

            MaterialTable materials = MaterialTable.FromContent(db, BiomeTable.FromContent(db));
            TileSet tiles = TileSet.FromContent(db, materials);
            VoxelTypes types = VoxelTypes.FromContent(db);
            var stock = new MaterialStock(materials);
            foreach (string material in held.Split(','))
            {
                int m = materials.IndexOf(material.Trim());
                if (m < 0) { Console.Error.WriteLine("no material called '" + material.Trim() + "'"); return 1; }
                stock.Add(m, 100000);
            }

            Structure built = Realizer.Realize(bp, tiles, materials, stock, palette, types, new RngStream((ulong)cli.Int("seed", 1)));
            var letters = new Dictionary<ushort, char>();
            var legend = new List<string>();
            const string alphabet = "abcdefghijklmnopqrstuvwxyz";
            for (int m = 0; m < materials.Count; m++)
            {
                if (built.Cost[m] == 0) continue;
                ushort id = types.IdOf(materials[m].Voxel);
                letters[id] = alphabet[legend.Count % alphabet.Length];
                legend.Add(alphabet[legend.Count % alphabet.Length] + " " + materials[m].Name + " " + built.Cost[m].ToString(c));
            }

            Console.WriteLine("\nbuilt from " + held + ":");
            for (int y = bp.Height - 1; y >= 0; y--)
            {
                var row = new StringBuilder();
                for (int x = 0; x < bp.Width; x++) row.Append(Material(built, letters, x, y, 0, 0, 1, bp.Depth));
                row.Append("   ");
                for (int z = bp.Depth - 1; z >= 0; z--) row.Append(Material(built, letters, 0, y, z, 1, 0, bp.Width));
                Console.WriteLine(row.ToString().TrimEnd());
            }
            Console.WriteLine(string.Join("  ", legend) + "   (" + built.TotalVoxels.ToString(c) + " voxels, "
                + built.MaterialCount.ToString(c) + " materials)");
            foreach (string note in built.Compromises) Console.WriteLine("  compromise: " + note);
            foreach (Symbol role in built.Missing) Console.WriteLine("  nothing can build " + role);
            return 0;
        }

        /// <summary>The first material seen from outside along a line of sight.</summary>
        static char Material(Structure s, Dictionary<ushort, char> letters, int x, int y, int z, int dx, int dz, int steps)
        {
            for (int k = 0; k < steps; k++, x += dx, z += dz)
            {
                ushort id = s.At(x, y, z);
                if (id == VoxelTypes.AirId) continue;
                char ch;
                return letters.TryGetValue(id, out ch) ? ch : '?';
            }
            return ' ';
        }

        /// <summary>The first thing seen from outside along a line of sight.</summary>
        static char Glyph(Blueprint bp, int x, int y, int z, int dx, int dz)
        {
            for (; bp.InBounds(x, y, z); x += dx, z += dz)
            {
                Symbol r = bp.At(x, y, z);
                if (r.IsNone) continue;
                string n = r.ToString();
                switch (n)
                {
                    case "role.wall": return '#';
                    case "role.window": return 'o';
                    case "role.door": return 'D';
                    case "role.roof": return '^';
                    case "role.post": return '|';
                    case "role.plinth": return '=';
                    case "role.floor": return '_';
                    case "role.hearth": return '*';
                    default: return '?';
                }
            }
            return ' ';
        }


        // ── sim separate ────────────────────────────────────────────────────

        /// <summary>
        /// G1's first test as a number. For each seed, the same culture builds
        /// on the best site in two biomes, and a nearest-centroid rule is asked
        /// to tell the two piles of buildings apart. With --gene, the biome is
        /// held still and one gene is moved instead: G1's second test.
        /// </summary>
        static int Separate(Args cli)
        {
            var c = CultureInfo.InvariantCulture;
            LoadResult loaded;
            try { loaded = ContentLoader.Load(new DirectoryContentSource(cli.Text("path", DefaultContentRoot()))); }
            catch (System.Exception e) { Console.Error.WriteLine("content error: " + e.Message); return 1; }

            ContentDatabase db = loaded.Database;
            var genes = Godless.Sim.Culture.GeneTable.FromContent(db);
            WorldChoice choice;
            try { choice = WorldChoice.Pick(db, cli.Text("map", "")); }
            catch (System.Exception e) { Console.Error.WriteLine(e.Message); return 1; }
            BiomeTable biomes = choice.Biomes;
            VoxelTypes types = VoxelTypes.FromContent(db);
            MaterialTable materials = MaterialTable.FromContent(db, biomes);
            TileSet tiles = TileSet.FromContent(db, materials);
            Palette palette = Palette.FromContent(db);
            Grammar grammar = GrammarTable.FromContent(db, genes).For("shelter");
            if (grammar == null) { Console.Error.WriteLine("no grammar builds shelter"); return 1; }
            NegotiationTable negotiation = NegotiationTable.FromContent(db, genes);
            foreach (string problem in negotiation.Problems) Console.WriteLine("refused: " + problem);

            string gene = cli.Text("gene", null);
            string[] wanted = cli.Text("biomes", "temperate,highland").Split(',');
            ulong first; int count;
            cli.Seeds(out first, out count, defaultCount: 20);

            var setA = new List<Silhouette>();
            var setB = new List<Silhouette>();
            var watch = Stopwatch.StartNew();
            int used = 0;

            for (int i = 0; i < count; i++)
            {
                ulong seed = first + (ulong)i;
                var store = new ChunkStore();
                var streams = new StreamRegistry(seed);
                IslandMap island = IslandGenerator.Generate(store, streams, biomes, types, choice.Preset);

                bool[] solid = TerrainBrush.SolidTable(db, types);
                var wet = new bool[types.Count];
                ushort water;
                if (types.TryGetId(Symbol.For("voxel.water"), out water)) wet[water] = true;
                ParcelGrid grid = ParcelGrid.Build(store, solid, wet);
                ConstraintFields fields = ConstraintFields.Compute(island, grid, biomes);

                string biomeA = wanted[0].Trim();
                string biomeB = gene == null ? (wanted.Length > 1 ? wanted[1].Trim() : "highland") : biomeA;
                Silhouette one = BuildIn(biomeA, grid, island, biomes, fields, negotiation, materials, tiles, palette, types, grammar, genes, streams, gene, 0.1);
                Silhouette two = BuildIn(biomeB, grid, island, biomes, fields, negotiation, materials, tiles, palette, types, grammar, genes, streams, gene, 0.9);
                if (one == null || two == null) continue;
                setA.Add(one); setB.Add(two);
                used++;
            }
            watch.Stop();

            if (setA.Count < 2) { Console.Error.WriteLine("not enough seeds had both sites"); return 1; }

            SeparationReport report = Separation.Between(setA, setB);
            string what = gene == null
                ? "biome." + wanted[0].Trim() + " against biome." + (wanted.Length > 1 ? wanted[1].Trim() : "highland")
                : "gene." + gene + " at 0.1 against 0.9, both in biome." + wanted[0].Trim();

            Console.WriteLine(what + ", " + used.ToString(c) + " seeds, " + watch.Elapsed.TotalSeconds.ToString("0.0", c) + "s");
            Console.WriteLine("\nseparation " + (report.Accuracy * 100).ToString("0.0", c)
                + "%  (50% is a coin; a stranger has to be able to do at least as well)\n");
            Console.WriteLine("  feature          difference   mean A   mean B");
            foreach (int f in report.Strongest)
            {
                double meanA = 0.0, meanB = 0.0;
                foreach (Silhouette s in setA) meanA += s.Values[f] / setA.Count;
                foreach (Silhouette s in setB) meanB += s.Values[f] / setB.Count;
                Console.WriteLine("  " + Silhouette.Names[f].PadRight(16)
                    + report.Difference[f].ToString("+0.00;-0.00", c).PadLeft(10)
                    + meanA.ToString("0.00", c).PadLeft(9) + meanB.ToString("0.00", c).PadLeft(9));
            }
            Console.WriteLine("\ndifference is Cohen's d: how many spreads apart the two means are.");
            return 0;
        }

        /// <summary>The house a culture builds on the best site it can find in a biome, from what that land gives.</summary>
        static Silhouette BuildIn(string biomeName, ParcelGrid grid, IslandMap island, BiomeTable biomes,
                                  ConstraintFields fields, NegotiationTable negotiation,
                                  MaterialTable materials, TileSet tiles, Palette palette, VoxelTypes types,
                                  Grammar grammar, Godless.Sim.Culture.GeneTable genes, StreamRegistry streams,
                                  string gene, double value)
        {
            int px, pz;
            if (!Founding.StandInSite(grid, island, biomes, Symbol.For("biome." + biomeName), out px, out pz)) return null;

            int hx = px * ParcelGrid.Size + 2, hz = pz * ParcelGrid.Size + 2;
            Catchment catchment = Catchment.Survey(island, biomes, materials, hx, hz);

            // What a settlement here would be holding: what its own land gives
            // most readily, in proportion.
            var stock = new MaterialStock(materials);
            double best = 0.0;
            for (int m = 0; m < materials.Count; m++) best = Math.Max(best, catchment.YieldPerLabourTick(m));
            for (int m = 0; m < materials.Count; m++)
            {
                if (catchment.YieldPerLabourTick(m) <= 0.0) continue;
                stock.Add(m, (long)(800.0 * catchment.YieldPerLabourTick(m) / best));
            }

            var genome = new Godless.Sim.Culture.Genome(genes);
            if (gene != null)
                genome.Mutate(Symbol.For("gene." + gene), value, 0, Symbol.None, RecordId.None, new Annalist());

            Blueprint plan = grammar.Build(genome, palette, 60, 60, 650);
            Structure built = Realizer.Realize(plan, tiles, materials, stock, palette, types,
                                               streams.Derive("build.realization", (ulong)(px * 1000 + pz)), catchment);

            GroundPlan ground = negotiation.Count > 0
                ? negotiation.Choose(px, pz, grid, fields, genome, plan.Width - 2 * Grammar.Margin, plan.Depth - 2 * Grammar.Margin)
                : null;
            return Silhouette.Measure(plan, built, materials, types, ground);
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
            WorldChoice choice;
            try { choice = WorldChoice.Pick(db, cli.Text("map", "")); }
            catch (System.Exception e) { Console.Error.WriteLine(e.Message); return 1; }
            BiomeTable biomes = choice.Biomes;
            DriveRules rules = DriveRules.FromContent(db);
            if (biomes.Count == 0) { Console.Error.WriteLine("no biomes declared — nowhere to settle"); return 1; }

            var world = new SimWorld(seed, db, types);
            IslandMap island = IslandGenerator.Generate(world.Voxels.Store, world.Streams, biomes, types, choice.Preset,
                                                       cli.Text("bare", "false") != "false" ? null : choice.Features);
            world.Island = island;
            world.BeginHistory();

            bool[] solid = TerrainBrush.SolidTable(db, types);
            var wet = new bool[types.Count];
            ushort water;
            if (types.TryGetId(Symbol.For("voxel.water"), out water)) wet[water] = true;
            ParcelGrid grid = ParcelGrid.Build(world.Voxels.Store, solid, wet);

            int px, pz;
            if (!Founding.StandInSite(grid, island, biomes, Symbol.For("biome." + wantBiome), out px, out pz)
                && !Founding.StandInSite(grid, island, biomes, Symbol.None, out px, out pz))
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
            Profiled(world, cli, new DriveSystem(rules)); Profiled(world, cli, new Subsistence(rules)); Profiled(world, cli, new StoreSystem(FoodRules.FromContent(db))); Profiled(world, cli, new IntentSystem());
            Profiled(world, cli, new TownSystem(TownRules.FromContent(db), db, grid, biomes));

            Console.WriteLine("seed " + seed.ToString(c) + ": " + people.ToString(c) + " people found a settlement at parcel ("
                + px.ToString(c) + ", " + pz.ToString(c) + ") in " + (biome == null ? "no biome" : biome.Id.ToString())
                + ", with " + roofs.ToString(c) + " roof(s)");
            Console.WriteLine("site is a stand-in: the flattest dry parcel within four of water. S15 and S30 replace it.");

            // S11: what the land within hauling range offers to build with.
            MaterialTable materials = MaterialTable.FromContent(db, biomes);
            // The same supplies the game founds a village with: a season and a half of food.
            s.Food = people * Subsistence.MealsADay * 45;
            s.Catchment = island.Deposits != null
                ? Catchment.FromDeposits(island, biomes, materials, island.Deposits, hx, hz)
                : Catchment.Survey(island, biomes, materials, hx, hz);
            s.Stock = new MaterialStock(materials);
            int[] depositsAtFounding = DepositsInReach(island, materials, hx, hz, true);
            var offered = new List<string>();
            for (int m = 0; m < materials.Count; m++)
                if (s.Catchment.Offers(m))
                    offered.Add(materials[m].Name + " " + (s.Catchment.YieldPerLabourTick(m) / materials[m].PerLabourTick * 100).ToString("0", c) + "%");
            Console.WriteLine("within " + materials.HaulRangeVoxels.ToString(c) + " voxels the land offers, at this share of full yield: "
                + (offered.Count == 0 ? "nothing" : string.Join(", ", offered)) + "\n");
            s.Tasks = new TaskBoard(TaskKindTable.FromContent(db), s, rules, world.Streams);

            // S15: the culture, and what it calls good ground.
            var genes = Godless.Sim.Culture.GeneTable.FromContent(db);
            var genome = new Godless.Sim.Culture.Genome(genes);
            var named = new List<string>();
            foreach (Godless.Sim.Culture.Gene gene in genes.All)
            {
                string text = cli.Text(gene.Name, null);
                double value;
                if (text == null || !double.TryParse(text, NumberStyles.Float, c, out value)) continue;
                genome.Mutate(gene.Id, value, 0, s.Id, s.Founded, world.Annals);
                named.Add(gene.Name + " " + genome[gene.Id].ToString("0.##", c));
            }
            s.Genome = genome;
            if (named.Count > 0) Console.WriteLine("culture: " + string.Join(", ", named));

            // S2N: the founders as families.
            HouseholdRules householdRules = HouseholdRules.FromContent(db);
            if (householdRules != null && cli.Text("no-families", "false") == "false")
                Households.Found(s, householdRules, world.Clock.Tick, world.Annals);

            ConstraintFields constraints = ConstraintFields.Compute(island, grid, biomes);
            TileSet tileset = TileSet.FromContent(db, materials);
            Palette palette = Palette.FromContent(db);
            Profiled(world, cli, new SiteSystem(GrammarTable.FromContent(db, genes),
                                     SitingTable.FromContent(db, genes, kinds),
                                     tileset, materials, palette, grid, constraints,
                                     NegotiationTable.FromContent(db, genes)));

            // S1A: hands that lay the voxels, allocated like any other work.
            var construction = new Construction(world.Voxels, materials, types, tileset, palette,
                                                deposits: island.Deposits, ticksPerDay: world.Clock.TicksPerDay);
            construction.GroundTable = solid;
            construction.Details = world.Details;
            construction.Models = DetailModelTable.FromContent(db);
            construction.Island = island;
            Profiled(world, cli, new DepositSystem(grid));
            Profiled(world, cli, new SupportSystem(solid, materials, DetailModelTable.FromContent(db), grid));
            Profiled(world, cli, new TaskSystem(construction, grid, HaulRules.FromContent(db)));
            Profiled(world, cli, new HaulingSystem(HaulRules.FromContent(db), FoodRules.FromContent(db), DetailModelTable.FromContent(db), grid));
            Profiled(world, cli, new FarmSystem(FarmRules.FromContent(db), CropTable.FromContent(db, genes), grid, constraints, DetailModelTable.FromContent(db)));
            Profiled(world, cli, new MovementSystem(grid, rules, PastimeTable.FromContent(db)));
            world.BeginHistory();

            var header = new StringBuilder("  day  weather     in open ");
            foreach (Activity a in rules.Activities.All) if (a.Name != "sleep") header.Append(a.Name.PadLeft(11));
            header.Append("   pressure:");
            foreach (Need n in rules.Needs.All) header.Append(n.Name.PadLeft(9));
            header.Append("     stock      built  people   food");
            Console.WriteLine(header.ToString());

            int lastIntents = 0, lastProjects = 0;
            var lastTicks = new long[rules.Activities.Count];
            var lastPressure = new double[rules.Needs.Count];
            for (int d = 0; d < days; d++)
            {
                // One row is dawn to night of one day, so an intent raised at
                // dawn lands on the row of the day it was raised.
                do world.Tick(); while (world.Clock.TickOfDay != world.Clock.TicksPerDay - 1);

                // S2T: bring the first standing house down on the day asked for, as a god would.
                if (d == cli.Int("demolish-day", -1))
                {
                    Project target = null;
                    foreach (Project candidate in s.Projects) if (candidate.Complete && candidate.Host == null) { target = candidate; break; }
                    if (target != null)
                    {
                        RecordId by = world.Annals.Write(world.Clock.Tick, Symbol.For("god.brought-down"), s.Id,
                                                         Construction.World(target, target.Plan.Width / 2, 0, target.Plan.Depth / 2), RecordId.None);
                        int parts = target.Added.Count;
                        int fell = Collapse.BringDown(world, s, target, by, solid, materials, DetailModelTable.FromContent(db), grid);
                        Console.WriteLine("  -- day " + world.Clock.TotalDays.ToString(c) + ": a god brought down " + target.Site.Record + " and "
                            + parts.ToString(c) + " addition(s); " + fell.ToString(c) + " voxels fell");
                    }
                }

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
                long held = 0;
                for (int m = 0; m < s.Stock.Materials.Count; m++) held += s.Stock.Of(m);
                long placed = 0, wanted = 0;
                foreach (Project project in s.Projects) { placed += project.Placed; wanted += project.Built.TotalVoxels; }
                line.Append(held.ToString(c).PadLeft(10))
                    .Append((wanted == 0 ? "" : placed.ToString(c) + "/" + wanted.ToString(c)).PadLeft(11))
                    .Append(s.People.Count.ToString(c).PadLeft(8))
                    .Append(s.Food.ToString("0", c).PadLeft(7)).Append("   ");
                for (; lastIntents < bus.Intents.Count; lastIntents++) line.Append("+" + bus.Intents[lastIntents].Kind.Name + " ");
                for (; lastProjects < s.Projects.Count; lastProjects++)
                {
                    Site site = s.Projects[lastProjects].Site;
                    line.Append("sited (" + site.ParcelX.ToString(c) + "," + site.ParcelZ.ToString(c) + ") ");
                }
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
            // S15 and S19: where each house went, and what it is made of.
            if (s.Projects.Count > 0) Console.WriteLine("\n" + s.Projects.Count.ToString(c) + " house(s) planned, sited and costed:");
            foreach (Project project in s.Projects)
            {
                var of = new List<string>();
                for (int m = 0; m < materials.Count; m++)
                    if (project.Built.Cost[m] > 0) of.Add(project.Built.Cost[m].ToString(c) + " " + materials[m].Name);
                Console.WriteLine("  " + project.Site.Record + " parcel (" + project.Site.ParcelX.ToString(c) + ", "
                    + project.Site.ParcelZ.ToString(c) + ") score " + project.Site.Score.ToString("0.0", c)
                    + ", sleeps " + project.Plan.Capacity.ToString(c) + (project.Complete ? " in " + project.Beds.Count.ToString(c) + " beds" : "")
                    + ", " + string.Join(" + ", of)
                    + "   " + (project.Complete ? "standing" : project.Placed + " of " + project.Built.TotalVoxels + " laid")
                    + (project.Ground == null ? "" : ", " + project.Ground.Strategy.ToString().ToLowerInvariant()
                        + (project.Ground.Moved > 0 ? " (" + project.Ground.Moved.ToString(c) + " voxels of earth moved)" : "")));
                if (project.Reasons.Count > 0) Console.WriteLine("      why: " + string.Join("; ", project.Reasons));
                if (project.Host != null && cli.Text("inspect", "false") != "false")
                {
                    // Where the part's body and its host's body stand, and how much of the part is really there.
                    int present = 0, expected = 0;
                    for (int y = 0; y < project.Plan.Height; y++)
                        for (int z = 0; z < project.Plan.Depth; z++)
                            for (int x = 0; x < project.Plan.Width; x++)
                            {
                                ushort want = project.Built.At(x, y, z);
                                if (want == 0) continue;
                                expected++;
                                if (world.Voxels.Get(Construction.World(project, x, y, z)) == want) present++;
                            }
                    Int3 a0 = Construction.World(project, Grammar.Margin, 0, Grammar.Margin);
                    Int3 a1 = Construction.World(project, project.Plan.Width - 1 - Grammar.Margin, project.Plan.Height - 1, project.Plan.Depth - 1 - Grammar.Margin);
                    Project h = project.Host;
                    Int3 h0 = Construction.World(h, Grammar.Margin, 0, Grammar.Margin);
                    Int3 h1 = Construction.World(h, h.Plan.Width - 1 - Grammar.Margin, h.Plan.Height - 1, h.Plan.Depth - 1 - Grammar.Margin);
                    Console.WriteLine("      inspect: " + project.PartKind + " body x " + a0.X + ".." + a1.X + " z " + a0.Z + ".." + a1.Z
                        + " y " + a0.Y + ".." + a1.Y + "; host body x " + h0.X + ".." + h1.X + " z " + h0.Z + ".." + h1.Z
                        + " y " + h0.Y + ".." + h1.Y + "; " + present + " of " + expected + " voxels standing"
                        + ", ground " + (project.Ground == null ? "none" : project.Ground.Strategy.ToString()));
                }
                foreach (string note in project.Built.Compromises) Console.WriteLine("      " + note);
            }

            // S1C's tell: nobody was assigned anything, and yet.
            if (s.Tasks.Count > 0)
            {
                Console.WriteLine("\nwho did the gathering (nobody was assigned anything):");
                for (int j = 0; j < s.Tasks.Count; j++)
                {
                    long total = s.Tasks.TotalWork(j);
                    if (total == 0) continue;
                    // The regulars: everyone who did at least a tenth of it.
                    var regulars = new List<string>();
                    long byRegulars = 0;
                    for (int i = 0; i < s.People.Count; i++)
                        if (s.Tasks.WorkBy(i, j) * 10 >= total) { regulars.Add("#" + i.ToString(c)); byRegulars += s.Tasks.WorkBy(i, j); }
                    int m = s.Tasks.MaterialOf(j);
                    Console.WriteLine("  " + s.Tasks.TaskId(j).ToString().Replace("task.", "").PadRight(16) + total.ToString(c).PadLeft(6)
                        + " ticks, " + Pct(byRegulars, total) + " by " + (regulars.Count == 0 ? "nobody in particular" : string.Join(" ", regulars))
                        + (m >= 0 ? "   holding " + s.Stock.Of(m).ToString(c) : ""));
                }
                double sum = 0.0; int gatherers = 0;
                for (int i = 0; i < s.People.Count; i++)
                {
                    long all = 0, main = 0;
                    for (int j = 0; j < s.Tasks.Count; j++) { all += s.Tasks.WorkBy(i, j); main = Math.Max(main, s.Tasks.WorkBy(i, j)); }
                    if (all < 20) continue;
                    sum += (double)main / all; gatherers++;
                }
                if (gatherers > 0)
                    Console.WriteLine("  " + gatherers.ToString(c) + " people gathered; on average " + (sum / gatherers * 100).ToString("0", c)
                        + "% of each one's gathering was their own main material");
                Console.WriteLine("  " + s.Tasks.IdleTicks.ToString(c) + " working ticks found nothing that needed doing");
            }
            if (s.Ruins.Count > 0 || s.Rubble.Count > 0)
            {
                // S2T: what fell, and what of it still lies there.
                long beds = 0;
                foreach (Project p in s.Projects) beds += p.Beds.Count;
                Console.WriteLine("\nruins (S2T): " + s.Ruins.Count.ToString(c) + " building(s) came down; "
                    + s.Rubble.Count.ToString(c) + " voxels of rubble still lie in the settlement; "
                    + world.Details.Count.ToString(c) + " detail objects stand (" + beds.ToString(c) + " beds)");
            }

            if (island.Deposits != null)
            {
                // S2F: what is left in reach, against what was there.
                int[] now = DepositsInReach(island, materials, hx, hz, false);
                Console.WriteLine("\nwhat is left within reach (S2F), of what stood at founding:");
                for (int m = 0; m < materials.Count; m++)
                {
                    if (depositsAtFounding[m] == 0) continue;
                    int nearest = s.Catchment.NearestSource(m);
                    string walk = nearest < 0 ? "none left in reach"
                        : "nearest " + ((int)System.Math.Sqrt((double)(island.Deposits.X(nearest) - hx) * (island.Deposits.X(nearest) - hx)
                                        + (double)(island.Deposits.Z(nearest) - hz) * (island.Deposits.Z(nearest) - hz))).ToString(c)
                          + " voxels out, a tick brings " + s.Catchment.YieldPerLabourTick(m).ToString("0.00", c);
                    Console.WriteLine("  " + materials[m].Name.PadRight(8) + (now[m] * 100L / depositsAtFounding[m]).ToString(c).PadLeft(4)
                        + "%  (" + now[m].ToString(c) + " of " + depositsAtFounding[m].ToString(c) + ")  " + walk);
                }
                Console.WriteLine("  " + world.Annals.OfKind(Catchment.ExhaustedKind).Count.ToString(c)
                    + " material(s) worked out of reach; foraging now feeds " + s.Catchment.FoodPerLabourTick.ToString("0.00", c)
                    + " a tick, and the land in reach gives up " + s.Catchment.ForagePerDay.ToString("0", c) + " meals a day at most");
            }
            if (s.Households.Count > 0)
            {
                // S2N: who lives where.
                // S2I, S2H, S2X: fields, stores and what is lying about.
                Console.WriteLine("\nfarms (S2I): " + s.Farms.Count.ToString(c));
                foreach (Farm f in s.Farms)
                {
                    int[] byState = new int[4];
                    double fert = 0.0;
                    foreach (Plot p in f.Plots) { byState[(int)p.State]++; fert += p.Fertility; }
                    Console.WriteLine("  " + f.Record + " " + f.Crop.Name.PadRight(7) + f.Plots.Count.ToString(c).PadLeft(3) + " plots ("
                        + byState[0].ToString(c) + " fallow, " + byState[1].ToString(c) + " growing, " + byState[2].ToString(c) + " ripe, "
                        + byState[3].ToString(c) + " stubble), soil " + (fert / Math.Max(1, f.Plots.Count)).ToString("0.00", c)
                        + ", " + f.MealsPerDay.ToString("0.0", c) + " meals a day; last weighed growing: " + f.LastGrowth);
                }
                int farmTask = -1;
                for (int j = 0; j < s.Tasks.Count; j++) if (s.Tasks.KindOf(j).Verb == "farm") farmTask = j;
                if (farmTask >= 0)
                    Console.WriteLine("  farm task: demand " + s.Tasks.Demand(farmTask).ToString("0.0", c) + ", stimulus "
                        + s.Tasks.Stimulus(farmTask).ToString("0.000", c) + ", " + s.Tasks.TotalWork(farmTask).ToString(c) + " ticks worked");
                else Console.WriteLine("  no farm task on the board");
                var hands = new SortedDictionary<string, int>();
                for (int i = 0; i < s.People.Count; i++)
                {
                    int ct = s.Tasks.CurrentTask(i);
                    string key = ct < 0 ? "(none)" : s.Tasks.TaskId(ct).ToString();
                    hands[key] = (hands.ContainsKey(key) ? hands[key] : 0) + 1;
                }
                var handText = new List<string>();
                foreach (KeyValuePair<string, int> kv in hands) handText.Add(kv.Key + " " + kv.Value.ToString(c));
                Console.WriteLine("  hands now: " + string.Join(", ", handText));
                for (int j = 0; j < s.Tasks.Count; j++)
                    if (s.Tasks.Demand(j) > 0.0)
                        Console.WriteLine("    " + s.Tasks.TaskId(j) + " demand " + s.Tasks.Demand(j).ToString("0.0", c) + " stimulus " + s.Tasks.Stimulus(j).ToString("0.00", c));
                long harvests = world.Annals.OfKind(Farms.HarvestedKind).Count, sowings = world.Annals.OfKind(Farms.SownKind).Count;
                Console.WriteLine("  " + sowings.ToString(c) + " sowings, " + harvests.ToString(c) + " harvests on record");
                foreach (Project p in s.Projects)
                {
                    if (p.Complete || p.Destroyed || p.Built == null) continue;
                    long[] owed = Construction.Owed(p);
                    var bill = new List<string>();
                    for (int m = 0; m < owed.Length; m++)
                        if (owed[m] > 0)
                            bill.Add(materials[m].Name + " " + owed[m].ToString(c) + " (held " + s.Stock.Of(m).ToString(c) + ", heaped "
                                     + Hauling.Piled(s, m).ToString("0", c) + ", a tick brings " + s.Catchment.YieldPerLabourTick(m).ToString("0.00", c) + ")");
                    Console.WriteLine("unfinished " + p.Site.Record + " " + p.Intent.Kind.Name + (p.Host != null ? " " + p.PartKind : "")
                        + ": " + p.Placed.ToString(c) + " laid, begun " + p.Begun.Exists + ", ready " + Construction.Ready(s, p)
                        + ", obtainable " + Construction.Obtainable(s, p) + "; owes " + string.Join(", ", bill));
                }
                if (s == world.Settlements[0])
                {
                    Console.WriteLine("towns (S2Y): " + world.Settlements.Count);
                    foreach (Settlement t in world.Settlements)
                    {
                        int housedT = 0, homelessT = Towns.Homeless(t);
                        Console.WriteLine("  " + t.Id + " at parcel (" + t.HearthParcelX + ", " + t.HearthParcelZ + "), founded day "
                            + world.Annals.Get(t.Founded).Tick / world.Clock.TicksPerDay + ": " + t.People.Count + " people, "
                            + t.ShelterCapacity + " sleeping places, " + homelessT + " homeless, " + t.Farms.Count + " farms, holds "
                            + (t.Borders != null ? t.Borders.Area(t) : 0) + " parcels; homeless for " + t.HomelessDays + " days, "
                            + (t.NoSiteTick < 0 ? "never short of ground" : "last short of ground on day " + t.NoSiteTick / world.Clock.TicksPerDay));
                        if (t.Borders != null)
                        {
                            int fx, fz;
                            TownRules tr = TownRules.FromContent(db);
                            bool found = Towns.FindSite(world, db, grid, biomes, t.Borders, t, tr, out fx, out fz);
                            Console.WriteLine("    a new town from here: " + (found ? "at parcel (" + fx + ", " + fz + ")" : "nowhere (" + Towns.WhyNowhere(world, db, grid, biomes, t.Borders, t, tr) + ")"));
                        }
                    }
                    foreach (AnnalRecord r in world.Annals.OfKind(Towns.OutgrownKind))
                        Console.WriteLine("  day " + r.Tick / world.Clock.TicksPerDay + ": " + r.Subject + " outgrown, " + r.ValueA + " of " + r.ValueB + " left");
                }
                Console.WriteLine("daylight at the fire: " + (s.DaylightTicks > 0 ? 100.0 * s.DaylightAtFire / s.DaylightTicks : 0.0).ToString("0.0", c)
                    + "% of " + s.DaylightTicks.ToString(c) + " agent-ticks");

                // Why a waiting house cannot be sited, if one is waiting.
                foreach (BuildIntent open in s.Intents.Intents)
                {
                    if (open.Status != IntentStatus.Open || open.Kind.Purpose != IntentPurpose.Home) continue;
                    GrammarTable grammarsNow = GrammarTable.FromContent(db, genes);
                    SitingTable sitingNow = SitingTable.FromContent(db, genes, kinds);
                    SitingRule rule = sitingNow.For(open.Kind.Name);
                    Blueprint probe = grammarsNow.For(open.Kind.Name).Build(s.Genome, palette, 80, 80, open.BudgetVoxels,
                        new Dictionary<string, double> { { "capacity", 6 } });
                    bool[] reach = SiteScorer.ReachableFromFire(s, grid);
                    int reachable = 0, claimedNear = 0;
                    for (int qz = open.ParcelZ - rule.SearchRadius; qz <= open.ParcelZ + rule.SearchRadius; qz++)
                        for (int qx = open.ParcelX - rule.SearchRadius; qx <= open.ParcelX + rule.SearchRadius; qx++)
                        {
                            if (!ParcelGrid.InBounds(qx, qz)) continue;
                            if (reach[qz * ParcelGrid.Width + qx]) reachable++;
                            if (s.IsClaimed(qx, qz)) claimedNear++;
                        }
                    int found = SiteScorer.Candidates(s, open, probe, rule, grid, constraints, s.Genome, 50, reach).Count;
                    int land = 0, dry = 0, free = 0, allowed = 0, walk = 0;
                    int far = rule.SearchRadius * 3;
                    for (int qz = open.ParcelZ - far; qz <= open.ParcelZ + far; qz++)
                        for (int qx = open.ParcelX - far; qx <= open.ParcelX + far; qx++)
                        {
                            if (!ParcelGrid.InBounds(qx, qz) || !grid.IsLand(qx, qz)) continue;
                            land++;
                            if (grid.WetColumns(qx, qz) > 4) continue;
                            dry++;
                            if (s.IsClaimed(qx, qz) && !s.IsField(qx, qz)) continue;
                            free++;
                            if (grid.Slope[qx, qz] >= 10) continue;
                            allowed++;
                            if (reach[qz * ParcelGrid.Width + qx]) walk++;
                        }
                    Console.WriteLine("  within " + far + ": land " + land + ", dry " + dry + ", free or field " + free + ", not a cliff " + allowed + ", walkable " + walk
                        + "; plan " + probe.Width + "x" + probe.Depth);
                    int pw = (probe.Width - 2 * Grammar.Margin + ParcelGrid.Size - 1) / ParcelGrid.Size, pd = (probe.Depth - 2 * Grammar.Margin + ParcelGrid.Size - 1) / ParcelGrid.Size;
                    int ringGap = rule.Weight("wing", s.Genome) > rule.Weight("storey", s.Genome) + 0.1 ? 2 : 1;
                    Console.WriteLine("  " + pw + "x" + pd + " parcels, gap " + ringGap + ": " + SiteScorer.WhyNoSite(s, grid, open.ParcelX, open.ParcelZ, far, pw, pd, ringGap, reach));
                    Console.WriteLine("waiting house " + open.Record + " at parcel (" + open.ParcelX + ", " + open.ParcelZ + "), radius " + rule.SearchRadius
                        + ": " + found + " sites for a family of 6; " + reachable + " parcels walkable from the fire, " + claimedNear + " claimed, in reach");
                    break;
                }
                Console.WriteLine("stores (S2H): " + Stores.Capacity(s).ToString("0", c) + " meals kept in stores; "
                    + s.FoodSpoiled.ToString("0", c) + " meals rotted so far");
                double heapedFood = Hauling.Piled(s, -1);
                Console.WriteLine("heaps (S2X): " + s.Piles.Count.ToString(c) + ", " + heapedFood.ToString("0", c) + " meals of food lying about");

                Households.Settle(s);
                int housed = 0, crowded = 0, roofless = 0;
                foreach (Household h in s.Households) { if (!h.Housed) roofless++; else if (h.Crowded) crowded++; else housed++; }
                Console.WriteLine("\nfamilies (S2N): " + s.Households.Count.ToString(c) + " — " + housed.ToString(c) + " housed, "
                    + crowded.ToString(c) + " crowded, " + roofless.ToString(c) + " with no roof of their own");
                foreach (Household h in s.Households)
                    Console.WriteLine("  " + h.Id.ToString().Replace(s.Id + ".", "").PadRight(14) + h.Size.ToString(c).PadLeft(3) + " people, "
                        + (h.Housed ? h.Beds.ToString(c) + " beds in " + h.Home.Count.ToString(c) + " home(s)" : "no roof"));
            }
            foreach (TimedSystem t in Timed)
                Console.WriteLine("  profile " + t.Id.ToString().PadRight(22) + t.Watch.Elapsed.TotalSeconds.ToString("0.00", c) + " s");
            Console.WriteLine("\nroofs now: " + s.ShelterCapacity.ToString(c) + " sleeping places for " + s.People.Count.ToString(c)
                + " people (" + s.Born.ToString(c) + " born, " + s.Died.ToString(c) + " lost); "
                + s.Food.ToString("0", c) + " meals in the store, land feeds "
                + s.Catchment.FoodPerLabourTick.ToString("0.00", c) + " a forager-tick.");
            Console.WriteLine("activity ticks are agent-ticks: " + people.ToString(c) + " people x 3 daylight ticks a day.");
            return 0;
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

        static readonly List<TimedSystem> Timed = new List<TimedSystem>();

        static void Profiled(SimWorld world, Args cli, ISimSystem system)
        {
            if (cli.Text("profile", "false") == "false") { world.Add(system); return; }
            var timed = new TimedSystem(system);
            Timed.Add(timed);
            world.Add(timed);
        }

        /// <summary>Wall-clock per system, for `sim settle --profile`. Headless only: the sim never sees a clock.</summary>
        sealed class TimedSystem : ISimSystem
        {
            readonly ISimSystem _inner;
            public readonly Stopwatch Watch = new Stopwatch();
            public TimedSystem(ISimSystem inner) { _inner = inner; }
            public Symbol Id { get { return _inner.Id; } }
            public void Tick(SimWorld world) { Watch.Start(); _inner.Tick(world); Watch.Stop(); }
        }

        /// <summary>Material units per material in features within haul range; with <paramref name="initial"/>, what they started with.</summary>
        static int[] DepositsInReach(IslandMap island, MaterialTable materials, int hx, int hz, bool initial)
        {
            var units = new int[materials.Count];
            if (island.Deposits == null) return units;
            foreach (int f in island.Deposits.Within(hx, hz, materials.DepositRangeVoxels))
            {
                int m = materials.IndexOf(island.Deposits.KindOf(f).Yields);
                if (m >= 0) units[m] += initial ? island.Deposits.Initial(f) : island.Deposits.Remaining(f);
            }
            return units;
        }

        static char GlyphFor(string biomeId)
        {
            if (biomeId.EndsWith("shore", StringComparison.Ordinal)) return '.';
            if (biomeId.EndsWith("flood-plain", StringComparison.Ordinal)) return ',';
            if (biomeId.EndsWith("temperate", StringComparison.Ordinal)) return 'n';
            if (biomeId.EndsWith("highland", StringComparison.Ordinal)) return '^';
            if (biomeId.EndsWith("pine-forest", StringComparison.Ordinal)) return 'Y';
            if (biomeId.EndsWith("mesa", StringComparison.Ordinal)) return ':';
            if (biomeId.EndsWith("alpine", StringComparison.Ordinal)) return '#';
            return '*';
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

        // ── sim maps ────────────────────────────────────────────────────────

        /// <summary>
        /// What worlds there are to play on, and what each one gives you to
        /// build with. Generates nothing: this is the content view, and the
        /// thing it is for is catching a map that admits no biome offering
        /// timber, or a biome window no map can reach.
        /// </summary>
        static int Maps(Args cli)
        {
            var c = CultureInfo.InvariantCulture;
            LoadResult loaded;
            try { loaded = ContentLoader.Load(new DirectoryContentSource(cli.Text("path", DefaultContentRoot()))); }
            catch (System.Exception e) { Console.Error.WriteLine("content error: " + e.Message); return 1; }

            ContentDatabase db = loaded.Database;
            BiomeTable all = BiomeTable.FromContent(db);
            WorldTable maps = WorldTable.FromContent(db, all);

            foreach (string problem in maps.Problems) Console.WriteLine("refused: " + problem);
            if (maps.Count == 0) { Console.WriteLine("no maps declared — every world is the built-in island"); return 0; }

            foreach (WorldPreset w in maps.All)
            {
                Console.WriteLine();
                Console.WriteLine(w.Name + "  \"" + w.Title + "\"");
                Console.WriteLine("  " + w.Tell);

                string shape = w.Centres == 1 ? "one land mass" : w.Centres.ToString(c) + " land masses";
                Console.WriteLine("  " + shape
                    + ", sea at " + w.SeaLevel.ToString(c)
                    + ", ground " + w.Base.ToString(c) + " to " + (w.Base + w.Relief).ToString(c)
                    + (w.Step > 0 ? ", terraced every " + w.Step.ToString(c) : "")
                    + ", curve " + w.Linear.ToString("0.00", c) + " straight / " + w.Cubic.ToString("0.00", c) + " cubed");
                Console.WriteLine("  water: channels at " + w.RiverFlow.ToString(c)
                    + " flow, lakes to " + w.MaxLakeDepth.ToString(c) + " deep"
                    + (w.MoistureBias > 0.0 ? ", air wetter by " + w.MoistureBias.ToString("0.00", c)
                       : w.MoistureBias < 0.0 ? ", air drier by " + (-w.MoistureBias).ToString("0.00", c) : ""));

                BiomeTable admitted = w.Biomes.Count == 0 ? all : BiomeTable.FromContent(db, w.Biomes);
                var names = new List<string>();
                var materials = new List<string>();
                var classes = new SortedDictionary<string, int>(StringComparer.Ordinal);
                foreach (Biome b in admitted.All)
                {
                    names.Add(b.Name);
                    foreach (string m in b.MaterialNames)
                    {
                        if (!materials.Contains(m)) materials.Add(m);
                        JsonValue doc = db.Get("voxel", m);
                        string cls = doc["class"].AsString("(none)");
                        int had; classes.TryGetValue(cls, out had);
                        classes[cls] = had + 1;
                    }
                }
                materials.Sort(StringComparer.Ordinal);

                Console.WriteLine("  biomes: " + string.Join(", ", names.ToArray()));
                Console.WriteLine("  builds with: " + string.Join(", ", materials.ToArray()));

                if (!classes.ContainsKey("timber"))
                    Console.WriteLine("  note: no timber anywhere on this map — everything here is stone or earth.");
                if (!classes.ContainsKey("stone"))
                    Console.WriteLine("  note: no stone anywhere on this map.");

                // A window no column can land in is content that will never
                // appear; report it rather than letting it pass silently.
                int reachable = 0;
                for (int e = 0; e <= 100; e += 2)
                    for (int m = 0; m <= 100; m += 2)
                    {
                        bool exact;
                        admitted.Select(e, m, out exact);
                        if (exact) reachable++;
                    }
                Console.WriteLine("  " + (reachable * 100 / (51 * 51)).ToString(c)
                    + "% of the elevation/moisture square lands inside a biome's own window");
            }

            return 0;
        }

        static void Help()
        {
            Console.WriteLine(
@"godless sim harness

  sim run      [--seeds A..B] [--years N] [--island | --settle [--map M] [--people P]]
                                            batch run, checking every invariant
  sim verify   [--seeds A..B] [--years N]   run each seed twice, compare byte for byte
  sim content  [--path P]                   load Assets/Content and report what it holds
  sim maps     [--path P]                   list the maps content declares, and what each is like
  sim island   [--seed N] [--width W] [--map M]
                                            generate an island and draw it
  sim parcels  [--seed N] [--field F]       draw a planning field: height, slope, water-distance,
                                            sun, snow-load, damp, exposure, flood-risk
  sim blueprint [--grammar G] [--<gene> V ...] [--stock a,b,c] [--lot N]
                                            run a grammar for a genome and draw the house (S18)
  sim separate [--seeds A..B] [--gene G] [--biomes A,B]
                                            measure whether two biomes (or two cultures) build differently (S1G)
  sim settle   [--seed N] [--days D] [--people P] [--roofs R] [--biome B] [--<gene> V ...]
                                            found a settlement and print its days (S12, S14)

--map picks one of the worlds in Assets/Content (see `sim maps`); without it
you get the built-in island with every biome content declares. It works on
run, island, parcels, separate and settle.

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
