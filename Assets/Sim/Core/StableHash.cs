namespace Godless.Sim.Core
{
    /// <summary>
    /// FNV-1a, 64-bit, over the UTF-8 encoding of the string.
    ///
    /// L2 exists because of this type. <c>string.GetHashCode()</c> is
    /// randomised per process in .NET Core, so a stream keyed by it would
    /// produce a different world on every launch — and the failure looks like
    /// a simulation bug, not a hashing bug.
    ///
    /// The UTF-8 encoding is done by hand rather than through
    /// <c>System.Text.Encoding</c> so that the result cannot depend on a
    /// platform's encoder, and so that the hash allocates nothing.
    ///
    /// This function is part of the save format. See the frozen-values test.
    /// </summary>
    public static class StableHash
    {
        const ulong Offset = 14695981039346656037UL; // 0xcbf29ce484222325
        const ulong Prime = 1099511628211UL;         // 0x100000001b3

        public static ulong OfString(string value)
        {
            ulong hash = Offset;
            if (value == null) return hash;

            for (int i = 0; i < value.Length; i++)
            {
                int cp = value[i];

                // Combine a surrogate pair into one code point so that a
                // string is hashed by what it means, not by how UTF-16
                // happens to store it.
                if (cp >= 0xD800 && cp <= 0xDBFF && i + 1 < value.Length)
                {
                    int low = value[i + 1];
                    if (low >= 0xDC00 && low <= 0xDFFF)
                    {
                        cp = 0x10000 + ((cp - 0xD800) << 10) + (low - 0xDC00);
                        i++;
                    }
                }

                if (cp < 0x80)
                {
                    hash = Fold(hash, (byte)cp);
                }
                else if (cp < 0x800)
                {
                    hash = Fold(hash, (byte)(0xC0 | (cp >> 6)));
                    hash = Fold(hash, (byte)(0x80 | (cp & 0x3F)));
                }
                else if (cp < 0x10000)
                {
                    hash = Fold(hash, (byte)(0xE0 | (cp >> 12)));
                    hash = Fold(hash, (byte)(0x80 | ((cp >> 6) & 0x3F)));
                    hash = Fold(hash, (byte)(0x80 | (cp & 0x3F)));
                }
                else
                {
                    hash = Fold(hash, (byte)(0xF0 | (cp >> 18)));
                    hash = Fold(hash, (byte)(0x80 | ((cp >> 12) & 0x3F)));
                    hash = Fold(hash, (byte)(0x80 | ((cp >> 6) & 0x3F)));
                    hash = Fold(hash, (byte)(0x80 | (cp & 0x3F)));
                }
            }

            return hash;
        }

        public static ulong Combine(ulong a, ulong b)
        {
            unchecked
            {
                ulong hash = a;
                for (int shift = 0; shift < 64; shift += 8)
                    hash = Fold(hash, (byte)(b >> shift));
                return hash;
            }
        }

        static ulong Fold(ulong hash, byte b)
        {
            unchecked { return (hash ^ b) * Prime; }
        }
    }
}
