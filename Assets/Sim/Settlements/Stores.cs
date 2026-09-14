using Godless.Sim.Annals;
using Godless.Sim.Build;
using Godless.Sim.Content;
using Godless.Sim.Core;
using Godless.Sim.Harness;

namespace Godless.Sim.Settlements
{
    /// <summary>How food keeps, as content declares it (`food/*.json`). S2H.</summary>
    public sealed class FoodRules
    {
        public string Tell { get; private set; }

        /// <summary>Meals a person's share of the open larder holds before the rest starts to rot.</summary>
        public double OpenMealsPerPerson { get; private set; }

        /// <summary>Share of the food above that allowance lost a day, with nowhere to keep it.</summary>
        public double OpenSpoilPerDay { get; private set; }

        /// <summary>Share of the food kept in a store lost a day.</summary>
        public double StoreSpoilPerDay { get; private set; }

        /// <summary>Share of a heap of harvest lying where it was cut lost a day (S2I), until it is carried in.</summary>
        public double PileSpoilPerDay { get; private set; }

        /// <summary>The first "food" document, or null: a world without one keeps food for ever, as before S2H.</summary>
        public static FoodRules FromContent(ContentDatabase content)
        {
            foreach (string id in content.Ids("food"))
            {
                JsonValue doc = content.Get("food", id);
                return new FoodRules
                {
                    Tell = doc["tell"].AsString(""),
                    OpenMealsPerPerson = doc["openMealsPerPerson"].AsDouble(50.0),
                    OpenSpoilPerDay = doc["openSpoilPerDay"].AsDouble(0.03),
                    StoreSpoilPerDay = doc["storeSpoilPerDay"].AsDouble(0.002),
                    PileSpoilPerDay = doc["pileSpoilPerDay"].AsDouble(0.05),
                };
            }
            return null;
        }
    }

    /// <summary>
    /// Food stores and spoilage. S2H.
    ///
    /// A settlement living off the land eats what it forages about as fast as
    /// it comes in, and a larder by the fire is enough. A harvest is not like
    /// that: it arrives all at once, more than anyone can eat before it rots.
    /// Food above what the open larder holds spoils a little every day, and the
    /// rotting is what presses for a store — at the place it rots, so a
    /// village's granary goes up by the fire and a big farm's barn by its
    /// fields. Food in a store keeps.
    ///
    /// The tell: a stranger sees heaps of harvest going grey where they were
    /// left, and then a windowless building on posts that people carry sacks
    /// into and walk to when they are hungry.
    /// </summary>
    public static class Stores
    {
        public static readonly Symbol SpoiledKind = Symbol.For("food.spoiled");

        /// <summary>What presses for a store: food that rotted for want of one.</summary>
        public const string Spoilage = "spoilage";

        /// <summary>Meals the standing stores keep.</summary>
        public static double Capacity(Settlement s)
        {
            double meals = 0.0;
            foreach (Project p in s.Projects)
                if (p.IsStore && p.Complete && !p.Destroyed) meals += p.Intent.Kind.StoresMeals;
            return meals;
        }

        /// <summary>The standing store nearest a column, or null.</summary>
        public static Project Nearest(Settlement s, int x, int z)
        {
            Project best = null;
            long bestD = long.MaxValue;
            foreach (Project p in s.Projects)
            {
                if (!p.IsStore || !p.Complete || p.Destroyed) continue;
                Int3 c = Construction.World(p, p.Plan.Width / 2, 0, p.Plan.Depth / 2);
                long dx = c.X - x, dz = c.Z - z, d = dx * dx + dz * dz;
                if (d < bestD) { bestD = d; best = p; }
            }
            return best;
        }

        /// <summary>A place by a store's door for the i-th person, so a queue for food is not one heap of people.</summary>
        public static Int3 Door(Project store, int index)
        {
            int side = store.DoorSide >= 0 ? store.DoorSide : 0;
            int dx, dz;
            Blueprint.SideStep(side, out dx, out dz);
            int w = store.Plan.Width, d = store.Plan.Depth;
            int ox = w / 2 + dx * (w / 2 + 1), oz = d / 2 + dz * (d / 2 + 1);
            int spread = (index % 5) - 2;
            if (dx == 0) ox += spread; else oz += spread;
            return Construction.World(store, ox, 0, oz);
        }

        /// <summary>
        /// A day of keeping food: what is in a store spoils slowly, what the
        /// open larder cannot hold spoils fast, and the loss presses for a store
        /// where it happened. Returns the meals lost.
        /// </summary>
        public static double Spoil(Settlement s, FoodRules rules, long tick, Annalist annals)
        {
            if (rules == null || s.Food <= 0.0) return 0.0;
            double capacity = Capacity(s);
            double kept = s.Food < capacity ? s.Food : capacity;
            double open = s.Food - kept;
            double allowance = s.People.Count * rules.OpenMealsPerPerson;
            double over = open > allowance ? open - allowance : 0.0;

            double lost = kept * rules.StoreSpoilPerDay + over * rules.OpenSpoilPerDay;
            if (lost <= 0.0) return 0.0;
            s.Food -= lost;
            if (s.Food < 0.0) s.Food = 0.0;

            // Only what rotted in the open says a store is wanted.
            double pressing = over * rules.OpenSpoilPerDay;
            if (pressing > 0.0) Press(s, s.HearthParcelX, s.HearthParcelZ, pressing, tick, annals);
            return lost;
        }

        /// <summary>
        /// Presses for a store with food lost at a parcel. The record of the
        /// rotting is written once a week at most per settlement, and carried by
        /// every unit of pressure until the next (L3).
        /// </summary>
        public static void Press(Settlement s, int px, int pz, double meals, long tick, Annalist annals)
        {
            if (s.Intents == null || meals <= 0.0) return;
            const long Week = 7 * SimClock.DefaultTicksPerDay;
            if (!s.SpoiledRecord.Exists || tick - s.SpoiledTick >= Week)
            {
                s.SpoiledRecord = annals.Write(tick, SpoiledKind, s.Id,
                                               new Int3(px * World.ParcelGrid.Size, s.Hearth.Y, pz * World.ParcelGrid.Size),
                                               s.Founded, (long)(meals * 1000.0), s.People.Count);
                s.SpoiledTick = tick;
            }
            s.Intents.Press(Spoilage, px, pz, meals, s.SpoiledRecord);
        }
    }

    /// <summary>Food rots once a day, at dawn, before anyone eats. S2H.</summary>
    public sealed class StoreSystem : ISimSystem
    {
        public static readonly Symbol SystemId = Symbol.For("system.stores");
        readonly FoodRules _rules;

        public StoreSystem(FoodRules rules) { _rules = rules; }

        public Symbol Id { get { return SystemId; } }

        public void Tick(SimWorld world)
        {
            if (!world.Clock.IsFirstTickOfDay || _rules == null) return;
            foreach (Settlement s in world.Settlements)
                s.FoodSpoiled += Stores.Spoil(s, _rules, world.Clock.Tick, world.Annals);
        }
    }
}
