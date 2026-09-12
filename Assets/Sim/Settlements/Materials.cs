using System.Collections.Generic;
using Godless.Sim.Content;
using Godless.Sim.Core;
using Godless.Sim.World;

namespace Godless.Sim.Settlements
{
    /// <summary>A voxel type people can gather and build with.</summary>
    public sealed class BuildingMaterial
    {
        /// <summary>The voxel type it is placed as. Also the key a stock counts it under.</summary>
        public Symbol Voxel { get; internal set; }
        public string Name { get; internal set; }

        /// <summary>Timber, stone, thatch, earth — what S19 substitutes within.</summary>
        public Symbol Class { get; internal set; }

        /// <summary>Voxels a tick of labour brings in where the land offers it in full.</summary>
        public double PerLabourTick { get; internal set; }
    }

    /// <summary>
    /// Every material that can be gathered, and how far people will carry it.
    /// S11.
    ///
    /// A material is a voxel type with a "gather" block. Where it can be
    /// gathered is the biomes' business — each lists what it offers (Part
    /// 05: "bind the palette to the biome") — so thatch in the lowlands and
    /// slate in the highlands is content, and the same genome in two biomes
    /// has two different stocks to build from.
    /// </summary>
    public sealed class MaterialTable
    {
        readonly BuildingMaterial[] _materials;
        readonly List<string> _problems;

        MaterialTable(BuildingMaterial[] materials, List<string> problems, int haul, int fullYield)
        {
            _materials = materials;
            _problems = problems;
            HaulRangeVoxels = haul;
            FullYieldColumns = fullYield;
        }

        public int Count { get { return _materials.Length; } }
        public BuildingMaterial this[int index] { get { return _materials[index]; } }
        public IReadOnlyList<BuildingMaterial> All { get { return _materials; } }
        public IReadOnlyList<string> Problems { get { return _problems; } }

        /// <summary>How far from the hearth people will carry material, in voxels.</summary>
        public int HaulRangeVoxels { get; private set; }

        /// <summary>Source columns in range at which a material gathers at its full rate.</summary>
        public int FullYieldColumns { get; private set; }

        public static MaterialTable FromContent(ContentDatabase content, BiomeTable biomes)
        {
            var problems = new List<string>();
            var loaded = new List<BuildingMaterial>();

            foreach (string id in content.Ids("voxel"))
            {
                JsonValue doc = content.Get("voxel", id);
                JsonValue gather = doc["gather"];
                if (gather.IsNull) continue;

                double rate = gather["perLabourTick"].AsDouble(0.0);
                if (!(rate > 0.0))
                {
                    problems.Add("voxel '" + id + "' can be gathered but at no rate; give gather.perLabourTick a positive value.");
                    continue;
                }
                loaded.Add(new BuildingMaterial
                {
                    Voxel = Symbol.For("voxel." + id),
                    Name = id,
                    Class = Symbol.For("class." + doc["class"].AsString("unclassified")),
                    PerLabourTick = rate,
                });
            }
            loaded.Sort((a, b) => a.Voxel.CompareTo(b.Voxel));

            // Every material a biome offers has to be something people can
            // actually gather, or the biome is promising a stock that never
            // arrives.
            foreach (Biome biome in biomes.All)
                for (int i = 0; i < biome.Offers.Count; i++)
                    if (loaded.Find(m => m.Voxel == biome.Offers[i]) == null)
                        problems.Add("biome '" + biome.Name + "' offers '" + biome.MaterialNames[i]
                                     + "', but no voxel of that name declares how to gather it.");

            int haul = 64, full = 1500;
            if (content.Contains("stock", "base"))
            {
                JsonValue stock = content.Get("stock", "base");
                haul = stock["haulRangeVoxels"].AsInt32(haul);
                full = stock["fullYieldColumns"].AsInt32(full);
            }
            if (full < 1) full = 1;

            return new MaterialTable(loaded.ToArray(), problems, haul, full);
        }

        public int IndexOf(Symbol voxel)
        {
            for (int i = 0; i < _materials.Length; i++) if (_materials[i].Voxel == voxel) return i;
            return -1;
        }

