namespace Godless.Sim.Core
{
    /// <summary>
    /// A running FNV-1a digest over a sequence of values. The cheap way to
    /// answer "did these two runs do exactly the same thing" without holding
    /// both runs in memory.
    ///
    /// Used by the determinism test now and by the batch harness (S08) later,
    /// where a per-run digest is what makes a 200-seed sweep comparable
    /// across builds.
    /// </summary>
    public struct Digest
    {
        const ulong Offset = 14695981039346656037UL;
        const ulong Prime = 1099511628211UL;

        ulong _hash;
        bool _started;

        public ulong Value { get { return _started ? _hash : Offset; } }

        public void Add(ulong value)
        {
            unchecked
            {
                if (!_started) { _hash = Offset; _started = true; }
                for (int shift = 0; shift < 64; shift += 8)
                    _hash = (_hash ^ (byte)(value >> shift)) * Prime;
            }
        }

        /// <summary>
        /// Folds two bytes rather than eight. Named rather than overloaded so
        /// that no existing Add(ushort-typed-expression) call silently changes
        /// which overload it binds to.
        /// </summary>
        public void AddShort(ushort value)
        {
            unchecked
            {
                if (!_started) { _hash = Offset; _started = true; }
                _hash = (_hash ^ (byte)value) * Prime;
                _hash = (_hash ^ (byte)(value >> 8)) * Prime;
            }
        }

        public void Add(long value) { Add(unchecked((ulong)value)); }
        public void Add(int value) { Add(unchecked((ulong)(long)value)); }
    }
}
