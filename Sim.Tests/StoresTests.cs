using System.IO;
using Godless.Sim.Build;
using Godless.Sim.Content;
using Godless.Sim.Culture;
using Godless.Sim.Harness;
using Godless.Sim.Settlements;
using Godless.Sim.World;
using Xunit;

namespace Godless.Sim.Tests
{
    /// <summary>
    /// Food stores and spoilage (S2H).
    ///
    /// The tell: a harvest left in the open goes grey where it lies, and a
    /// windowless building on posts goes up where the food rotted.
    /// </summary>
    public class StoresTests
    {
        readonly Xunit.Abstractions.ITestOutputHelper _out;
        public StoresTests(Xunit.Abstractions.ITestOutputHelper output) { _out = output; }

        static ContentDatabase Shipped()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Assets", "Content"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return ContentLoader.Load(new DirectoryContentSource(Path.Combine(dir.FullName, "Assets", "Content"))).Database;
        }

        static readonly ContentDatabase Content = Shipped();

        [Fact]
        public void TheStoreKindAndItsBuildingLoadClean()
        {
            GeneTable genes = GeneTable.FromContent(Content);
            var needs = Drives.DriveRules.FromContent(Content).Needs;
            IntentKindTable kinds = IntentKindTable.FromContent(Content, needs);
            Assert.Empty(kinds.Problems);
            IntentKind store = null;
            foreach (IntentKind k in kinds.All) if (k.Purpose == IntentPurpose.Store) store = k;
            Assert.NotNull(store);
            Assert.Equal(Stores.Spoilage, store.PressedBy);
            Assert.True(store.StoresMeals > 0);

            GrammarTable grammars = GrammarTable.FromContent(Content, genes, kinds);
            Assert.Empty(grammars.Problems);
            Blueprint plan = grammars.For("store").Build(new Genome(genes), Palette.FromContent(Content), 80, 80, store.BudgetVoxels);
            Assert.True(plan.Volume > 0);
            Assert.Equal(0, plan.Capacity);   // nobody sleeps in a store
            Assert.NotNull(FoodRules.FromContent(Content));
        }

        [Fact]
        public void FoodThatCannotBeKeptRotsAndAStoreGoesUpWhereItRotted()
        {
            WorldChoice choice = WorldChoice.Pick(Content, "green-shore");
            SimWorld world = SettlementInvariants.Settled(Content, choice, 20, TestIslands.Generate)(7);
            Settlement s = world.Settlements[0];
            FoodRules rules = FoodRules.FromContent(Content);

            // A harvest's worth more than the open larder holds.
            s.Food = s.People.Count * rules.OpenMealsPerPerson * 4;
            double before = s.Food;
            for (int t = 0; t < world.Clock.TicksPerDay; t++) world.Tick();
            Assert.True(s.FoodSpoiled > 0.0, "nothing rotted");
            Assert.NotEmpty(world.Annals.OfKind(Stores.SpoiledKind));

            Project store = null;
            for (int day = 0; day < 300 && (store == null || !store.Complete); day++)
            {
                if (s.Food < s.People.Count * rules.OpenMealsPerPerson * 3) s.Food = s.People.Count * rules.OpenMealsPerPerson * 4;
                for (int t = 0; t < world.Clock.TicksPerDay; t++) world.Tick();
                foreach (Project p in s.Projects) if (p.IsStore) store = p;
            }
            Assert.True(store != null, "no store was commissioned for food rotting in the open");
            Assert.True(store.Complete, "the store was not finished in 300 days");
            Assert.False(store.IsHome);
            Assert.Empty(store.Beds);
            Assert.Equal(store.Intent.Kind.StoresMeals, (int)Stores.Capacity(s));
            _out.WriteLine("store " + store.Site.Record + " at " + store.Site.ParcelX + "," + store.Site.ParcelZ + ": " + string.Join("; ", store.Reasons));

            // The chain back: the store's intent was pressed by the rotting.
            bool causedByRot = false;
            foreach (Annals.RecordId c in store.Intent.Causes)
                if (world.Annals.Get(c).Kind == Stores.SpoiledKind) causedByRot = true;
            Assert.True(causedByRot, "the store's intent does not cite the food that rotted");

            // Kept in the store, the same food loses far less.
            double capacity = Stores.Capacity(s);
            s.Food = capacity;
            double kept = Stores.Spoil(s, rules, world.Clock.Tick, world.Annals);
            s.Food = capacity + s.People.Count * rules.OpenMealsPerPerson + capacity;
            double open = Stores.Spoil(s, rules, world.Clock.Tick, world.Annals) - kept;
            Assert.True(kept * 5 < open, kept.ToString("0.0") + " lost in the store against " + open.ToString("0.0") + " in the open");
        }
    }
}
