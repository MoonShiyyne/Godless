using System.Collections.Generic;
using Godless.Sim.Annals;
using Godless.Sim.Core;

namespace Godless.Sim.Voxels
{
    /// <summary>One detail model standing in the world. S2R.</summary>
    public sealed class DetailInstance
    {
        public int Id { get; internal set; }
        public Symbol Model { get; internal set; }

        /// <summary>The model's lowest corner, in detail cells (world voxel times CellsPerVoxel).</summary>
        public int X { get; internal set; }
        public int Y { get; internal set; }
        public int Z { get; internal set; }

        /// <summary>Quarter turns clockwise from above.</summary>
        public int Turn { get; internal set; }

        /// <summary>The material voxel filling each of the model's slots, in slot order.</summary>
        internal ushort[] SlotMaterials;
        public IReadOnlyList<ushort> Slots { get { return SlotMaterials; } }

        /// <summary>Why it is there (L3).</summary>
        public RecordId Cause { get; internal set; }

        /// <summary>The world voxel its lowest corner is in.</summary>
        public int VoxelX { get { return Floor(X); } }
        public int VoxelY { get { return Floor(Y); } }
        public int VoxelZ { get { return Floor(Z); } }

        static int Floor(int cells) { return cells >= 0 ? cells / DetailModelTable.CellsPerVoxel : (cells + 1) / DetailModelTable.CellsPerVoxel - 1; }
    }

    /// <summary>A placement or a removal, with the tick and the cause: the detail layer's own delta stream.</summary>
    public struct DetailChange
    {
        public long Tick;
        public bool Added;
        public int Instance;
        public Symbol Model;
        public int X, Y, Z, Turn;
        public ushort[] Slots;
        public RecordId Cause;
    }

    /// <summary>
    /// Everything drawn in detail cells, where it stands and why. S2R.
    ///
    /// Not a second world grid: the island at 12.5 cm would be sixty-four times
    /// the voxels, and the things that want this scale — furniture, rubble,
    /// later people and animals — are few and some of them move. So the layer
    /// holds placed models, indexed by the world chunk they stand in, and
    /// records every placement and removal with its cause the way the voxel
    /// log does (L3). A renderer reads what changed since it last looked.
    /// </summary>
    public sealed class DetailLayer
    {
        readonly SortedDictionary<int, DetailInstance> _instances = new SortedDictionary<int, DetailInstance>();
        readonly Dictionary<int, SortedSet<int>> _byChunk = new Dictionary<int, SortedSet<int>>();   // lookup only
        readonly List<DetailChange> _log = new List<DetailChange>();
        int _next = 1;

        public int Count { get { return _instances.Count; } }
        public IReadOnlyList<DetailChange> Log { get { return _log; } }

        public DetailInstance Get(int id)
        {
            DetailInstance i;
            return _instances.TryGetValue(id, out i) ? i : null;
        }

        /// <summary>Every instance, in id order.</summary>
        public IEnumerable<DetailInstance> All { get { return _instances.Values; } }

        /// <summary>Instances standing in a world chunk, in id order. Empty when none.</summary>
        public IEnumerable<DetailInstance> InChunk(int chunkIndex)
        {
            SortedSet<int> ids;
            if (!_byChunk.TryGetValue(chunkIndex, out ids)) yield break;
            foreach (int id in ids) yield return _instances[id];
        }

        public static int ChunkOf(int voxelX, int voxelY, int voxelZ)
        {
            int cx = voxelX / Voxels.Chunk.Size, cy = voxelY / Voxels.Chunk.Size, cz = voxelZ / Voxels.Chunk.Size;
            return (cy * ChunkStore.ChunksZ + cz) * ChunkStore.ChunksX + cx;
        }

