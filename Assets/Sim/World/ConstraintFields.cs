using Godless.Sim.Core;

namespace Godless.Sim.World
{
    /// <summary>
    /// What the land is like to live on, parcel by parcel: sun, snow, damp,
    /// exposure and how readily it floods. S1F.
    ///
    /// Part 05 lists what site scoring weighs — "slope, drainage and flood
    /// history, sun exposure, prevailing wind, defensibility, distance to
    /// water" — and until now the grid produced only the geometric half of
    /// that. These are the rest, and they are what makes the same genome
    /// settle differently on a cold highland and a wet delta.
    ///
    /// Every field runs 0 to 1 so a genome can weigh them against each other
    /// without knowing their units. They are computed once from the island and
    /// change only when the ground does.
    /// </summary>
    public sealed class ConstraintFields
    {
        public static readonly Symbol SunField = Symbol.For("field.sun");
        public static readonly Symbol SnowField = Symbol.For("field.snow-load");
        public static readonly Symbol DampField = Symbol.For("field.damp");
        public static readonly Symbol ExposureField = Symbol.For("field.exposure");
        public static readonly Symbol FloodField = Symbol.For("field.flood-risk");

        /// <summary>Voxels above the nearest water at which a parcel stops flooding.</summary>
        public const int FloodReach = 8;

        ConstraintFields()
        {
            Sun = new InfluenceMap(SunField);
            SnowLoad = new InfluenceMap(SnowField);
            Damp = new InfluenceMap(DampField);
            Exposure = new InfluenceMap(ExposureField);
            FloodRisk = new InfluenceMap(FloodField);
        }

        /// <summary>1 where the ground falls toward the sun, 0 where it turns away.</summary>
        public InfluenceMap Sun { get; private set; }

        /// <summary>Winter's weight on a roof: the biome's severity, worse with height.</summary>
        public InfluenceMap SnowLoad { get; private set; }

        /// <summary>Rain and standing water together — what rots timber and softens earth.</summary>
        public InfluenceMap Damp { get; private set; }

        /// <summary>How far a parcel stands above the land around it: wind, and a view of who is coming.</summary>
        public InfluenceMap Exposure { get; private set; }

        /// <summary>1 where water reaches in a flood, 0 where it never has.</summary>
        public InfluenceMap FloodRisk { get; private set; }

        /// <summary>The island's sea level, so a parcel's height can be read as height above the sea (S2I).</summary>
        public int SeaLevel { get; private set; } = IslandMap.DefaultSeaLevel;

        public static ConstraintFields Compute(IslandMap map, ParcelGrid grid, BiomeTable biomes)
        {
            var f = new ConstraintFields();
            if (map != null) f.SeaLevel = map.SeaLevel;
            int size = ParcelGrid.Size;

            for (int pz = 0; pz < ParcelGrid.Depth; pz++)
                for (int px = 0; px < ParcelGrid.Width; px++)
                {
                    // The parcel's own columns: how high, how far above water,
                    // and which biome it mostly is.
                    int rain = 0, severity = 0, samples = 0;
                    long aboveWater = 0;
                    for (int dz = 0; dz < size; dz++)
                        for (int dx = 0; dx < size; dx++)
                        {
                            int x = px * size + dx, z = pz * size + dz;
                            aboveWater += map.HeightAboveWaterAt(x, z);
                            int b = map.BiomeAt(x, z);
                            if (b < 0) continue;
                            rain += biomes.At(b).RainfallMm;
                            severity += biomes.At(b).WinterSeverity;
                            samples++;
                        }

                    double hand = aboveWater / (double)(size * size);
                    double meanRain = samples > 0 ? rain / (double)samples : 0.0;
                    double meanSeverity = samples > 0 ? severity / (double)samples : 0.0;
                    double height = grid.Height[px, pz];

                    // Sun: the ground falling away toward the sun catches it;
                    // ground tilted the other way stands in its own shadow.
                    double fall = grid.Height[px, pz + 1] - grid.Height[px, pz - 1];
                    if (!ParcelGrid.InBounds(px, pz + 1) || !ParcelGrid.InBounds(px, pz - 1)) fall = 0.0;
                    f.Sun[px, pz] = SimMath.Clamp01(0.5 - fall / 8.0);

                    // Snow: the biome's winter, worse the higher it stands.
                    double aboveSea = height - map.SeaLevel;
                    double climb = SimMath.Clamp01(aboveSea / 60.0);
                    f.SnowLoad[px, pz] = SimMath.Clamp01(meanSeverity / 5.0 * (0.4 + 0.6 * climb));

                    // Damp: what falls on it, and what stands near it.
                    double wet = 1.0 / (1.0 + hand / 6.0);
                    f.Damp[px, pz] = SimMath.Clamp01(0.55 * SimMath.Clamp01(meanRain / 1500.0) + 0.45 * wet);

                    // Flood: how far the water has to rise to arrive.
                    f.FloodRisk[px, pz] = SimMath.Clamp01(1.0 - hand / FloodReach);
                }

            // Exposure needs the neighbourhood, so it comes after the heights.
            for (int pz = 0; pz < ParcelGrid.Depth; pz++)
                for (int px = 0; px < ParcelGrid.Width; px++)
                {
                    double sum = 0.0;
                    int n = 0;
                    for (int dz = -3; dz <= 3; dz++)
                        for (int dx = -3; dx <= 3; dx++)
                        {
                            if (dx == 0 && dz == 0) continue;
                            if (!ParcelGrid.InBounds(px + dx, pz + dz)) continue;
                            sum += grid.Height[px + dx, pz + dz];
                            n++;
                        }
                    double around = n > 0 ? sum / n : grid.Height[px, pz];
                    f.Exposure[px, pz] = SimMath.Clamp01(0.5 + (grid.Height[px, pz] - around) / 12.0);
                }

            return f;
        }

        public InfluenceMap Find(Symbol field)
        {
            if (field == SunField) return Sun;
            if (field == SnowField) return SnowLoad;
            if (field == DampField) return Damp;
            if (field == ExposureField) return Exposure;
            if (field == FloodField) return FloodRisk;
            return null;
        }

        public ulong Digest()
        {
            var d = new Digest();
            d.Add(Sun.Digest()); d.Add(SnowLoad.Digest()); d.Add(Damp.Digest());
            d.Add(Exposure.Digest()); d.Add(FloodRisk.Digest());
            return d.Value;
        }
    }
}
