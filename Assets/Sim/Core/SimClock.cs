namespace Godless.Sim.Core
{
    /// <summary>
    /// The fixed tick. Integer only — no accumulated float delta, no wall
    /// clock, so the same seed reaches the same year on every machine.
    ///
    /// v2's three clocks all read from this one count, so there is one
    /// definition of "now". Action time is the tick itself — a step, and at
    /// the v2 default a step is a day: people walk, dig and fight a step at a
    /// time. Calendar time is months and years of those steps: aging, harvests,
    /// building progress. Political time is weighed once a year. v1 ran four
    /// six-hour ticks a day at two minutes a day, and the first farm took 68
    /// real hours; v2 runs a day a step and ten steps a real second at 1x.
    /// </summary>
    public sealed class SimClock
    {
        public const int DefaultTicksPerDay = 1;
        public const int DefaultDaysPerYear = 360;
        public const int DefaultDaysPerMonth = 30;

        readonly int _ticksPerDay;
        readonly int _daysPerYear;
        readonly int _daysPerMonth;
        long _tick;

        public SimClock(int ticksPerDay, int daysPerYear, int daysPerMonth = DefaultDaysPerMonth)
        {
            if (ticksPerDay <= 0)
                throw new System.ArgumentOutOfRangeException(nameof(ticksPerDay));
            if (daysPerYear <= 0)
                throw new System.ArgumentOutOfRangeException(nameof(daysPerYear));
            if (daysPerMonth <= 0 || daysPerMonth > daysPerYear)
                throw new System.ArgumentOutOfRangeException(nameof(daysPerMonth));

            _ticksPerDay = ticksPerDay;
            _daysPerYear = daysPerYear;
            _daysPerMonth = daysPerMonth;
            _tick = 0;
        }

        /// <summary>
        /// A step a day, 30-day months, 360-day years. A world with content
        /// takes these from its time document (Harness.TimeRules, L5); the
        /// defaults are for worlds made without any.
        /// </summary>
        public static SimClock Default()
        {
            return new SimClock(DefaultTicksPerDay, DefaultDaysPerYear);
        }

        public long Tick { get { return _tick; } }
        public int TicksPerDay { get { return _ticksPerDay; } }
        public int DaysPerYear { get { return _daysPerYear; } }
        public int DaysPerMonth { get { return _daysPerMonth; } }

        public long TotalDays { get { return _tick / _ticksPerDay; } }
        public int Year { get { return (int)(TotalDays / _daysPerYear); } }
        public int DayOfYear { get { return (int)(TotalDays % _daysPerYear); } }
        public int TickOfDay { get { return (int)(_tick % _ticksPerDay); } }

        public int Month { get { return DayOfYear / _daysPerMonth; } }
        public int DayOfMonth { get { return DayOfYear % _daysPerMonth; } }

        public bool IsFirstTickOfDay { get { return TickOfDay == 0; } }
        public bool IsFirstTickOfMonth { get { return IsFirstTickOfDay && DayOfMonth == 0; } }
        public bool IsFirstTickOfYear { get { return IsFirstTickOfDay && DayOfYear == 0; } }

        public void Advance() { _tick++; }

        public long TicksInYears(int years)
        {
            return (long)years * _daysPerYear * _ticksPerDay;
        }

        public long TicksInDays(int days)
        {
            return (long)days * _ticksPerDay;
        }

        /// <summary>Restores a clock to a recorded tick, for replay and load.</summary>
        public void RewindTo(long tick)
        {
            if (tick < 0) throw new System.ArgumentOutOfRangeException(nameof(tick));
            _tick = tick;
        }
    }
}