        /// <summary>Puts a model in the world at a corner in detail cells. Returns its id.</summary>
        public int Place(DetailModel model, int x, int y, int z, int turn, ushort[] slots, long tick, RecordId cause)
        {
            var inst = new DetailInstance
            {
                Id = _next++, Model = model.Id, X = x, Y = y, Z = z, Turn = ((turn % 4) + 4) % 4,
                SlotMaterials = slots ?? new ushort[0], Cause = cause,
            };
            Insert(inst);
            _log.Add(new DetailChange { Tick = tick, Added = true, Instance = inst.Id, Model = inst.Model, X = x, Y = y, Z = z,
                                        Turn = inst.Turn, Slots = inst.SlotMaterials, Cause = cause });
            return inst.Id;
        }

        /// <summary>Takes an instance out of the world. False if it was not there.</summary>
        public bool Remove(int id, long tick, RecordId cause)
        {
            DetailInstance inst;
            if (!_instances.TryGetValue(id, out inst)) return false;
            _instances.Remove(id);
            SortedSet<int> ids;
            if (_byChunk.TryGetValue(ChunkIndexOf(inst), out ids)) ids.Remove(id);
            _log.Add(new DetailChange { Tick = tick, Added = false, Instance = id, Model = inst.Model, X = inst.X, Y = inst.Y, Z = inst.Z,
                                        Turn = inst.Turn, Slots = inst.SlotMaterials, Cause = cause });
            _changed.Add(ChunkIndexOf(inst));
            return true;
        }

        /// <summary>Replays a saved change, keeping the saved instance id (S0C).</summary>
        public void Replay(DetailChange c)
        {
            if (c.Added)
            {
                Insert(new DetailInstance { Id = c.Instance, Model = c.Model, X = c.X, Y = c.Y, Z = c.Z, Turn = c.Turn,
                                            SlotMaterials = c.Slots ?? new ushort[0], Cause = c.Cause });
                if (c.Instance >= _next) _next = c.Instance + 1;
            }
            else
            {
                DetailInstance inst;
                if (_instances.TryGetValue(c.Instance, out inst))
                {
                    _instances.Remove(c.Instance);
                    SortedSet<int> ids;
                    if (_byChunk.TryGetValue(ChunkIndexOf(inst), out ids)) ids.Remove(c.Instance);
                    _changed.Add(ChunkIndexOf(inst));
                }
            }
            _log.Add(c);
        }

        void Insert(DetailInstance inst)
        {
            _instances[inst.Id] = inst;
            int chunk = ChunkIndexOf(inst);
            SortedSet<int> ids;
            if (!_byChunk.TryGetValue(chunk, out ids)) { ids = new SortedSet<int>(); _byChunk[chunk] = ids; }
            ids.Add(inst.Id);
            _changed.Add(chunk);
        }

        static int ChunkIndexOf(DetailInstance inst)
        {
            int vx = System.Math.Max(0, System.Math.Min(ChunkStore.SizeX - 1, inst.VoxelX));
            int vy = System.Math.Max(0, System.Math.Min(ChunkStore.SizeY - 1, inst.VoxelY));
            int vz = System.Math.Max(0, System.Math.Min(ChunkStore.SizeZ - 1, inst.VoxelZ));
            return ChunkOf(vx, vy, vz);
        }

        // Chunks whose details changed since a renderer last took them. Presentation only.
        readonly SortedSet<int> _changed = new SortedSet<int>();

        /// <summary>Chunks whose details changed since the last call, in order, and forgets them.</summary>
        public List<int> TakeChangedChunks()
        {
            var list = new List<int>(_changed);
            _changed.Clear();
            return list;
        }

        public ulong Digest()
        {
            var d = new Digest();
            foreach (KeyValuePair<int, DetailInstance> e in _instances)
            {
                DetailInstance i = e.Value;
                d.Add(i.Id); d.Add(i.Model.Hash); d.Add(i.X); d.Add(i.Y); d.Add(i.Z); d.Add(i.Turn); d.Add(i.Cause.Index);
                foreach (ushort s in i.SlotMaterials) d.Add(s);
            }
            return d.Value;
        }
    }
}
