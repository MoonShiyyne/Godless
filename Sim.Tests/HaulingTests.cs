using System.IO;
using Godless.Sim.Annals;
using Godless.Sim.Build;
using Godless.Sim.Content;
using Godless.Sim.Harness;
using Godless.Sim.Settlements;
using Godless.Sim.World;
using Xunit;

namespace Godless.Sim.Tests
{
    /// <summary>
    /// Hauling (S2X).
    ///
    /// The tell: stacks of logs by the stumps, and people walking between
    /// them and the yard with their arms full.
    /// </summary>
    public class HaulingTests
    {
        readonly Xunit.Abstractions.ITestOutputHelper _out;
        public HaulingTests(Xunit.Abstractions.ITestOutputHelper output) { _out = output; }

        static ContentDatabase Shipped()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Assets", "Content"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return ContentLoader.Load(new DirectoryContentSource(Path.Combine(dir.FullName, "Assets", "Content"))).Database;
        }

        static readonly ContentDatabase Content = Shipped();

        static SimWorld Settled(ulong seed)
        {
            WorldChoice choice = WorldChoice.Pick(Content, "green-shore");
            return SettlementInvariants.Settled(Content, choice, 20, TestIslands.Generate)(seed);
        }

        [Fact]
        public void WhatIsCutLiesByTheStumpUntilSomeoneCarriesItToTheYard()
        {
            SimWorld world = Settled(7);
            Settlement s = world.Settlements[0];
            HaulRules rules = HaulRules.FromContent(Content);
            Assert.NotNull(rules);
            Voxels.DetailModelTable models = Voxels.DetailModelTable.FromContent(Content);
            foreach (string m in rules.FoodModels) Assert.NotNull(models.Find(m));
            foreach (string m in rules.MaterialModels) Assert.NotNull(models.Find(m));

            int heapsSeen = 0, carriers = 0, drawn = 0;
            bool carriedToYard = false;
            for (int day = 0; day < 60; day++)
                for (int t = 0; t < world.Clock.TicksPerDay; t++)
                {
                    world.Tick();
                    foreach (Pile p in s.Piles)
                    {
                        Assert.True(p.Amount >= 0.0, "a heap holds " + p.Amount);
                        Assert.True(p.Cause.Exists, "a heap nobody's work made");
                        if (!p.IsFood)
                        {
                            heapsSeen++;
                            // By a stump or a rock: well away from the fire.
                            Assert.True(System.Math.Abs(p.X - s.Hearth.X) + System.Math.Abs(p.Z - s.Hearth.Z) > 2);
                        }
                        if (p.Detail >= 0) { drawn++; Assert.NotNull(world.Details.Get(p.Detail)); }
                    }
                    foreach (Drives.Agent a in s.People)
                    {
                        if (!a.Doing.StartsWith("carrying")) continue;
                        carriers++;
                        if (a.Doing.Contains("to the yard")) carriedToYard = true;
                        Assert.Equal("carry", a.Pose);
                    }
                }
            _out.WriteLine(heapsSeen + " heap-ticks, " + drawn + " drawn, " + carriers + " carrier-ticks, " + s.Piles.Count + " heaps now");
            Assert.True(heapsSeen > 0, "nothing cut was left in a heap");
            Assert.True(drawn > 0, "no heap was drawn");
            Assert.True(carriedToYard, "nobody carried anything to the yard");

            // Everything carried in is in the yard: what was felled and is not
            // lying in heaps is in stock or already in a wall.
            int oak = s.Stock.Materials.IndexOf(Core.Symbol.For("voxel.oak"));
            if (oak >= 0) Assert.True(s.Stock.Of(oak) + Hauling.Piled(s, oak) > 0);
        }

        [Fact]
        public void FoodLeftInAHeapRotsAndPressesForAStoreWhereItLies()
        {
            SimWorld world = Settled(7);
            Settlement s = world.Settlements[0];
            FoodRules food = FoodRules.FromContent(Content);

            int x = s.Hearth.X + 30, z = s.Hearth.Z;
            RecordId cause = s.Founded;
            Pile heap = Hauling.Drop(s, x, z, -1, 600.0, cause);
            double spoiledBefore = s.FoodSpoiled;
            Hauling.Weather(s, food, world.Details, world.Clock.Tick, world.Annals);
            Assert.True(heap.Amount < 600.0);
            Assert.True(s.FoodSpoiled > spoiledBefore);
            Assert.NotEmpty(world.Annals.OfKind(Stores.SpoiledKind));

            // With no store and the larder by the fire full, it has nowhere to go:
            // it stays in the field (and goes on rotting there).
            HaulRules rules = HaulRules.FromContent(Content);
            s.Food = s.People.Count * rules.LarderMealsPerPerson * 2;
            double lying = heap.Amount;
            for (int t = 0; t < world.Clock.TicksPerDay; t++) world.Tick();
            Assert.True(!Contains(s, heap) ? false : heap.Amount >= lying * 0.9, "a heap was carried to a full larder");

            // Room made, it is fetched in: food in hand grows, the heap shrinks.
            s.Food = 0.0;
            double foodBefore = s.Food, heapBefore = heap.Amount;
            for (int t = 0; t < 8 * world.Clock.TicksPerDay && s.Piles.Count > 0; t++) world.Tick();
            Assert.True(heap.Amount < heapBefore * 0.5 || !Contains(s, heap), "the heap of food was not fetched in: " + heap.Amount.ToString("0"));
            _out.WriteLine("food " + foodBefore.ToString("0") + " -> " + s.Food.ToString("0") + ", heap " + heapBefore.ToString("0") + " -> " + heap.Amount.ToString("0"));
        }

        static bool Contains(Settlement s, Pile p)
        {
            foreach (Pile q in s.Piles) if (q == p) return true;
            return false;
        }
    }
}
