using System.Collections.Generic;
using Godless.Sim.Annals;
using Godless.Sim.Build;
using Godless.Sim.Collective;
using Godless.Sim.Content;
using Godless.Sim.Core;
using Godless.Sim.Culture;
using Godless.Sim.Drives;
using Godless.Sim.Harness;
using Godless.Sim.Voxels;
using Godless.Sim.World;

namespace Godless.Sim.Settlements
{
    /// <summary>
    /// Putting one settlement on an island and giving it everything stratum 1
    /// needs to live: drives, an intent bus, a task board, a stock, a genome,
    /// and the systems that turn those into houses.
    ///
    /// The site is a stand-in — the flattest dry parcel near water — and so is
    /// the fact that there is exactly one settlement. Both belong to S30, which
    /// founds settlements by quorum over candidate sites (S3A). This exists so
    /// the command line, the Editor and the tests all start a village the same
    /// way rather than three subtly different ways.
    /// </summary>
    /// <summary>
    /// What a parcel offers as the place a first fire is lit (the player's
    /// choice before the world starts): whether people can settle there at all,
    /// and what the land in reach gives them to build with and to eat.
    /// </summary>
    public sealed class SiteReport
    {
        public int ParcelX { get; internal set; }
        public int ParcelZ { get; internal set; }

        /// <summary>Whether a settlement may be founded here.</summary>
        public bool CanSettle { get; internal set; }

        /// <summary>Why not, in words, when it may not; empty when it may.</summary>
        public string Why { get; internal set; } = "";

        public string Biome { get; internal set; } = "";
        public double Slope { get; internal set; }

        /// <summary>Parcels to the nearest water.</summary>
        public double WaterParcels { get; internal set; }

        /// <summary>Height above the sea, in voxels.</summary>
        public int Elevation { get; internal set; }

        /// <summary>Meals the land in reach gives foragers a day, at most: the ceiling a village grows to before it farms.</summary>
        public double ForagePerDay { get; internal set; }

        /// <summary>Materials in reach, each with the share of full yield a tick of gathering brings, best first.</summary>
        public IReadOnlyList<KeyValuePair<string, double>> Materials { get { return MaterialList; } }
        internal readonly List<KeyValuePair<string, double>> MaterialList = new List<KeyValuePair<string, double>>();

        /// <summary>Flat dry parcels within ten: room for the first houses and fields.</summary>
        public int RoomNearby { get; internal set; }
    }

    public static class Founding
    {
        /// <summary>Steepest ground a fire is lit on, in voxels of rise across its parcel.</summary>
        public const double MostSlopeForAFire = 6.0;

        /// <summary>Farthest water may be, in parcels: people walk to drink (S2V).</summary>
        public const double FarthestWater = 20.0;

        /// <summary>Parcels from the map's edge a fire may be lit, so a village has somewhere to grow.</summary>
        public const int FromTheEdge = 12;

