using System.Globalization;
using System.IO;
using Godless.Sim.Annals;
using Godless.Sim.Content;
using Godless.Sim.Core;
using Godless.Sim.Culture;
using Xunit;

namespace Godless.Sim.Tests
{
    public class DeterministicDecimalTests
    {
        static double P(string s) { Assert.True(DeterministicDecimal.TryParse(s, out double v), s); return v; }

        [Fact]
        public void ParsesTheShapesContentUses()
        {
            Assert.Equal(0.45, P("0.45"));
            Assert.Equal(0.5, P("0.5"));
            Assert.Equal(1.0, P("1"));
            Assert.Equal(-3.25, P("-3.25"));
            Assert.Equal(1e-3, P("1e-3"));
            Assert.Equal(2.5e10, P("2.5E10"));
            Assert.Equal(0.0, P("0"));
            Assert.Equal(123456.789, P("123456.789"));
        }

        [Theory]
        [InlineData("")]
        [InlineData("-")]
        [InlineData("1.2.3")]
        [InlineData("1e")]
        [InlineData("abc")]
        public void RefusesWhatIsNotANumber(string s)
        {
            Assert.False(DeterministicDecimal.TryParse(s, out _));
        }

        /// <summary>
        /// CoreCLR's parser is correctly rounded, so on this runtime it is a
        /// valid oracle: for any decimal of up to fifteen significant digits
        /// with a modest exponent, one division must give the same bits. The
        /// point of the parser is that Mono gets those bits too.
        /// </summary>
        [Fact]
        public void AgreesBitForBitWithACorrectlyRoundedParser()
        {
            var rng = new RngStream(StableHash.OfString("test.decimals"));
            for (int i = 0; i < 20000; i++)
            {
                long digits = (long)(rng.NextUInt64() % 1000000000000000UL);
                int point = rng.NextInt(16);
                string text = digits.ToString(CultureInfo.InvariantCulture);
                if (point > 0 && point < text.Length) text = text.Insert(text.Length - point, ".");
                if (rng.NextBool()) text = "-" + text;
                if (rng.NextInt(4) == 0) text += "e" + (rng.NextInt(15) - 7).ToString(CultureInfo.InvariantCulture);

                double expected = double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
                Assert.Equal(System.BitConverter.DoubleToInt64Bits(expected),
                             System.BitConverter.DoubleToInt64Bits(P(text)));
            }
        }
    }

    public class GenomeTests
    {
        static ContentDatabase Shipped()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Assets", "Content"))) dir = dir.Parent;
            return ContentLoader.Load(new DirectoryContentSource(Path.Combine(dir.FullName, "Assets", "Content"))).Database;
        }

        static readonly Symbol RoofPitch = Symbol.For("gene.roof_pitch");
        static readonly Symbol Elevation = Symbol.For("gene.elevation_bias");
        static readonly Symbol Hearth = Symbol.For("settlement.hearth");

        [Fact]
        public void TheShippedGenesAllLoadAndEachNamesWhatAStrangerSees()
        {
            GeneTable table = GeneTable.FromContent(Shipped());

            Assert.Empty(table.Problems);
            Assert.Equal(6, table.Count);
            foreach (Gene g in table.All)
            {
                Assert.False(string.IsNullOrWhiteSpace(g.Tell), g.Name + " has no tell");
                Assert.InRange(g.Default, g.Min, g.Max);
            }
            for (int i = 1; i < table.Count; i++)
                Assert.True(table[i - 1].Id.CompareTo(table[i].Id) < 0, "genes must be in stable-hash order");
        }

        static ContentDatabase With(params string[] genes)
        {
            var src = new MemoryContentSource().Add("base", "mod.json", "{}");
            for (int i = 0; i < genes.Length; i++) src.Add("base", "g" + i + ".json", genes[i]);
            return ContentLoader.Load(src).Database;
        }

        /// <summary>
        /// The gene rule, enforced at load: a gene that cannot say what a
        /// stranger would see is not loaded, and the refusal names it.
        /// </summary>
        [Fact]
        public void AGeneWithNoTellIsRefusedByName()
        {
            GeneTable table = GeneTable.FromContent(With(
                "{\"type\":\"gene\",\"id\":\"mystery\",\"min\":0,\"max\":1,\"default\":0.5}",
                "{\"type\":\"gene\",\"id\":\"blank\",\"min\":0,\"max\":1,\"default\":0.5,\"tell\":\"   \"}",
                "{\"type\":\"gene\",\"id\":\"roof_pitch\",\"min\":0,\"max\":1,\"default\":0.5,\"tell\":\"Roofs steepen.\"}"));

            Assert.Equal(1, table.Count);
            Assert.Equal(2, table.Problems.Count);
            Assert.Contains(table.Problems, p => p.Contains("'mystery'"));
            Assert.Contains(table.Problems, p => p.Contains("'blank'"));
        }

