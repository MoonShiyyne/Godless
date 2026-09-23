using Godless.Sim.Content;
using Godless.Sim.Core;

namespace Godless.Sim.Harness
{
    /// <summary>
    /// How time runs, as content declares it (L5): steps in a day, days in a
    /// month and a year, and how many steps a real second shows at 1x.
    ///
    /// The last number is a display decision and never reaches a system; it is
    /// here so the Editor and the tools agree on what 1x means. The tell: at
    /// 1x a person walks across a village in a few seconds while a year goes
    /// by in about half a minute.
    /// </summary>
    public sealed class TimeRules
    {
        public int TicksPerDay { get; private set; } = SimClock.DefaultTicksPerDay;
        public int DaysPerMonth { get; private set; } = SimClock.DefaultDaysPerMonth;
        public int DaysPerYear { get; private set; } = SimClock.DefaultDaysPerYear;

        /// <summary>Steps (ticks) shown per real second at 1x.</summary>
        public double TicksPerSecondAt1x { get; private set; } = 10.0;

        public SimClock NewClock() { return new SimClock(TicksPerDay, DaysPerYear, DaysPerMonth); }

        /// <summary>Real seconds a year takes at 1x.</summary>
        public double SecondsPerYearAt1x { get { return DaysPerYear * TicksPerDay / TicksPerSecondAt1x; } }

        /// <summary>The first time document content declares, or the defaults when there is none.</summary>
        public static TimeRules FromContent(ContentDatabase content)
        {
            var r = new TimeRules();
            if (content == null) return r;
            foreach (string id in content.Ids("time"))
            {
                JsonValue doc = content.Get("time", id);
                r.TicksPerDay = System.Math.Max(1, doc["ticksPerDay"].AsInt32(r.TicksPerDay));
                r.DaysPerYear = System.Math.Max(1, doc["daysPerYear"].AsInt32(r.DaysPerYear));
                r.DaysPerMonth = System.Math.Min(r.DaysPerYear, System.Math.Max(1, doc["daysPerMonth"].AsInt32(r.DaysPerMonth)));
                double tps = doc["ticksPerSecondAt1x"].AsDouble(r.TicksPerSecondAt1x);
                if (tps > 0.0) r.TicksPerSecondAt1x = tps;
                break;
            }
            return r;
        }
    }
}
