using System.Collections.Generic;
using Godless.Sim.Content;

namespace Godless.Sim.World
{
    /// <summary>
    /// One place that turns a map's name into the two things worldgen needs:
    /// the preset that shapes the land, and the biome table that map admits.
    ///
    /// Those two must be chosen together or not at all. A map's biome subset
    /// decides the biome *indices*, and indices are what the island stores per
    /// column and what a save replays against — so a caller that took the
    /// preset from one map and the table from all of content would generate a
    /// world that loads back as a different one. Making that mistake
    /// unavailable is the whole job of this class.
    /// </summary>
    public sealed class WorldChoice
    {
        WorldChoice() { }

        /// <summary>The chosen map. Never null: an unnamed choice gets the built-in default.</summary>
        public WorldPreset Preset { get; private set; }

        /// <summary>Only the biomes this map admits, in the order the table gives them.</summary>
        public BiomeTable Biomes { get; private set; }

        /// <summary>Every map the content declares, for a listing or an error message.</summary>
        public WorldTable Maps { get; private set; }

        /// <summary>The name this choice round-trips as: the map's id, or empty for the default.</summary>
        public string Name { get { return Preset.IsDefault ? "" : Preset.Name; } }

        /// <summary>
        /// Picks a map by id. An empty name is the built-in island with every
        /// biome in content — what the world was before maps were data, and
        /// what the test suite and the batch harness still generate.
        /// </summary>
        public static WorldChoice Pick(ContentDatabase content, string name)
        {
            var choice = new WorldChoice();
            BiomeTable all = BiomeTable.FromContent(content);
            choice.Maps = WorldTable.FromContent(content, all);

            if (string.IsNullOrEmpty(name))
            {
                choice.Preset = WorldPreset.Default();
                choice.Biomes = all;
                return choice;
            }

            WorldPreset preset = choice.Maps.Find(name);
            if (preset == null)
                throw new ContentException("no map called '" + name + "'. Content declares: " + Names(choice.Maps));

            choice.Preset = preset;
            choice.Biomes = preset.Biomes.Count == 0 ? all : BiomeTable.FromContent(content, preset.Biomes);
            if (choice.Biomes.Count == 0)
                throw new ContentException("map '" + name + "' admits no biome that content declares.");
            return choice;
        }

        public static string Names(WorldTable maps)
        {
            if (maps.Count == 0) return "(none)";
            var names = new List<string>();
            foreach (WorldPreset w in maps.All) names.Add(w.Name);
            return string.Join(", ", names.ToArray());
        }
    }
}
