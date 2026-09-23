using System.Collections.Generic;
using Godless.Sim.Content;
using Godless.Sim.Core;
using Godless.Sim.Culture;
using Godless.Sim.Voxels;
using Godless.Sim.World;

namespace Godless.Sim.Build
{
    /// <summary>How a building meets ground that is not flat.</summary>
    public enum GroundStrategy
    {
        /// <summary>Level the site: cut the high side away and fill the low side up.</summary>
        CutAndFill,

        /// <summary>Step it: two or three levels, each cut into the slope.</summary>
        Terrace,

        /// <summary>Touch it as little as possible: posts down to whatever the ground is doing.</summary>
        Stilt,
    }

    /// <summary>One way of meeting the ground, and when a culture reaches for it.</summary>
    public sealed class GroundRule
    {
        public GroundStrategy Strategy { get; internal set; }
        public string Name { get; internal set; }
        public string Tell { get; internal set; }
        internal Expr Score;
    }

    /// <summary>
    /// Terrain negotiation. S16.
    ///
    /// Part 05: "The genome and the slope pick a strategy: cut-and-fill,
    /// terrace, stilt, plinth or carve. This one stage does more for 'responds
    /// to its environment' than anything else in the pipeline." S1G measured
    /// the want of it: without this, two biomes' buildings differ only in what
    /// they are made of, because nothing makes the shape answer to the ground.
    ///
    /// Which strategy a culture reaches for is content, scored in the same
    /// expression language as everything else, over the site's own slope and
    /// wetness and the genome. Carve into the cliff is left to a later stratum
    /// with the biome that deserves it.
    /// </summary>
    public sealed class NegotiationTable
    {
        readonly GroundRule[] _rules;
        readonly List<string> _problems;

        NegotiationTable(GroundRule[] rules, List<string> problems) { _rules = rules; _problems = problems; }

        public int Count { get { return _rules.Length; } }
        public IReadOnlyList<GroundRule> All { get { return _rules; } }
        public IReadOnlyList<string> Problems { get { return _problems; } }

        static readonly string[] Known =
        {
            "site.slope", "site.drop", "site.wet", "site.water", "field.flood", "field.damp", "field.exposure",
        };

        static readonly string[] Strategies = { "cut-and-fill", "terrace", "stilt" };

        public static NegotiationTable FromContent(ContentDatabase content, GeneTable genes, string id = "ground")
        {
            var problems = new List<string>();
            var loaded = new List<GroundRule>();
            if (!content.Contains("negotiation", id)) return new NegotiationTable(loaded.ToArray(), problems);

            JsonValue doc = content.Get("negotiation", id);
            JsonValue ways = doc["strategies"];
            for (int i = 0; i < ways.Keys.Count; i++)
            {
                string name = ways.Keys[i];
                int which = System.Array.IndexOf(Strategies, name);
                if (which < 0) { problems.Add("negotiation names '" + name + "', which is not one of " + string.Join(", ", Strategies) + "."); continue; }

                JsonValue rule = ways[name];
                string tell = rule["tell"].AsString("").Trim();
                if (tell.Length == 0) { problems.Add("negotiation '" + name + "' declares no tell."); continue; }

                Expr score;
                try { score = Expr.Parse(rule["score"].AsString("0")); }
                catch (ExprException e) { problems.Add("negotiation '" + name + "' has a broken score: " + e.Message); continue; }

                string fault = null;
                var names = new List<string>();
                score.Names(names);
                foreach (string n in names)
                {
                    if (System.Array.IndexOf(Known, n) >= 0) continue;
                    if (n.StartsWith("gene.", System.StringComparison.Ordinal) && genes.IndexOf(Symbol.For(n)) >= 0) continue;
                    fault = "reads '" + n + "', which is not a site fact or a gene";
                    break;
                }
                if (fault != null) { problems.Add("negotiation '" + name + "' " + fault + "."); continue; }

                loaded.Add(new GroundRule { Strategy = (GroundStrategy)which, Name = name, Tell = tell, Score = score });
            }

            loaded.Sort((a, b) => ((int)a.Strategy).CompareTo((int)b.Strategy));
            return new NegotiationTable(loaded.ToArray(), problems);
        }

