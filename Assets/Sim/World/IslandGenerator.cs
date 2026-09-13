using Godless.Sim.Core;
using Godless.Sim.Voxels;

namespace Godless.Sim.World
{
    /// <summary>
    /// The generated island: its surface height, its biome and its moisture,
    /// one entry per column. Systems that plan on the coarse grid read this
    /// rather than probing voxels.
    /// </summary>
    public sealed class IslandMap
    {
        /// <summary>Where the sea stands on a map that does not say otherwise.</summary>
        public const int DefaultSeaLevel = 42;

        /// <summary>
        /// Where the sea stands on this island. A map sets it (S09): a delta
        /// sits at a different waterline from a massif, and everything that
        /// asks what is land has to ask this island rather than a constant.
        /// </summary>
        public int SeaLevel { get; internal set; } = DefaultSeaLevel;

        readonly byte[] _height = new byte[ChunkStore.SizeX * ChunkStore.SizeZ];
        readonly byte[] _biome = new byte[ChunkStore.SizeX * ChunkStore.SizeZ];
        readonly byte[] _moisture = new byte[ChunkStore.SizeX * ChunkStore.SizeZ];

        // S0B. Zero until hydrology runs.
        readonly byte[] _water = new byte[ChunkStore.SizeX * ChunkStore.SizeZ];
        readonly byte[] _aboveWater = new byte[ChunkStore.SizeX * ChunkStore.SizeZ];
        readonly bool[] _river = new bool[ChunkStore.SizeX * ChunkStore.SizeZ];

        internal void Set(int x, int z, int height, int biomeIndex, int moisture)
        {
            int i = z * ChunkStore.SizeX + x;
            _height[i] = (byte)height;
            _biome[i] = (byte)(biomeIndex + 1); // 0 means open sea
            _moisture[i] = (byte)moisture;
        }

        /// <summary>Solid below this height. Under a river, the riverbed.</summary>
        public int HeightAt(int x, int z) { return _height[z * ChunkStore.SizeX + x]; }
        public int MoistureAt(int x, int z) { return _moisture[z * ChunkStore.SizeX + x]; }

        /// <summary>Biome index into the table, or -1 for open sea.</summary>
        public int BiomeAt(int x, int z) { return _biome[z * ChunkStore.SizeX + x] - 1; }

        public bool IsLand(int x, int z) { return HeightAt(x, z) > SeaLevel; }

        /// <summary>The map this island was made from, for anything that needs its numbers again.</summary>
        public WorldPreset Map { get; internal set; }

        /// <summary>
        /// What grows and lies on the island to be gathered (S2F), or null on an
        /// island generated bare. Whether it has any is part of what the island
        /// is: a save records it.
        /// </summary>
        public DepositMap Deposits { get; internal set; }

        /// <summary>Water stands here up to, not including, this height. Zero where the column is dry.</summary>
        public int WaterLevelAt(int x, int z) { return _water[z * ChunkStore.SizeX + x]; }

        public bool IsRiver(int x, int z) { return _river[z * ChunkStore.SizeX + x]; }

        /// <summary>Standing water on land that is not a river: a lake.</summary>
        public bool IsLake(int x, int z) { return IsLand(x, z) && WaterLevelAt(x, z) > 0 && !IsRiver(x, z); }

        /// <summary>
        /// How far this column stands above the water it drains to — its
        /// depth to the water table, and how high a flood has to rise to
        /// reach it. Zero on water and on low ground that already floods.
        /// </summary>
        public int HeightAboveWaterAt(int x, int z) { return _aboveWater[z * ChunkStore.SizeX + x]; }

        internal void SetWater(int[] ground, int[] level, int[] above, bool[] river)
        {
            for (int i = 0; i < _height.Length; i++)
            {
                _height[i] = (byte)ground[i];
                _water[i] = (byte)level[i];
                _aboveWater[i] = (byte)(above[i] > 255 ? 255 : above[i]);
                _river[i] = river[i];
            }
        }

        public ulong Digest()
        {
            var d = new Digest();
            for (int i = 0; i < _height.Length; i++)
            {
                d.Add(_height[i]);
                d.Add(_biome[i]);
                d.Add(_moisture[i]);
                d.Add(_water[i]);
                d.Add(_aboveWater[i]);
            }
            return d.Value;
        }
    }

    /// <summary>
    /// Builds one island from a seed. S09.
    ///
    /// Bounded and deliberately so: Part 23 rules out infinite streaming, and
    /// "one island, one civilization, one history" is the frame the whole
    /// design rests on. A radial falloff makes the edge sea rather than a
    /// wall, so the island is finite because the world is, not because the
    /// array ended.
    ///
    /// Everything here is integer or SimMath, driven by one seeded stream, so
    /// two players trading a seed get the same island down to the voxel.
    /// </summary>
    public static class IslandGenerator
    {
        public const string StreamId = "world.island";

