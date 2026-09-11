using System.Collections.Generic;
using Godless.Sim.Voxels;

namespace Godless.Meshing
{
    /// <summary>
    /// Turns one 32^3 chunk into a mesh: greedy merging over binary row masks,
    /// with ambient occlusion baked into the vertex colours. S06.
    ///
    /// Split into two halves on purpose, because they have different threading
    /// rules. CopyPadded reads the live ChunkStore and must run where nothing
    /// is writing to it — the main thread, in the game. BuildFromPadded touches
    /// only the copy, so it is safe on any thread, and it is the expensive
    /// half. That split is what lets a terrain edit remesh off the main thread
    /// without the store needing a lock.
    ///
    /// The padded copy is 34^3: the chunk plus a one-voxel border from its
    /// neighbours, so faces on a chunk edge are culled against what is really
    /// next to them rather than against an assumed wall of air.
    ///
    /// Merging is binary: each row of a slice becomes a 32-bit mask per
    /// (material, AO pattern) key, runs are found with trailing-zero counts,
    /// and a run grows down the rows while the next row carries the same bits.
    /// Faces merge only when their four AO corners match — merging across an
    /// AO change would interpolate occlusion across a quad and smear it.
    /// </summary>
    public static class ChunkMesher
    {
        public const int Size = Chunk.Size;   // 32
        public const int Padded = Size + 2;   // 34
        public const int PaddedVolume = Padded * Padded * Padded;

        // Brightness per AO level: 0 is a fully enclosed corner, 3 is open.
        static readonly float[] AoLight = { 0.46f, 0.64f, 0.82f, 1.0f };

        // Per-thread scratch, so parallel builds never share a buffer and a
        // steady-state remesh allocates nothing but the output.
        [System.ThreadStatic] static int[] _opaqueKeys;
        [System.ThreadStatic] static int[] _liquidKeys;
        [System.ThreadStatic] static uint[] _rows;
        [System.ThreadStatic] static List<int> _distinct;

        const int StrideX = 1;
        const int StrideZ = Padded;
        const int StrideY = Padded * Padded;
        const int Origin = StrideY + StrideZ + StrideX; // padded index of local (0,0,0)

        public static int PaddedIndex(int x, int y, int z)
        {
            return (y + 1) * StrideY + (z + 1) * StrideZ + (x + 1);
        }

        /// <summary>
        /// Copies the chunk and a one-voxel border into a padded buffer. Returns
        /// false for a chunk that is entirely air, which has nothing to draw —
        /// any face pointing into it belongs to its neighbour's mesh.
        /// </summary>
        public static bool CopyPadded(ChunkStore store, int cx, int cy, int cz, ushort[] pad)
        {
            Chunk chunk = store.ChunkAt(cx, cy, cz);
            if (chunk == null) return false;
            if (chunk.IsUniform && chunk.UniformType == VoxelTypes.AirId) return false;

            int ox = cx * Size - 1, oy = cy * Size - 1, oz = cz * Size - 1;
            int i = 0;
            for (int y = 0; y < Padded; y++)
                for (int z = 0; z < Padded; z++)
                    for (int x = 0; x < Padded; x++)
                        pad[i++] = store.Get(ox + x, oy + y, oz + z);
            return true;
        }

        /// <summary>Convenience for tests and tools: copy and build in one call.</summary>
        public static bool Build(ChunkStore store, int cx, int cy, int cz,
                                 VoxelVisuals visuals, MeshData opaque, MeshData liquid)
        {
            var pad = new ushort[PaddedVolume];
            if (!CopyPadded(store, cx, cy, cz, pad)) return false;
            BuildFromPadded(pad, cx * Size, cy * Size, cz * Size, visuals, opaque, liquid);
            return true;
        }

