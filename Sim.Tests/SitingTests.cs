using System.Collections.Generic;
using System.IO;
using Godless.Sim.Annals;
using Godless.Sim.Build;
using Godless.Sim.Content;
using Godless.Sim.Core;
using Godless.Sim.Culture;
using Godless.Sim.Drives;
using Godless.Sim.Harness;
using Godless.Sim.Settlements;
using Godless.Sim.Voxels;
using Godless.Sim.World;
using Xunit;

namespace Godless.Sim.Tests
{
    public class SitingTests
    {
        static ContentDatabase Load()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Assets", "Content"))) dir = dir.Parent;
            return ContentLoader.Load(new DirectoryContentSource(Path.Combine(dir.FullName, "Assets", "Content"))).Database;
        }

        static readonly ContentDatabase Content = Load();
        static readonly GeneTable Genes = GeneTable.FromContent(Content);

        /// <summary>One island, one settlement on it, ready to be asked for a house.</summary>
        sealed class Place
        {
            public SimWorld World;
            public Settlement Town;
            public ParcelGrid Grid;
            public ConstraintFields Fields;
            public SiteSystem Siting;
            public IntentBus Bus;
            public DriveRules Rules;

            public Place(double elevationBias)
            {
                BiomeTable biomes = BiomeTable.FromContent(Content);
                VoxelTypes types = VoxelTypes.FromContent(Content);
                World = new SimWorld(7, Content, types);
                World.Island = IslandGenerator.Generate(World.Voxels.Store, World.Streams, biomes, types);

                bool[] solid = TerrainBrush.SolidTable(Content, types);
                var wet = new bool[types.Count];
                wet[types.IdOf(Symbol.For("voxel.water"))] = true;
                Grid = ParcelGrid.Build(World.Voxels.Store, solid, wet);
                Fields = ConstraintFields.Compute(World.Island, Grid, biomes);

                Rules = DriveRules.FromContent(Content);
                MaterialTable materials = MaterialTable.FromContent(Content, biomes);
                var hearth = new Int3(292, Grid.GroundAt(292, 124) + 1, 124);
                Town = Settlement.Found("test", hearth, biomes.At(World.Island.BiomeAt(292, 124)), 20, Rules, 0, World.Annals, RecordId.None);
                Town.Stock = new MaterialStock(materials);
                for (int m = 0; m < materials.Count; m++) Town.Stock.Add(m, 5000);
                Town.Catchment = Catchment.Survey(World.Island, biomes, materials, 292, 124);
                Town.Genome = new Genome(Genes);
                Town.Genome.Mutate(Symbol.For("gene.elevation_bias"), elevationBias, 0, Town.Id, RecordId.None, World.Annals);

                IntentKindTable kinds = IntentKindTable.FromContent(Content, Rules.Needs);
                Bus = new IntentBus(kinds, Rules.Needs.Count);
                Town.AttachIntents(Bus);
                World.Settlements.Add(Town);

                Siting = new SiteSystem(GrammarTable.FromContent(Content, Genes),
                                        SitingTable.FromContent(Content, Genes, kinds),
                                        TileSet.FromContent(Content, materials), materials,
                                        Palette.FromContent(Content), Grid, Fields);
                World.Add(Siting);
            }

            /// <summary>Asks for a house, the way a wet fortnight would.</summary>
            public BuildIntent Ask()
            {
                int shelter = Rules.Needs.IndexOf("shelter");
                Bus.Add(shelter, Town.HearthParcelX, Town.HearthParcelZ, 500.0, Town.Founded);
                var raised = Bus.Evaluate(Town, World.Clock.Tick, World.Annals);
                return raised.Count > 0 ? raised[0] : null;
            }

            public Project Site(BuildIntent intent)
            {
                Project p = Siting.Plan(Town, intent, World, World.Streams.Get(SiteSystem.StreamId));
                if (p != null) Town.Projects.Add(p);
                return p;
            }
        }

        [Fact]
        public void TheShippedSitingRuleLoads()
        {
            DriveRules rules = DriveRules.FromContent(Content);
            SitingTable table = SitingTable.FromContent(Content, Genes, IntentKindTable.FromContent(Content, rules.Needs));
            Assert.Empty(table.Problems);
            Assert.NotNull(table.For("shelter"));
            Assert.False(string.IsNullOrWhiteSpace(table.For("shelter").Tell));
        }