        /// <summary>
        /// Whether people can settle on a parcel, and what it offers. Any dry,
        /// flat-enough parcel of land with water within walking distance, away
        /// from the edge of the world; the report says why not otherwise.
        /// </summary>
        public static SiteReport Appraise(SimWorld world, ContentDatabase content, ParcelGrid grid, BiomeTable biomes,
                                          int px, int pz)
        {
            var report = new SiteReport { ParcelX = px, ParcelZ = pz };
            if (px < FromTheEdge || pz < FromTheEdge || px >= ParcelGrid.Width - FromTheEdge || pz >= ParcelGrid.Depth - FromTheEdge)
            { report.Why = "too near the edge of the world"; return report; }
            if (!grid.IsLand(px, pz)) { report.Why = "that is water"; return report; }

            int cx = px * ParcelGrid.Size + ParcelGrid.Size / 2, cz = pz * ParcelGrid.Size + ParcelGrid.Size / 2;
            int b = world.Island != null ? world.Island.BiomeAt(cx, cz) : -1;
            report.Biome = b >= 0 ? biomes.At(b).Id.ToString().Replace("biome.", "") : "";
            report.Slope = grid.Slope[px, pz];
            report.WaterParcels = grid.WaterDistance[px, pz];
            report.Elevation = (int)grid.Height[px, pz] - (world.Island != null ? world.Island.SeaLevel : IslandMap.DefaultSeaLevel);

            int room = 0;
            for (int z = pz - 10; z <= pz + 10; z++)
                for (int x = px - 10; x <= px + 10; x++)
                    if (ParcelGrid.InBounds(x, z) && grid.IsLand(x, z) && grid.WetColumns(x, z) == 0 && grid.Slope[x, z] < 3.0) room++;
            report.RoomNearby = room;

            if (grid.WetColumns(px, pz) > 0) { report.Why = "the ground is wet"; return report; }
            if (report.Slope >= MostSlopeForAFire) { report.Why = "too steep for a fire"; return report; }
            if (report.WaterParcels > FarthestWater) { report.Why = "too far from water"; return report; }

            if (world.Island != null && content != null)
            {
                MaterialTable materials = MaterialTable.FromContent(content, biomes);
                Catchment catchment = world.Island.Deposits != null
                    ? Catchment.FromDeposits(world.Island, biomes, materials, world.Island.Deposits, cx, cz)
                    : Catchment.Survey(world.Island, biomes, materials, cx, cz);
                report.ForagePerDay = catchment.ForagePerDay;
                for (int m = 0; m < materials.Count; m++)
                    if (catchment.Offers(m))
                        report.MaterialList.Add(new KeyValuePair<string, double>(materials[m].Name,
                            catchment.YieldPerLabourTick(m) / materials[m].PerLabourTick));
                report.MaterialList.Sort((x, y) => { int c = y.Value.CompareTo(x.Value); return c != 0 ? c : string.CompareOrdinal(x.Key, y.Key); });
            }

            report.CanSettle = true;
            return report;
        }

        /// <summary>
        /// The flattest dry parcel within four of water, in the wanted biome
        /// if one is named and anywhere if not. False when the island has
        /// nowhere that qualifies.
        /// </summary>
        public static bool StandInSite(ParcelGrid grid, IslandMap island, BiomeTable biomes, Symbol biome,
                                       out int parcelX, out int parcelZ)
        {
            parcelX = parcelZ = -1;
            double flattest = double.MaxValue;
            for (int pz = 0; pz < ParcelGrid.Depth; pz++)
                for (int px = 0; px < ParcelGrid.Width; px++)
                {
                    if (!grid.IsLand(px, pz) || grid.WetColumns(px, pz) > 0) continue;
                    double water = grid.WaterDistance[px, pz];
                    if (water < 1.0 || water > 4.0) continue;
                    if (!biome.IsNone)
                    {
                        int b = island.BiomeAt(px * ParcelGrid.Size + 2, pz * ParcelGrid.Size + 2);
                        if (b < 0 || biomes.At(b).Id != biome) continue;
                    }
                    if (grid.Slope[px, pz] >= flattest) continue;
                    flattest = grid.Slope[px, pz];
                    parcelX = px;
                    parcelZ = pz;
                }
            return parcelX >= 0;
        }

        /// <summary>
        /// Founds a settlement at a parcel and gives it its stock, its
        /// catchment, its task board and its genome. The systems are added to
        /// the world by <see cref="AddSystems"/>, once, whatever the number of
        /// settlements.
        /// </summary>
        public static Settlement Begin(SimWorld world, ContentDatabase content, ParcelGrid grid, BiomeTable biomes,
                                       string name, int people, int parcelX, int parcelZ, Genome genome, int roofs = 0)
        {
            return Begin(world, content, grid, biomes, name, people, parcelX, parcelZ, genome, roofs, RecordId.None);
        }

