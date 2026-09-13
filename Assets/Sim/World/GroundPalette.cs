using Godless.Sim.Content;
using Godless.Sim.Core;
using Godless.Sim.Voxels;

namespace Godless.Sim.World
{
    /// <summary>
    /// What the ground is made of at a given place and height.
    ///
    /// Worldgen already knows this — it fills every column with its biome's
    /// surface over its subsurface — but the god's brush did not, so raising
    /// land built a grey stone mound wherever it was used, on a beach or in a
    /// forest. That is the wrong tell twice over: the new ground should look
    /// like ground, and it should look like *this* ground.
    ///
    /// The rule is the one the generator uses, applied to the height the
    /// column has after the stroke rather than before. Raise a beach far
    /// enough and its top stops being sand and starts being the highland's
    /// stone, because at that height, in that moisture, that is what the
    /// biome table says the land is. The player learns the biome bands by
    /// pushing a hill through them.
    /// </summary>
    public sealed class GroundPalette
    {
        readonly IslandMap _map;
        readonly BiomeTable _biomes;
        readonly ushort[] _surface;      // per biome index
        readonly ushort[] _subsurface;
        readonly ushort _fallback;
        readonly ushort _seabed;

        /// <summary>Voxels of the top of a column that are surface rather than subsurface. Matches worldgen.</summary>
        public const int SoilDepth = 4;

        GroundPalette(IslandMap map, BiomeTable biomes, ushort[] surface, ushort[] subsurface,
                      ushort fallback, ushort seabed)
        {
            _map = map; _biomes = biomes;
            _surface = surface; _subsurface = subsurface;
            _fallback = fallback; _seabed = seabed;
        }

        public static GroundPalette From(IslandMap map, BiomeTable biomes, VoxelTypes types)
        {
            var surface = new ushort[biomes.Count];
            var subsurface = new ushort[biomes.Count];
            for (int i = 0; i < biomes.Count; i++)
            {
                Biome b = biomes.At(i);
                surface[i] = types.IdOf(b.Surface);
                subsurface[i] = types.IdOf(b.Subsurface);
            }

            // If content declares no granite there is no privileged fallback
            // to reach for, so the first biome's subsurface stands in (L5).
            ushort fallback;
            if (!types.TryGetId(Symbol.For("voxel.granite"), out fallback))
                fallback = subsurface.Length > 0 ? subsurface[0] : (ushort)0;

            ushort sand;
            if (!types.TryGetId(Symbol.For("voxel.sand"), out sand)) sand = fallback;

            return new GroundPalette(map, biomes, surface, subsurface, fallback, sand);
        }

        /// <summary>
        /// The material for the voxel at (x, y, z) in a column whose top will
        /// be <paramref name="top"/>. Below the waterline it is seabed, near
        /// the top it is the surface of the biome that height belongs to, and
        /// below that it is that biome's rock.
        /// </summary>
        public ushort At(int x, int y, int z, int top)
        {
            if (!ChunkStore.InBounds(x, 0, z)) return _fallback;

            // Ground pushed up from under the sea is seabed until it is land.
            if (top <= _map.SeaLevel) return _seabed;

            int moisture = _map.MoistureAt(x, z);
            bool exact;
            Biome biome = _biomes.Select(top, moisture, out exact);
            if (biome == null) return _fallback;

            int index = _biomes.IndexOf(biome.Id);
            if (index < 0) return _fallback;
            return y >= top - SoilDepth ? _surface[index] : _subsurface[index];
        }
    }
}
