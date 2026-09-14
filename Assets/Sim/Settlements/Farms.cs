using System.Collections.Generic;
using Godless.Sim.Annals;
using Godless.Sim.Build;
using Godless.Sim.Content;
using Godless.Sim.Core;
using Godless.Sim.Culture;
using Godless.Sim.Drives;
using Godless.Sim.Harness;
using Godless.Sim.Voxels;
using Godless.Sim.World;

namespace Godless.Sim.Settlements
{
    /// <summary>One crop, as content declares it (`crops/*.json`). S2I.</summary>
    public sealed class Crop
    {
        public Symbol Id { get; internal set; }
        public string Name { get; internal set; }
        public string Tell { get; internal set; }

        /// <summary>How well a parcel suits it, 0 to 1, in the siting language over parcel facts.</summary>
        internal Expr Suits;

        /// <summary>Meals a plot gives at harvest, on unworn soil.</summary>
        public double MealsPerPlot { get; internal set; }

        /// <summary>Days from sowing to ripe.</summary>
        public int GrowDays { get; internal set; }

        /// <summary>Labour ticks to sow a plot, and to bring its harvest in.</summary>
        public double SowLabour { get; internal set; }
        public double HarvestLabour { get; internal set; }

        /// <summary>Soil a harvest takes out of a plot, as a share of full fertility.</summary>
        public double SoilUse { get; internal set; }

        /// <summary>Days a plot lies as stubble after harvest before it can be sown again.</summary>
        public int RestDays { get; internal set; }

        /// <summary>Detail models for a plot's column as it grows: just sown, growing, ripe; and stubble after.</summary>
        public string SownModel { get; internal set; }
        public string GrowingModel { get; internal set; }
        public string RipeModel { get; internal set; }
        public string StubbleModel { get; internal set; }

        /// <summary>A whole round of a plot, sowing to sowing again.</summary>
        public int CycleDays { get { return GrowDays + RestDays; } }

        /// <summary>Meals a day a plot of it gives on a parcel it suits this well.</summary>
        public double MealsPerDay(double suit) { return suit * MealsPerPlot / CycleDays; }

        public double Suitability(IExprScope parcel) { return SimMath.Clamp01(Suits.Eval(parcel)); }
    }

    /// <summary>Every crop the content declares, in stable-hash order. S2I.</summary>
    public sealed class CropTable
    {
        readonly Crop[] _crops;
        readonly List<string> _problems;

        CropTable(Crop[] crops, List<string> problems) { _crops = crops; _problems = problems; }

        public int Count { get { return _crops.Length; } }
        public Crop this[int i] { get { return _crops[i]; } }
        public IReadOnlyList<Crop> All { get { return _crops; } }
        public IReadOnlyList<string> Problems { get { return _problems; } }

        public Crop Find(string name)
        {
            foreach (Crop c in _crops) if (c.Name == name) return c;
            return null;
        }

        public static CropTable FromContent(ContentDatabase content, GeneTable genes)
        {
            var problems = new List<string>();
            var loaded = new List<Crop>();
            foreach (string id in content.Ids("crop"))
            {
                JsonValue doc = content.Get("crop", id);
                string fault = null;
                var c = new Crop
                {
                    Id = Symbol.For("crop." + id),
                    Name = id,
                    Tell = doc["tell"].AsString("").Trim(),
                    MealsPerPlot = doc["mealsPerPlot"].AsDouble(0.0),
                    GrowDays = doc["growDays"].AsInt32(0),
                    SowLabour = doc["sowLabour"].AsDouble(1.0),
                    HarvestLabour = doc["harvestLabour"].AsDouble(1.0),
                    SoilUse = doc["soilUse"].AsDouble(0.0),
                    RestDays = doc["restDays"].AsInt32(10),
                    SownModel = doc["models"]["sown"].AsString(""),
                    GrowingModel = doc["models"]["growing"].AsString(""),
                    RipeModel = doc["models"]["ripe"].AsString(""),
                    StubbleModel = doc["models"]["stubble"].AsString(""),
                };
                if (c.Tell.Length == 0) fault = "declares no tell";
                else if (!(c.MealsPerPlot > 0.0) || c.GrowDays < 1) fault = "gives no meals, or never ripens";
                if (fault == null)
                {
                    try { c.Suits = Expr.Parse(doc["suits"].AsString("0")); }
                    catch (ExprException e) { fault = "has a broken 'suits': " + e.Message; }
                }
                if (fault == null) fault = SitingTable.Check(c.Suits, genes);
                if (fault != null) { problems.Add("crop '" + id + "' " + fault + "."); continue; }
                loaded.Add(c);
            }
            loaded.Sort((a, b) => a.Id.CompareTo(b.Id));
            return new CropTable(loaded.ToArray(), problems);
        }
    }

    /// <summary>How farms are laid out and grown, as content declares it (`farming/*.json`). S2I.</summary>
    public sealed class FarmRules
    {
        public string Tell { get; private set; }

        /// <summary>Parcels round the fire a first farm is looked for in.</summary>
        public int SearchRadius { get; private set; }

        /// <summary>Clear parcels, at the least, between a field and any house or the fire.</summary>
        public int AwayFromHouses { get; private set; }

