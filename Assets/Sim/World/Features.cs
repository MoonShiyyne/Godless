using System.Collections.Generic;
using Godless.Sim.Content;
using Godless.Sim.Core;
using Godless.Sim.Voxels;

namespace Godless.Sim.World
{
    /// <summary>How a feature stands in the world, which decides how it is placed and how it comes apart.</summary>
    public enum FeatureShape
    {
        /// <summary>A trunk with a crown. Felled whole; the logs are what is gathered.</summary>
        Tree,
        /// <summary>A short stand of grass or reed. Cut whole.</summary>
        Tuft,
        /// <summary>A lump of rock on the ground. Broken up a voxel at a time, top down.</summary>
        Boulder,
        /// <summary>A patch of the ground itself. Dug a voxel at a time, top down, into a pit.</summary>
        Bed,
    }

    /// <summary>
    /// One kind of deposit: what it looks like, where it grows, what it
    /// yields and whether it comes back. S2F.
    /// </summary>
    public sealed class FeatureKind
    {
        public Symbol Id { get; internal set; }
        public string Name { get; internal set; }
        public string Tell { get; internal set; }
        public FeatureShape Shape { get; internal set; }

        /// <summary>The trunk, tuft, boulder or bed voxel.</summary>
        public ushort Voxel { get; internal set; }

        /// <summary>A tree's crown voxel. Air for anything else.</summary>
        public ushort Crown { get; internal set; }
        public int CrownRadius { get; internal set; }

        /// <summary>A pine's crown rather than an oak's.</summary>
        public bool ConeCrown { get; internal set; }

        /// <summary>Trunk or tuft height; boulder or bed radius.</summary>
        public int SizeLeast { get; internal set; }
        public int SizeMost { get; internal set; }

        /// <summary>The building material it turns into, as a voxel symbol.</summary>
        public Symbol Yields { get; internal set; }
        public int PerVoxel { get; internal set; }

        public int Spacing { get; internal set; }

        /// <summary>Chance a spacing cell holds one, per biome index.</summary>
        internal double[] Density;

        public double DensityIn(int biome) { return biome >= 0 && biome < Density.Length ? Density[biome] : 0.0; }

        /// <summary>Days until a spent one grows back. Zero never.</summary>
        public int RegrowDays { get; internal set; }

        /// <summary>Only this close above the water, or -1 anywhere.</summary>
        public int MaxAboveWater { get; internal set; }

        public bool Renews { get { return RegrowDays > 0; } }
    }

    /// <summary>Every deposit kind content declares, in stable id order.</summary>
    public sealed class FeatureTable
    {
        readonly FeatureKind[] _kinds;
        readonly List<string> _problems;

        FeatureTable(FeatureKind[] kinds, List<string> problems) { _kinds = kinds; _problems = problems; }

        public int Count { get { return _kinds.Length; } }
        public FeatureKind this[int index] { get { return _kinds[index]; } }
        public IReadOnlyList<FeatureKind> All { get { return _kinds; } }
        public IReadOnlyList<string> Problems { get { return _problems; } }

