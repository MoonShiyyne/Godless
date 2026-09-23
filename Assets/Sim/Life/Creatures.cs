using System.Collections.Generic;
using Godless.Sim.Annals;
using Godless.Sim.Core;

namespace Godless.Sim.Life
{
    /// <summary>What a creature is doing this step, for anyone drawing or reading it.</summary>
    public enum Doing : byte { Idle, Wandering, Grazing, Seeking, Hunting, Fleeing, Fighting, Resting, Following, Roaming }

    /// <summary>Why a creature died.</summary>
    public enum Death : byte { None, OldAge, Starved, Killed, Smitten, Burned }

    /// <summary>
    /// Every creature, as columns of plain data (v2 M1, law L10).
    ///
    /// v1 made each person an object with a dozen lists and paid 60 s per 20
    /// years for 350 of them. Here a creature is a row across arrays, dead rows
    /// are reused in a fixed order, and nothing allocates per step, so two
    /// thousand cost what the arithmetic costs.
    ///
    /// Row order is the order systems visit creatures, and reuse is a stack,
    /// so the same seed and the same acts fill the same rows (L2).
    /// </summary>
    public sealed class Creatures
    {
        public const byte Blessed = 1, Cursed = 2;

        int _length;
        readonly Stack<int> _free = new Stack<int>();
        ulong _serial;

        public ulong[] Id = new ulong[256];
        public int[] Species = new int[256];
        public double[] X = new double[256], Z = new double[256];
        public double[] FaceX = new double[256], FaceZ = new double[256];
        public long[] Born = new long[256];
        public int[] LifeDays = new int[256];
        public double[] Hunger = new double[256], Health = new double[256];
        public Doing[] Doing = new Doing[256];
        public int[] Target = new int[256];
        public double[] GoalX = new double[256], GoalZ = new double[256];
        public int[] Band = new int[256];
        public byte[] Flags = new byte[256];
        public int[] StarvingDays = new int[256];
        public long[] LastBirth = new long[256];
        public bool[] Alive = new bool[256];

        /// <summary>Rows in use, dead or alive: the range to visit.</summary>
        public int Length { get { return _length; } }

        /// <summary>Living creatures.</summary>
        public int Count { get; private set; }

        /// <summary>Deaths so far, by cause.</summary>
        public readonly long[] Deaths = new long[6];
        public long BornCount { get; private set; }

        /// <summary>A new creature in a free row. Returns the row.</summary>
        public int Add(int species, double x, double z, long born, int lifeDays, double health, int band)
        {
            int i;
            if (_free.Count > 0) i = _free.Pop();
            else
            {
                if (_length == Id.Length) Grow();
                i = _length++;
            }
            Id[i] = ++_serial;
            Species[i] = species;
            X[i] = x; Z[i] = z;
            FaceX[i] = 0.0; FaceZ[i] = 1.0;
            Born[i] = born;
            LifeDays[i] = lifeDays;
            Hunger[i] = 0.2;
            Health[i] = health;
            Doing[i] = Life.Doing.Idle;
            Target[i] = -1;
            GoalX[i] = x; GoalZ[i] = z;
            Band[i] = band;
            Flags[i] = 0;
            StarvingDays[i] = 0;
            LastBirth[i] = born;
            Alive[i] = true;
            Count++;
            BornCount++;
            return i;
        }

        public void Kill(int i, Death cause)
        {
            if (!Alive[i]) return;
            Alive[i] = false;
            Count--;
            Deaths[(int)cause]++;
            _free.Push(i);
        }

        public bool Has(int i, byte flag) { return (Flags[i] & flag) != 0; }

        void Grow()
        {
            int n = Id.Length * 2;
            System.Array.Resize(ref Id, n); System.Array.Resize(ref Species, n);
            System.Array.Resize(ref X, n); System.Array.Resize(ref Z, n);
            System.Array.Resize(ref FaceX, n); System.Array.Resize(ref FaceZ, n);
            System.Array.Resize(ref Born, n); System.Array.Resize(ref LifeDays, n);
            System.Array.Resize(ref Hunger, n); System.Array.Resize(ref Health, n);
            System.Array.Resize(ref Doing, n); System.Array.Resize(ref Target, n);
            System.Array.Resize(ref GoalX, n); System.Array.Resize(ref GoalZ, n);
            System.Array.Resize(ref Band, n); System.Array.Resize(ref Flags, n);
            System.Array.Resize(ref StarvingDays, n); System.Array.Resize(ref LastBirth, n);
            System.Array.Resize(ref Alive, n);
        }

        public ulong Digest()
        {
            var d = new Digest();
            d.Add(_length); d.Add(Count);
            for (int i = 0; i < _length; i++)
            {
                if (!Alive[i]) continue;
                d.Add(Id[i]); d.Add(Species[i]);
                d.Add(System.BitConverter.DoubleToInt64Bits(X[i])); d.Add(System.BitConverter.DoubleToInt64Bits(Z[i]));
                d.Add(System.BitConverter.DoubleToInt64Bits(Hunger[i])); d.Add(System.BitConverter.DoubleToInt64Bits(Health[i]));
                d.Add(Band[i]); d.Add(Flags[i]);
            }
            return d.Value;
        }
    }

    /// <summary>
    /// A band of people and its camp (v2 M1). People keep near the camp; once
    /// a month the band weighs the food round it and moves on when it is eaten
    /// bare. A camp unmoved for a year has settled — which M2 turns into a
    /// village.
    /// </summary>
    public sealed class Band
    {
        public int Number { get; internal set; }
        public int Species { get; internal set; }
        public double CampX { get; internal set; }
        public double CampZ { get; internal set; }
        public long Founded { get; internal set; }
        public long CampSince { get; internal set; }
        public bool Settled { get; internal set; }
        public bool Gone { get; internal set; }
        public int Members { get; internal set; }
        public int Moves { get; internal set; }
        public RecordId Record { get; internal set; }
    }

    /// <summary>
    /// How much the land holds to eat, parcel by parcel (v2 M1). Each biome
    /// sets what a parcel holds at full growth; grazing and gathering eat it
    /// down; it grows back a share of the gap each month, faster after rain,
    /// and from nothing after fire.
    /// </summary>
    public sealed class Grazing
    {
        public readonly double[] Food, Capacity;
        public readonly int[] RainMonths;

        public Grazing(int parcels)
        {
            Food = new double[parcels];
            Capacity = new double[parcels];
            RainMonths = new int[parcels];
        }

        public double At(int px, int pz) { return Food[pz * World.ParcelGrid.Width + px]; }

        public ulong Digest()
        {
            var d = new Digest();
            for (int i = 0; i < Food.Length; i++) if (Capacity[i] > 0.0) d.Add(System.BitConverter.DoubleToInt64Bits(Food[i]));
            return d.Value;
        }
    }
}