        /// <summary>Steepest ground a plot is laid on, and most water columns it may hold.</summary>
        public double MaxSlope { get; private set; }
        public int MaxWetColumns { get; private set; }

        /// <summary>Poorest suitability a plot of the farm's crop is laid on.</summary>
        public double MinSuitability { get; private set; }

        /// <summary>Plots a new farm starts with, and fewest it is worth laying.</summary>
        public int FirstPlots { get; private set; }
        public int FewestPlots { get; private set; }

        /// <summary>Days between a farm weighing whether to grow, plots it adds, and most it grows to.</summary>
        public int GrowEveryDays { get; private set; }
        public int PlotsPerGrowth { get; private set; }
        public int MostPlots { get; private set; }

        /// <summary>Plots one farm hand keeps up with; the settlement's hands decide how many plots it can work.</summary>
        public double PlotsPerHand { get; private set; }

        /// <summary>Share of a settlement that could take to the fields, at most.</summary>
        public double HandsShare { get; private set; }

        /// <summary>Days a plot may wait for sowing or harvest before a farm counts itself short of hands.</summary>
        public int WaitDays { get; private set; }

        /// <summary>Fertility a resting plot gains back a day.</summary>
        public double RestorePerDay { get; private set; }

        /// <summary>Share of a ripe crop lost each day it stands unharvested past its wait.</summary>
        public double ShedPerDay { get; private set; }

        /// <summary>The voxel a plot's top becomes when it is broken for the plough.</summary>
        public string TilledVoxel { get; private set; }

        public static FarmRules FromContent(ContentDatabase content)
        {
            foreach (string id in content.Ids("farming"))
            {
                JsonValue doc = content.Get("farming", id);
                return new FarmRules
                {
                    Tell = doc["tell"].AsString(""),
                    SearchRadius = doc["searchRadiusParcels"].AsInt32(24),
                    AwayFromHouses = doc["awayFromHouses"].AsInt32(2),
                    MaxSlope = doc["maxSlope"].AsDouble(3.0),
                    MaxWetColumns = doc["maxWetColumns"].AsInt32(4),
                    MinSuitability = doc["minSuitability"].AsDouble(0.3),
                    FirstPlots = doc["firstPlots"].AsInt32(6),
                    FewestPlots = doc["fewestPlots"].AsInt32(3),
                    GrowEveryDays = doc["growEveryDays"].AsInt32(10),
                    PlotsPerGrowth = doc["plotsPerGrowth"].AsInt32(2),
                    MostPlots = doc["mostPlots"].AsInt32(40),
                    PlotsPerHand = doc["plotsPerHand"].AsDouble(12.0),
                    HandsShare = doc["handsShare"].AsDouble(0.6),
                    WaitDays = doc["waitDays"].AsInt32(6),
                    RestorePerDay = doc["restorePerDay"].AsDouble(0.004),
                    ShedPerDay = doc["shedPerDay"].AsDouble(0.02),
                    TilledVoxel = doc["tilledVoxel"].AsString("voxel.tilled"),
                };
            }
            return null;
        }
    }

    /// <summary>What a farm found when it last weighed growing.</summary>
    public enum Growth
    {
        NotYet,
        Grew,
        /// <summary>The farms and the land already feed everyone.</summary>
        Fed,
        /// <summary>Plots are waiting past their time for hands.</summary>
        HandsBehind,
        /// <summary>The settlement has no hands to spare for more plots.</summary>
        NoHands,
        /// <summary>No good ground free beside it.</summary>
        NoGround,
        /// <summary>As big as a farm grows.</summary>
        Biggest,
        /// <summary>Its harvest already lies rotting for want of somewhere to keep it.</summary>
        NoRoom,
    }

    /// <summary>Where a plot is in its round.</summary>
    public enum PlotState
    {
        /// <summary>Broken ground, waiting to be sown.</summary>
        Fallow,
        /// <summary>Sown, and growing.</summary>
        Growing,
        /// <summary>Ripe, waiting to be harvested.</summary>
        Ripe,
        /// <summary>Harvested; stubble resting before it can be sown again.</summary>
        Stubble,
    }

    /// <summary>One parcel of a farm. S2I.</summary>
    public sealed class Plot
    {
        public int ParcelX { get; internal set; }
        public int ParcelZ { get; internal set; }
        public PlotState State { get; internal set; }

        /// <summary>Days in its present state.</summary>
        public int Days { get; internal set; }

        /// <summary>Labour done toward the next sowing or harvest.</summary>
        public double Labour { get; internal set; }

        /// <summary>How much of a full harvest the soil still gives, 0 to 1.</summary>
        public double Fertility { get; internal set; } = 1.0;

        /// <summary>Share of the ripe crop still standing; what is shed past waiting is lost.</summary>
        public double Standing { get; internal set; } = 1.0;

        /// <summary>How well the ground suits the farm's crop, when the plot was laid.</summary>
        public double Suitability { get; internal set; }

        /// <summary>The last thing that happened to it on record: laid, sown, harvested (L3).</summary>
        public RecordId Last { get; internal set; }

        // The detail instances drawing its crop, and which model they show.
        internal readonly List<int> DetailList = new List<int>();

        /// <summary>The model its columns are drawn with now; empty for bare tilled ground.</summary>
        public string Shown { get; internal set; } = "";
        public IReadOnlyList<int> Details { get { return DetailList; } }

