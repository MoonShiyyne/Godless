namespace Godless.Sim.Harness
{
    /// <summary>
    /// How fast the world runs while somebody is watching it.
    ///
    /// The whole of speed lives here, and none of it reaches the simulation.
    /// A tick is a tick: the same seed and the same number of ticks give the
    /// same world, whether those ticks were taken one a frame at 1x, eight a
    /// frame at 8x, or all at once by a tool. This class only decides *how
    /// many whole ticks are due now*; SimWorld.Tick does the rest and has no
    /// idea anybody is in a hurry.
    ///
    /// That separation is the thing to keep. The day something in the tick
    /// reads a clock, a frame rate or a speed setting, saves stop replaying,
    /// the harness stops agreeing with the Editor, and two players with one
    /// seed stop getting one island. L2 already forbids it; this is where it
    /// would be tempting.
    ///
    /// Faster speeds are a display decision and can be extended freely — the
    /// Silence (S46b) will want to run two hundred years unattended, and a
    /// timelapse (S54a) faster still. Adding 32x here changes nothing in the
    /// sim; it changes how long the player waits.
    /// </summary>
    public sealed class TickPacer
    {
        /// <summary>Speeds, as multiples of the base rate. The first is paused.</summary>
        public static readonly int[] Multipliers = { 0, 1, 2, 4, 8, 16, 32, 64, 128 };

        /// <summary>
        /// Ticks of debt that may be carried. A frame that took a second —
        /// a breakpoint, a load, an Editor pause — must not be repaid as a
        /// burst of simulation the player never sees and the mesher cannot
        /// keep up with. Anything past this is dropped: the world runs
        /// slower than real time for a moment rather than lurching.
        /// </summary>
        public const double MaxDebtTicks = 64.0;

        double _debt;
        int _level;

        /// <param name="daysPerSecondAt1x">Simulated days a real second at 1x.</param>
        /// <param name="ticksPerDay">From the clock, so the two cannot drift apart.</param>
        public TickPacer(double daysPerSecondAt1x = 1.0, int ticksPerDay = SimClockTicksPerDay, int level = 1)
        {
            DaysPerSecondAt1x = daysPerSecondAt1x > 0.0 ? daysPerSecondAt1x : 1.0;
            TicksPerDay = ticksPerDay > 0 ? ticksPerDay : SimClockTicksPerDay;
            _level = Clamp(level);
        }

        const int SimClockTicksPerDay = Core.SimClock.DefaultTicksPerDay;

        public double DaysPerSecondAt1x { get; set; }
        public int TicksPerDay { get; private set; }

        /// <summary>Index into <see cref="Multipliers"/>. Zero is paused.</summary>
        public int Level
        {
            get { return _level; }
            set { _level = Clamp(value); }
        }

        public int Multiplier { get { return Multipliers[_level]; } }
        public bool IsPaused { get { return Multipliers[_level] == 0; } }

        /// <summary>Ticks owed but not yet taken. Above a tick or two, the watcher is ahead of the world.</summary>
        public double Behind { get { return _debt; } }

        /// <summary>"1x", "4x", or "paused" — for a corner of the screen.</summary>
        public string Label { get { return IsPaused ? "paused" : Multiplier + "x"; } }

        public void Faster() { Level = _level + 1; }
        public void Slower() { Level = _level - 1; }

        /// <summary>Pause, or go back to the speed before the pause.</summary>
        public void TogglePause()
        {
            if (IsPaused) { Level = _resume; return; }
            _resume = _level;
            Level = 0;
        }

        int _resume = 1;

        /// <summary>
        /// Real time passes. Returns nothing: whether that is a whole tick yet
        /// is <see cref="Take"/>'s business, because a frame at 60 fps and 1x
        /// is a quarter of a tick and a quarter of a tick is not a thing that
        /// can happen.
        /// </summary>
        public void Advance(double realSeconds)
        {
            if (realSeconds <= 0.0 || IsPaused) return;
            _debt += realSeconds * DaysPerSecondAt1x * Multiplier * TicksPerDay;
            if (_debt > MaxDebtTicks) _debt = MaxDebtTicks;
        }

        /// <summary>
        /// Asks for a number of ticks outright, whatever the speed: a step
        /// button while paused, or a tool winding the world forward. It goes
        /// through the same debt, so nothing else has to know it happened.
        /// </summary>
        public void Request(int ticks)
        {
            if (ticks <= 0) return;
            _debt += ticks;
        }

        /// <summary>
        /// Whole ticks due now, at most <paramref name="mostTicks"/>. What is
        /// left stays owed, so a frame that could only afford two ticks of
        /// eight does not lose the other six.
        /// </summary>
        public int Take(int mostTicks)
        {
            if (mostTicks <= 0 || _debt < 1.0) return 0;
            int ticks = (int)_debt;
            if (ticks > mostTicks) ticks = mostTicks;
            _debt -= ticks;
            return ticks;
        }

        static int Clamp(int level) { return level < 0 ? 0 : (level >= Multipliers.Length ? Multipliers.Length - 1 : level); }
    }
}