        /// <summary>
        /// How this culture will meet the ground at a parcel, and the level each
        /// column of the footprint should end up at. Stilts leave the ground alone.
        /// </summary>
        public GroundPlan Choose(int parcelX, int parcelZ, ParcelGrid grid, ConstraintFields fields, Genome genome, int width, int depth)
        {
            int x0 = parcelX * ParcelGrid.Size, z0 = parcelZ * ParcelGrid.Size;
            var ground = new int[width * depth];
            int lowest = int.MaxValue, highest = int.MinValue;
            for (int z = 0; z < depth; z++)
                for (int x = 0; x < width; x++)
                {
                    int g = grid.GroundAt(Clamp(x0 + x, ChunkStore.SizeX), Clamp(z0 + z, ChunkStore.SizeZ)) + 1;
                    ground[z * width + x] = g;
                    if (g < lowest) lowest = g;
                    if (g > highest) highest = g;
                }

            var scope = new SiteScope
            {
                Genome = genome,
                Slope = grid.Slope[parcelX, parcelZ],
                Drop = highest - lowest,
                Wet = grid.WetColumns(parcelX, parcelZ),
                Water = grid.WaterDistance[parcelX, parcelZ],
                Flood = fields.FloodRisk[parcelX, parcelZ],
                Damp = fields.Damp[parcelX, parcelZ],
                Exposure = fields.Exposure[parcelX, parcelZ],
            };

            GroundRule best = null;
            double bestScore = double.NegativeInfinity;
            foreach (GroundRule rule in _rules)
            {
                double score = rule.Score.Eval(scope);
                if (score > bestScore) { bestScore = score; best = rule; }
            }
            if (best == null) return new GroundPlan { Strategy = GroundStrategy.CutAndFill, Level = Flat(ground, width, depth, Median(ground)), Width = width, Depth = depth, Floor = Median(ground) };

            switch (best.Strategy)
            {
                case GroundStrategy.Stilt:
                    // Nothing is moved. The floor clears the highest ground and
                    // the posts run down to whatever is under each of them.
                    return new GroundPlan { Strategy = GroundStrategy.Stilt, Level = null, Width = width, Depth = depth, Floor = highest, Ground = ground };

                case GroundStrategy.Terrace:
                {
                    // Steps across the steeper axis, each level in itself.
                    int steps = scope.Drop >= 6 ? 3 : 2;
                    var level = new int[width * depth];
                    bool alongX = SpanOf(ground, width, depth, true) >= SpanOf(ground, width, depth, false);
                    for (int z = 0; z < depth; z++)
                        for (int x = 0; x < width; x++)
                        {
                            int along = alongX ? x : z, len = alongX ? width : depth;
                            int step = along * steps / System.Math.Max(1, len);
                            level[z * width + x] = StepLevel(ground, width, depth, alongX, steps, step);
                        }
                    return new GroundPlan { Strategy = GroundStrategy.Terrace, Level = level, Width = width, Depth = depth, Floor = Median(level), Ground = ground };
                }

                default:
                {
                    int flat = Median(ground);
                    return new GroundPlan { Strategy = GroundStrategy.CutAndFill, Level = Flat(ground, width, depth, flat), Width = width, Depth = depth, Floor = flat, Ground = ground };
                }
            }
        }

