using System.Collections.Generic;
using Godless.Sim.Content;
using Godless.Sim.Core;

namespace Godless.Sim.World
{
    /// <summary>
    /// One map: the shape of the land, the wetness of the air, and which
    /// biomes are allowed to appear on it. S09, made content.
    ///
    /// The generator's constants were the same for every world, so every seed
    /// gave the same kind of island with a different coastline. These are the
    /// knobs that make a map an archipelago rather than a continent, a massif
    /// rather than a delta — and, through the biomes a map admits, what its
    /// people can build with. A settlement on the Dry Reach has clay and
    /// granite and no timber at all, and its houses say so.
    ///
    /// Nothing here is a named culture or an authored place (Part 19's
    /// warning): these are constraint windows on a generator, and the seed
    /// still decides where the coast falls.
    /// </summary>
    public sealed class WorldPreset
    {
        public Symbol Id { get; internal set; }
        public string Name { get; internal set; }

        /// <summary>True for the built-in island, which no content declares and a save stores as no map at all.</summary>
        public bool IsDefault { get; internal set; }
        public string Title { get; internal set; }
        public string Tell { get; internal set; }

        /// <summary>Everything at or below this is sea.</summary>
        public int SeaLevel { get; internal set; }

        /// <summary>Lowest ground anywhere, and how far the land can rise above it.</summary>
        public int Base { get; internal set; }
        public int Relief { get; internal set; }

        /// <summary>
        /// The elevation curve: how much of the rise is straight and how much
        /// is cubed. Straight gives rolling country; cubed leaves the lowlands
        /// low and pulls a summit out of the middle.
        /// </summary>
        public double Linear { get; internal set; }
        public double Cubic { get; internal set; }
        public double Peak { get; internal set; }

        /// <summary>Terraces the land into steps this many voxels deep. Zero for smooth ground.</summary>
        public int Step { get; internal set; }

        /// <summary>Noise: the continent's own scale, and the roughness laid over it.</summary>
        public double ContinentScale { get; internal set; }
        public double RoughScale { get; internal set; }
        public double RoughWeight { get; internal set; }

        /// <summary>Moisture noise, and a thumb on the scale: dry maps bias it down.</summary>
        public double MoistureScale { get; internal set; }
        public double MoistureBias { get; internal set; }

        /// <summary>Where the land is, as fractions of the map, with the radius each centre reaches.</summary>
        internal List<double> CentreX = new List<double>();
        internal List<double> CentreZ = new List<double>();
        internal List<double> CentreRadius = new List<double>();

        public int Centres { get { return CentreX.Count; } }

        /// <summary>
        /// How much of the map this world's land is allowed to cover. The
        /// harness checks a generated world against these, because "an island,
        /// not a continent and not a reef" is a different number on a delta
        /// than on a massif — it belongs to the map, not to the generator.
        /// </summary>
        public double MinLand { get; internal set; }
        public double MaxLand { get; internal set; }

        /// <summary>
        /// The most of the land one biome may cover. A delta is mostly flood
        /// plain on purpose and a green island is not; what the harness holds
        /// each map to is the map's to say, so long as something is left over
        /// for G1's "change the biome" to change.
        /// </summary>
        public double MaxDominantBiome { get; internal set; }

        /// <summary>Flow at which water cuts a channel here. Wet maps run more rivers.</summary>
        public long RiverFlow { get; internal set; }

        /// <summary>How deep a lake may stand over its basin floor.</summary>
        public int MaxLakeDepth { get; internal set; }

        /// <summary>Biomes this map admits, by name. Empty means all of them.</summary>
        internal List<string> AllowedBiomes = new List<string>();

        public IReadOnlyList<string> Biomes { get { return AllowedBiomes; } }

        /// <summary>How much land the falloff leaves at a column, 0 to 1.</summary>
        public double Falloff(int x, int z, int width, int depth)
        {
            double best = 0.0;
            for (int i = 0; i < CentreX.Count; i++)
            {
                double dx = (x - CentreX[i] * width) / (CentreRadius[i] * width);
                double dz = (z - CentreZ[i] * depth) / (CentreRadius[i] * depth);
                double distance = SimMath.Sqrt(dx * dx + dz * dz);
                double here = SimMath.Clamp01(1.0 - distance * distance);
                if (here > best) best = here;
            }
            return best;
        }

        /// <summary>The shape the plain island had before maps were content. Any field a map omits falls back to this.</summary>
        public static WorldPreset Default()
        {
            var preset = new WorldPreset
            {
                Id = Symbol.For("world.default"),
                Name = "default",
                IsDefault = true,
                Title = "An island",
                Tell = "One island in the middle of the sea, with a summit in it.",
                SeaLevel = IslandMap.DefaultSeaLevel,
                Base = 18,
                Relief = 118,
                Linear = 0.35,
                Cubic = 0.65,
                Peak = 0.92,
                Step = 0,
                ContinentScale = 220.0,
                RoughScale = 55.0,
                RoughWeight = 0.28,
                MoistureScale = 130.0,
                MoistureBias = 0.0,
                RiverFlow = Hydrology.RiverFlow,
                MaxLakeDepth = Hydrology.MaxLakeDepth,
                MinLand = 0.02,
                MaxLand = 0.75,
                MaxDominantBiome = 0.9,
            };
            preset.CentreX.Add(0.5);
            preset.CentreZ.Add(0.5);
            preset.CentreRadius.Add(0.46);
            return preset;
        }
    }