        /// <summary>The column in the middle of the plot.</summary>
        public int CentreX { get { return ParcelX * ParcelGrid.Size + ParcelGrid.Size / 2; } }
        public int CentreZ { get { return ParcelZ * ParcelGrid.Size + ParcelGrid.Size / 2; } }
    }

    /// <summary>A farm: plots of one crop, laid together away from the houses. S2I.</summary>
    public sealed class Farm
    {
        /// <summary>The farm.laid record: what every claim, tilled voxel and plot cites.</summary>
        public RecordId Record { get; internal set; }
        public Crop Crop { get; internal set; }
        public long LaidDay { get; internal set; }

        internal readonly List<Plot> PlotList = new List<Plot>();
        public IReadOnlyList<Plot> Plots { get { return PlotList; } }

        /// <summary>Why it grew or did not, last time it weighed it, in words.</summary>
        public string LastGrowth { get; internal set; } = "";

        /// <summary>What it found, last time it weighed growing.</summary>
        public Growth LastGrowthResult { get; internal set; }

        // How much food had rotted when it last weighed growing (S2H).
        internal double SpoiledWhenWeighed;

        /// <summary>Meals a day its plots give over a round, on the soil they have now.</summary>
        public double MealsPerDay
        {
            get
            {
                double m = 0.0;
                foreach (Plot p in PlotList) m += Crop.MealsPerDay(p.Suitability) * p.Fertility;
                return m;
            }
        }

        /// <summary>The middle of its plots, in parcels.</summary>
        public void Centre(out int px, out int pz)
        {
            long sx = 0, sz = 0;
            foreach (Plot p in PlotList) { sx += p.ParcelX; sz += p.ParcelZ; }
            int n = PlotList.Count > 0 ? PlotList.Count : 1;   // a farm built over entirely has no middle; the origin will do
            px = (int)(sx / n); pz = (int)(sz / n);
        }
    }

    /// <summary>
    /// Farms. S2I.
    ///
    /// Foraging feeds as many as the land round the fire gives up, and no
    /// more. When a settlement outgrows it, people go hungry, and hunger
    /// presses for a farm the way nights in the open press for a house. A farm
    /// is laid where the ground suits a crop, clear of the houses: wheat on
    /// middling ground a walk from water, barley where it is cold or high,
    /// millet where it is dry, taro in the wet ground by the water. Each gives
    /// a different harvest for different work, and wears the soil differently.
    ///
    /// Plots are sown, grow, ripen and are cut, and the cut sheaves lie in the
    /// field until someone carries them in (S2X). A farm grows by a plot or two
    /// when food is still short, there are hands to work more, and the ground
    /// beside it is free and good — and families who work it build near it.
    ///
    /// The tell: squares of brown furrows away from the houses turning green,
    /// then gold, then stubble, with the same few people bent over them.
    /// </summary>
    public static class Farms
    {
        public static readonly Symbol LaidKind = Symbol.For("farm.laid");
        public static readonly Symbol GrewKind = Symbol.For("farm.grew");
        public static readonly Symbol SownKind = Symbol.For("farm.sown");
        public static readonly Symbol HarvestedKind = Symbol.For("farm.harvested");
        public static readonly Symbol ShortKind = Symbol.For("food.short");

        /// <summary>What presses for a farm besides hunger: the land picked clean while food runs low.</summary>
        public const string Shortage = "shortage";

        /// <summary>
        /// A day that says the land alone no longer feeds the village: yesterday
        /// the foragers picked it clean, and there is under half a season of food
        /// in hand. The shortfall presses for a farm, citing a food.short record
        /// written at most weekly (L3).
        /// </summary>
        public static void PressShortage(Settlement s, long tick, Annalist annals)
        {
            if (s.Intents == null || s.Catchment == null || !s.Catchment.PickedCleanYesterday) return;
            if (HarvestRotting(s)) return;   // the want is a store, not more field
            double inHand = s.Food + Hauling.Piled(s, -1);
            double wanted = Subsistence.Wanted(s);
            if (inHand >= wanted * 0.5) return;
            const long Week = 7 * SimClock.DefaultTicksPerDay;
            if (!s.ShortRecord.Exists || tick - s.ShortTick >= Week)
            {
                s.ShortRecord = annals.Write(tick, ShortKind, s.Id, s.Hearth, s.Founded, (long)inHand, s.People.Count);
                s.ShortTick = tick;
            }
            s.Intents.Press(Shortage, s.HearthParcelX, s.HearthParcelZ, (wanted * 0.5 - inHand) / 10.0, s.ShortRecord);
        }

        /// <summary>
        /// A plot given up to a building (S2I): out of its farm, whatever grew on
        /// it lost, its plants cleared the next time the fields are drawn. The
        /// building's claim replaces the farm's.
        /// </summary>
        public static void Surrender(Settlement s, int px, int pz, RecordId by)
        {
            foreach (Farm f in s.FarmList)
                for (int i = 0; i < f.PlotList.Count; i++)
                {
                    Plot p = f.PlotList[i];
                    if (p.ParcelX != px || p.ParcelZ != pz) continue;
                    f.PlotList.RemoveAt(i);
                    p.Last = by;
                    s.GivenUpPlots.Add(p);
                    return;
                }
        }

