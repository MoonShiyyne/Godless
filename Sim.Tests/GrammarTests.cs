using System.Collections.Generic;
using System.IO;
using Godless.Sim.Annals;
using Godless.Sim.Build;
using Godless.Sim.Content;
using Godless.Sim.Core;
using Godless.Sim.Culture;
using Godless.Sim.World;
using Xunit;

namespace Godless.Sim.Tests
{
    public class ExprTests
    {
        sealed class Names : IExprScope
        {
            public double Resolve(string name) { return name == "x" ? 4.0 : (name == "gene.a" ? 0.25 : 0.0); }
        }

        static double E(string s) { return Expr.Parse(s).Eval(new Names()); }

        [Fact]
        public void ArithmeticReadsTheWayItIsWritten()
        {
            Assert.Equal(7.0, E("1 + 2 * 3"));
            Assert.Equal(9.0, E("(1 + 2) * 3"));
            Assert.Equal(-2.0, E("-x / 2"));
            Assert.Equal(5.0, E("lerp(4, 8, gene.a)"));
            Assert.Equal(3.0, E("round(2.5)"));
            Assert.Equal(2.0, E("sqrt(x)"));
            Assert.Equal(1.0, E("x > 3"));
            Assert.Equal(0.0, E("x < 3"));
            Assert.Equal(1.0, E("x == 4"));
            Assert.Equal(6.0, E("clamp(9, 0, 6)"));
            Assert.Equal(0.0, E("x / 0"));   // a zero size, never a NaN wall
        }

        [Theory]
        [InlineData("1 +")]
        [InlineData("lerp(1, 2)")]
        [InlineData("teleport(3)")]
        [InlineData("(1 + 2")]
        [InlineData("3 $ 4")]
        public void BrokenExpressionsAreRefused(string s)
        {
            Assert.Throws<ExprException>(() => Expr.Parse(s));
        }
    }

    public class GrammarTests
    {
        static ContentDatabase Shipped()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Assets", "Content"))) dir = dir.Parent;
            return ContentLoader.Load(new DirectoryContentSource(Path.Combine(dir.FullName, "Assets", "Content"))).Database;
        }

        static readonly ContentDatabase Content = Shipped();
        static readonly GeneTable Genes = GeneTable.FromContent(Content);
        static readonly Palette Pal = Palette.FromContent(Content);

        static Grammar Dwelling()
        {
            GrammarTable table = GrammarTable.FromContent(Content, Genes);
            Assert.Empty(table.Problems);
            return table.For("shelter");
        }

        static Blueprint Build(params (string gene, double value)[] set)
        {
            var genome = new Genome(Genes);
            foreach (var (gene, value) in set) genome.Mutate(Symbol.For("gene." + gene), value, 0, Symbol.None, RecordId.None, new Annalist());
            return Dwelling().Build(genome, Pal, 60, 60, 600);
        }

        static Symbol R(string role) { return Symbol.For("role." + role); }

        [Fact]
        public void TheShippedDwellingGrammarLoadsAndAnswersShelter()
        {
            Grammar g = Dwelling();
            Assert.NotNull(g);
            Assert.False(string.IsNullOrWhiteSpace(g.Tell));
        }

        static string WithRules(string lets, string rules)
        {
            return "{\"type\":\"grammar\",\"id\":\"bad\",\"builds\":\"shelter\",\"tell\":\"x\",\"let\":{" + lets + "},\"rules\":{" + rules + "}}";
        }

        const string Box = "\"width\":\"6\",\"depth\":\"6\",\"height\":\"6\"";

        [Theory]
        [InlineData(Box + ",\"x\":\"gene.wingspan\"", "\"Lot\":{\"op\":\"fill\",\"role\":\"wall\"}", "'gene.wingspan'")]
        [InlineData(Box, "\"Lot\":\"Missing\"", "'Missing'")]
        [InlineData(Box + ",\"a\":\"b + 1\",\"b\":\"a + 1\"", "\"Lot\":{\"op\":\"fill\",\"role\":\"wall\"}", "circle")]
        [InlineData(Box + ",\"a\":\"scope.u\"", "\"Lot\":{\"op\":\"fill\",\"role\":\"wall\"}", "'scope.u'")]
        [InlineData(Box, "\"Lot\":{\"op\":\"teleport\"}", "'teleport'")]
        [InlineData(Box, "\"Lot\":{\"op\":\"repeat\",\"axis\":\"u\",\"size\":\"(2\",\"rule\":null}", "broken expression")]
        [InlineData("\"width\":\"6\",\"height\":\"6\"", "\"Lot\":{\"op\":\"fill\",\"role\":\"wall\"}", "'depth'")]
        public void AGrammarThatCannotWorkIsRefusedAndSaysWhy(string lets, string rules, string expected)
        {
            var src = new MemoryContentSource().Add("base", "mod.json", "{}").Add("base", "g.json", WithRules(lets, rules));
            GrammarTable table = GrammarTable.FromContent(ContentLoader.Load(src).Database, Genes);
            Assert.Equal(0, table.Count);
            string problem = Assert.Single(table.Problems);
            Assert.Contains(expected, problem);
        }

