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
    /// <summary>
    /// Farms (S2I).
    ///
    /// The tell: squares of brown furrows away from the houses turning green,
    /// then gold, then stubble, with the same few people bent over them.
    /// </summary>
    public class FarmsTests
    {
        readonly Xunit.Abstractions.ITestOutputHelper _out;
        public FarmsTests(Xunit.Abstractions.ITestOutputHelper output) { _out = output; }

        static ContentDatabase Shipped()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Assets", "Content"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return ContentLoader.Load(new DirectoryContentSource(Path.Combine(dir.FullName, "Assets", "Content"))).Database;
        }

        static readonly ContentDatabase Content = Shipped();
        static readonly GeneTable Genes = GeneTable.FromContent(Content);

        sealed class Facts : IExprScope
        {
            readonly Dictionary<string, double> _v;
            public Facts(Dictionary<string, double> v) { _v = v; }
            public double Resolve(string name) { double d; return _v.TryGetValue(name, out d) ? d : 0.0; }
        }

        static Facts Ground(double elevation, double water, double damp, double flood, double snow, double sun)
        {
            return new Facts(new Dictionary<string, double>
            {
                { "parcel.elevation", elevation }, { "parcel.water", water }, { "field.damp", damp },
                { "field.flood", flood }, { "field.snow", snow }, { "field.sun", sun },
            });
        }

        static Crop Best(CropTable crops, IExprScope ground)
        {
            Crop best = null;
            double value = 0.0;
            foreach (Crop c in crops.All)
            {
                double v = c.MealsPerDay(c.Suitability(ground));
                if (v > value) { value = v; best = c; }
            }
            return best;
        }

        [Fact]
        public void CropsFarmingAndTheFarmIntentLoadClean()
        {
            CropTable crops = CropTable.FromContent(Content, Genes);
            Assert.Empty(crops.Problems);
            Assert.True(crops.Count >= 4);
            DetailModelTable models = DetailModelTable.FromContent(Content);
            foreach (Crop c in crops.All)
                foreach (string m in new[] { c.SownModel, c.GrowingModel, c.RipeModel, c.StubbleModel })
                    Assert.True(models.Find(m) != null, c.Name + " draws with '" + m + "', which is not a model");
            Assert.NotNull(FarmRules.FromContent(Content));

            IntentKindTable kinds = IntentKindTable.FromContent(Content, DriveRules.FromContent(Content).Needs);
            Assert.Empty(kinds.Problems);
            IntentKind farm = null;
            foreach (IntentKind k in kinds.All) if (k.Purpose == IntentPurpose.Farm) farm = k;
            Assert.NotNull(farm);
            Assert.Equal(DriveRules.FromContent(Content).Needs.IndexOf("hunger"), farm.Answers);
            Assert.Equal(Farms.Shortage, farm.PressedBy);
        }

        /// <summary>Each crop has ground it is the best choice for, and its weakness is ground it is not.</summary>
        [Fact]
        public void EachCropIsTheBestChoiceOnItsOwnGround()
        {
            CropTable crops = CropTable.FromContent(Content, Genes);
            Assert.Equal("taro", Best(crops, Ground(elevation: 2, water: 0.5, damp: 0.9, flood: 0.8, snow: 0, sun: 0.5)).Name);
            Assert.Equal("barley", Best(crops, Ground(elevation: 30, water: 3, damp: 0.4, flood: 0, snow: 0.8, sun: 0.3)).Name);
            Assert.Equal("millet", Best(crops, Ground(elevation: 8, water: 12, damp: 0.2, flood: 0, snow: 0.05, sun: 0.9)).Name);
            Assert.Equal("wheat", Best(crops, Ground(elevation: 10, water: 3, damp: 0.5, flood: 0, snow: 0, sun: 0.6)).Name);

            // And the weaknesses: taro high and dry, wheat in the snow, millet in the wet.
            Crop taro = crops.Find("taro"), wheat = crops.Find("wheat"), millet = crops.Find("millet");
            Assert.Equal(0.0, taro.Suitability(Ground(30, 10, 0.3, 0, 0, 0.8)));
            Assert.True(wheat.Suitability(Ground(10, 3, 0.5, 0, 0.9, 0.6)) < 0.15);
            Assert.True(millet.Suitability(Ground(2, 1, 0.95, 0.9, 0, 0.5)) < 0.1);
        }

        static SimWorld Settled(ulong seed, out Settlement s)
        {
            WorldChoice choice = WorldChoice.Pick(Content, "green-shore");
            SimWorld world = SettlementInvariants.Settled(Content, choice, 20, TestIslands.Generate)(seed);
            s = world.Settlements[0];
            return world;
        }

        static void Survey(SimWorld world, out ParcelGrid grid, out ConstraintFields fields)
        {
            WorldChoice choice = WorldChoice.Pick(Content, "green-shore");
            grid = Founding.Survey(world, Content, choice.Biomes, out fields);
        }

        /// <summary>A farm is laid clear of every house, claimed, cleared, tilled, and on record.</summary>
        [Fact]
        public void AFarmIsLaidAwayFromTheHousesOnTilledGround()
        {
            Settlement s;
            SimWorld world = Settled(7, out s);
            for (int day = 0; day < 150; day++)
                for (int t = 0; t < world.Clock.TicksPerDay; t++) world.Tick();
            ParcelGrid grid;
            ConstraintFields fields;
            Survey(world, out grid, out fields);
            FarmRules rules = FarmRules.FromContent(Content);

            Farm farm = Farms.Lay(s, s.Founded, Symbol.For("test.farm"), rules, CropTable.FromContent(Content, Genes), grid, fields, world);
            Assert.NotNull(farm);
            _out.WriteLine(farm.Crop.Name + ", " + farm.Plots.Count + " plots");
            Assert.True(farm.Plots.Count >= rules.FewestPlots);
            Assert.Equal(Farms.LaidKind, world.Annals.Get(farm.Record).Kind);

            ushort tilled;
            Assert.True(world.VoxelTypes.TryGetId(Symbol.For(rules.TilledVoxel), out tilled));
            foreach (Plot p in farm.Plots)
            {
                Assert.Equal(farm.Record, s.ClaimOn(p.ParcelX, p.ParcelZ));
                Assert.True(p.Suitability >= rules.MinSuitability);
                for (int dz = -rules.AwayFromHouses; dz <= rules.AwayFromHouses; dz++)
                    for (int dx = -rules.AwayFromHouses; dx <= rules.AwayFromHouses; dx++)
                    {
                        RecordId owner = s.ClaimOn(p.ParcelX + dx, p.ParcelZ + dz);
                        Assert.True(!owner.Exists || Farms.Owning(s, p.ParcelX + dx, p.ParcelZ + dz) != null,
                                    "a plot at " + p.ParcelX + "," + p.ParcelZ + " is within " + rules.AwayFromHouses + " parcels of a house");
                    }

                int x = p.CentreX, z = p.CentreZ, y = grid.GroundAt(x, z);
                if (!grid.IsWetColumn(x, z)) Assert.Equal(tilled, world.Voxels.Get(x, y, z));
            }
        }

        /// <summary>A plot's round: sown by someone's work, growing a day at a time, ripe, cut into a heap, resting, fallow again.</summary>
        [Fact]
        public void APlotIsSownGrowsRipensAndIsCutIntoAHeap()
        {
            Settlement s;
            SimWorld world = Settled(7, out s);
            for (int t = 0; t < world.Clock.TicksPerDay; t++) world.Tick();   // history begins after founding
            ParcelGrid grid;
            ConstraintFields fields;
            Survey(world, out grid, out fields);
            FarmRules rules = FarmRules.FromContent(Content);
            DetailModelTable models = DetailModelTable.FromContent(Content);
            Farm farm = Farms.Lay(s, s.Founded, Symbol.For("test.farm"), rules, CropTable.FromContent(Content, Genes), grid, fields, world);
            Assert.NotNull(farm);
            Plot plot = farm.Plots[0];
            Agent hand = s.People[0];
            long tick = world.Clock.Tick;

            Assert.True(Farms.Wanted(s) > 0.0);
            while (plot.State == PlotState.Fallow) Assert.True(Farms.Work(s, hand, tick++, world.Annals));
            Assert.Equal(PlotState.Growing, plot.State);
            Assert.Equal(Farms.SownKind, world.Annals.Get(plot.Last).Kind);
            Assert.StartsWith("sowing", hand.FieldWork);

            // The rest of the farm sown the same day, so every plot ripens together.
            while (Farms.Wanted(s) > 0.0) Assert.True(Farms.Work(s, hand, tick++, world.Annals));
            // Harvest starts from the nearest ripe plot; this one is where the hand last worked.
            Farms.Show(s, world.Details, models, grid, tick);
            Assert.Equal(farm.Crop.SownModel, plot.Shown);
            Assert.NotEmpty(plot.Details);
            foreach (int id in plot.Details) Assert.NotNull(world.Details.Get(id));

            for (int d = 0; d < farm.Crop.GrowDays; d++) Farms.Day(s, rules);
            Assert.Equal(PlotState.Ripe, plot.State);
            Farms.Show(s, world.Details, models, grid, tick);
            Assert.Equal(farm.Crop.RipeModel, plot.Shown);

            double before = Hauling.Piled(s, -1);
            while (plot.State == PlotState.Ripe) Assert.True(Farms.Work(s, hand, tick++, world.Annals));
            Assert.Equal(PlotState.Stubble, plot.State);
            foreach (Plot other in farm.Plots) Assert.NotEqual(PlotState.Fallow, other.State);
            Assert.Equal(Farms.HarvestedKind, world.Annals.Get(plot.Last).Kind);
            double cut = Hauling.Piled(s, -1) - before;
            Assert.True(cut > farm.Crop.MealsPerPlot * 0.5, "the harvest heaped " + cut.ToString("0") + " meals");
            Assert.True(plot.Fertility < 1.0 || farm.Crop.SoilUse == 0.0, "harvest took nothing out of the soil");

            for (int d = 0; d < farm.Crop.RestDays; d++) Farms.Day(s, rules);
            Assert.Equal(PlotState.Fallow, plot.State);
        }

        /// <summary>A farm grows only when food is short, hands are free and ground is free — and not while its harvest rots.</summary>
        [Fact]
        public void AFarmGrowsWhenShortWithHandsAndGroundButNotWhileItsHarvestRots()
        {
            Settlement s;
            SimWorld world = Settled(7, out s);
            for (int t = 0; t < world.Clock.TicksPerDay; t++) world.Tick();   // history begins after founding
            ParcelGrid grid;
            ConstraintFields fields;
            Survey(world, out grid, out fields);
            FarmRules rules = FarmRules.FromContent(Content);
            Farm farm = Farms.Lay(s, s.Founded, Symbol.For("test.farm"), rules, CropTable.FromContent(Content, Genes), grid, fields, world);
            Assert.NotNull(farm);
            int plots = farm.Plots.Count;

            // Plenty in hand, and the land's wild food alone is counted as well: fed.
            s.Food = Subsistence.Wanted(s) * 2;
            for (int i = 0; i < 40; i++) s.Add(world.Streams);   // enough mouths that the land alone is short
            s.Food = Subsistence.Wanted(s) * 2;
            Farms.Consider(s, farm, rules, grid, fields, world, RecordId.None);
            _out.WriteLine("plenty: " + farm.LastGrowth);

            // Short, with hands, with ground: it grows, on record.
            s.Food = 0.0;
            RecordId grew = Farms.Consider(s, farm, rules, grid, fields, world, RecordId.None);
            _out.WriteLine("short: " + farm.LastGrowth);
            Assert.True(grew.Exists, "a farm short of food, with hands and ground, did not grow: " + farm.LastGrowth);
            Assert.True(farm.Plots.Count > plots);
            Assert.Equal(Farms.GrewKind, world.Annals.Get(grew).Kind);

            // Its harvest lying in the field with nowhere to put it: no more field.
            Hauling.Drop(s, farm.Plots[0].CentreX, farm.Plots[0].CentreZ, -1, 5000.0, farm.Plots[0].Last);
            Assert.False(Farms.Consider(s, farm, rules, grid, fields, world, RecordId.None).Exists);
            Assert.Equal(Growth.NoRoom, farm.LastGrowthResult);
        }
    }
}