        [Theory]
        [InlineData("{\"type\":\"siting\",\"id\":\"x\",\"for\":\"shelter\",\"score\":\"field.sun\"}", "no tell")]
        [InlineData("{\"type\":\"siting\",\"id\":\"x\",\"for\":\"shelter\",\"tell\":\"t\",\"score\":\"field.luck\"}", "'field.luck'")]
        [InlineData("{\"type\":\"siting\",\"id\":\"x\",\"for\":\"shelter\",\"tell\":\"t\",\"score\":\"gene.nope\"}", "'gene.nope'")]
        [InlineData("{\"type\":\"siting\",\"id\":\"x\",\"for\":\"shelter\",\"tell\":\"t\",\"allow\":\"1 +\"}", "broken 'allow'")]
        public void ASitingRuleThatCannotWorkIsRefused(string doc, string expected)
        {
            var src = new MemoryContentSource().Add("base", "mod.json", "{}").Add("base", "s.json", doc);
            SitingTable table = SitingTable.FromContent(ContentLoader.Load(src).Database, Genes);
            Assert.Equal(0, table.Count);
            Assert.Contains(expected, Assert.Single(table.Problems));
        }

        /// <summary>
        /// S15's tell at stratum 1: from the same ground, a culture that
        /// builds high takes the dry, exposed ridge and one that builds low
        /// takes the sheltered hollow.
        /// </summary>
        [Fact]
        public void TwoCulturesTakeDifferentGroundFromTheSameIsland()
        {
            var high = new Place(0.95);
            var low = new Place(0.05);
            Project hill = high.Site(high.Ask());
            Project hollow = low.Site(low.Ask());

            Assert.NotNull(hill);
            Assert.NotNull(hollow);
            Assert.True(hill.Site.ParcelX != hollow.Site.ParcelX || hill.Site.ParcelZ != hollow.Site.ParcelZ,
                        "both took the same parcel");

            double hillFlood = high.Fields.FloodRisk[hill.Site.ParcelX, hill.Site.ParcelZ];
            double hollowFlood = low.Fields.FloodRisk[hollow.Site.ParcelX, hollow.Site.ParcelZ];
            double hillExposure = high.Fields.Exposure[hill.Site.ParcelX, hill.Site.ParcelZ];
            double hollowExposure = low.Fields.Exposure[hollow.Site.ParcelX, hollow.Site.ParcelZ];

            Assert.True(hillFlood < hollowFlood, "flood risk " + hillFlood + " against " + hollowFlood);
            Assert.True(hillExposure > hollowExposure, "exposure " + hillExposure + " against " + hollowExposure);
        }

        [Fact]
        public void ASiteIsGroundTheBuildingFitsOnAndNobodyElseHas()
        {
            var place = new Place(0.5);
            var taken = new List<int>();
            for (int i = 0; i < 2; i++)
            {
                BuildIntent intent = place.Ask();
                Assert.NotNull(intent);
                Project p = place.Site(intent);
                Assert.NotNull(p);
                Assert.Equal(IntentStatus.Open, p.Intent.Status);

                for (int dz = 0; dz < p.Site.ParcelsDeep; dz++)
                    for (int dx = 0; dx < p.Site.ParcelsWide; dx++)
                    {
                        int px = p.Site.ParcelX + dx, pz = p.Site.ParcelZ + dz;
                        Assert.True(place.Grid.IsLand(px, pz), "sited on water");
                        Assert.Equal(0, place.Grid.WetColumns(px, pz));
                        int key = pz * ParcelGrid.Width + px;
                        Assert.DoesNotContain(key, taken);
                        taken.Add(key);
                    }

                // Its chain runs back through the intent to the founding.
                var chain = place.World.Annals.CausalChain(p.Site.Record);
                Assert.Equal(SiteScorer.ChosenKind, chain[0].Kind);
                Assert.Equal(IntentBus.RaisedKind, chain[1].Kind);
                Assert.Equal(Settlement.FoundedKind, chain[chain.Count - 1].Kind);
            }
        }

        [Fact]
        public void TheDaysWorkClaimsTheIntentItSites()
        {
            var place = new Place(0.5);
            place.Ask();
            place.World.RunYears(0);
            for (int t = 0; t < 4; t++) place.World.Tick();

            Project p = Assert.Single(place.Town.Projects);
            Assert.Equal(IntentStatus.Claimed, p.Intent.Status);
            Assert.True(p.Built.TotalVoxels > 0);
            Assert.Empty(p.Built.Missing);
        }

        [Fact]
        public void TheSameIslandAndCultureChooseTheSameGround()
        {
            var a = new Place(0.6);
            var b = new Place(0.6);
            Project pa = a.Site(a.Ask()), pb = b.Site(b.Ask());
            Assert.Equal(pa.Site.ParcelX, pb.Site.ParcelX);
            Assert.Equal(pa.Site.ParcelZ, pb.Site.ParcelZ);
            Assert.Equal(a.Town.Digest(), b.Town.Digest());
        }
    }
}