        /// <summary>The farm a parcel belongs to, or null.</summary>
        public static Farm Owning(Settlement s, int px, int pz)
        {
            RecordId owner = s.ClaimOn(px, pz);
            if (!owner.Exists) return null;
            foreach (Farm f in s.FarmList) if (f.Record == owner) return f;
            return null;
        }

        /// <summary>Whether a parcel may take a plot: flat, dry enough, walkable to, unclaimed, and clear of every house.</summary>
        static bool Open(Settlement s, FarmRules rules, ParcelGrid grid, bool[] reachable, int px, int pz)
        {
            if (!ParcelGrid.InBounds(px, pz) || !grid.IsLand(px, pz)) return false;
            if (grid.Slope[px, pz] >= rules.MaxSlope || grid.WetColumns(px, pz) > rules.MaxWetColumns) return false;
            if (s.IsClaimed(px, pz)) return false;
            if (s.Borders != null && s.Borders.BelongsToAnother(s, px, pz)) return false;   // another town's ground (S2Y)
            if (reachable != null && !reachable[pz * ParcelGrid.Width + px]) return false;

            int r = rules.AwayFromHouses;
            for (int dz = -r; dz <= r; dz++)
                for (int dx = -r; dx <= r; dx++)
                {
                    int x = px + dx, z = pz + dz;
                    if (s.IsClaimed(x, z) && !s.IsField(x, z)) return false;   // a house's, a store's, or the fire's
                }
            return true;
        }

        /// <summary>
        /// Lays a farm for an intent: the best ground within reach for the crop
        /// that suits it best, grown out from there. Claims the plots, clears
        /// and tills them, and records it. Null when there is nowhere worth it.
        /// </summary>
        public static Farm Lay(Settlement s, BuildIntent intent, FarmRules rules, CropTable crops, ParcelGrid grid,
                               ConstraintFields fields, SimWorld world)
        {
            return Lay(s, intent.Record, intent.Kind.Id, rules, crops, grid, fields, world);
        }

        /// <summary>A farm laid for a cause other than an intent: a farm that could not grow while food was short.</summary>
        public static Farm Lay(Settlement s, RecordId cause, Symbol why, FarmRules rules, CropTable crops, ParcelGrid grid,
                               ConstraintFields fields, SimWorld world, bool[] reachable = null)
        {
            if (rules == null || crops == null || crops.Count == 0) return null;
            if (reachable == null) reachable = SiteScorer.ReachableFromFire(s, grid);

            // Near first; with the ground round the fire taken by houses and
            // fields, further out (S2V), as the houses do.
            for (int widen = 1; widen <= 3; widen++)
            {
                Farm farm = LayWithin(s, cause, why, rules, crops, grid, fields, world, reachable, rules.SearchRadius * widen);
                if (farm != null) return farm;
            }
            return null;
        }

        static Farm LayWithin(Settlement s, RecordId cause, Symbol why, FarmRules rules, CropTable crops, ParcelGrid grid,
                              ConstraintFields fields, SimWorld world, bool[] reachable, int radius)
        {
            int hx = s.HearthParcelX, hz = s.HearthParcelZ;

            // Every open parcel's best crop, and what it is worth a day.
            var seeds = new List<KeyValuePair<double, int>>();
            var bestCrop = new Dictionary<int, int>();     // lookup only; never iterated
            for (int pz = hz - radius; pz <= hz + radius; pz++)
                for (int px = hx - radius; px <= hx + radius; px++)
                {
                    if (!Open(s, rules, grid, reachable, px, pz)) continue;
                    IExprScope facts = SiteScorer.Facts(s, grid, fields, s.Genome, px, pz);
                    int best = -1;
                    double bestValue = 0.0;
                    for (int c = 0; c < crops.Count; c++)
                    {
                        double suit = crops[c].Suitability(facts);
                        if (suit < rules.MinSuitability) continue;
                        double value = crops[c].MealsPerDay(suit);
                        if (value > bestValue) { bestValue = value; best = c; }
                    }
                    if (best < 0) continue;
                    double dist = SimMath.Sqrt((double)((px - hx) * (px - hx) + (pz - hz) * (pz - hz)));
                    int key = pz * ParcelGrid.Width + px;
                    bestCrop[key] = best;
                    seeds.Add(new KeyValuePair<double, int>(bestValue * (1.0 - dist / (radius * 2.0)), key));
                }
            if (seeds.Count == 0) return null;
            seeds.Sort((a, b) => { int c = b.Key.CompareTo(a.Key); return c != 0 ? c : a.Value.CompareTo(b.Value); });

            List<int> chosen = null;
            Crop chosenCrop = null;
            double chosenScore = double.NegativeInfinity;
            for (int i = 0; i < seeds.Count && i < 12; i++)
            {
                int seed = seeds[i].Value;
                Crop crop = crops[bestCrop[seed]];
                List<int> field = Grow(s, rules, crop, grid, fields, reachable, new List<int> { seed }, rules.FirstPlots - 1);
                if (field.Count < rules.FewestPlots) continue;
                double score = 0.0;
                foreach (int p in field)
                    score += crop.MealsPerDay(crop.Suitability(SiteScorer.Facts(s, grid, fields, s.Genome, p % ParcelGrid.Width, p / ParcelGrid.Width)));
                score *= 1.0 - Distance(seed % ParcelGrid.Width, seed / ParcelGrid.Width, hx, hz) / (radius * 2.0);
                if (score > chosenScore) { chosenScore = score; chosen = field; chosenCrop = crop; }
            }
            if (chosen == null) return null;

            long tick = world.Clock.Tick;
            int sx = 0, sz = 0;
            foreach (int p in chosen) { sx += p % ParcelGrid.Width; sz += p / ParcelGrid.Width; }
            var place = new Int3((sx / chosen.Count) * ParcelGrid.Size, s.Hearth.Y, (sz / chosen.Count) * ParcelGrid.Size);
            RecordId laid = world.Annals.Write(tick, LaidKind, s.Id, place, cause, chosen.Count, (long)(chosenScore * 1000.0),
                                               new[] { chosenCrop.Id, why });
            var farm = new Farm { Record = laid, Crop = chosenCrop, LaidDay = world.Clock.TotalDays };
            s.FarmList.Add(farm);
            s.FarmRecords.Add(laid.Index);
            foreach (int p in chosen) Break(s, farm, rules, grid, fields, world, p % ParcelGrid.Width, p / ParcelGrid.Width, laid);
            return farm;
        }