        [Fact]
        public void ABrokenRangeIsRefused()
        {
            GeneTable table = GeneTable.FromContent(With(
                "{\"type\":\"gene\",\"id\":\"upside\",\"min\":1,\"max\":0,\"default\":0.5,\"tell\":\"x\"}",
                "{\"type\":\"gene\",\"id\":\"outside\",\"min\":0,\"max\":1,\"default\":2,\"tell\":\"x\"}"));
            Assert.Equal(0, table.Count);
            Assert.Equal(2, table.Problems.Count);
        }

        [Fact]
        public void AFoundingGenomeStartsAtTheDeclaredDefaults()
        {
            GeneTable table = GeneTable.FromContent(Shipped());
            var genome = new Genome(table);
            Assert.Equal(0.45, genome[RoofPitch]);
            Assert.Equal(0.15, genome[Elevation]);
            Assert.True(double.IsNaN(genome[Symbol.For("gene.never-declared")]));
        }

        /// <summary>
        /// L3 for genes: every mutation says what caused it, and the chain
        /// reads back from the gene to the event.
        /// </summary>
        [Fact]
        public void AMutationRecordsItsCauseAndItsBeforeAndAfter()
        {
            var annals = new Annalist();
            var genome = new Genome(GeneTable.FromContent(Shipped()));

            RecordId flood = annals.Write(40, Symbol.For("event.flood"), RecordId.None);
            RecordId mutation = genome.Mutate(Elevation, 0.6, 52, Hearth, flood, annals);

            Assert.Equal(0.6, genome[Elevation]);
            AnnalRecord r = annals.Get(mutation);
            Assert.Equal(Genome.MutatedKind, r.Kind);
            Assert.Equal(Hearth, r.Subject);
            Assert.Equal(Elevation, r.Participants[0]);
            Assert.Equal(150000, r.ValueA);   // 0.15 in millionths
            Assert.Equal(600000, r.ValueB);

            var chain = annals.CausalChain(mutation);
            Assert.Equal("event.flood", chain[chain.Count - 1].Kind.ToString());
        }

        [Fact]
        public void MutationIsClampedAndANoOpWritesNothing()
        {
            var annals = new Annalist();
            var genome = new Genome(GeneTable.FromContent(Shipped()));

            genome.Mutate(RoofPitch, 9.0, 1, Hearth, RecordId.None, annals);
            Assert.Equal(1.0, genome[RoofPitch]);

            int before = annals.Count;
            Assert.False(genome.Mutate(RoofPitch, 1.0, 2, Hearth, RecordId.None, annals).Exists);
            Assert.False(genome.Mutate(Symbol.For("gene.nope"), 0.3, 2, Hearth, RecordId.None, annals).Exists);
            Assert.Equal(before, annals.Count);
        }

        [Fact]
        public void DistanceIsZeroForTwinsAndGrowsWithDivergence()
        {
            var annals = new Annalist();
            GeneTable table = GeneTable.FromContent(Shipped());
            var a = new Genome(table);
            var b = a.Clone();
            Assert.Equal(0.0, a.DistanceTo(b));

            a.Mutate(RoofPitch, 0.0, 1, Hearth, RecordId.None, annals);
            b.Mutate(RoofPitch, 1.0, 1, Hearth, RecordId.None, annals);
            // One of six genes at opposite ends: sqrt(1/6).
            Assert.Equal(System.Math.Sqrt(1.0 / 6.0), a.DistanceTo(b), 12);
            Assert.Equal(a.DistanceTo(b), b.DistanceTo(a));
        }

        /// <summary>
        /// The reason genes are keyed by stable hash and not by position: a
        /// mod that adds a gene must not move any existing gene's value.
        /// </summary>
        [Fact]
        public void AModAddingAGeneLeavesEveryOtherGeneWhereItWas()
        {
            ContentDatabase baseContent = Shipped();
            var src = new MemoryContentSource().Add("base", "mod.json", "{}");
            foreach (string id in baseContent.Ids("gene"))
                src.Add("base", id + ".json", baseContent.Get("gene", id).ToString());
            src.Add("more", "mod.json", "{}")
               .Add("more", "g.json", "{\"type\":\"gene\",\"id\":\"aa_first\",\"min\":0,\"max\":1,\"default\":0.9,\"tell\":\"Everything is painted.\"}");

            var original = new Genome(GeneTable.FromContent(baseContent));
            var modded = new Genome(GeneTable.FromContent(ContentLoader.Load(src).Database));

            Assert.Equal(7, modded.Table.Count);
            foreach (Gene g in original.Table.All)
                Assert.Equal(original[g.Id], modded[g.Id]);
        }

        [Fact]
        public void TheSameGenomeDigestsTheSame()
        {
            GeneTable table = GeneTable.FromContent(Shipped());
            Assert.Equal(new Genome(table).Digest(), new Genome(table).Digest());
            var moved = new Genome(table);
            moved.Mutate(RoofPitch, 0.9, 1, Hearth, RecordId.None, new Annalist());
            Assert.NotEqual(new Genome(table).Digest(), moved.Digest());
        }
    }
}