        /// <summary>
        /// Builds meshes from a padded copy. Thread-safe: it reads only its
        /// arguments. Vertices are in world space, offset by the chunk origin.
        /// </summary>
        public static void BuildFromPadded(ushort[] pad, int originX, int originY, int originZ,
                                           VoxelVisuals visuals, MeshData opaque, MeshData liquid)
        {
            if (_opaqueKeys == null)
            {
                _opaqueKeys = new int[Size * Size];
                _liquidKeys = new int[Size * Size];
                _rows = new uint[Size];
                _distinct = new List<int>(16);
            }

            int[] strides = { StrideX, StrideY, StrideZ };
            int[] origin = { originX, originY, originZ };

            for (int axis = 0; axis < 3; axis++)
            {
                int uAxis = (axis + 1) % 3;
                int vAxis = (axis + 2) % 3;
                int sA = strides[axis], sU = strides[uAxis], sV = strides[vAxis];

                for (int sign = 1; sign >= -1; sign -= 2)
                {
                    for (int k = 0; k < Size; k++)
                    {
                        bool anyOpaque = false, anyLiquid = false;

                        for (int u = 0; u < Size; u++)
                        {
                            for (int v = 0; v < Size; v++)
                            {
                                int here = Origin + k * sA + u * sU + v * sV;
                                int cell = u * Size + v;
                                _opaqueKeys[cell] = 0;
                                _liquidKeys[cell] = 0;

                                ushort type = pad[here];
                                if (type == VoxelTypes.AirId) continue;

                                VoxelVisual vis = visuals[type];
                                VoxelVisual next = visuals[pad[here + sign * sA]];

                                if (vis.Opaque && !next.Opaque)
                                {
                                    int layer = here + sign * sA;
                                    int ao = Ao(pad, visuals, layer, -sU, -sV)
                                           | (Ao(pad, visuals, layer, +sU, -sV) << 2)
                                           | (Ao(pad, visuals, layer, +sU, +sV) << 4)
                                           | (Ao(pad, visuals, layer, -sU, +sV) << 6);
                                    _opaqueKeys[cell] = (type << 8) | ao;
                                    anyOpaque = true;
                                }
                                else if (vis.Liquid && !next.Opaque && !next.Liquid)
                                {
                                    _liquidKeys[cell] = (type << 8) | 0xFF; // liquids carry no AO
                                    anyLiquid = true;
                                }
                            }
                        }

                        if (anyOpaque) Merge(_opaqueKeys, axis, uAxis, vAxis, sign, k, origin, visuals, opaque);
                        if (anyLiquid) Merge(_liquidKeys, axis, uAxis, vAxis, sign, k, origin, visuals, liquid);
                    }
                }
            }
        }

        /// <summary>
        /// Occlusion at one corner of a face, 0 (enclosed) to 3 (open), from the
        /// two side neighbours and the diagonal in the layer the face looks into.
        /// Two sides occupied means fully enclosed regardless of the diagonal.
        /// </summary>
        static int Ao(ushort[] pad, VoxelVisuals visuals, int layer, int du, int dv)
        {
            bool side1 = visuals[pad[layer + du]].Opaque;
            bool side2 = visuals[pad[layer + dv]].Opaque;
            if (side1 && side2) return 0;
            bool corner = visuals[pad[layer + du + dv]].Opaque;
            return 3 - ((side1 ? 1 : 0) + (side2 ? 1 : 0) + (corner ? 1 : 0));
        }

        static void Merge(int[] keys, int axis, int uAxis, int vAxis, int sign, int k,
                          int[] origin, VoxelVisuals visuals, MeshData mesh)
        {
            _distinct.Clear();
            for (int i = 0; i < keys.Length; i++)
            {
                int key = keys[i];
                if (key != 0 && !_distinct.Contains(key)) _distinct.Add(key);
            }

            for (int d = 0; d < _distinct.Count; d++)
            {
                int key = _distinct[d];

                for (int u = 0; u < Size; u++)
                {
                    uint row = 0;
                    int start = u * Size;
                    for (int v = 0; v < Size; v++)
                        if (keys[start + v] == key) row |= 1u << v;
                    _rows[u] = row;
                }

                for (int u = 0; u < Size; u++)
                {
                    uint row = _rows[u];
                    while (row != 0)
                    {
                        int v0 = TrailingZeros(row);
                        int h = TrailingZeros(~(row >> v0));
                        if (h > Size - v0) h = Size - v0;
                        uint mask = (h >= 32 ? 0xFFFFFFFFu : ((1u << h) - 1u)) << v0;

                        int w = 1;
                        while (u + w < Size && (_rows[u + w] & mask) == mask)
                        {
                            _rows[u + w] &= ~mask;
                            w++;
                        }
                        row &= ~mask;

                        EmitQuad(mesh, visuals, key, axis, uAxis, vAxis, sign, k, u, v0, w, h, origin);
                    }
                    _rows[u] = 0;
                }
            }
        }