        /// <summary>
        /// A field grown out from the parcels given, best-suited neighbour first,
        /// until it has <paramref name="more"/> more. Ties to the lowest parcel.
        /// </summary>
        static List<int> Grow(Settlement s, FarmRules rules, Crop crop, ParcelGrid grid, ConstraintFields fields,
                              bool[] reachable, List<int> start, int more)
        {
            var field = new List<int>(start);
            var inField = new HashSet<int>(start);      // lookup only
            int[] ox = { 1, -1, 0, 0 }, oz = { 0, 0, 1, -1 };
            for (int added = 0; added < more; added++)
            {
                int best = -1;
                double bestSuit = -1.0;
                foreach (int p in field)
                    for (int k = 0; k < 4; k++)
                    {
                        int x = p % ParcelGrid.Width + ox[k], z = p / ParcelGrid.Width + oz[k];
                        int key = z * ParcelGrid.Width + x;
                        if (inField.Contains(key) || !Open(s, rules, grid, reachable, x, z)) continue;
                        double suit = crop.Suitability(SiteScorer.Facts(s, grid, fields, s.Genome, x, z));
                        if (suit < rules.MinSuitability) continue;
                        if (suit > bestSuit || (suit == bestSuit && key < best)) { bestSuit = suit; best = key; }
                    }
                if (best < 0) break;
                field.Add(best);
                inField.Add(best);
            }
            return field;
        }

        /// <summary>A plot broken for the plough: claimed, cleared of what grew on it, its top tilled, on record.</summary>
        static void Break(Settlement s, Farm farm, FarmRules rules, ParcelGrid grid, ConstraintFields fields, SimWorld world,
                          int px, int pz, RecordId cause)
        {
            long tick = world.Clock.Tick;
            s.ClaimParcel(px, pz, farm.Record);
            var plot = new Plot
            {
                ParcelX = px, ParcelZ = pz, State = PlotState.Fallow, Last = cause,
                Suitability = farm.Crop.Suitability(SiteScorer.Facts(s, grid, fields, s.Genome, px, pz)),
            };
            farm.PlotList.Add(plot);

            int x0 = px * ParcelGrid.Size, z0 = pz * ParcelGrid.Size, x1 = x0 + ParcelGrid.Size - 1, z1 = z0 + ParcelGrid.Size - 1;
            if (world.Island != null && world.Island.Deposits != null && s.Stock != null)
            {
                // What stood there is cut into a heap at the plot's edge for the haulers (S2X).
                int[] got = world.Island.Deposits.Clear(x0, z0, x1, z1, world.Voxels, tick, cause, world.Clock.TicksPerDay);
                for (int k = 0; k < got.Length; k++)
                {
                    if (got[k] <= 0) continue;
                    int m = s.Stock.Materials.IndexOf(world.Island.Deposits.Kinds[k].Yields);
                    if (m >= 0) Hauling.Drop(s, x0, z0, m, got[k], cause);
                }
            }

            ushort tilled;
            if (!world.VoxelTypes.TryGetId(Symbol.For(rules.TilledVoxel), out tilled)) return;
            for (int z = z0; z <= z1; z++)
                for (int x = x0; x <= x1; x++)
                {
                    int y = grid.GroundAt(x, z);
                    if (y <= 0 || grid.IsWetColumn(x, z)) continue;
                    ushort here = world.Voxels.Get(x, y, z);
                    if (here == VoxelTypes.AirId || here == tilled) continue;
                    world.Voxels.Set(x, y, z, tilled, tick, cause);
                }
        }