        /// <summary>A settlement founded because of something on record (S2Y: a town it came from, outgrown).</summary>
        public static Settlement Begin(SimWorld world, ContentDatabase content, ParcelGrid grid, BiomeTable biomes,
                                       string name, int people, int parcelX, int parcelZ, Genome genome, int roofs, RecordId cause)
        {
            DriveRules rules = DriveRules.FromContent(content);
            MaterialTable materials = MaterialTable.FromContent(content, biomes);

            int hx = parcelX * ParcelGrid.Size + ParcelGrid.Size / 2;
            int hz = parcelZ * ParcelGrid.Size + ParcelGrid.Size / 2;
            var hearth = new Int3(hx, grid.GroundAt(hx, hz) + 1, hz);
            int b = world.Island != null ? world.Island.BiomeAt(hx, hz) : -1;

            Settlement settlement = Settlement.Found(name, hearth, b >= 0 ? biomes.At(b) : null, people, rules,
                                                     world.Clock.Tick, world.Annals, cause);
            settlement.ShelterCapacity = roofs;
            settlement.Stock = new MaterialStock(materials);
            settlement.Catchment = world.Island != null && world.Island.Deposits != null
                ? Catchment.FromDeposits(world.Island, biomes, materials, world.Island.Deposits, hx, hz)
                : Catchment.Survey(world.Island, biomes, materials, hx, hz);

            // People arrive with food and nothing else: a season and a half of
            // it, which is what stands between a hard first year and a
            // settlement that starves while it is still building its first
            // house (S1E).
            settlement.Food = people * Subsistence.MealsADay * 45;
            settlement.Genome = genome ?? new Genome(GeneTable.FromContent(content));
            settlement.AttachIntents(new IntentBus(IntentKindTable.FromContent(content, rules.Needs), rules.Needs.Count));

            // S2N: the founders as families.
            HouseholdRules households = HouseholdRules.FromContent(content);
            if (households != null) Households.Found(settlement, households, world.Clock.Tick, world.Annals);
            settlement.Tasks = new TaskBoard(TaskKindTable.FromContent(content), settlement, rules, world.Streams);
            world.Settlements.Add(settlement);
            return settlement;
        }

        /// <summary>
        /// The stratum-1 systems, in tick order: needs, then what the
        /// settlement decides to ask for, then where it puts it, then who does
        /// the work of gathering and building it.
        /// </summary>
        public static void AddSystems(SimWorld world, ContentDatabase content, ParcelGrid grid,
                                      ConstraintFields fields, BiomeTable biomes)
        {
            DriveRules rules = DriveRules.FromContent(content);
            MaterialTable materials = MaterialTable.FromContent(content, biomes);
            var genes = GeneTable.FromContent(content);
            TileSet tiles = TileSet.FromContent(content, materials);
            Palette palette = Palette.FromContent(content);

            world.Add(new DriveSystem(rules))
                 .Add(new Subsistence(rules))
                 .Add(new StoreSystem(FoodRules.FromContent(content)))
                 .Add(new IntentSystem())
                 .Add(new TownSystem(TownRules.FromContent(content), content, grid, biomes))
                 .Add(new SiteSystem(GrammarTable.FromContent(content, genes),
                                     SitingTable.FromContent(content, genes, IntentKindTable.FromContent(content, rules.Needs)),
                                     tiles, materials, palette, grid, fields,
                                     NegotiationTable.FromContent(content, genes)))
                 .Add(new DepositSystem(grid))
                 .Add(new SupportSystem(TerrainBrush.SolidTable(content, world.VoxelTypes), materials,
                                        DetailModelTable.FromContent(content), grid))
                 .Add(new TaskSystem(new Construction(world.Voxels, materials, world.VoxelTypes, tiles, palette,
                                                      deposits: world.Island != null ? world.Island.Deposits : null,
                                                      ticksPerDay: world.Clock.TicksPerDay)
                 {
                     Island = world.Island,
                     Details = world.Details,
                     Models = DetailModelTable.FromContent(content),
                     GroundTable = TerrainBrush.SolidTable(content, world.VoxelTypes),
                 }, grid, HaulRules.FromContent(content)))
                 .Add(new HaulingSystem(HaulRules.FromContent(content), FoodRules.FromContent(content),
                                        DetailModelTable.FromContent(content), grid))
                 .Add(new FarmSystem(FarmRules.FromContent(content), CropTable.FromContent(content, genes), grid, fields,
                                     DetailModelTable.FromContent(content)))
                 .Add(new MovementSystem(grid, rules, PastimeTable.FromContent(content)));
        }

        /// <summary>The parcel grid and the fields an island needs before anybody can settle it.</summary>
        public static ParcelGrid Survey(SimWorld world, ContentDatabase content, BiomeTable biomes, out ConstraintFields fields)
        {
            VoxelTypes types = world.VoxelTypes;
            bool[] solid = TerrainBrush.SolidTable(content, types);
            var wet = new bool[types.Count];
            ushort water;
            if (types.TryGetId(Symbol.For("voxel.water"), out water)) wet[water] = true;

            ParcelGrid grid = ParcelGrid.Build(world.Voxels.Store, solid, wet);
            fields = ConstraintFields.Compute(world.Island, grid, biomes);
            return grid;
        }
    }
}
