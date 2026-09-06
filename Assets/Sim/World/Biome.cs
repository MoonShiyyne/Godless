using System.Collections.Generic;
using Godless.Sim.Content;
using Godless.Sim.Core;

namespace Godless.Sim.World
{
    /// <summary>
    /// A biome, read from content. Nothing here is authored in code — law L5,
    /// and Part 19's warning besides: the moment a file in the repo says
    /// "Puebloan", the emergence claim is dead and you have built a style
    /// picker. These are constraint windows, not cultures.
    /// </summary>
    public sealed class Biome
    {
        public Symbol Id { get; private set; }
        public Symbol Surface { get; private set; }
        public Symbol Subsurface { get; private set; }

        public int RainfallMm { get; private set; }
        public int WinterSeverity { get; private set; }
        public int TreeCoverPercent { get; private set; }

        public int MinElevation { get; private set; }
        public int MaxElevation { get; private set; }
        public int MinMoisture { get; private set; }
        public int MaxMoisture { get; private set; }

        /// <summary>Materials the settlement can build with here (Part 05).</summary>
        public IReadOnlyList<Symbol> Materials { get; private set; }

        public bool Accepts(int elevation, int moisture)
        {
            return elevation >= MinElevation && elevation <= MaxElevation
                && moisture >= MinMoisture && moisture <= MaxMoisture;
        }

        public static Biome FromDocument(string id, JsonValue doc)
        {
            JsonValue select = doc["select"];

            var materials = new List<Symbol>();
            JsonValue list = doc["materials"];
            for (int i = 0; i < list.Count; i++)
            {
                string name = list[i].AsString(null);
                if (!string.IsNullOrEmpty(name)) materials.Add(Symbol.For("material." + name));
            }

            return new Biome
            {
                Id = Symbol.For("biome." + id),
                Surface = Symbol.For("voxel." + doc["surface"].AsString("soil")),
                Subsurface = Symbol.For("voxel." + doc["subsurface"].AsString("granite")),
                RainfallMm = doc["rainfallMm"].AsInt32(0),
                WinterSeverity = doc["winterSeverity"].AsInt32(0),
                TreeCoverPercent = doc["treeCoverPercent"].AsInt32(0),
                MinElevation = select["minElevation"].AsInt32(0),
                MaxElevation = select["maxElevation"].AsInt32(int.MaxValue),
                MinMoisture = select["minMoisture"].AsInt32(0),
                MaxMoisture = select["maxMoisture"].AsInt32(int.MaxValue),
                Materials = materials,
            };
        }
    }

    /// <summary>
    /// Every biome the loaded content declares, in id order.
    ///
    /// The order is what makes selection deterministic when two windows
    /// overlap: the first accepting biome wins, and "first" means sorted by
    /// id, never by which file the loader happened to read first.
    /// </summary>
    public sealed class BiomeTable
    {
        readonly List<Biome> _biomes = new List<Biome>();

        public static BiomeTable FromContent(ContentDatabase content)
        {
            var table = new BiomeTable();
            foreach (string id in content.Ids("biome"))
                table._biomes.Add(Biome.FromDocument(id, content.Get("biome", id)));
            return table;
        }

        public int Count { get { return _biomes.Count; } }
        public IReadOnlyList<Biome> All { get { return _biomes; } }

        public Biome At(int index) { return _biomes[index]; }

        /// <summary>The first biome accepting this column, or null if none does.</summary>
        public Biome Select(int elevation, int moisture)
        {
            for (int i = 0; i < _biomes.Count; i++)
                if (_biomes[i].Accepts(elevation, moisture)) return _biomes[i];
            return null;
        }

        public int IndexOf(Symbol biomeId)
        {
            for (int i = 0; i < _biomes.Count; i++) if (_biomes[i].Id == biomeId) return i;
            return -1;
        }

        /// <summary>Every voxel type any biome can place, for building the voxel table.</summary>
        public IReadOnlyList<Symbol> DeclaredVoxels()
        {
            var set = new SortedSet<ulong>();
            var symbols = new List<Symbol>();
            foreach (Biome b in _biomes)
            {
                if (set.Add(b.Surface.Hash)) symbols.Add(b.Surface);
                if (set.Add(b.Subsurface.Hash)) symbols.Add(b.Subsurface);
            }
            return symbols;
        }
    }
}
