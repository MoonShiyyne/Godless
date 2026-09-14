using System.Collections.Generic;
using Godless.Sim.Annals;
using Godless.Sim.Build;
using Godless.Sim.Content;
using Godless.Sim.Core;
using Godless.Sim.Drives;
using Godless.Sim.Voxels;

namespace Godless.Sim.Settlements
{
    /// <summary>A heap of something where it was cut, dug or harvested, waiting to be carried in. S2X.</summary>
    public sealed class Pile
    {
        /// <summary>The column it lies on.</summary>
        public int X { get; internal set; }
        public int Z { get; internal set; }

        /// <summary>The material it is, or -1 for food.</summary>
        public int Material { get; internal set; }

        /// <summary>Voxels of material, or meals of food.</summary>
        public double Amount { get; internal set; }

        /// <summary>The work that heaped it (L3): a deposit worked, a plot harvested.</summary>
        public RecordId Cause { get; internal set; }

        public bool IsFood { get { return Material < 0; } }

        /// <summary>The detail instance that draws it, or -1 while it is too small to see.</summary>
        public int Detail { get; internal set; } = -1;

        // Which size of heap is drawn.
        internal int Shown;
    }

    /// <summary>How much a person carries and how long a load takes, as content declares it (`hauling/*.json`). S2X.</summary>
    public sealed class HaulRules
    {
        public string Tell { get; private set; }

        /// <summary>Voxels of material in one load.</summary>
        public double LoadVoxels { get; private set; }

        /// <summary>Meals of food in one load.</summary>
        public double LoadMeals { get; private set; }

        /// <summary>Part of a tick spent picking a load up and putting it down, on top of the walk.</summary>
        public double HandlingTicks { get; private set; }

        /// <summary>
        /// Meals of harvest a person's share of the larder by the fire takes in
        /// from the fields, with no store (S2H). A harvest is more than that:
        /// the rest waits in the field, and rots, until there is a store to put it in.
        /// </summary>
        public double LarderMealsPerPerson { get; private set; }

        /// <summary>Detail models for a heap, smallest first, and the amount (in loads) each is shown from.</summary>
        internal readonly List<string> FoodModelList = new List<string>();
        internal readonly List<string> MaterialModelList = new List<string>();
        internal readonly List<double> ShownFromLoads = new List<double>();
        public IReadOnlyList<string> FoodModels { get { return FoodModelList; } }
        public IReadOnlyList<string> MaterialModels { get { return MaterialModelList; } }

        public static HaulRules FromContent(ContentDatabase content)
        {
            foreach (string id in content.Ids("hauling"))
            {
                JsonValue doc = content.Get("hauling", id);
                var r = new HaulRules
                {
                    Tell = doc["tell"].AsString(""),
                    LoadVoxels = doc["loadVoxels"].AsDouble(4.0),
                    LoadMeals = doc["loadMeals"].AsDouble(12.0),
                    HandlingTicks = doc["handlingTicks"].AsDouble(0.05),
                    LarderMealsPerPerson = doc["larderMealsPerPerson"].AsDouble(double.PositiveInfinity),
                };
                JsonValue heaps = doc["heaps"];
                for (int i = 0; i < heaps.Count; i++)
                {
                    r.ShownFromLoads.Add(heaps[i]["fromLoads"].AsDouble(0.0));
                    r.FoodModelList.Add(heaps[i]["food"].AsString(""));
                    r.MaterialModelList.Add(heaps[i]["material"].AsString(""));
                }
                return r;
            }
            return null;
        }
    }

    /// <summary>
    /// Hauling. S2X.
    ///
    /// Cutting a tree does not put timber in the yard; it puts a stack of it
    /// by the stump. Harvest does not fill the store; it leaves sheaves in the
    /// field. Somebody has to carry it, and the task board finds somebody the
    /// way it finds everyone else: the heaps are the stimulus. A heap of food
    /// left too long rots where it lies (S2H).
    ///
    /// The tell: stacks of logs by the stumps and stones by the boulders, and
    /// people walking between them and the yard with their arms full.
    /// </summary>
    public static class Hauling
    {
        /// <summary>A heap smaller than this is picked up with the last load and gone.</summary>
        public const double Crumbs = 0.05;

        /// <summary>
        /// Adds to the heap of this at this column, or starts one. Food and
        /// materials never share a heap.
        /// </summary>
        public static Pile Drop(Settlement s, int x, int z, int material, double amount, RecordId cause)
        {
            if (amount <= 0.0) return null;
            foreach (Pile p in s.PileList)
                if (p.X == x && p.Z == z && p.Material == material)
                {
                    p.Amount += amount;
                    p.Cause = cause;
                    return p;
                }
            var pile = new Pile { X = x, Z = z, Material = material, Amount = amount, Cause = cause };
            s.PileList.Add(pile);
            return pile;
        }

