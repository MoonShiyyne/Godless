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
        /// <summary>The group it belongs to (a band, herd or pack), or -1 alone.</summary>
        public int[] Group = new int[256];
        /// <summary>
        /// Its own temperament, 0 to 1, drawn about its kind's and passed to
        /// its young with a little drift. Bold: lets danger come closer, stands
        /// and fights, looks round less often. Social: keeps closer to its
        /// group and joins others readily. Restless: strays, leaves, strikes
        /// out on its own and takes others with it.
        /// </summary>
        public double[] Bold = new double[256], Social = new double[256], Restless = new double[256];
        /// <summary>Its own walking pace, a share of its kind's speed.</summary>
        public double[] Pace = new double[256];
        /// <summary>Where it keeps in its group, as an offset from the group's leader or camp.</summary>
        public double[] SlotX = new double[256], SlotZ = new double[256];
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
        public int Add(int species, double x, double z, long born, int lifeDays, double health)
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
            Group[i] = -1;
            Bold[i] = Social[i] = Restless[i] = 0.5;
            Pace[i] = 1.0;
            SlotX[i] = SlotZ[i] = 0.0;
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
            System.Array.Resize(ref Group, n); System.Array.Resize(ref Flags, n);
            System.Array.Resize(ref Bold, n); System.Array.Resize(ref Social, n);
            System.Array.Resize(ref Restless, n); System.Array.Resize(ref Pace, n);
            System.Array.Resize(ref SlotX, n); System.Array.Resize(ref SlotZ, n);
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
                d.Add(Group[i]); d.Add(Flags[i]);
                d.Add(System.BitConverter.DoubleToInt64Bits(Bold[i])); d.Add(System.BitConverter.DoubleToInt64Bits(Restless[i]));
            }
            return d.Value;
        }
    }

    /// <summary>
    /// A group of one kind of creature that keeps together (v2 M1): a band of
    /// people with a camp, or a herd or pack of animals. A group has no will of
    /// its own. Its leader goes where it chooses; every other member decides
    /// for itself, step by step, how close to keep, and month by month whether
    /// to stay, to leave, or to strike out and take others with it. Loners
    /// join groups they meet. So groups form, grow, split and dissolve out of
    /// individual choices.
    ///
    /// A band's camp is the one thing the group owns: the leader moves it when
    /// the land round it is eaten bare, and each member chooses whether to go
    /// too. A camp unmoved for a year has settled — which M2 turns into a
    /// village.
    /// </summary>
    public sealed class Group
    {
        public int Number { get; internal set; }
        public int Species { get; internal set; }
        /// <summary>A band of people, with a camp and a place on record; otherwise a herd or pack.</summary>
        public bool IsBand { get; internal set; }
        /// <summary>The row of the member the others keep with, or -1.</summary>
        public int Leader { get; internal set; } = -1;
        public int Members { get; internal set; }
        public long Founded { get; internal set; }
        public bool Gone { get; internal set; }
        public double CampX { get; internal set; }
        public double CampZ { get; internal set; }
        public long CampSince { get; internal set; }
        public bool Settled { get; internal set; }
        public int Moves { get; internal set; }
        /// <summary>The band's founding record (bands only).</summary>
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
