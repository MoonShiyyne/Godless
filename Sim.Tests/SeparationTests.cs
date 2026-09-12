using System.Collections.Generic;
using System.IO;
using Godless.Sim.Annals;
using Godless.Sim.Build;
using Godless.Sim.Content;
using Godless.Sim.Core;
using Godless.Sim.Culture;
using Godless.Sim.Settlements;
using Godless.Sim.Voxels;
using Godless.Sim.World;
using Xunit;

namespace Godless.Sim.Tests
{
    public class SeparationTests
    {
        static ContentDatabase Load()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Assets", "Content"))) dir = dir.Parent;
            return ContentLoader.Load(new DirectoryContentSource(Path.Combine(dir.FullName, "Assets", "Content"))).Database;
        }

        static readonly ContentDatabase Content = Load();
        static readonly GeneTable Genes = GeneTable.FromContent(Content);
        static readonly Palette Pal = Palette.FromContent(Content);
        static readonly BiomeTable Biomes = BiomeTable.FromContent(Content);
        static readonly MaterialTable Materials = MaterialTable.FromContent(Content, Biomes);
        static readonly TileSet Tiles = TileSet.FromContent(Content, Materials);
        static readonly VoxelTypes Types = VoxelTypes.FromContent(Content);

        static Silhouette Made(params double[] values)
        {
            var s = new Silhouette();
            for (int i = 0; i < values.Length && i < s.Values.Length; i++) s.Values[i] = values[i];
            return s;
        }

        [Fact]
        public void TwoPilesOfTheSameThingSeparateNoBetterThanAGuess()
        {
            var rng = new RngStream(StableHash.OfString("test.same"));
            var a = new List<Silhouette>();
            var b = new List<Silhouette>();
            for (int i = 0; i < 40; i++)
            {
                (i % 2 == 0 ? a : b).Add(Made(10 + rng.NextInt(6), 30 + rng.NextInt(10), 1.0 + rng.NextInt(5) / 10.0));
            }
            SeparationReport report = Separation.Between(a, b);
            Assert.InRange(report.Accuracy, 0.3, 0.7);
        }

        [Fact]
        public void TwoPilesOfDifferentThingsSeparateCompletely()
        {
            var rng = new RngStream(StableHash.OfString("test.different"));
            var a = new List<Silhouette>();
            var b = new List<Silhouette>();
            for (int i = 0; i < 20; i++)
            {
                a.Add(Made(10 + rng.NextInt(3), 30 + rng.NextInt(4)));
                b.Add(Made(40 + rng.NextInt(3), 90 + rng.NextInt(4)));
            }
            SeparationReport report = Separation.Between(a, b);
            Assert.True(report.Accuracy > 0.95, report.Accuracy.ToString());
            Assert.True(System.Math.Abs(report.Difference[0]) > 3.0);
        }

        /// <summary>
        /// The trap the metric fell into first: a feature that is the same in
        /// every building on each side and different between them has no
        /// within-set spread, and scaling by that spread threw away the
        /// strongest evidence there was. Every roof here rises two or seven.
        /// </summary>
        [Fact]
        public void AFeatureWithNoSpreadStillSeparates()
        {
            var a = new List<Silhouette>();
            var b = new List<Silhouette>();
            for (int i = 0; i < 12; i++)
            {
                a.Add(Made(16, 40, 1.6, 2));
                b.Add(Made(16, 40, 1.6, 7));
            }
            SeparationReport report = Separation.Between(a, b);
            Assert.Equal(1.0, report.Accuracy);
            Assert.Equal(3, report.Strongest[0]);   // roof rise
        }

        static Silhouette House(Genome genome, MaterialStock stock, Catchment catchment, ulong seed)
        {
            Grammar grammar = GrammarTable.FromContent(Content, Genes).For("shelter");
            Blueprint plan = grammar.Build(genome, Pal, 60, 60, 650);
            Structure built = Realizer.Realize(plan, Tiles, Materials, stock, Pal, Types, new RngStream(seed), catchment);
            return Silhouette.Measure(plan, built, Materials, Types);
        }

        static MaterialStock Yard(params string[] materials)
        {
            var stock = new MaterialStock(Materials);
            foreach (string m in materials) stock.Add(Materials.IndexOf(m), 900);
            return stock;
        }