        /// <summary>
        /// A farm grows by a plot or two when three things hold: food is still
        /// short of what the settlement eats; there are hands to work more (no
        /// plot is kept waiting, and the settlement has people to spare); and
        /// good ground is free beside it. Returns the farm.grew record, or None,
        /// and says why either way.
        /// </summary>
        public static RecordId Consider(Settlement s, Farm farm, FarmRules rules, ParcelGrid grid, ConstraintFields fields,
                                        SimWorld world, RecordId cause, bool[] reachable = null)
        {
            if (farm.PlotList.Count >= rules.MostPlots) { farm.SpoiledWhenWeighed = s.FoodSpoiled; return Result(farm, Growth.Biggest, "as big as a farm grows"); }

            // Short of food: what the farms and the land give a day, against what everyone eats.
            // Room: harvest lying in the field with nowhere to go, or a good
            // share of the food since it last looked lost to rot.
            double rotted = s.FoodSpoiled - farm.SpoiledWhenWeighed;
            farm.SpoiledWhenWeighed = s.FoodSpoiled;
            if (HarvestRotting(s) || rotted > s.People.Count * Subsistence.MealsADay * rules.GrowEveryDays * 0.25)
                return Result(farm, Growth.NoRoom, "its harvest is rotting for want of a store");

            if (!Short(s)) { farm.SpoiledWhenWeighed = s.FoodSpoiled; return Result(farm, Growth.Fed, "the farms and the land feed everyone"); }


            // Hands: nothing waiting past its time, and people enough for more plots.
            foreach (Plot p in farm.PlotList)
                if ((p.State == PlotState.Fallow || p.State == PlotState.Ripe) && p.Days > rules.WaitDays)
                    return Result(farm, Growth.HandsBehind, "plots are waiting for hands");
            if (!HandsFor(s, rules, rules.PlotsPerGrowth)) return Result(farm, Growth.NoHands, "no hands to spare for more");

            // Ground: the best free neighbours that suit the crop.
            if (reachable == null) reachable = SiteScorer.ReachableFromFire(s, grid);
            var start = new List<int>();
            foreach (Plot p in farm.PlotList) start.Add(p.ParcelZ * ParcelGrid.Width + p.ParcelX);
            List<int> field = Grow(s, rules, farm.Crop, grid, fields, reachable, start, rules.PlotsPerGrowth);
            if (field.Count == start.Count) return Result(farm, Growth.NoGround, "no good ground free beside it");

            int cx, cz;
            farm.Centre(out cx, out cz);
            RecordId grew = world.Annals.Write(world.Clock.Tick, GrewKind, s.Id,
                                               new Int3(cx * ParcelGrid.Size, s.Hearth.Y, cz * ParcelGrid.Size), farm.Record,
                                               field.Count - start.Count, field.Count, new[] { farm.Crop.Id },
                                               cause.Exists ? new[] { cause } : null);
            for (int i = start.Count; i < field.Count; i++)
                Break(s, farm, rules, grid, fields, world, field[i] % ParcelGrid.Width, field[i] / ParcelGrid.Width, grew);
            Result(farm, Growth.Grew, "grew by " + (field.Count - start.Count) + ": food short, hands to spare, ground free");
            return grew;
        }

        /// <summary>Harvest lies in the fields with nowhere to put it: the settlement's want is a store (S2H), not more field.</summary>
        public static bool HarvestRotting(Settlement s)
        {
            return Hauling.Piled(s, -1) > s.HarvestToFetch + 12.0
                || s.RotPerDay > s.People.Count * Subsistence.MealsADay * 0.2;
        }

        static RecordId Result(Farm farm, Growth result, string words)
        {
            farm.LastGrowthResult = result;
            farm.LastGrowth = words;
            return RecordId.None;
        }

        /// <summary>
        /// Short of food: less than half a season in hand (in the store and
        /// fetched from the fields), or what the farms and the land could give
        /// a day falls short of what everyone eats, with a margin.
        /// </summary>
        public static bool Short(Settlement s)
        {
            if (s.Food + Hauling.Piled(s, -1) < Subsistence.Wanted(s) * 0.5) return true;
            double grown = 0.0;
            foreach (Farm f in s.FarmList) grown += f.MealsPerDay;
            double wild = s.Catchment != null ? s.Catchment.ForagePerDay : 0.0;
            return grown + wild < s.People.Count * Subsistence.MealsADay * 1.15;
        }

        /// <summary>Whether the settlement has hands for this many more plots.</summary>
        public static bool HandsFor(Settlement s, FarmRules rules, int more)
        {
            int plots = 0;
            foreach (Farm f in s.FarmList) plots += f.PlotList.Count;
            return plots + more <= s.People.Count * rules.HandsShare * rules.PlotsPerHand;
        }

        /// <summary>A day of the fields: growing, ripening, shedding, resting.</summary>
        public static void Day(Settlement s, FarmRules rules)
        {
            foreach (Farm farm in s.FarmList)
                foreach (Plot p in farm.PlotList)
                {
                    p.Days++;
                    switch (p.State)
                    {
                        case PlotState.Growing:
                            if (p.Days >= farm.Crop.GrowDays) { p.State = PlotState.Ripe; p.Days = 0; p.Standing = 1.0; p.Labour = 0.0; }
                            break;
                        case PlotState.Ripe:
                            if (p.Days > rules.WaitDays) p.Standing = p.Standing * (1.0 - rules.ShedPerDay);
                            break;
                        case PlotState.Stubble:
                            p.Fertility = SimMath.Clamp01(p.Fertility + rules.RestorePerDay);
                            if (p.Days >= farm.Crop.RestDays) { p.State = PlotState.Fallow; p.Days = 0; p.Labour = 0.0; }
                            break;
                        case PlotState.Fallow:
                            p.Fertility = SimMath.Clamp01(p.Fertility + rules.RestorePerDay);
                            break;
                    }
                }
        }