        public static FeatureTable FromContent(ContentDatabase content, BiomeTable biomes, VoxelTypes types)
        {
            var problems = new List<string>();
            var kinds = new List<FeatureKind>();

            foreach (string id in content.Ids("feature"))
            {
                JsonValue doc = content.Get("feature", id);
                string tell = doc["tell"].AsString("").Trim();
                if (tell.Length == 0) { problems.Add("feature '" + id + "' declares no tell."); continue; }

                FeatureShape shape;
                switch (doc["shape"].AsString(""))
                {
                    case "tree": shape = FeatureShape.Tree; break;
                    case "tuft": shape = FeatureShape.Tuft; break;
                    case "boulder": shape = FeatureShape.Boulder; break;
                    case "bed": shape = FeatureShape.Bed; break;
                    default:
                        problems.Add("feature '" + id + "' has shape '" + doc["shape"].AsString("") + "'; it must be tree, tuft, boulder or bed.");
                        continue;
                }

                ushort voxel;
                if (!types.TryGetId(Symbol.For("voxel." + doc["voxel"].AsString("")), out voxel))
                { problems.Add("feature '" + id + "' is made of voxel '" + doc["voxel"].AsString("") + "', which no content declares."); continue; }

                ushort crown = VoxelTypes.AirId;
                if (shape == FeatureShape.Tree && !types.TryGetId(Symbol.For("voxel." + doc["crown"].AsString("")), out crown))
                { problems.Add("tree '" + id + "' has crown '" + doc["crown"].AsString("") + "', which no content declares."); continue; }

                string material = doc["yields"]["material"].AsString("");
                if (!content.Contains("voxel", material) || content.Get("voxel", material)["gather"].IsNull)
                { problems.Add("feature '" + id + "' yields '" + material + "', which is not a material anyone can build with."); continue; }

                var kind = new FeatureKind
                {
                    Id = Symbol.For("feature." + id),
                    Name = id,
                    Tell = tell,
                    Shape = shape,
                    Voxel = voxel,
                    Crown = crown,
                    CrownRadius = doc["crownRadius"].AsInt32(2),
                    ConeCrown = doc["crownShape"].AsString("round") == "cone",
                    SizeLeast = doc["size"]["least"].AsInt32(1),
                    SizeMost = doc["size"]["most"].AsInt32(1),
                    Yields = Symbol.For("voxel." + material),
                    PerVoxel = doc["yields"]["perVoxel"].AsInt32(1),
                    Spacing = doc["spacing"].AsInt32(5),
                    RegrowDays = doc["regrowDays"].AsInt32(0),
                    MaxAboveWater = doc["maxAboveWater"].AsInt32(-1),
                    Density = new double[biomes.Count],
                };
                if (kind.SizeMost < kind.SizeLeast) kind.SizeMost = kind.SizeLeast;
                if (kind.Spacing < 2) kind.Spacing = 2;
                if (kind.PerVoxel < 1) kind.PerVoxel = 1;

                JsonValue density = doc["density"];
                foreach (string biome in density.Keys)
                {
                    int b = biomes.IndexOf(Symbol.For("biome." + biome));
                    // A map that admits a subset of biomes simply has no use for
                    // the rest; only a name no content declares is a mistake.
                    if (b < 0)
                    {
                        if (!content.Contains("biome", biome))
                            problems.Add("feature '" + id + "' grows in biome '" + biome + "', which no content declares.");
                        continue;
                    }
                    kind.Density[b] = SimMath.Clamp01(density[biome].AsDouble(0.0));
                }
                kinds.Add(kind);
            }

            kinds.Sort((a, b) => a.Id.CompareTo(b.Id));

            // The biome promises a material; some feature must actually put it
            // on the ground there, or the promise is a stock that never exists.
            foreach (Biome biome in biomes.All)
            {
                int b = biomes.IndexOf(biome.Id);
                for (int i = 0; i < biome.Offers.Count; i++)
                {
                    bool placed = false;
                    foreach (FeatureKind k in kinds) if (k.Yields == biome.Offers[i] && k.DensityIn(b) > 0.0) placed = true;
                    if (!placed)
                        problems.Add("biome '" + biome.Name + "' offers '" + biome.MaterialNames[i]
                                     + "', but no feature grows it there.");
                }
            }

            return new FeatureTable(kinds.ToArray(), problems);
        }

        /// <summary>Which kind yields a material, or -1.</summary>
        public int IndexOf(Symbol id)
        {
            for (int i = 0; i < _kinds.Length; i++) if (_kinds[i].Id == id) return i;
            return -1;
        }
    }

    /// <summary>
    /// Puts the deposits on a generated island. S2F.
    ///
    /// One candidate per spacing cell, at a hashed offset inside it, taken at
    /// the biome's density: trees stand apart, and the same seed grows the same
    /// wood. Runs after hydrology so nothing grows in a river, and writes
    /// through SetRaw like the rest of worldgen — there is no history yet.
    /// </summary>
    public static class FeaturePlanter
    {
        public const string StreamId = "world.features";