        /// <summary>How much of a material (or food, -1) is lying in heaps.</summary>
        public static double Piled(Settlement s, int material)
        {
            double sum = 0.0;
            foreach (Pile p in s.PileList) if (p.Material == material) sum += p.Amount;
            return sum;
        }

        /// <summary>Loads of work lying in heaps that can be carried somewhere now: the hauling task's demand.</summary>
        public static double Loads(Settlement s, HaulRules rules)
        {
            if (rules == null) return 0.0;
            double loads = 0.0, food = 0.0;
            foreach (Pile p in s.PileList)
                if (p.IsFood) food += p.Amount; else loads += p.Amount / rules.LoadVoxels;
            double room = RoomForFood(s, rules);
            return loads + (food < room ? food : room) / rules.LoadMeals;
        }

        /// <summary>Meals of harvest there is somewhere to put: the stores' room and the larder's, less what is already kept.</summary>
        public static double RoomForFood(Settlement s, HaulRules rules)
        {
            if (rules == null) return 0.0;
            double room = Stores.Capacity(s) + s.People.Count * rules.LarderMealsPerPerson - s.Food;
            return room > 0.0 ? room : 0.0;
        }

        /// <summary>Where a heap goes: food to the nearest store (the fire until there is one), material to the yard.</summary>
        public static void Destination(Settlement s, Pile p, out int x, out int z, out string name)
        {
            if (p.IsFood)
            {
                Project store = Stores.Nearest(s, p.X, p.Z);
                if (store != null)
                {
                    Int3 door = Stores.Door(store, 0);
                    x = door.X; z = door.Z; name = "the store";
                    return;
                }
                Movement.AtFire(s, 8, out x, out z);
                name = "the fire";
                return;
            }
            Yard(s, p.Material, out x, out z);
            name = "the yard";
        }

        /// <summary>The spot in the yard a material is stacked: a little way off the fire, each material its own place.</summary>
        public static void Yard(Settlement s, int material, out int x, out int z)
        {
            int m = material < 0 ? 0 : material;
            x = s.Hearth.X - 6 - (m % 3) * 2;
            z = s.Hearth.Z + 6 + (m / 3 % 3) * 2;
        }

        /// <summary>
        /// A tick of carrying: the heap worth fetching most — biggest, nearest,
        /// food first while the store is low — as many loads as the walk and
        /// the handling allow in the person's share of the tick. False when
        /// there is nothing to carry.
        /// </summary>
        public static bool Carry(Settlement s, Agent agent, HaulRules rules, DetailLayer details, long tick)
        {
            if (rules == null || s.PileList.Count == 0) return false;
            bool hungry = s.Food < Subsistence.Wanted(s) * 0.5;
            double room = RoomForFood(s, rules);

            Pile best = null;
            double bestScore = double.NegativeInfinity;
            int bestIndex = -1;
            for (int i = 0; i < s.PileList.Count; i++)
            {
                Pile p = s.PileList[i];
                if (p.IsFood && room < 1.0) continue;   // nowhere to put it
                int dx, dz;
                string ignored;
                Destination(s, p, out dx, out dz, out ignored);
                double trip = Distance(p.X, p.Z, dx, dz) + Distance(agent.X, agent.Z, p.X, p.Z) * 0.5;
                double loads = p.Amount / (p.IsFood ? rules.LoadMeals : rules.LoadVoxels);
                double score = (loads > 8.0 ? 8.0 : loads) / (1.0 + trip / 32.0) * (p.IsFood && hungry ? 3.0 : 1.0);
                if (score > bestScore) { bestScore = score; best = p; bestIndex = i; }
            }
            if (best == null) return false;

            int tx, tz;
            string to;
            Destination(s, best, out tx, out tz, out to);
            double walk = Distance(best.X, best.Z, tx, tz);
            double perLoad = rules.HandlingTicks + 2.0 * walk / DriveSystem.VoxelsWalkedPerTick;
            double share = agent.LabourShare;
            int loadsNow = (int)(share / perLoad);
            if (loadsNow < 1) loadsNow = 1;

            double capacity = best.IsFood ? rules.LoadMeals : rules.LoadVoxels;
            double moved = loadsNow * capacity;
            if (moved > best.Amount) moved = best.Amount;
            if (best.IsFood && moved > room) moved = room;

            if (best.IsFood) s.Food += moved;
            else
            {
                long whole = (long)moved;
                if (whole == 0 && best.Amount <= 1.0 + 1e-9) whole = (long)SimMath.Round(best.Amount);
                s.Stock.Add(best.Material, whole);
                moved = whole > 0 ? whole : moved;
            }
            best.Amount -= moved;

            agent.HaulFromX = best.X; agent.HaulFromZ = best.Z;
            agent.HaulToX = tx; agent.HaulToZ = tz;
            agent.HaulWhat = best.IsFood ? "food" : s.Stock.Materials[best.Material].Name;
            agent.HaulTo = to;

            if (best.Amount < Crumbs) Remove(s, bestIndex, details, tick);
            return true;
        }

