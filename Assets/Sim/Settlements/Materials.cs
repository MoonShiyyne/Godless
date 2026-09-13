using System.Collections.Generic;
using Godless.Sim.Annals;
using Godless.Sim.Content;
using Godless.Sim.Core;
using Godless.Sim.Deltas;
using Godless.Sim.Voxels;
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

        /// <summary>
        /// Distance from the fire, in voxels, at which a tick of gathering brings
        /// in half what it would at the fireside (S2F): the rest of the tick is
        /// the walk. What makes the far trees worth less than the near ones.
        /// </summary>
        public int TravelScaleVoxels { get; private set; } = 24;

        /// <summary>How far people walk for real deposits (S2F), in voxels from the fire.</summary>
        public int DepositRangeVoxels { get; private set; } = 64;

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

            int haul = 64, full = 1500, travel = 24, depositRange = -1;
            if (content.Contains("stock", "base"))
            {
                JsonValue stock = content.Get("stock", "base");
                haul = stock["haulRangeVoxels"].AsInt32(haul);
                full = stock["fullYieldColumns"].AsInt32(full);
                travel = stock["travelScaleVoxels"].AsInt32(travel);
                depositRange = stock["depositRangeVoxels"].AsInt32(haul);
            }
            if (full < 1) full = 1;
            if (travel < 1) travel = 1;

            var table = new MaterialTable(loaded.ToArray(), problems, haul, full);
            table.TravelScaleVoxels = travel;
            table.DepositRangeVoxels = depositRange > 0 ? depositRange : haul;
            return table;
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
        public static readonly Symbol WorkedKind = Symbol.For("deposit.worked");
        public static readonly Symbol ExhaustedKind = Symbol.For("deposit.exhausted");

        readonly long[] _sources;
        readonly double[] _yield;

        // S2F. Null on a bare island, where the catchment is the biome sum it
        // always was.
        DepositMap _deposits;
        MaterialTable _materials;
        int _hearthX, _hearthZ;
        List<int>[] _nearest;       // per material: features in range yielding it, nearest first
        int[] _cursor;              // per material: first of those with anything left
        double[] _carry;            // per material: fractions of a voxel gathered and not yet whole
        RecordId[] _worked;         // per material: the record every cut cites
        bool[] _exhausted;          // per material: whether the last in reach has been recorded
        List<int> _vegetation;      // trees and tufts in range, for foraging
        int _vegetationAtFounding;
        double _foodAtFounding;

        Catchment(long[] sources, double[] yield, int haul, double food = 0.0)
        {
            _sources = sources;
            _yield = yield;
            HaulRangeVoxels = haul;
            FoodPerLabourTick = food;
        }

        /// <summary>Whether this catchment reads real deposits (S2F) rather than a biome sum.</summary>
        public bool HasDeposits { get { return _deposits != null; } }

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

        /// <summary>The nearest feature with this material left in it, or -1. S2F only.</summary>
        public int NearestSource(int material)
        {
            if (_deposits == null) return -1;
            List<int> list = _nearest[material];
            int c = _cursor[material];
            while (c < list.Count && !_deposits.Available(list[c])) c++;
            _cursor[material] = c;
            return c < list.Count ? list[c] : -1;
        }

        /// <summary>
        /// A catchment over real deposits (S2F). The biome survey still sets the
        /// best the land could ever feed a forager; what is standing decides how
        /// much of that is left.
        /// </summary>
        public static Catchment FromDeposits(IslandMap map, BiomeTable biomes, MaterialTable materials,
                                             DepositMap deposits, int hearthX, int hearthZ)
        {
            Catchment c = Survey(map, biomes, materials, hearthX, hearthZ);
            c._deposits = deposits;
            c._materials = materials;
            c._hearthX = hearthX;
            c._hearthZ = hearthZ;
            c._nearest = new List<int>[materials.Count];
            c._cursor = new int[materials.Count];
            c._carry = new double[materials.Count];
            c._worked = new RecordId[materials.Count];
            c._exhausted = new bool[materials.Count];
            c._vegetation = new List<int>();
            for (int m = 0; m < materials.Count; m++) { c._nearest[m] = new List<int>(); c._worked[m] = RecordId.None; }

            foreach (int f in deposits.Within(hearthX, hearthZ, materials.DepositRangeVoxels))
            {
                FeatureKind kind = deposits.KindOf(f);
                int m = materials.IndexOf(kind.Yields);
                if (m >= 0) c._nearest[m].Add(f);
                if (kind.Shape == FeatureShape.Tree || kind.Shape == FeatureShape.Tuft) c._vegetation.Add(f);
            }

            c._foodAtFounding = c.FoodPerLabourTick;
            c._vegetationAtFounding = c.StandingVegetation();
            c.Refresh();
            return c;
        }

        /// <summary>
        /// Where a forager goes today: one of the nearest dozen standing trees
        /// or tufts, picked by the person and the day so the foragers spread
        /// out and move on (S2G). -1 when nothing stands, or on a bare island.
        /// </summary>
        public int ForageSpot(ulong person, long day)
        {
            if (_deposits == null) return -1;
            int seen = 0, pick = (int)(StableHash.Combine(person, (ulong)day) % 12UL);
            foreach (int f in _vegetation)
            {
                if (!_deposits.Standing(f)) continue;
                if (seen++ == pick) return f;
            }
            if (seen == 0) return -1;
            pick %= seen;
            seen = 0;
            foreach (int f in _vegetation)
                if (_deposits.Standing(f) && seen++ == pick) return f;
            return -1;
        }

        int StandingVegetation()
        {
            int v = 0;
            foreach (int f in _vegetation)
                if (_deposits.Standing(f)) v += _deposits.KindOf(f).Shape == FeatureShape.Tree ? 3 : 1;
            return v;
        }

        /// <summary>A tick of labour's worth at this distance from the fire: the rest is the walk.</summary>
        double Travel(int feature)
        {
            double dx = _deposits.X(feature) - _hearthX, dz = _deposits.Z(feature) - _hearthZ;
            double d = SimMath.Sqrt(dx * dx + dz * dz);
            return 1.0 / (1.0 + d / _materials.TravelScaleVoxels);
        }

        /// <summary>
        /// Re-reads what is left: what each material is worth a tick now, and
        /// what the cleared land still feeds a forager. Daily, and after
        /// anything grows back. A no-op on a bare island.
        /// </summary>
        public void Refresh()
        {
            if (_deposits == null) return;
            for (int m = 0; m < _materials.Count; m++)
            {
                _cursor[m] = 0;
                long left = 0;
                foreach (int f in _nearest[m]) left += _deposits.Remaining(f);
                _sources[m] = left;
                int nearest = NearestSource(m);
                _yield[m] = nearest < 0 ? 0.0 : _materials[m].PerLabourTick * Travel(nearest);
                if (nearest >= 0) _exhausted[m] = false;
            }

            // Foraging lives off what grows. A felled wood feeds a quarter of
            // what it did, which is the pressure that will ask for fields (S2I).
            double standing = _vegetationAtFounding > 0 ? (double)StandingVegetation() / _vegetationAtFounding : 0.0;
            FoodPerLabourTick = _foodAtFounding * (0.25 + 0.75 * SimMath.Clamp01(standing));
        }

        /// <summary>
        /// A tick of gathering against the real deposits (S2F): the nearest
        /// feature with anything left gives up the tick's worth, its voxels
        /// leave the world citing this settlement's work, and the yard gains
        /// what was carried. Returns the feature worked, or -1 when there is
        /// nothing left in reach.
        /// </summary>
        public int Harvest(int material, double labourTicks, MaterialStock stock, VoxelWorld world, long tick,
                           int ticksPerDay, Annalist annals, Symbol settlement, Int3 hearth, RecordId founded)
        {
            if (_deposits == null) { stock.Gather(material, labourTicks, this); return -1; }

            int feature = NearestSource(material);
            if (feature < 0) { Exhausted(material, tick, annals, settlement, hearth); return -1; }

            if (!_worked[material].Exists)
                _worked[material] = annals.Write(tick, WorkedKind, settlement, hearth, founded, 0, 0,
                                                 new[] { _materials[material].Voxel });

            double got = _carry[material] + labourTicks * _materials[material].PerLabourTick * Travel(feature);
            int whole = (int)got;
            _carry[material] = got - whole;

            int taken = 0;
            while (taken < whole)
            {
                int f = NearestSource(material);
                if (f < 0) { _carry[material] = 0.0; break; }
                taken += _deposits.Take(f, whole - taken, world, tick, _worked[material], ticksPerDay);
            }
            if (taken > 0) stock.Add(material, taken);

            // The yield follows the walk to whatever is nearest now.
            int next = NearestSource(material);
            _yield[material] = next < 0 ? 0.0 : _materials[material].PerLabourTick * Travel(next);
            if (next < 0) Exhausted(material, tick, annals, settlement, hearth);
            return feature;
        }

        void Exhausted(int material, long tick, Annalist annals, Symbol settlement, Int3 hearth)
        {
            _yield[material] = 0.0;
            if (_exhausted[material]) return;
            _exhausted[material] = true;
            annals.Write(tick, ExhaustedKind, settlement, hearth,
                         _worked[material].Exists ? _worked[material] : RecordId.None, 0, 0,
                         new[] { _materials[material].Voxel });
        }

        /// <summary>The record every cut of this material cites, or None before the first.</summary>
        public RecordId WorkRecord(int material) { return _worked != null ? _worked[material] : RecordId.None; }

        public ulong Digest()
        {
            var d = new Digest();
            for (int m = 0; m < _sources.Length; m++)
            {
                d.Add(_sources[m]);
                d.Add(System.BitConverter.DoubleToInt64Bits(_yield[m]));
                if (_carry != null) d.Add(System.BitConverter.DoubleToInt64Bits(_carry[m]));
            }
            d.Add(System.BitConverter.DoubleToInt64Bits(FoodPerLabourTick));
            return d.Value;
        }

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
