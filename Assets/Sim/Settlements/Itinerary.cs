using System.Collections.Generic;
using Godless.Sim.Core;
using Godless.Sim.Drives;

namespace Godless.Sim.Settlements
{
    /// <summary>
    /// One stretch of a person's tick: from a column to a column, from one
    /// share of the tick to another, in a pose, doing something. A stretch
    /// that starts and ends on the same column is a stay. S2W.
    /// </summary>
    public struct Leg
    {
        public double Start, End;
        public int FromX, FromZ, ToX, ToZ;
        public string Pose;
        public string Doing;

        public bool Moves { get { return FromX != ToX || FromZ != ToZ; } }
    }

    /// <summary>
    /// A person's tick as the ground they cover and what they do on it, in
    /// order and timed. S2W.
    ///
    /// A tick is six hours, and until this a person was seen once in it: at
    /// the tree, then at the fire, then in bed, a quarter of a second apart at
    /// the speed the world was watched at. Nothing about what they did was
    /// wrong, only that the six hours between were never shown. Now the
    /// movement system writes the six hours down as it places them — the walk
    /// out, the work, the drink at the river on the way, the walk home, the
    /// supper by the hearth — and the view plays it back across the tick.
    ///
    /// Nothing in the simulation reads it. Where a person ends up is still
    /// decided by the drives, the task board and the walk; this only says how
    /// they got there, and it always ends where they stand.
    ///
    /// The tell: a line of people walking out of the village in the morning,
    /// one stopping at the river on the way, and coming back in the evening.
    /// </summary>
    public sealed class Itinerary
    {
        struct Step
        {
            public int X, Z;
            public double Takes;
            public bool Fills;
            public string Pose, Doing;
        }

        /// <summary>Share of a tick a stretch that takes up the slack is given at least, so a hurried tick still shows it.</summary>
        public const double LeastFill = 0.08;

        readonly List<Step> _steps = new List<Step>();
        readonly List<Leg> _legs = new List<Leg>();
        int _startX, _startZ, _x, _z;

        /// <summary>The stretches, in order, from the start of the tick to its end.</summary>
        public IReadOnlyList<Leg> Legs { get { return _legs; } }

        /// <summary>Where the tick began and where, so far, it goes.</summary>
        public int StartX { get { return _startX; } }
        public int StartZ { get { return _startZ; } }
        public int X { get { return _x; } }
        public int Z { get { return _z; } }

        /// <summary>Share of the tick the stretches so far take, before any slack is shared out.</summary>
        public double Planned
        {
            get
            {
                double sum = 0.0;
                foreach (Step s in _steps) if (!s.Fills) sum += s.Takes;
                return sum;
            }
        }

        /// <summary>A new tick, from where the person stands.</summary>
        public void Begin(int x, int z)
        {
            _steps.Clear();
            _legs.Clear();
            _startX = _x = x;
            _startZ = _z = z;
        }

        /// <summary>
        /// A walk to a column along the given way (columns between, in order),
        /// at the pace people walk. A walk that fills takes whatever the tick
        /// has left instead: someone still on the way when the tick ends.
        /// </summary>
        public void Walk(IReadOnlyList<Int3> via, int x, int z, string pose, string doing, bool fills = false)
        {
            if (via != null)
                foreach (Int3 p in via) To(p.X, p.Z, pose, doing, fills);
            To(x, z, pose, doing, fills);
        }

        void To(int x, int z, string pose, string doing, bool fills)
        {
            if (x == _x && z == _z) return;
            double dx = x - _x, dz = z - _z;
            _steps.Add(new Step
            {
                X = x, Z = z, Pose = pose, Doing = doing, Fills = fills,
                Takes = SimMath.Sqrt(dx * dx + dz * dz) / DriveSystem.VoxelsWalkedPerTick,
            });
            _x = x;
            _z = z;
        }

        /// <summary>A share of the tick spent where they are.</summary>
        public void Stay(double share, string pose, string doing)
        {
            if (share <= 0.0) return;
            _steps.Add(new Step { X = _x, Z = _z, Takes = share, Pose = pose, Doing = doing });
        }

        /// <summary>Whatever the tick has left, spent where they are.</summary>
        public void Fill(string pose, string doing)
        {
            _steps.Add(new Step { X = _x, Z = _z, Fills = true, Pose = pose, Doing = doing });
        }

        /// <summary>
        /// Times every stretch. What is planned runs at its own pace and the
        /// stretches that fill share the rest; a tick planned past its end is
        /// hurried through, all of it at once, never cut short, so the tick
        /// always ends where the person stands.
        /// </summary>
        public void Finish()
        {
            _legs.Clear();
            if (_steps.Count == 0) Fill("stand", "");

            double planned = 0.0;
            int fills = 0;
            foreach (Step s in _steps)
            {
                if (s.Fills) fills++;
                else planned += s.Takes;
            }

            double room = fills > 0 ? 1.0 - LeastFill * fills : 1.0;
            double scale = planned > room && planned > 0.0 ? room / planned : 1.0;
            double slack = fills > 0 ? (1.0 - planned * scale) / fills : 0.0;
            // Nothing fills and the plan comes up short: the last stretch is held.
            double tail = fills == 0 && planned * scale < 1.0 ? 1.0 - planned * scale : 0.0;

            double t = 0.0;
            int x = _startX, z = _startZ;
            for (int i = 0; i < _steps.Count; i++)
            {
                Step s = _steps[i];
                double takes = s.Fills ? slack : s.Takes * scale;
                if (i == _steps.Count - 1) takes += tail;
                double end = i == _steps.Count - 1 ? 1.0 : System.Math.Min(1.0, t + takes);
                _legs.Add(new Leg { Start = t, End = end, FromX = x, FromZ = z, ToX = s.X, ToZ = s.Z, Pose = s.Pose, Doing = s.Doing });
                t = end;
                x = s.X;
                z = s.Z;
            }
        }

        /// <summary>The stretch under way at a share of the tick.</summary>
        public Leg At(double share)
        {
            for (int i = 0; i < _legs.Count; i++)
                if (share < _legs[i].End) return _legs[i];
            return _legs.Count > 0 ? _legs[_legs.Count - 1] : default(Leg);
        }
    }
}
