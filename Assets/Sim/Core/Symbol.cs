using System.Collections.Generic;

namespace Godless.Sim.Core
{
    /// <summary>
    /// An interned identifier: the stable 64-bit hash of a string, and
    /// nothing else.
    ///
    /// Everything the simulation names — record kinds, gene ids, materials,
    /// settlements, mods — is identified this way rather than by an enum or
    /// an index. An enum would put the name in code, which L5 forbids since
    /// content declares these. An index would be load-order dependent, which
    /// L2 forbids for exactly the reason the RNG streams are not indexed.
    ///
    /// Comparison and ordering use the hash alone, so a Symbol means the same
    /// thing in every process and every save.
    /// </summary>
    public readonly struct Symbol : System.IEquatable<Symbol>, System.IComparable<Symbol>
    {
        public static readonly Symbol None = new Symbol(0UL);

        public readonly ulong Hash;

        Symbol(ulong hash) { Hash = hash; }

        public static Symbol For(string name)
        {
            if (string.IsNullOrEmpty(name)) return None;
            ulong hash = StableHash.OfString(name);
            SymbolNames.Register(hash, name);
            return new Symbol(hash);
        }

        /// <summary>Rebuilds a symbol from a saved hash, with no name available.</summary>
        public static Symbol FromHash(ulong hash) { return new Symbol(hash); }

        public bool IsNone { get { return Hash == 0UL; } }

        public bool Equals(Symbol other) { return Hash == other.Hash; }
        public override bool Equals(object obj) { return obj is Symbol && Equals((Symbol)obj); }

        /// <summary>
        /// The hash folded to 32 bits. Safe to use in a hash table because it
        /// derives from StableHash rather than from string.GetHashCode.
        /// </summary>
        public override int GetHashCode() { return (int)(Hash ^ (Hash >> 32)); }

        public int CompareTo(Symbol other) { return Hash.CompareTo(other.Hash); }

        public static bool operator ==(Symbol a, Symbol b) { return a.Hash == b.Hash; }
        public static bool operator !=(Symbol a, Symbol b) { return a.Hash != b.Hash; }

        public override string ToString()
        {
            if (IsNone) return "<none>";
            string name = SymbolNames.Lookup(Hash);
            return name ?? ("#" + Hash.ToString("x16", System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    /// <summary>
    /// Diagnostic-only reverse lookup from hash to name, so a chronicle line
    /// and a failing assertion can read as words instead of hex.
    ///
    /// Nothing in the simulation may branch on what is in here: it is
    /// populated by whoever happened to call Symbol.For, which depends on
    /// execution path. Identity is the hash; this is for humans.
    /// </summary>
    public static class SymbolNames
    {
        static readonly Dictionary<ulong, string> Names = new Dictionary<ulong, string>();
        static readonly object Gate = new object();

        internal static void Register(ulong hash, string name)
        {
            lock (Gate) { Names[hash] = name; }
        }

        public static string Lookup(ulong hash)
        {
            lock (Gate)
            {
                string name;
                return Names.TryGetValue(hash, out name) ? name : null;
            }
        }
    }
}
