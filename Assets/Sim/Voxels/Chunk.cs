namespace Godless.Sim.Voxels
{
    /// <summary>
    /// A 32^3 block of voxels, palette compressed.
    ///
    /// Part 23: a per-chunk palette plus a byte index per voxel drops memory
    /// by an order of magnitude against an int per voxel. This goes further
    /// and sizes the index to the palette — 1, 2, 4 or 8 bits — because most
    /// of an island is air, then stone, then two or three materials. A chunk
    /// holding one type carries no index array at all, which is what makes
    /// "the whole island is resident" true rather than aspirational.
    ///
    /// 32^3 and not 64^3 because the island is 160 voxels tall and 160 does
    /// not divide by 64.
    /// </summary>
    public sealed class Chunk
    {
        public const int Size = 32;
        public const int Volume = Size * Size * Size; // 32768

        ushort[] _palette;   // local index -> global voxel type id
        int[] _counts;       // voxels currently using each palette slot
        int _bits;           // 0, 1, 2, 4 or 8
        ulong[] _data;       // null while the chunk is uniform

        public Chunk(ushort fill)
        {
            _palette = new ushort[] { fill };
            _counts = new int[] { Volume };
            _bits = 0;
            _data = null;
        }

        /// <summary>True while every voxel is the same type, so no index array exists.</summary>
        public bool IsUniform { get { return _bits == 0; } }

        public ushort UniformType { get { return _palette[0]; } }

        public int PaletteCount { get { return _palette.Length; } }

        public int BitsPerIndex { get { return _bits; } }

        public static int Index(int x, int y, int z)
        {
            return (y << 10) | (z << 5) | x;
        }

        public ushort Get(int x, int y, int z)
        {
            if (_bits == 0) return _palette[0];
            return _palette[ReadIndex(Index(x, y, z))];
        }

        public void Set(int x, int y, int z, ushort type)
        {
            int i = Index(x, y, z);

            if (_bits == 0)
            {
                if (_palette[0] == type) return;
                Grow(type);          // uniform -> two entries, allocates _data
                WriteIndex(i, 1);
                _counts[0]--; _counts[1]++;
                return;
            }

            int previous = ReadIndex(i);
            int slot = SlotOf(type);
            if (slot == previous) return;

            if (slot < 0)
            {
                slot = Grow(type);
                previous = ReadIndex(i); // Grow may have repacked
            }

            WriteIndex(i, slot);
            _counts[previous]--;
            _counts[slot]++;

            if (_counts[previous] == 0) Collapse();
        }

        public void Fill(ushort type)
        {
            _palette = new ushort[] { type };
            _counts = new int[] { Volume };
            _bits = 0;
            _data = null;
        }

        int SlotOf(ushort type)
        {
            for (int i = 0; i < _palette.Length; i++) if (_palette[i] == type) return i;
            return -1;
        }

        /// <summary>Adds a palette entry, widening and repacking the indices if needed.</summary>
        int Grow(ushort type)
        {
            int newSlot = _palette.Length;

            var palette = new ushort[newSlot + 1];
            System.Array.Copy(_palette, palette, newSlot);
            palette[newSlot] = type;

            var counts = new int[newSlot + 1];
            System.Array.Copy(_counts, counts, newSlot);

            int bits = BitsFor(palette.Length);
            if (bits != _bits)
            {
                var data = new ulong[Volume * bits / 64];
                if (_bits > 0)
                {
                    // Repack every index at the wider width.
                    int perOld = 64 / _bits;
                    int perNew = 64 / bits;
                    ulong oldMask = (_bits == 64) ? ulong.MaxValue : ((1UL << _bits) - 1UL);
                    for (int i = 0; i < Volume; i++)
                    {
                        ulong v = (_data[i / perOld] >> ((i % perOld) * _bits)) & oldMask;
                        data[i / perNew] |= v << ((i % perNew) * bits);
                    }
                }
                _data = data;
                _bits = bits;
            }

            _palette = palette;
            _counts = counts;
            return newSlot;
        }

        /// <summary>Drops back to uniform when only one type is left.</summary>
        void Collapse()
        {
            int live = 0, liveSlot = -1;
            for (int i = 0; i < _counts.Length; i++)
                if (_counts[i] > 0) { live++; liveSlot = i; }

            if (live != 1) return;
            Fill(_palette[liveSlot]);
        }

        static int BitsFor(int paletteCount)
        {
            if (paletteCount <= 1) return 0;
            if (paletteCount <= 2) return 1;
            if (paletteCount <= 4) return 2;
            if (paletteCount <= 16) return 4;
            return 8; // 256 types in one 32^3 chunk is already implausible
        }

        int ReadIndex(int i)
        {
            int per = 64 / _bits;
            ulong mask = (1UL << _bits) - 1UL;
            return (int)((_data[i / per] >> ((i % per) * _bits)) & mask);
        }

        void WriteIndex(int i, int value)
        {
            int per = 64 / _bits;
            int shift = (i % per) * _bits;
            ulong mask = (1UL << _bits) - 1UL;
            int word = i / per;
            _data[word] = (_data[word] & ~(mask << shift)) | (((ulong)value & mask) << shift);
        }

        /// <summary>A deep copy, for snapshots.</summary>
        public Chunk Clone()
        {
            var copy = new Chunk(_palette[0]);
            copy._palette = (ushort[])_palette.Clone();
            copy._counts = (int[])_counts.Clone();
            copy._bits = _bits;
            copy._data = _data == null ? null : (ulong[])_data.Clone();
            return copy;
        }

        /// <summary>Approximate heap cost, for the memory-budget assertion.</summary>
        public int MemoryBytes
        {
            get
            {
                int bytes = 32; // object header and fields, near enough
                bytes += _palette.Length * 2;
                bytes += _counts.Length * 4;
                if (_data != null) bytes += _data.Length * 8;
                return bytes;
            }
        }
    }
}
