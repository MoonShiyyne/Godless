namespace Godless.Sim.Core
{
    /// <summary>
    /// An integer voxel coordinate. Y is up, matching the island's 512 x 512
    /// x 160 extent. Integer throughout: a place is a cell, never a position,
    /// so nothing about where something happened can drift.
    /// </summary>
    public readonly struct Int3 : System.IEquatable<Int3>, System.IComparable<Int3>
    {
        public static readonly Int3 Zero = new Int3(0, 0, 0);

        /// <summary>The place a record has when it did not happen anywhere.</summary>
        public static readonly Int3 Nowhere = new Int3(int.MinValue, int.MinValue, int.MinValue);

        public readonly int X, Y, Z;

        public Int3(int x, int y, int z) { X = x; Y = y; Z = z; }

        public bool IsNowhere { get { return X == int.MinValue && Y == int.MinValue && Z == int.MinValue; } }

        public bool Equals(Int3 o) { return X == o.X && Y == o.Y && Z == o.Z; }
        public override bool Equals(object obj) { return obj is Int3 && Equals((Int3)obj); }

        public override int GetHashCode()
        {
            ulong h = StableHash.Combine((ulong)(uint)X, (ulong)(uint)Y);
            h = StableHash.Combine(h, (ulong)(uint)Z);
            return (int)(h ^ (h >> 32));
        }

        /// <summary>Ordinal ordering, so any sorted structure keyed by place is L2-safe.</summary>
        public int CompareTo(Int3 o)
        {
            if (X != o.X) return X < o.X ? -1 : 1;
            if (Y != o.Y) return Y < o.Y ? -1 : 1;
            if (Z != o.Z) return Z < o.Z ? -1 : 1;
            return 0;
        }

        public static bool operator ==(Int3 a, Int3 b) { return a.Equals(b); }
        public static bool operator !=(Int3 a, Int3 b) { return !a.Equals(b); }

        public static Int3 operator +(Int3 a, Int3 b) { return new Int3(a.X + b.X, a.Y + b.Y, a.Z + b.Z); }
        public static Int3 operator -(Int3 a, Int3 b) { return new Int3(a.X - b.X, a.Y - b.Y, a.Z - b.Z); }

        /// <summary>
        /// Squared distance on the horizontal plane. Squared because the
        /// comparison is what matters and a square root would be a needless
        /// trip through the floating-point rules.
        /// </summary>
        public long HorizontalDistanceSquared(Int3 o)
        {
            long dx = (long)X - o.X;
            long dz = (long)Z - o.Z;
            return dx * dx + dz * dz;
        }

        public override string ToString()
        {
            if (IsNowhere) return "<nowhere>";
            var c = System.Globalization.CultureInfo.InvariantCulture;
            return "(" + X.ToString(c) + "," + Y.ToString(c) + "," + Z.ToString(c) + ")";
        }
    }
}