        /// <summary>
        /// G1's second test, in the suite. Two ways of asking it, because they
        /// are different questions: with the rest of the culture held still —
        /// which is how the gate asks it, one settlement before and after —
        /// the buildings separate outright. With the other five genes varying
        /// underneath, one gene no longer identifies a building on its own;
        /// what survives is the direction, pair by pair.
        /// </summary>
        [Theory]
        [InlineData("roof_pitch", 3)]
        [InlineData("verticality", 0)]
        [InlineData("footprint_area", 1)]
        [InlineData("elevation_bias", 4)]
        [InlineData("aperture_ratio", 5)]
        [InlineData("communal_ratio", 2)]
        public void NudgeOneGeneAndTheBuildingsSeparate(string gene, int feature)
        {
            var rng = new RngStream(StableHash.OfString("test.separate." + gene));
            MaterialStock yard = Yard("oak", "granite", "thatch");

            // As the gate asks it: everything else held, the same house before
            // and after. Different seeds only vary the weave of the materials.
            var still = new List<Silhouette>();
            var moved = new List<Silhouette>();
            for (int i = 0; i < 12; i++)
            {
                var a = new Genome(Genes);
                var b = new Genome(Genes);
                a.Mutate(Symbol.For("gene." + gene), 0.1, 0, Symbol.None, RecordId.None, new Annalist());
                b.Mutate(Symbol.For("gene." + gene), 0.9, 0, Symbol.None, RecordId.None, new Annalist());
                still.Add(House(a, yard, null, (ulong)i + 1));
                moved.Add(House(b, yard, null, (ulong)i + 1));
            }
            SeparationReport report = Separation.Between(still, moved);
            Assert.True(report.Accuracy >= 0.9, gene + " separates at " + report.Accuracy);

            // And with the rest of the culture varying: the gene still moves
            // its own feature the same way in nearly every pair.
            int agreed = 0, pairs = 14, up = 0;
            for (int i = 0; i < pairs; i++)
            {
                var a = new Genome(Genes);
                var b = new Genome(Genes);
                foreach (Gene other in Genes.All)
                {
                    if (other.Name == gene) continue;
                    double v = rng.NextInt(1001) / 1000.0;
                    a.Mutate(other.Id, v, 0, Symbol.None, RecordId.None, new Annalist());
                    b.Mutate(other.Id, v, 0, Symbol.None, RecordId.None, new Annalist());
                }
                a.Mutate(Symbol.For("gene." + gene), 0.1, 0, Symbol.None, RecordId.None, new Annalist());
                b.Mutate(Symbol.For("gene." + gene), 0.9, 0, Symbol.None, RecordId.None, new Annalist());

                double low = House(a, yard, null, (ulong)i + 1).Values[feature];
                double high = House(b, yard, null, (ulong)i + 1).Values[feature];
                if (high > low) up++;
                if (high != low) agreed++;
            }
            Assert.True(agreed >= pairs - 1, gene + " changed nothing in " + (pairs - agreed) + " of " + pairs + " pairs");
            Assert.True(up >= pairs - 1 || up <= 1, gene + " moved " + Silhouette.Names[feature] + " both ways: up in " + up + " of " + pairs);
        }

        /// <summary>
        /// G1's first test, in the suite and at small scale: the same culture
        /// on two islands' worth of different land builds differently, because
        /// what it builds with is what the land gives.
        /// </summary>
        [Fact]
        public void ChangeTheLandAndTheBuildingsChange()
        {
            var woods = new List<Silhouette>();
            var uplands = new List<Silhouette>();
            for (int i = 0; i < 12; i++)
            {
                var genome = new Genome(Genes);
                woods.Add(House(genome, Yard("oak", "thatch", "granite"), null, (ulong)i + 1));
                uplands.Add(House(genome, Yard("slate", "granite"), null, (ulong)i + 1));
            }

            SeparationReport report = Separation.Between(woods, uplands);
            Assert.True(report.Accuracy > 0.9, "the two stocks build alike: " + report.Accuracy);
        }

        [Fact]
        public void ASilhouetteMeasuresWhatCanBeSeen()
        {
            var tall = new Genome(Genes);
            tall.Mutate(Symbol.For("gene.verticality"), 1.0, 0, Symbol.None, RecordId.None, new Annalist());
            var stilted = new Genome(Genes);
            stilted.Mutate(Symbol.For("gene.elevation_bias"), 0.95, 0, Symbol.None, RecordId.None, new Annalist());

            MaterialStock yard = Yard("oak", "granite", "thatch");
            Silhouette plain = House(new Genome(Genes), yard, null, 1);
            Silhouette high = House(tall, yard, null, 1);
            Silhouette up = House(stilted, yard, null, 1);

            Assert.True(high.Height > plain.Height);
            Assert.Equal(0.0, plain.RaisedFloor);
            Assert.True(up.RaisedFloor > 4);
            Assert.InRange(plain.Openness, 0.0, 1.0);

            double shares = 0.0;
            for (int k = 0; k < 4; k++) shares += plain.ShareOf(k);
            Assert.InRange(shares, 0.99, 1.01);
        }
    }
}