        public static DepositMap Plant(IslandMap island, ChunkStore store, FeatureTable kinds, ulong worldSeed)
        {
            var deposits = new DepositMap(kinds);
            ulong seed = StableHash.Combine(worldSeed, StableHash.OfString(StreamId));

            // Candidates from every kind in one fixed order, so a tree and a
            // boulder wanting the same column resolve the same way on every
            // machine: whoever is first in that order stands.
            var taken = new bool[ChunkStore.SizeX * ChunkStore.SizeZ];
            var candidates = new List<long>();

            for (int k = 0; k < kinds.Count; k++)
            {
                FeatureKind kind = kinds[k];
                ulong kindSeed = StableHash.Combine(seed, kind.Id.Hash);
                int s = kind.Spacing;
                for (int cz = 0; cz < ChunkStore.SizeZ / s; cz++)
                    for (int cx = 0; cx < ChunkStore.SizeX / s; cx++)
                    {
                        ulong h = StableHash.Combine(kindSeed, (ulong)(cz * 65536 + cx));
                        int x = cx * s + (int)(h % (ulong)s);
                        int z = cz * s + (int)((h >> 16) % (ulong)s);
                        if (x < 8 || z < 8 || x >= ChunkStore.SizeX - 8 || z >= ChunkStore.SizeZ - 8) continue;
                        if (!island.IsLand(x, z) || island.WaterLevelAt(x, z) > 0) continue;

                        int biome = island.BiomeAt(x, z);
                        double chance = kind.DensityIn(biome);
                        if (chance <= 0.0) continue;
                        if (((h >> 32) % 100000) / 100000.0 >= chance) continue;
                        if (kind.MaxAboveWater >= 0 && island.HeightAboveWaterAt(x, z) > kind.MaxAboveWater) continue;

                        // Ground first — beds and boulders need a clear footprint
                        // and growth can stand anywhere around them — then by
                        // position, then kind.
                        long rank = kind.Shape == FeatureShape.Bed ? 0 : kind.Shape == FeatureShape.Boulder ? 1 : 2;
                        candidates.Add((rank << 40) | ((long)(z * ChunkStore.SizeX + x) << 8) | (long)k);
                    }
            }
            candidates.Sort();

            foreach (long c in candidates)
            {
                int k = (int)(c & 0xFF);
                int column = (int)((c >> 8) & 0xFFFFFFFF);
                int x = column % ChunkStore.SizeX, z = column / ChunkStore.SizeX;
                FeatureKind kind = kinds[k];

                // A boulder or a bed needs its whole footprint; a tree or a tuft its own column.
                int reach = kind.Shape == FeatureShape.Boulder || kind.Shape == FeatureShape.Bed ? kind.SizeMost : 0;
                if (!Free(taken, x, z, reach)) continue;

                ulong h = StableHash.Combine(StableHash.Combine(seed, kind.Id.Hash), (ulong)column);
                int size = kind.SizeLeast + (int)(h % (ulong)(kind.SizeMost - kind.SizeLeast + 1));
                int ground = island.HeightAt(x, z);   // first air voxel

                int voxels = Place(store, island, kind, x, ground, z, size);
                if (voxels <= 0) continue;
                Mark(taken, x, z, reach);
                deposits.Add(k, x, ground, z, size, voxels);
            }

            deposits.Seal();
            return deposits;
        }

        static bool Free(bool[] taken, int x, int z, int reach)
        {
            for (int dz = -reach; dz <= reach; dz++)
                for (int dx = -reach; dx <= reach; dx++)
                    if (taken[(z + dz) * ChunkStore.SizeX + x + dx]) return false;
            return true;
        }

        static void Mark(bool[] taken, int x, int z, int reach)
        {
            for (int dz = -reach; dz <= reach; dz++)
                for (int dx = -reach; dx <= reach; dx++)
                    taken[(z + dz) * ChunkStore.SizeX + x + dx] = true;
        }

        /// <summary>Writes one feature's voxels. Returns how many yield material.</summary>
        static int Place(ChunkStore store, IslandMap island, FeatureKind kind, int x, int ground, int z, int size)
        {
            var cells = new List<Int3>();
            DepositMap.Cells(kind, x, ground, z, size, cells, yielding: false);
            int yielding = 0;
            foreach (Int3 at in cells)
            {
                if (at.Y <= 0 || at.Y >= ChunkStore.SizeY - 1) continue;
                ushort want = DepositMap.VoxelAt(kind, x, ground, z, size, at);
                ushort here = store.Get(at.X, at.Y, at.Z);

                // Growing things fill air only. Ground features replace the
                // surface they sit in, but never water.
                // A trunk may push through a neighbour's crown of the same wood.
                bool growth = kind.Shape == FeatureShape.Tree || kind.Shape == FeatureShape.Tuft;
                bool throughCrown = want == kind.Voxel && kind.Crown != VoxelTypes.AirId && here == kind.Crown;
                if (growth && here != VoxelTypes.AirId && !throughCrown) continue;
                // Rock never sits on water: a boulder's rim over a river bank
                // would be a lid on the river.
                if (kind.Shape == FeatureShape.Boulder && (here != VoxelTypes.AirId || !island.IsLand(at.X, at.Z)
                                                           || island.WaterLevelAt(at.X, at.Z) > 0)) continue;
                if (kind.Shape == FeatureShape.Bed && (here == VoxelTypes.AirId || !island.IsLand(at.X, at.Z)
                                                       || island.WaterLevelAt(at.X, at.Z) > 0)) continue;

                store.SetRaw(at.X, at.Y, at.Z, want);
                if (want == kind.Voxel) yielding++;
            }
            return yielding;
        }
    }
}