        /// <summary>Labour ticks the fields want now: every plot waiting to be sown or cut, what is left of each.</summary>
        public static double Wanted(Settlement s)
        {
            double work = 0.0;
            foreach (Farm farm in s.FarmList)
                foreach (Plot p in farm.PlotList)
                {
                    if (p.State == PlotState.Fallow) work += farm.Crop.SowLabour - p.Labour;
                    else if (p.State == PlotState.Ripe) work += (farm.Crop.HarvestLabour - p.Labour) * 2.0;   // it will not wait
                }
            return work;
        }

        /// <summary>
        /// A tick in the fields: the plot this person was working if it still
        /// wants work, else the ripe plot nearest them, else the nearest to sow.
        /// Sowing done, it grows; harvest done, the sheaves lie in a heap on it.
        /// False when no plot wants anyone.
        /// </summary>
        public static bool Work(Settlement s, Agent agent, long tick, Annalist annals)
        {
            Farm atFarm = null;
            Plot at = null;
            double bestD = double.MaxValue;
            bool bestRipe = false, stay = false;
            foreach (Farm farm in s.FarmList)
            {
                foreach (Plot p in farm.PlotList)
                {
                    bool ripe = p.State == PlotState.Ripe;
                    if (!ripe && p.State != PlotState.Fallow) continue;
                    if (p.CentreX == agent.FieldX && p.CentreZ == agent.FieldZ) { at = p; atFarm = farm; stay = true; break; }
                    double d = Distance(agent.X, agent.Z, p.CentreX, p.CentreZ);
                    bool better = at == null || (ripe && !bestRipe) || (ripe == bestRipe && d < bestD);
                    if (better) { at = p; atFarm = farm; bestD = d; bestRipe = ripe; }
                }
                if (stay) break;
            }
            if (at == null) return false;

            Crop crop = atFarm.Crop;
            at.Labour += agent.LabourShare;
            agent.FieldX = at.CentreX;
            agent.FieldZ = at.CentreZ;
            agent.FieldFarm = atFarm.Record.Index;

            var place = new Int3(at.CentreX, s.Hearth.Y, at.CentreZ);
            if (at.State == PlotState.Fallow)
            {
                agent.FieldWork = "sowing " + crop.Name;
                if (at.Labour >= crop.SowLabour)
                {
                    at.Last = annals.Write(tick, SownKind, s.Id, place, atFarm.Record, 0, (long)(at.Fertility * 1000.0), new[] { crop.Id });
                    at.State = PlotState.Growing; at.Days = 0; at.Labour = 0.0;
                }
            }
            else
            {
                agent.FieldWork = "harvesting " + crop.Name;
                if (at.Labour >= crop.HarvestLabour)
                {
                    double meals = crop.MealsPerPlot * at.Fertility * at.Standing;
                    at.Last = annals.Write(tick, HarvestedKind, s.Id, place, atFarm.Record, (long)meals, (long)(at.Fertility * 1000.0), new[] { crop.Id });
                    Hauling.Drop(s, at.CentreX, at.CentreZ, -1, meals, at.Last);
                    at.Fertility = System.Math.Max(0.1, at.Fertility - crop.SoilUse);
                    at.State = PlotState.Stubble; at.Days = 0; at.Labour = 0.0; at.Standing = 1.0;
                }
            }
            return true;
        }

        /// <summary>
        /// Brings each plot's plants up to date with where it is in its round:
        /// a detail per column, placed and removed only when the stage changes,
        /// each citing what last happened to the plot.
        /// </summary>
        public static void Show(Settlement s, DetailLayer details, DetailModelTable models, ParcelGrid grid, long tick)
        {
            if (details == null || models == null || grid == null) return;
            foreach (Plot gone in s.GivenUpPlots)
                foreach (int id in gone.DetailList) details.Remove(id, tick, gone.Last);
            s.GivenUpPlots.Clear();
            foreach (Farm farm in s.FarmList)
                foreach (Plot p in farm.PlotList)
                {
                    string want;
                    switch (p.State)
                    {
                        case PlotState.Growing: want = p.Days * 3 < farm.Crop.GrowDays ? farm.Crop.SownModel : farm.Crop.GrowingModel; break;
                        case PlotState.Ripe: want = farm.Crop.RipeModel; break;
                        case PlotState.Stubble: want = farm.Crop.StubbleModel; break;
                        default: want = ""; break;
                    }
                    if (want == p.Shown) continue;
                    foreach (int id in p.DetailList) details.Remove(id, tick, p.Last);
                    p.DetailList.Clear();
                    p.Shown = want;
                    DetailModel model = want.Length > 0 ? models.Find(want) : null;
                    if (model == null) continue;

                    int cells = DetailModelTable.CellsPerVoxel;
                    int x0 = p.ParcelX * ParcelGrid.Size, z0 = p.ParcelZ * ParcelGrid.Size;
                    for (int z = z0; z < z0 + ParcelGrid.Size; z++)
                        for (int x = x0; x < x0 + ParcelGrid.Size; x++)
                        {
                            if (grid.IsWetColumn(x, z)) continue;
                            int y = grid.GroundAt(x, z) + 1;
                            p.DetailList.Add(details.Place(model, x * cells, y * cells, z * cells, 0, null, tick, p.Last));
                        }
                }
        }

