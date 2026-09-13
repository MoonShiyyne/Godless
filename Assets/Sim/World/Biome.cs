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

        /// <summary>
        /// The same materials as the voxel types they are built from, which
        /// is what a settlement's stock counts (S11). Derived from the names in
        /// the file, never from a Symbol's printed name (L2).
        /// </summary>
        public IReadOnlyList<Symbol> Offers { get; private set; }

        /// <summary>The material names as the file spells them, for refusal messages.</summary>
        public IReadOnlyList<string> MaterialNames { get; private set; }

        public string Name { get; private set; }

        public bool Accepts(int elevation, int moisture)
        {
            return elevation >= MinElevation && elevation <= MaxElevation
                && moisture >= MinMoisture && moisture <= MaxMoisture;
        }

        public static Biome FromDocument(string id, JsonValue doc)
        {
            JsonValue select = doc["select"];

            var materials = new List<Symbol>();
            var offers = new List<Symbol>();
            var names = new List<string>();
            JsonValue list = doc["materials"];
            for (int i = 0; i < list.Count; i++)
            {
                string name = list[i].AsString(null);
                if (string.IsNullOrEmpty(name)) continue;
                materials.Add(Symbol.For("material." + name));
                offers.Add(Symbol.For("voxel." + name));
                names.Add(name);
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
                Offers = offers,
                MaterialNames = names,
                Name = id,
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

        /// <param name="only">
        /// Biomes this table may contain, by name — a map's own list (S09).
        /// Null or empty means every biome the content declares.
        /// </param>
        public static BiomeTable FromContent(ContentDatabase content, IReadOnlyList<string> only = null)
        {
            var table = new BiomeTable();
            foreach (string id in content.Ids("biome"))
            {
                if (only != null && only.Count > 0 && !Contains(only, id)) continue;
                table._biomes.Add(Biome.FromDocument(id, content.Get("biome", id)));
            }
            return table;
        }

        static bool Contains(IReadOnlyList<string> names, string id)
        {
            for (int i = 0; i < names.Count; i++) if (names[i] == id) return true;
            return false;
        }

        public int Count { get { return _biomes.Count; } }
        public IReadOnlyList<Biome> All { get { return _biomes; } }

        public Biome At(int index) { return _biomes[index]; }

        /// <summary>The first biome accepting this column, or null if none does.</summary>
        public Biome Select(int elevation, int moisture)
        {
            bool exact;
            return Select(elevation, moisture, out exact);
        }

        /// <summary>
        /// The first biome, in id order, whose window contains the column. A
        /// map that admits only some biomes can produce ground none of them
        /// asked for — a delta's table has no highland in it, and the seed can
        /// still throw up a hill — so the nearest window takes it rather than
        /// leaving the column unclaimed. <paramref name="exact"/> says which
        /// happened, and `sim content` reports the gaps per map.
        /// </summary>
        public Biome Select(int elevation, int moisture, out bool exact)
        {
            for (int i = 0; i < _biomes.Count; i++)
                if (_biomes[i].Accepts(elevation, moisture)) { exact = true; return _biomes[i]; }

            exact = false;
            Biome nearest = null;
            long best = long.MaxValue;
            for (int i = 0; i < _biomes.Count; i++)
            {
                Biome b = _biomes[i];
                long de = Outside(elevation, b.MinElevation, b.MaxElevation);
                long dm = Outside(moisture, b.MinMoisture, b.MaxMoisture);
                long distance = de * de + dm * dm;
                if (distance < best) { best = distance; nearest = b; }
            }
            return nearest;
        }

        static long Outside(int value, int low, int high)
        {
            if (value < low) return low - value;
            if (value > high) return value - high;
            return 0;
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
