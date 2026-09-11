using System.Collections.Generic;
using Godless.Sim.Core;

namespace Godless.Sim.Build
{
    /// <summary>
    /// What a grammar produces: a box of cells, each empty or holding a role —
    /// wall, floor, roof, window, door, post, plinth, hearth. No materials.
    /// S18.
    ///
    /// Part 05 keeps the two questions apart: the grammar decides what the
    /// building is — its massing, rooms, floors and openings — and S19's WFC
    /// decides what it looks like, against the stock. So a blueprint names a
    /// wall, never an oak wall, and the same blueprint in timber country and
    /// in stone country is two visibly different buildings.
    /// </summary>
    public sealed class Blueprint
    {
        readonly byte[] _cells;
        readonly List<Symbol> _roles = new List<Symbol> { Symbol.None };

        public Blueprint(int width, int height, int depth)
        {
            Width = width; Height = height; Depth = depth;
            _cells = new byte[width * height * depth];
        }

        /// <summary>Cells along x, y, z. The building's own footprint sits inside a margin for eaves.</summary>
        public int Width { get; private set; }
        public int Height { get; private set; }
        public int Depth { get; private set; }

        /// <summary>How many people it sleeps, as the grammar decided.</summary>
        public int Capacity { get; internal set; }

        public IReadOnlyList<Symbol> Roles { get { return _roles; } }

        public bool InBounds(int x, int y, int z) { return x >= 0 && y >= 0 && z >= 0 && x < Width && y < Height && z < Depth; }

        int Index(int x, int y, int z) { return (y * Depth + z) * Width + x; }

        /// <summary>The role at a cell, or Symbol.None where it is empty.</summary>
        public Symbol At(int x, int y, int z) { return InBounds(x, y, z) ? _roles[_cells[Index(x, y, z)]] : Symbol.None; }

        public void Set(int x, int y, int z, Symbol role)
        {
            if (!InBounds(x, y, z)) return;
            _cells[Index(x, y, z)] = (byte)RoleIndex(role);
        }

        int RoleIndex(Symbol role)
        {
            if (role.IsNone) return 0;
            for (int i = 1; i < _roles.Count; i++) if (_roles[i] == role) return i;
            if (_roles.Count >= 255) throw new System.InvalidOperationException("a blueprint holds at most 254 roles");
            _roles.Add(role);
            return _roles.Count - 1;
        }

        /// <summary>Non-empty cells: the voxels it would take to build.</summary>
        public int Volume
        {
            get { int n = 0; for (int i = 0; i < _cells.Length; i++) if (_cells[i] != 0) n++; return n; }
        }

        public int Count(Symbol role)
        {
            int r = _roles.IndexOf(role);
            if (r <= 0) return 0;
            int n = 0;
            for (int i = 0; i < _cells.Length; i++) if (_cells[i] == r) n++;
            return n;
        }

        /// <summary>Lowest and highest y holding a role, or -1 when it holds none.</summary>
        public void Span(Symbol role, out int lowest, out int highest)
        {
            lowest = highest = -1;
            int r = _roles.IndexOf(role);
            if (r <= 0) return;
            for (int y = 0; y < Height; y++)
                for (int z = 0; z < Depth; z++)
                    for (int x = 0; x < Width; x++)
                        if (_cells[Index(x, y, z)] == r) { if (lowest < 0) lowest = y; highest = y; }
        }

        /// <summary>Columns holding a role at any height: its footprint.</summary>
        public int Footprint(Symbol role)
        {
            int r = _roles.IndexOf(role);
            if (r <= 0) return 0;
            int n = 0;
            for (int z = 0; z < Depth; z++)
                for (int x = 0; x < Width; x++)
                    for (int y = 0; y < Height; y++)
                        if (_cells[Index(x, y, z)] == r) { n++; break; }
            return n;
        }

        /// <summary>Top of the building: the highest occupied cell plus one, or zero.</summary>
        public int OccupiedHeight
        {
            get
            {
                for (int y = Height - 1; y >= 0; y--)
                    for (int z = 0; z < Depth; z++)
                        for (int x = 0; x < Width; x++)
                            if (_cells[Index(x, y, z)] != 0) return y + 1;
                return 0;
            }
        }

        public ulong Digest()
        {
            var d = new Digest();
            d.Add(Width); d.Add(Height); d.Add(Depth); d.Add(Capacity);
            for (int i = 0; i < _cells.Length; i++) d.Add(_roles[_cells[i]].Hash);
            return d.Value;
        }
    }
}