        static double Distance(int ax, int az, int bx, int bz)
        {
            double dx = ax - bx, dz = az - bz;
            return SimMath.Sqrt(dx * dx + dz * dz);
        }
    }

    /// <summary>
    /// Lays farms for open farm intents and grows the ones that stand, once a
    /// day; brings the fields' plants up to date every tick, after the day's
    /// work. S2I.
    /// </summary>
    public sealed class FarmSystem : ISimSystem
    {
        public static readonly Symbol SystemId = Symbol.For("system.farms");

        readonly FarmRules _rules;
        readonly CropTable _crops;
        readonly ParcelGrid _grid;
        readonly ConstraintFields _fields;
        readonly DetailModelTable _models;

        public FarmSystem(FarmRules rules, CropTable crops, ParcelGrid grid, ConstraintFields fields, DetailModelTable models)
        {
            _rules = rules; _crops = crops; _grid = grid; _fields = fields; _models = models;
        }

        public Symbol Id { get { return SystemId; } }

        public void Tick(SimWorld world)
        {
            if (_rules == null || _crops == null || _grid == null) return;
            foreach (Settlement s in world.Settlements)
            {
                if (world.Clock.IsFirstTickOfDay) Daily(s, world);
                Farms.Show(s, world.Details, _models, _grid, world.Clock.Tick);
            }
        }

        void Daily(Settlement s, SimWorld world)
        {
            // How much food has been rotting a day, lately: a slow average, so a
            // village between harvests still remembers the last one it lost.
            double today = s.FoodSpoiled - s.SpoiledYesterday;
            s.SpoiledYesterday = s.FoodSpoiled;
            s.RotPerDay = s.RotPerDay * 0.97 + today * 0.03;

            Farms.PressShortage(s, world.Clock.Tick, world.Annals);
            Farms.Day(s, _rules);
            long day = world.Clock.TotalDays;

            // Walkable ground from the fire, found once today and only if some
            // farm is weighing anything: every farm and every laying reads the same.
            bool[] reachable = null;
            System.Func<bool[]> reach = () => reachable ?? (reachable = SiteScorer.ReachableFromFire(s, _grid));

            // Hunger's intent: grow a farm that can, else lay a new one.
            if (s.Intents != null)
                foreach (BuildIntent intent in s.Intents.Intents)
                {
                    if (intent.Status != IntentStatus.Open || intent.Kind.Purpose != IntentPurpose.Farm) continue;
                    if (world.Clock.Tick < intent.RetryAt) continue;
                    RecordId done = RecordId.None;
                    if (Farms.HarvestRotting(s))
                    {
                        if (world.Clock.Tick - intent.RaisedTick > SiteSystem.GivesUpAfterDays * world.Clock.TicksPerDay)
                            s.Intents.Abandon(intent, world.Clock.Tick, world.Annals, s.Founded);
                        continue;
                    }
                    foreach (Farm f in s.FarmList)
                    {
                        done = Farms.Consider(s, f, _rules, _grid, _fields, world, intent.Record, reach());
                        if (done.Exists) break;
                    }
                    if (!done.Exists)
                    {
                        Farm laid = Farms.Lay(s, intent.Record, intent.Kind.Id, _rules, _crops, _grid, _fields, world, reach());
                        if (laid != null) done = laid.Record;
                    }
                    if (done.Exists)
                    {
                        s.Intents.Claim(intent, world.Clock.Tick, world.Annals, done);
                        s.Intents.Resolve(intent, world.Clock.Tick, world.Annals, done);
                    }
                    else if (world.Clock.Tick - intent.RaisedTick > SiteSystem.GivesUpAfterDays * world.Clock.TicksPerDay)
                        s.Intents.Abandon(intent, world.Clock.Tick, world.Annals, s.Founded);
                    else intent.RetryAt = world.Clock.Tick + SiteSystem.RetryAfterDays * world.Clock.TicksPerDay;
                }

            // And every farm weighs growing on its own, now and then. A farm
            // hemmed in while food is still short and hands are free is the
            // cause of the next one, somewhere with ground.
            Farm hemmed = null;
            bool grew = false;
            foreach (Farm f in s.FarmList)
                if ((day - f.LaidDay) > 0 && (day - f.LaidDay) % _rules.GrowEveryDays == 0)
                {
                    if (Farms.Consider(s, f, _rules, _grid, _fields, world, RecordId.None, reach()).Exists) grew = true;
                    else if (f.LastGrowthResult == Growth.NoGround || f.LastGrowthResult == Growth.Biggest) hemmed = f;
                }
            if (hemmed != null && !grew && hemmed.LastGrowthResult != Growth.NoRoom && Farms.Short(s) && !Farms.HarvestRotting(s)
                && Farms.HandsFor(s, _rules, _rules.FirstPlots))
                Farms.Lay(s, hemmed.Record, hemmed.Crop.Id, _rules, _crops, _grid, _fields, world, reach());
        }
    }
}