        /// <summary>
        /// Ground for a part that must meet its host's floor (S2P): levelled to
        /// that floor, or on posts to it if the host stands on posts. Columns
        /// start at a world column, not a parcel, because a part is set against
        /// a wall rather than on a parcel line.
        /// </summary>
        public static GroundPlan AtFloor(ParcelGrid grid, int worldX, int worldZ, int width, int depth, int floor, bool stilts)
        {
            var ground = new int[width * depth];
            for (int z = 0; z < depth; z++)
                for (int x = 0; x < width; x++)
                    ground[z * width + x] = grid.GroundAt(Clamp(worldX + x, ChunkStore.SizeX), Clamp(worldZ + z, ChunkStore.SizeZ)) + 1;
            if (stilts) return new GroundPlan { Strategy = GroundStrategy.Stilt, Level = null, Width = width, Depth = depth, Floor = floor, Ground = ground };
            return new GroundPlan { Strategy = GroundStrategy.CutAndFill, Level = Flat(ground, width, depth, floor), Width = width, Depth = depth, Floor = floor, Ground = ground };
        }

        static int Clamp(int v, int n) { return v < 0 ? 0 : (v >= n ? n - 1 : v); }

        static int[] Flat(int[] ground, int width, int depth, int level)
        {
            var flat = new int[width * depth];
            for (int i = 0; i < flat.Length; i++) flat[i] = level;
            return flat;
        }

        static int SpanOf(int[] ground, int width, int depth, bool alongX)
        {
            int first = 0, last = 0, n = 0;
            for (int z = 0; z < depth; z++)
                for (int x = 0; x < width; x++)
                {
                    int at = ground[z * width + x];
                    bool low = alongX ? x < width / 2 : z < depth / 2;
                    if (low) { first += at; n++; } else last += at;
                }
            if (n == 0) return 0;
            int d = first / n - last / System.Math.Max(1, width * depth - n);
            return d < 0 ? -d : d;
        }

        static int StepLevel(int[] ground, int width, int depth, bool alongX, int steps, int step)
        {
            var run = new List<int>();
            for (int z = 0; z < depth; z++)
                for (int x = 0; x < width; x++)
                {
                    int along = alongX ? x : z, len = alongX ? width : depth;
                    if (along * steps / System.Math.Max(1, len) == step) run.Add(ground[z * width + x]);
            }
            return run.Count == 0 ? 0 : Median(run.ToArray());
        }

        static int Median(int[] values)
        {
            var sorted = new List<int>(values);
            sorted.Sort();
            return sorted.Count == 0 ? 0 : sorted[sorted.Count / 2];
        }

        sealed class SiteScope : IExprScope
        {
            public Genome Genome;
            public double Slope, Drop, Wet, Water, Flood, Damp, Exposure;

            public double Resolve(string name)
            {
                switch (name)
                {
                    case "site.slope": return Slope;
                    case "site.drop": return Drop;
                    case "site.wet": return Wet;
                    case "site.water": return Water;
                    case "field.flood": return Flood;
                    case "field.damp": return Damp;
                    case "field.exposure": return Exposure;
                }
                if (name.StartsWith("gene.", System.StringComparison.Ordinal))
                {
                    double v = Genome[Symbol.For(name)];
                    return double.IsNaN(v) ? 0.0 : v;
                }
                return 0.0;
            }
        }
    }

    /// <summary>What the ground under a building should end up as, and how the building meets it.</summary>
    public sealed class GroundPlan
    {
        public GroundStrategy Strategy { get; internal set; }

        /// <summary>Level each column should be cut or filled to, or null under stilts.</summary>
        internal int[] Level;

        /// <summary>What the ground was before anybody touched it.</summary>
        internal int[] Ground;

        public int Width { get; internal set; }
        public int Depth { get; internal set; }

        /// <summary>The level the building's own floor sits at.</summary>
        public int Floor { get; internal set; }

        public int LevelAt(int x, int z)
        {
            return Level == null || x < 0 || z < 0 || x >= Width || z >= Depth ? -1 : Level[z * Width + x];
        }

        public int GroundAt(int x, int z)
        {
            return Ground == null || x < 0 || z < 0 || x >= Width || z >= Depth ? -1 : Ground[z * Width + x];
        }

        /// <summary>Voxels of earth moved: cut away plus filled in.</summary>
        public int Moved { get; internal set; }
    }
}