        /// <summary>A heap gone: out of the list, and its detail out of the world citing what heaped it.</summary>
        static void Remove(Settlement s, int index, DetailLayer details, long tick)
        {
            Pile p = s.PileList[index];
            if (details != null && p.Detail >= 0) details.Remove(p.Detail, tick, p.Cause);
            s.PileList.RemoveAt(index);
        }

        /// <summary>
        /// A day of heaps: food lying in the open rots and presses for a store
        /// where it lies (S2H); an empty heap is cleared away.
        /// </summary>
        public static void Weather(Settlement s, FoodRules food, DetailLayer details, long tick, Annalist annals)
        {
            for (int i = s.PileList.Count - 1; i >= 0; i--)
            {
                Pile p = s.PileList[i];
                if (p.IsFood && food != null && food.PileSpoilPerDay > 0.0)
                {
                    double lost = p.Amount * food.PileSpoilPerDay;
                    p.Amount -= lost;
                    s.FoodSpoiled += lost;
                    Stores.Press(s, p.X / World.ParcelGrid.Size, p.Z / World.ParcelGrid.Size, lost, tick, annals);
                }
                if (p.Amount < Crumbs) Remove(s, i, details, tick);
            }
        }

        /// <summary>
        /// Brings each heap's detail up to date with how big it is: nothing, a
        /// small heap or a big one. Placed and removed only when the size
        /// changes, each change citing the work that made the heap.
        /// </summary>
        public static void Show(Settlement s, HaulRules rules, DetailLayer details, DetailModelTable models,
                                World.ParcelGrid grid, VoxelTypes types, long tick)
        {
            if (rules == null || details == null || models == null || grid == null || rules.ShownFromLoads.Count == 0) return;
            foreach (Pile p in s.PileList)
            {
                double loads = p.Amount / (p.IsFood ? rules.LoadMeals : rules.LoadVoxels);
                int size = 0;
                for (int k = 0; k < rules.ShownFromLoads.Count; k++) if (loads >= rules.ShownFromLoads[k]) size = k + 1;
                if (size == p.Shown) continue;

                if (p.Detail >= 0) { details.Remove(p.Detail, tick, p.Cause); p.Detail = -1; }
                p.Shown = size;
                if (size == 0) continue;

                string name = p.IsFood ? rules.FoodModels[size - 1] : rules.MaterialModels[size - 1];
                DetailModel model = models.Find(name);
                if (model == null) continue;
                ushort[] slots = null;
                if (!p.IsFood && model.Slots.Count > 0)
                {
                    slots = new ushort[model.Slots.Count];
                    ushort voxel = types.IdOf(s.Stock.Materials[p.Material].Voxel);
                    for (int k = 0; k < slots.Length; k++) slots[k] = voxel;
                }
                int y = grid.GroundAt(p.X, p.Z) + 1;
                int cells = DetailModelTable.CellsPerVoxel;
                int cx = p.X * cells + (cells - model.SizeX) / 2, cz = p.Z * cells + (cells - model.SizeZ) / 2;
                p.Detail = details.Place(model, cx, y * cells, cz, (int)((ulong)(p.X * 7 + p.Z * 13) % 4), slots, tick, p.Cause);
            }
        }

        static double Distance(int ax, int az, int bx, int bz)
        {
            double dx = ax - bx, dz = az - bz;
            return SimMath.Sqrt(dx * dx + dz * dz);
        }
    }

    /// <summary>Heaps weather at dawn and are drawn as they change, after the day's work. S2X.</summary>
    public sealed class HaulingSystem : Harness.ISimSystem
    {
        public static readonly Symbol SystemId = Symbol.For("system.hauling");
        readonly HaulRules _rules;
        readonly FoodRules _food;
        readonly DetailModelTable _models;
        readonly World.ParcelGrid _grid;

        public HaulingSystem(HaulRules rules, FoodRules food, DetailModelTable models, World.ParcelGrid grid)
        {
            _rules = rules; _food = food; _models = models; _grid = grid;
        }

        public Symbol Id { get { return SystemId; } }

        public void Tick(Harness.SimWorld world)
        {
            foreach (Settlement s in world.Settlements)
            {
                if (world.Clock.IsFirstTickOfDay) Hauling.Weather(s, _food, world.Details, world.Clock.Tick, world.Annals);
                s.HarvestToFetch = System.Math.Min(Hauling.Piled(s, -1), Hauling.RoomForFood(s, _rules));
                Hauling.Show(s, _rules, world.Details, _models, _grid, world.VoxelTypes, world.Clock.Tick);
            }
        }
    }
}