        /// <summary>
        /// The gene rule, finally testable (S17's tell): every gene must change
        /// the silhouette at normal camera distance. Each of the six moves its
        /// own feature of the house, by a margin a stranger could see, and the
        /// house seen from outside is a different shape.
        /// </summary>
        [Fact]
        public void EveryGeneMovesTheSilhouette()
        {
            var cases = new (string gene, System.Func<Blueprint, double> measure, double minChange, string what)[]
            {
                ("roof_pitch", b => { b.Span(R("roof"), out int lo, out int hi); return hi - lo; }, 4, "roof rise"),
                ("verticality", b => b.OccupiedHeight, 4, "height"),   // a storey, less what the roof loses on a tighter plan
                ("footprint_area", b => b.Footprint(R("floor")), 30, "footprint"),
                ("elevation_bias", b => { b.Span(R("floor"), out int lo, out _); return lo; }, 6, "floor raised"),
                ("aperture_ratio", b => (b.Count(R("window")) + b.Count(R("door"))) / (double)(b.Count(R("wall")) + b.Count(R("window")) + b.Count(R("door"))), 0.12, "openness"),
                ("communal_ratio", b => b.Capacity, 7, "sleeps"),
            };

            foreach (var c in cases)
            {
                Blueprint low = Build((c.gene, 0.1)), high = Build((c.gene, 0.9));
                double a = c.measure(low), b = c.measure(high);
                Assert.True(b - a >= c.minChange, c.gene + ": " + c.what + " went from " + a + " to " + b);
                Assert.True(SilhouetteDifference(low, high) > 20, c.gene + " barely changes the outline");
            }
        }

        /// <summary>The front and side outlines, as seen from outside: which cells are sky and which are house.</summary>
        static int SilhouetteDifference(Blueprint a, Blueprint b)
        {
            int diff = 0, w = System.Math.Max(a.Width, b.Width), d = System.Math.Max(a.Depth, b.Depth), h = System.Math.Max(a.Height, b.Height);
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++) if (Seen(a, x, y, true) != Seen(b, x, y, true)) diff++;
                for (int z = 0; z < d; z++) if (Seen(a, z, y, false) != Seen(b, z, y, false)) diff++;
            }
            return diff;
        }

        static bool Seen(Blueprint bp, int i, int y, bool front)
        {
            int n = front ? bp.Depth : bp.Width;
            for (int k = 0; k < n; k++)
            {
                Symbol r = front ? bp.At(i, y, k) : bp.At(k, y, i);
                if (!r.IsNone && r != R("window") && r != R("door")) return true;
            }
            return false;
        }

        /// <summary>
        /// Across two hundred random genomes every house is a house: a floor,
        /// walls, a roof, a hearth, a door at floor level, and a size the
        /// shelter budget can recognise.
        /// </summary>
        [Fact]
        public void EveryGenomeBuildsAHouse()
        {
            Grammar g = Dwelling();
            var rng = new RngStream(StableHash.OfString("test.genomes"));
            for (int i = 0; i < 200; i++)
            {
                var genome = new Genome(Genes);
                foreach (Gene gene in Genes.All)
                    genome.Mutate(gene.Id, rng.NextInt(1001) / 1000.0, 0, Symbol.None, RecordId.None, new Annalist());
                Blueprint bp = g.Build(genome, Pal, 60, 60, 600);

                foreach (string role in new[] { "floor", "wall", "roof", "hearth", "door" })
                    Assert.True(bp.Count(R(role)) > 0, "genome " + i + " has no " + role);
                bp.Span(R("floor"), out int floor, out _);
                bp.Span(R("door"), out int door, out _);
                Assert.Equal(floor + 1, door);
                Assert.True(bp.Capacity >= 3);
                Assert.InRange(bp.Volume, 150, 8000);   // a runaway-grammar bound, not a style rule
            }
        }

        [Fact]
        public void TheSameGenomeBuildsTheSameHouse()
        {
            Assert.Equal(Build(("roof_pitch", 0.7)).Digest(), Build(("roof_pitch", 0.7)).Digest());
            Assert.NotEqual(Build(("roof_pitch", 0.7)).Digest(), Build(("roof_pitch", 0.2)).Digest());
        }

        [Fact]
        public void AHouseNeverOutgrowsItsLot()
        {
            var genome = new Genome(Genes);
            genome.Mutate(Symbol.For("gene.footprint_area"), 1.0, 0, Symbol.None, RecordId.None, new Annalist());
            genome.Mutate(Symbol.For("gene.communal_ratio"), 1.0, 0, Symbol.None, RecordId.None, new Annalist());
            Blueprint bp = Dwelling().Build(genome, Pal, 12, 9, 600);
            Assert.True(bp.Width <= 12 + 2 * Grammar.Margin);
            Assert.True(bp.Depth <= 9 + 2 * Grammar.Margin);
        }
    }
}
