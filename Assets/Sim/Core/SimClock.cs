namespace Godless.Sim.Core
{
    /// <summary>
    /// The fixed tick. Integer only — no accumulated float delta, no wall
    /// clock, so the same seed reaches the same year on every machine.
    ///
    /// The design's three cadences (drives per tick, settlement intent every
    /// few seconds, construction over days) all read from this rather than
    /// keeping their own counters, so there is one definition of "now".
    /// </summary>
    public sealed class SimClock
    {
        public const int DefaultTicksPerDay = 4;
        public const int DefaultDaysPerYear = 360;

        readonly int _ticksPerDay;
        readonly int _daysPerYear;
        long _tick;

        public SimClock(int ticksPerDay, int daysPerYear)
        {
            if (ticksPerDay <= 0)
                throw new System.ArgumentOutOfRangeException(nameof(ticksPerDay));
            if (daysPerYear <= 0)
                throw new System.ArgumentOutOfRangeException(nameof(daysPerYear));

            _ticksPerDay = ticksPerDay;
            _daysPerYear = daysPerYear;
            _tick = 0;
        }

        /// <summary>
        /// Four ticks a day, 360 days a year. These move to content when the
        /// pipeline lands (S02, law L5); they are constants here because
        /// nothing can load data yet.
        /// </summary>
        public static SimClock Default()
        {
            return new SimClock(DefaultTicksPerDay, DefaultDaysPerYear);
        }

        public long Tick { get { return _tick; } }
        public int TicksPerDay { get { return _ticksPerDay; } }
        public int DaysPerYear { get { return _daysPerYear; } }

        public long TotalDays { get { return _tick / _ticksPerDay; } }
        public int Year { get { return (int)(TotalDays / _daysPerYear); } }
        public int DayOfYear { get { return (int)(TotalDays % _daysPerYear); } }
        public int TickOfDay { get { return (int)(_tick % _ticksPerDay); } }

        public bool IsFirstTickOfDay { get { return TickOfDay == 0; } }
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