    /// <summary>Every map the loaded content declares, in stable-hash order.</summary>
    public sealed class WorldTable
    {
        readonly WorldPreset[] _worlds;
        readonly List<string> _problems;

        WorldTable(WorldPreset[] worlds, List<string> problems) { _worlds = worlds; _problems = problems; }

        public int Count { get { return _worlds.Length; } }
        public WorldPreset this[int index] { get { return _worlds[index]; } }
        public IReadOnlyList<WorldPreset> All { get { return _worlds; } }
        public IReadOnlyList<string> Problems { get { return _problems; } }

        public WorldPreset Find(string name)
        {
            foreach (WorldPreset w in _worlds) if (w.Name == name) return w;
            return null;
        }

        public static WorldTable FromContent(ContentDatabase content, BiomeTable biomes = null)
        {
            var problems = new List<string>();
            var loaded = new List<WorldPreset>();

            foreach (string id in content.Ids("world"))
            {
                JsonValue doc = content.Get("world", id);
                WorldPreset fallback = WorldPreset.Default();
                string tell = doc["tell"].AsString("").Trim();
                if (tell.Length == 0) { problems.Add("map '" + id + "' declares no tell."); continue; }

                JsonValue height = doc["height"], noise = doc["noise"], water = doc["water"];
                var preset = new WorldPreset
                {
                    Id = Symbol.For("world." + id),
                    Name = id,
                    Title = doc["title"].AsString(id),
                    Tell = tell,
                    SeaLevel = doc["seaLevel"].AsInt32(fallback.SeaLevel),
                    Base = height["base"].AsInt32(fallback.Base),
                    Relief = height["relief"].AsInt32(fallback.Relief),
                    Linear = height["linear"].AsDouble(fallback.Linear),
                    Cubic = height["cubic"].AsDouble(fallback.Cubic),
                    Peak = height["peak"].AsDouble(fallback.Peak),
                    Step = height["step"].AsInt32(fallback.Step),
                    ContinentScale = noise["continentScale"].AsDouble(fallback.ContinentScale),
                    RoughScale = noise["roughScale"].AsDouble(fallback.RoughScale),
                    RoughWeight = noise["roughWeight"].AsDouble(fallback.RoughWeight),
                    MoistureScale = noise["moistureScale"].AsDouble(fallback.MoistureScale),
                    MoistureBias = noise["moistureBias"].AsDouble(fallback.MoistureBias),
                    RiverFlow = water["riverFlow"].AsInt64(fallback.RiverFlow),
                    MaxLakeDepth = water["maxLakeDepth"].AsInt32(fallback.MaxLakeDepth),
                    MinLand = doc["land"]["least"].AsDouble(fallback.MinLand),
                    MaxLand = doc["land"]["most"].AsDouble(fallback.MaxLand),
                    MaxDominantBiome = doc["dominantBiome"]["most"].AsDouble(fallback.MaxDominantBiome),
                };

                JsonValue centres = doc["centres"];
                for (int i = 0; i < centres.Count; i++)
                {
                    JsonValue c = centres[i];
                    preset.CentreX.Add(c["x"].AsDouble(0.5));
                    preset.CentreZ.Add(c["z"].AsDouble(0.5));
                    preset.CentreRadius.Add(c["radius"].AsDouble(0.46));
                }
                if (preset.Centres == 0)
                {
                    preset.CentreX.Add(0.5); preset.CentreZ.Add(0.5); preset.CentreRadius.Add(0.46);
                }

                JsonValue only = doc["biomes"];
                for (int i = 0; i < only.Count; i++)
                {
                    string name = only[i].AsString("");
                    if (name.Length == 0) continue;
                    if (biomes != null && biomes.IndexOf(Symbol.For("biome." + name)) < 0)
                    {
                        problems.Add("map '" + id + "' admits biome '" + name + "', which no content declares.");
                        continue;
                    }
                    preset.AllowedBiomes.Add(name);
                }

                if (preset.SeaLevel <= 0 || preset.SeaLevel >= Voxels.ChunkStore.SizeY - 8)
                { problems.Add("map '" + id + "' has a sea level outside the world."); continue; }
                if (preset.Relief <= 0) { problems.Add("map '" + id + "' has no relief: the land would be flat."); continue; }
                if (preset.MinLand <= 0.0 || preset.MaxLand <= preset.MinLand || preset.MaxLand > 1.0)
                { problems.Add("map '" + id + "' asks for a land fraction between " + preset.MinLand + " and " + preset.MaxLand + "."); continue; }
                if (preset.MaxDominantBiome <= 0.0 || preset.MaxDominantBiome >= 1.0)
                { problems.Add("map '" + id + "' lets one biome cover " + preset.MaxDominantBiome + " of its land: that is no share at all, or the whole island."); continue; }

                loaded.Add(preset);
            }

            loaded.Sort((a, b) => a.Id.CompareTo(b.Id));
            return new WorldTable(loaded.ToArray(), problems);
        }
    }
}
