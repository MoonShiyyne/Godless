using System.Collections.Generic;
using Godless.Sim.Core;

namespace Godless.Sim.Voxels
{
    /// <summary>
    /// Maps voxel type Symbols to the compact ids the chunk store packs.
    ///
    /// The ids are runtime-only and deliberately so. They are assigned by
    /// sorting the declared symbols, which is deterministic for a given set
    /// of content but shifts the moment a mod adds a type. The durable
    /// identity of a voxel is its Symbol; anything written to a save or an
    /// annal record stores the Symbol and resolves the id on load. Store an
    /// id and installing a mod turns every wall in an existing world into a
    /// different material.
    ///
    /// Air is always id 0 so an unallocated chunk reads as empty for free.
    /// </summary>
    public sealed class VoxelTypes
    {
        public const ushort AirId = 0;
        public static readonly Symbol Air = Symbol.For("voxel.air");

        readonly Symbol[] _byId;
        readonly SortedDictionary<ulong, ushort> _idByHash;

        VoxelTypes(Symbol[] byId, SortedDictionary<ulong, ushort> idByHash)
        {
            _byId = byId;
            _idByHash = idByHash;
        }

        /// <summary>
        /// Builds the table from the declared types. Air is forced to id 0;
        /// everything else is ordered by stable hash, so the same content
        /// always yields the same table on every machine.
        /// </summary>
        public static VoxelTypes Build(IEnumerable<Symbol> declared)
        {
            var hashes = new SortedSet<ulong>();
            foreach (Symbol s in declared)
                if (!s.IsNone && s != Air) hashes.Add(s.Hash);

            var byId = new Symbol[hashes.Count + 1];
            var idByHash = new SortedDictionary<ulong, ushort>();

            byId[0] = Air;
            idByHash[Air.Hash] = AirId;

            ushort next = 1;
            foreach (ulong h in hashes)
            {
                byId[next] = Symbol.FromHash(h);
                idByHash[h] = next;
                next++;
            }
            return new VoxelTypes(byId, idByHash);
        }

        /// <summary>
        /// Builds the table from the loaded content's voxel documents. The
        /// L5 path: types are declared in Assets/Content, never in code.
        /// </summary>
        public static VoxelTypes FromContent(Godless.Sim.Content.ContentDatabase content)
        {
            var declared = new List<Symbol>();
            foreach (string id in content.Ids("voxel")) declared.Add(Symbol.For("voxel." + id));
            return Build(declared);
        }

        public int Count { get { return _byId.Length; } }

        public Symbol SymbolOf(ushort id)
        {
            return (id < _byId.Length) ? _byId[id] : Symbol.None;
        }

        public bool TryGetId(Symbol type, out ushort id)
        {
            return _idByHash.TryGetValue(type.Hash, out id);
        }

        public ushort IdOf(Symbol type)
        {
            ushort id;
            if (_idByHash.TryGetValue(type.Hash, out id)) return id;
            throw new System.ArgumentException("no voxel type '" + type + "' is declared");
        }
    }
}
