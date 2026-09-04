using System.Collections.Generic;
using Godless.Sim.Annals;
using Godless.Sim.Core;
using Xunit;

namespace Godless.Sim.Tests
{
    public class AnnalsTests
    {
        static readonly Symbol Flood = Symbol.For("event.flood");
        static readonly Symbol Raid = Symbol.For("event.raid");
        static readonly Symbol Scarred = Symbol.For("scar.formed");
        static readonly Symbol Thickened = Symbol.For("gene.mutated");
        static readonly Symbol Longhouse = Symbol.For("structure.longhouse-2");
        static readonly Symbol Hearth = Symbol.For("settlement.hearth");

        [Fact]
        public void RecordsCarryCausePlaceAndTick()
        {
            var annals = new Annalist();
            RecordId flood = annals.Write(40, Flood, Symbol.None, new Int3(100, 12, 100), RecordId.None, 3);

            AnnalRecord r = annals.Get(flood);
            Assert.Equal(40, r.Tick);
            Assert.Equal(Flood, r.Kind);
            Assert.Equal(new Int3(100, 12, 100), r.Place);
            Assert.False(r.Cause.Exists);
            Assert.Equal(3, r.ValueA);
        }

        /// <summary>
        /// L3's payoff. A silhouette is explained by walking back from the
        /// thing you can see to the thing that caused it.
        /// </summary>
        [Fact]
        public void CausalChain_WalksBackToTheRoot()
        {
            var annals = new Annalist();
            RecordId raid = annals.Write(47, Raid, Symbol.None, new Int3(60, 10, 60), RecordId.None);
            RecordId scar = annals.Write(47, Scarred, Hearth, new Int3(60, 10, 60), raid);
            RecordId gene = annals.Write(52, Thickened, Hearth, Int3.Nowhere, scar, 4, +12);

            IReadOnlyList<AnnalRecord> chain = annals.CausalChain(gene);

            Assert.Equal(3, chain.Count);
            Assert.Equal(Thickened, chain[0].Kind);
            Assert.Equal(Scarred, chain[1].Kind);
            Assert.Equal(Raid, chain[2].Kind);   // the root, four years earlier
            Assert.Equal(47, chain[2].Tick);
        }

        [Fact]
        public void Consequences_ReadTheChainForward()
        {
            var annals = new Annalist();
            RecordId raid = annals.Write(47, Raid, Symbol.None, new Int3(60, 10, 60), RecordId.None);
            annals.Write(47, Scarred, Hearth, new Int3(60, 10, 60), raid);
            annals.Write(48, Scarred, Longhouse, new Int3(61, 10, 60), raid);

            Assert.Equal(2, annals.Consequences(raid).Count);
        }

        /// <summary>
        /// Part 24: "the question players actually ask is not what happened
        /// in year 200. It is what happened here."
        /// </summary>
        [Fact]
        public void AtPlace_FindsWhatHappenedHereAndExcludesWhatDidNot()
        {
            var annals = new Annalist();
            annals.Write(10, Flood, Symbol.None, new Int3(100, 8, 100), RecordId.None);
            annals.Write(11, Raid, Symbol.None, new Int3(108, 8, 104), RecordId.None);
            annals.Write(12, Flood, Symbol.None, new Int3(400, 8, 400), RecordId.None);

            IReadOnlyList<AnnalRecord> near = annals.AtPlace(new Int3(100, 8, 100), 20);
            Assert.Equal(2, near.Count);

            Assert.Single(annals.AtPlace(new Int3(100, 8, 100), 5));
            Assert.Empty(annals.AtPlace(new Int3(250, 8, 250), 10));
        }

        [Fact]
        public void AtPlace_WorksAcrossBucketBoundariesAndNegativeCoordinates()
        {
            var annals = new Annalist();
            // Straddles the bucket edge at x = 0 and at x = 16.
            annals.Write(1, Flood, Symbol.None, new Int3(-1, 0, -1), RecordId.None);
            annals.Write(2, Flood, Symbol.None, new Int3(1, 0, 1), RecordId.None);
            annals.Write(3, Flood, Symbol.None, new Int3(15, 0, 15), RecordId.None);
            annals.Write(4, Flood, Symbol.None, new Int3(17, 0, 17), RecordId.None);

            Assert.Equal(4, annals.AtPlace(Int3.Zero, 40).Count);
            Assert.Equal(2, annals.AtPlace(Int3.Zero, 2).Count);
        }

        [Fact]
        public void About_FindsWhatHappenedToAStructure()
        {
            var annals = new Annalist();
            annals.Write(41, Symbol.For("structure.raised"), Longhouse, new Int3(60, 10, 60), RecordId.None);
            annals.Write(47, Thickened, Longhouse, Int3.Nowhere, RecordId.None);
            annals.Write(47, Thickened, Hearth, Int3.Nowhere, RecordId.None);

            Assert.Equal(2, annals.About(Longhouse).Count);
            Assert.Empty(annals.About(Symbol.For("structure.never-built")));
        }

        [Fact]
        public void RecordsWithoutAPlaceAreNotFoundByPlace()
        {
            var annals = new Annalist();
            annals.Write(1, Thickened, Hearth, Int3.Nowhere, RecordId.None);
            Assert.Empty(annals.AtPlace(Int3.Zero, 1000));
            Assert.Single(annals.About(Hearth));
        }

        [Fact]
        public void ARecordCannotBeCausedByOneThatDoesNotExistYet()
        {
            var annals = new Annalist();
            Assert.Throws<System.ArgumentException>(() =>
                annals.Write(1, Flood, Symbol.None, Int3.Nowhere, new RecordId(7)));
        }

        [Fact]
        public void RecordsAreWrittenInTickOrder()
        {
            var annals = new Annalist();
            annals.Write(10, Flood, RecordId.None);
            Assert.Throws<System.ArgumentException>(() => annals.Write(9, Flood, RecordId.None));
        }

        [Fact]
        public void ARecordMustHaveAKind()
        {
            var annals = new Annalist();
            Assert.Throws<System.ArgumentException>(() =>
                annals.Write(1, Symbol.None, Symbol.None, Int3.Nowhere, RecordId.None));
        }

        /// <summary>S05's tell, and the concrete form of "byte-identical annals".</summary>
        [Fact]
        public void Digest_IsIdenticalForIdenticalHistories()
        {
            Assert.Equal(BuildHistory().Digest(), BuildHistory().Digest());

            Annalist divergent = BuildHistory();
            divergent.Write(99, Flood, Symbol.None, new Int3(1, 1, 1), RecordId.None);
            Assert.NotEqual(BuildHistory().Digest(), divergent.Digest());
        }

        static Annalist BuildHistory()
        {
            var annals = new Annalist();
            RecordId raid = annals.Write(47, Raid, Symbol.None, new Int3(60, 10, 60), RecordId.None, 15);
            RecordId scar = annals.Write(47, Scarred, Hearth, new Int3(60, 10, 60), raid, 200);
            annals.Write(52, Thickened, Hearth, Int3.Nowhere, scar, 4, +12);
            return annals;
        }

        [Fact]
        public void SymbolsAreStableAcrossConstructionAndCarryTheirName()
        {
            Assert.Equal(Symbol.For("event.flood"), Symbol.For("event.flood"));
            Assert.NotEqual(Symbol.For("event.flood"), Symbol.For("event.fire"));
            Assert.Equal("event.flood", Symbol.For("event.flood").ToString());

            // Identity survives a save round trip through the raw hash.
            Symbol restored = Symbol.FromHash(Symbol.For("event.flood").Hash);
            Assert.Equal(Symbol.For("event.flood"), restored);
        }
    }
}