        /// <param name="map">
        /// Which map this is (S09 as content): the shape of the land, the
        /// wetness of the air and the biomes it admits. Null is the plain
        /// island the generator made before maps existed.
        /// </param>
        /// <param name="features">
        /// What to grow and lay on the land once it is shaped (S2F). Null makes a
        /// bare island, which is what everything before S2F was built and
        /// tested against.
        /// </param>
        public static IslandMap Generate(ChunkStore store, StreamRegistry streams,
                                         BiomeTable biomes, VoxelTypes voxelTypes, WorldPreset map = null,
                                         FeatureTable features = null)
        {
            WorldPreset preset = map ?? WorldPreset.Default();
            ulong seed = StableHash.Combine(streams.WorldSeed, StableHash.OfString(StreamId));
            ulong heightSeed = StableHash.Combine(seed, StableHash.OfString("height"));
            ulong moistureSeed = StableHash.Combine(seed, StableHash.OfString("moisture"));
            ulong roughSeed = StableHash.Combine(seed, StableHash.OfString("rough"));

            var island = new IslandMap { SeaLevel = preset.SeaLevel, Map = preset };

            ushort water = voxelTypes.IdOf(Symbol.For("voxel.water"));

            // Pass one: the raw shape, and its peak.
            //
            // The peak matters because relief is a property of an island, not
            // of a lucky seed. Without normalising, some seeds produce a flat
            // shelf with no ground above the highland threshold, so the world
            // has one usable biome and G1's "change the biome" test has
            // nothing to change. Found by the harness at 200 seeds after 20
            // seeds passed clean, which is the whole argument for the harness.
            var shaped = new double[ChunkStore.SizeX * ChunkStore.SizeZ];
            double peak = 0.0;

            for (int z = 0; z < ChunkStore.SizeZ; z++)
                for (int x = 0; x < ChunkStore.SizeX; x++)
                {
                    double continent = Noise.Fractal(heightSeed, x, z, 5, 1.0 / preset.ContinentScale);
                    double rough = Noise.Fractal(roughSeed, x, z, 4, 1.0 / preset.RoughScale);
                    double falloff = preset.Falloff(x, z, ChunkStore.SizeX, ChunkStore.SizeZ);

                    double v = SimMath.Clamp01(continent * (1.0 - preset.RoughWeight) + rough * preset.RoughWeight) * falloff;
                    v = v * v * (3.0 - 2.0 * v);

                    shaped[z * ChunkStore.SizeX + x] = v;
                    if (v > peak) peak = v;
                }

            // Normalise against the peak, then bend the curve rather than
            // scaling it. A linear rescale lifts the whole island together
            // and turns a flat seed into a mountain range; the cubic term
            // leaves the lowlands low and pulls only the core upward, which
            // is what relief actually looks like. Highland is then a summit
            // on every seed instead of a coin flip, and still a minority of
            // the land.
            double inversePeak = peak > 0.001 ? 1.0 / peak : 0.0;

            // Pass two: heights, biomes, voxels.
            for (int z = 0; z < ChunkStore.SizeZ; z++)
                for (int x = 0; x < ChunkStore.SizeX; x++)
                {
                    double t = SimMath.Clamp01(shaped[z * ChunkStore.SizeX + x] * inversePeak);
                    double v = preset.Peak * (preset.Linear * t + preset.Cubic * t * t * t);

                    int height = (int)SimMath.Round(preset.Base + v * preset.Relief);

                    // A stepped map terraces: mesas and benches instead of
                    // slopes, which is a landform and not a rendering trick.
                    if (preset.Step > 1 && height > preset.SeaLevel)
                    {
                        int above = height - preset.SeaLevel;
                        height = preset.SeaLevel + (above / preset.Step) * preset.Step;
                    }
                    if (height < 1) height = 1;
                    if (height > ChunkStore.SizeY - 2) height = ChunkStore.SizeY - 2;

                    int moisture = (int)SimMath.Round(
                        SimMath.Clamp01(Noise.Fractal(moistureSeed, x, z, 4, 1.0 / preset.MoistureScale)
                                        + preset.MoistureBias) * 100.0);

                    Biome biome = biomes.Select(height, moisture);
                    int biomeIndex = biome == null ? -1 : biomes.IndexOf(biome.Id);
                    island.Set(x, z, height, biomeIndex, moisture);

                    FillColumn(store, voxelTypes, biome, x, z, height, water, preset.SeaLevel);
                }

            // S0B: rivers, lakes and the water table, from the heights above.
            Hydrology.Apply(island, store, biomes, water, preset);

            // S2F: the woods, the reed beds and the rock, on the finished land.
            if (features != null && features.Count > 0)
                island.Deposits = FeaturePlanter.Plant(island, store, features, streams.WorldSeed);
            return island;
        }

        static void FillColumn(ChunkStore store, VoxelTypes types, Biome biome,
                               int x, int z, int height, ushort water, int seaLevel)
        {
            // Worldgen is one of the two legitimate callers of SetRaw: this
            // runs before history starts, so there is no event to record and
            // no delta worth keeping. Everything after this point goes
            // through VoxelWorld.Set and carries its cause (L3).
            ushort surface = biome == null ? water : types.IdOf(biome.Surface);
            ushort subsurface = biome == null ? water : types.IdOf(biome.Subsurface);

            int soilDepth = 4;
            for (int y = 0; y < height; y++)
            {
                ushort type = (y >= height - soilDepth) ? surface : subsurface;
                if (biome == null) type = types.IdOf(Symbol.For("voxel.granite"));
                store.SetRaw(x, y, z, type);
            }

            // Sea fills everything below the waterline that the land did not.
            for (int y = height; y <= seaLevel; y++)
                store.SetRaw(x, y, z, water);
        }
    }
}
