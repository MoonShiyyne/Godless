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

        /// <summary>
        /// The same building turned a quarter at a time about the vertical,
        /// clockwise seen from above (S2O). Width and depth swap on odd turns.
        /// </summary>
        public Blueprint Rotated(int quarterTurns)
        {
            int q = ((quarterTurns % 4) + 4) % 4;
            if (q == 0) return this;
            bool odd = (q & 1) == 1;
            var r = new Blueprint(odd ? Depth : Width, Height, odd ? Width : Depth) { Capacity = Capacity };
            for (int y = 0; y < Height; y++)
                for (int z = 0; z < Depth; z++)
                    for (int x = 0; x < Width; x++)
                    {
                        Symbol role = At(x, y, z);
                        if (role.IsNone) continue;
                        int nx, nz;
                        switch (q)
                        {
                            case 1: nx = Depth - 1 - z; nz = x; break;
                            case 2: nx = Width - 1 - x; nz = Depth - 1 - z; break;
                            default: nx = z; nz = Width - 1 - x; break;
                        }
                        r.Set(nx, y, nz, role);
                    }
            return r;
        }

        /// <summary>
        /// Which side the door is on (S2O): 0 toward -z, 1 toward +x, 2 toward
        /// +z, 3 toward -x. -1 when the building has no door.
        /// </summary>
        public int DoorSide()
        {
            Symbol door = Symbol.For("role.door");
            long sx = 0, sz = 0, n = 0;
            for (int y = 0; y < Height; y++)
                for (int z = 0; z < Depth; z++)
                    for (int x = 0; x < Width; x++)
                        if (At(x, y, z) == door) { sx += 2 * x - (Width - 1); sz += 2 * z - (Depth - 1); n++; }
            if (n == 0) return -1;
            if (System.Math.Abs(sx) * Depth > System.Math.Abs(sz) * Width) return sx > 0 ? 1 : 3;
            return sz > 0 ? 2 : 0;
        }

        /// <summary>A side as a unit step on the grid: 0 (0,-1), 1 (1,0), 2 (0,1), 3 (-1,0).</summary>
        public static void SideStep(int side, out int dx, out int dz)
        {
            dx = side == 1 ? 1 : side == 3 ? -1 : 0;
            dz = side == 2 ? 1 : side == 0 ? -1 : 0;
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
