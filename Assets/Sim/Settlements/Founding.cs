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
    public static class Founding
    {
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
            DriveRules rules = DriveRules.FromContent(content);
            MaterialTable materials = MaterialTable.FromContent(content, biomes);

            int hx = parcelX * ParcelGrid.Size + ParcelGrid.Size / 2;
            int hz = parcelZ * ParcelGrid.Size + ParcelGrid.Size / 2;
            var hearth = new Int3(hx, grid.GroundAt(hx, hz) + 1, hz);
            int b = world.Island != null ? world.Island.BiomeAt(hx, hz) : -1;

            Settlement settlement = Settlement.Found(name, hearth, b >= 0 ? biomes.At(b) : null, people, rules,
                                                     world.Clock.Tick, world.Annals, RecordId.None);
            settlement.ShelterCapacity = roofs;
            settlement.Stock = new MaterialStock(materials);
            settlement.Catchment = Catchment.Survey(world.Island, biomes, materials, hx, hz);

            // People arrive with food and nothing else: a fortnight to find
            // their feet, which is the difference between a hard first season
            // and a settlement that starves before it can forage (S1E).
            settlement.Food = people * Subsistence.MealsADay * 14;
            settlement.Genome = genome ?? new Genome(GeneTable.FromContent(content));
            settlement.AttachIntents(new IntentBus(IntentKindTable.FromContent(content, rules.Needs), rules.Needs.Count));
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
                 .Add(new IntentSystem())
                 .Add(new SiteSystem(GrammarTable.FromContent(content, genes),
                                     SitingTable.FromContent(content, genes, IntentKindTable.FromContent(content, rules.Needs)),
                                     tiles, materials, palette, grid, fields))
                 .Add(new TaskSystem(new Construction(world.Voxels, materials, world.VoxelTypes, tiles, palette), grid));
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
