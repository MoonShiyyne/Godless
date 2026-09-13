using System.Collections.Generic;
using Godless.Sim.Core;
using Godless.Sim.Voxels;
using Godless.Sim.World;

namespace Godless.Sim.Tests
{
    /// <summary>
    /// Generated islands, shared across the whole test run.
    ///
    /// Generating an island is a second or more, and most tests want the same
    /// one — seed 7, the shipped content — so the suite was generating it
    /// dozens of times over and fighting itself for the CPU. Each distinct
    /// island is made once here and every test gets its own deep copy: the
    /// voxels and the deposits are copied, so nothing one test does reaches
    /// another. Tests that are about generation itself (determinism, content
    /// changes) must keep calling IslandGenerator directly.
    /// </summary>
    static class TestIslands
    {
        sealed class Made { public ChunkStore Store; public IslandMap Map; }

        static readonly Dictionary<string, Made> Cache = new Dictionary<string, Made>();
        static readonly object Gate = new object();

        public static IslandMap Generate(ChunkStore into, StreamRegistry streams, BiomeTable biomes, VoxelTypes types,
                                         WorldPreset preset = null, FeatureTable features = null)
        {
            string key = streams.WorldSeed + "|" + (preset == null ? "-" : preset.Name) + "|" + biomes.Count
                       + "|" + (features == null ? 0 : features.Count) + "|" + types.Count;
            Made made;
            lock (Gate)
            {
                if (!Cache.TryGetValue(key, out made))
                {
                    var store = new ChunkStore();
                    IslandMap map = IslandGenerator.Generate(store, new StreamRegistry(streams.WorldSeed), biomes, types, preset, features);
                    made = new Made { Store = store, Map = map };
                    Cache[key] = made;
                }
            }
            into.CopyFrom(made.Store);
            return made.Map.Clone();
        }
    }
}
