using Godless.Sim.Core;

namespace Godless.Sim.World
{
    /// <summary>One day's weather over one biome.</summary>
    public readonly struct Sky
    {
        public readonly bool Rain;
        public readonly bool Cold;

        public Sky(bool rain, bool cold) { Rain = rain; Cold = cold; }

        public static readonly Sky Fair = new Sky(false, false);

        public override string ToString() { return Rain ? (Cold ? "cold rain" : "rain") : (Cold ? "cold" : "fair"); }
    }

    /// <summary>
    /// Daily weather from the biome's climate. Part of S12, because S12's tell
    /// is "an agent who slept in the rain behaves differently tomorrow" and
    /// nothing else produces rain.
    ///
    /// Stateless: a day's weather is derived from the world seed and the day
    /// number alone, so it is the same whichever day is asked about first,
    /// the timeline can show the weather of any year, and one storm falls on
    /// the whole island at once because every biome reads the same draw.
    ///
    /// The climate is content (L5): a biome's annual rainfall and winter
    /// severity. The mapping from those to a daily chance is the model, and
    /// lives here.
    /// </summary>
    public static class Weather
    {
        public const string StreamId = "world.weather";

        /// <summary>
        /// Millimetres of annual rainfall per rainy day. At 7, a temperate
        /// 800 mm falls on about 114 days a year and a 1400 mm flood plain on
        /// 200.
        /// </summary>
        public const int MillimetresPerRainyDay = 7;

        public static Sky On(StreamRegistry streams, Biome biome, long day, int daysPerYear)
        {
            RngStream rng = streams.Derive(StreamId, (ulong)day);
            int rainRoll = rng.NextInt(1000);
            int coldRoll = rng.NextInt(1000);
            if (biome == null) return Sky.Fair;

            int season = (int)(day % daysPerYear) * 4 / daysPerYear;
            return new Sky(rainRoll < RainPerMille(biome, daysPerYear), coldRoll < ColdPerMille(biome, season));
        }

        public static int RainPerMille(Biome biome, int daysPerYear)
        {
            int perMille = (int)((long)biome.RainfallMm * 1000L / ((long)MillimetresPerRainyDay * daysPerYear));
            return perMille > 900 ? 900 : (perMille < 0 ? 0 : perMille);
        }

        /// <summary>
        /// Season 0 is spring, 3 is winter. Winter severity 5 means nine cold
        /// days in ten through the winter and one in five either side of it;
        /// severity 0 is never cold.
        /// </summary>
        public static int ColdPerMille(Biome biome, int season)
        {
            int s = biome.WinterSeverity < 0 ? 0 : biome.WinterSeverity;
            int perMille;
            switch (season)
            {
                case 3: perMille = 180 * s; break;
                case 0:
                case 2: perMille = 40 * s; break;
                default: perMille = 0; break;
            }
            return perMille > 900 ? 900 : perMille;
        }
    }
}