        public int IndexOf(string name) { return IndexOf(Symbol.For("voxel." + name)); }
    }

    /// <summary>
    /// What the land within hauling range of a hearth offers: per material,
    /// how many columns it can be gathered from, and so how fast. S11.
    /// </summary>
    public sealed class Catchment
    {
        readonly long[] _sources;
        readonly double[] _yield;

        Catchment(long[] sources, double[] yield, int haul, double food = 0.0)
        {
            _sources = sources;
            _yield = yield;
            HaulRangeVoxels = haul;
            FoodPerLabourTick = food;
        }

        public int HaulRangeVoxels { get; private set; }

        /// <summary>
        /// Meals a tick of foraging brings in here (S1E). Wooded, rainy land
        /// feeds people; bare highland and sand do not.
        /// </summary>
        public double FoodPerLabourTick { get; private set; }

        /// <summary>Land columns within range whose biome offers this material.</summary>
        public long Sources(int material) { return _sources[material]; }

        /// <summary>Voxels a tick of labour gathers here. Zero where the land offers none.</summary>
        public double YieldPerLabourTick(int material) { return _yield[material]; }

        public bool Offers(int material) { return _sources[material] > 0; }

        /// <summary>
        /// A catchment from source counts directly. For tests, and for the
        /// systems that will change what the land offers — depletion (S2F), a
        /// god seeding an ore vein.
        /// </summary>
        public static Catchment FromSources(MaterialTable materials, long[] sources, double foodPerLabourTick = 1.0)
        {
            if (sources.Length != materials.Count) throw new System.ArgumentException("one source count per material", nameof(sources));
            return new Catchment((long[])sources.Clone(), Yields(materials, sources), materials.HaulRangeVoxels, foodPerLabourTick);
        }

        static double[] Yields(MaterialTable materials, long[] sources)
        {
            var yield = new double[materials.Count];
            for (int m = 0; m < materials.Count; m++)
            {
                double share = sources[m] >= materials.FullYieldColumns ? 1.0 : (double)sources[m] / materials.FullYieldColumns;
                yield[m] = materials[m].PerLabourTick * share;
            }
            return yield;
        }

        public static Catchment Survey(IslandMap map, BiomeTable biomes, MaterialTable materials, int hearthX, int hearthZ)
        {
            int r = materials.HaulRangeVoxels;
            long r2 = (long)r * r;
            var sources = new long[materials.Count];

            // Per biome, the material indices it offers, resolved once.
            var offered = new int[biomes.Count][];
            for (int b = 0; b < biomes.Count; b++)
            {
                var idx = new List<int>();
                foreach (Symbol voxel in biomes.At(b).Offers)
                {
                    int m = materials.IndexOf(voxel);
                    if (m >= 0) idx.Add(m);
                }
                offered[b] = idx.ToArray();
            }

            double food = 0.0;
            long columns = 0;
            for (int z = hearthZ - r; z <= hearthZ + r; z++)
                for (int x = hearthX - r; x <= hearthX + r; x++)
                {
                    if (x < 0 || z < 0 || x >= Voxels.ChunkStore.SizeX || z >= Voxels.ChunkStore.SizeZ) continue;
                    long dx = x - hearthX, dz = z - hearthZ;
                    if (dx * dx + dz * dz > r2 || !map.IsLand(x, z)) continue;
                    int b = map.BiomeAt(x, z);
                    if (b < 0) continue;
                    foreach (int m in offered[b]) sources[m]++;

                    // What the land itself feeds people: cover to forage under
                    // and rain to grow it. S1E.
                    Biome biome = biomes.At(b);
                    food += biome.TreeCoverPercent / 100.0 * 0.6 + SimMath.Clamp01(biome.RainfallMm / 1500.0) * 0.4;
                    columns++;
                }

            // A full catchment of the best land feeds a forager about three
            // meals a tick — four a day, so five foragers feed twenty and the
            // rest of the settlement can build. Thin land much less.
            double yieldPerTick = columns > 0 ? 3.0 * food / columns : 0.0;
            return new Catchment(sources, Yields(materials, sources), r, yieldPerTick);
        }
    }
}