        static void EmitQuad(MeshData mesh, VoxelVisuals visuals, int key,
                             int axis, int uAxis, int vAxis, int sign, int k,
                             int u0, int v0, int w, int h, int[] origin)
        {
            VoxelVisual vis = visuals[(ushort)(key >> 8)];
            int ao0 = key & 3, ao1 = (key >> 2) & 3, ao2 = (key >> 4) & 3, ao3 = (key >> 6) & 3;

            // The face sits on the far side of the voxel when facing +axis.
            int plane = k + (sign > 0 ? 1 : 0);

            int baseIndex = mesh.VertexCount;
            Corner(mesh, axis, uAxis, vAxis, plane, u0, v0, origin);
            Corner(mesh, axis, uAxis, vAxis, plane, u0 + w, v0, origin);
            Corner(mesh, axis, uAxis, vAxis, plane, u0 + w, v0 + h, origin);
            Corner(mesh, axis, uAxis, vAxis, plane, u0, v0 + h, origin);

            for (int i = 0; i < 4; i++)
            {
                mesh.Normals.Add(axis == 0 ? sign : 0);
                mesh.Normals.Add(axis == 1 ? sign : 0);
                mesh.Normals.Add(axis == 2 ? sign : 0);
            }

            Color(mesh, vis, ao0);
            Color(mesh, vis, ao1);
            Color(mesh, vis, ao2);
            Color(mesh, vis, ao3);

            // Corners 0,1,2 wind around +axis (e_u x e_v = e_axis for a cyclic
            // u, v), so a face pointing along -axis reverses. Unity treats a
            // triangle as front-facing when cross(b - a, c - a) points out.
            //
            // The shared diagonal follows the AO: splitting a quad across its
            // darker diagonal is the standard fix for the anisotropy that
            // otherwise shows up as a visible seam on occluded faces.
            bool flip = ao0 + ao2 < ao1 + ao3;
            int a0 = baseIndex, a1 = baseIndex + 1, a2 = baseIndex + 2, a3 = baseIndex + 3;

            if (!flip)
            {
                if (sign > 0) Tri(mesh, a0, a1, a2, a0, a2, a3);
                else Tri(mesh, a0, a2, a1, a0, a3, a2);
            }
            else
            {
                if (sign > 0) Tri(mesh, a1, a2, a3, a1, a3, a0);
                else Tri(mesh, a1, a3, a2, a1, a0, a3);
            }
        }

        static void Corner(MeshData mesh, int axis, int uAxis, int vAxis,
                           int plane, int u, int v, int[] origin)
        {
            int x = 0, y = 0, z = 0;
            Assign(ref x, ref y, ref z, axis, plane);
            Assign(ref x, ref y, ref z, uAxis, u);
            Assign(ref x, ref y, ref z, vAxis, v);
            mesh.Positions.Add(origin[0] + x);
            mesh.Positions.Add(origin[1] + y);
            mesh.Positions.Add(origin[2] + z);
        }

        static void Assign(ref int x, ref int y, ref int z, int axis, int value)
        {
            if (axis == 0) x = value; else if (axis == 1) y = value; else z = value;
        }

        static void Color(MeshData mesh, VoxelVisual vis, int ao)
        {
            float light = AoLight[ao];
            mesh.Colors.Add((byte)(vis.R * light));
            mesh.Colors.Add((byte)(vis.G * light));
            mesh.Colors.Add((byte)(vis.B * light));
            mesh.Colors.Add(255);
        }

        static void Tri(MeshData mesh, int a, int b, int c, int d, int e, int f)
        {
            mesh.Indices.Add(a); mesh.Indices.Add(b); mesh.Indices.Add(c);
            mesh.Indices.Add(d); mesh.Indices.Add(e); mesh.Indices.Add(f);
        }

        // De Bruijn trailing-zero count. BitOperations is not in netstandard2.1.
        static readonly int[] DeBruijn =
        {
            0, 1, 28, 2, 29, 14, 24, 3, 30, 22, 20, 15, 25, 17, 4, 8,
            31, 27, 13, 23, 21, 19, 16, 7, 26, 12, 18, 6, 11, 5, 10, 9,
        };

        public static int TrailingZeros(uint value)
        {
            if (value == 0) return 32;
            return DeBruijn[((value & (uint)-(int)value) * 0x077CB531u) >> 27];
        }
    }
}
