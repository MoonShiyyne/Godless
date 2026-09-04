using System;
using System.Collections.Generic;

namespace Godless.Sim.Content
{
    /// <summary>
    /// The merged result of loading every mod. Ordinal-sorted throughout, so
    /// iterating it is L2-safe and a digest over it is comparable between
    /// runs and between machines.
    /// </summary>
    public sealed class ContentDatabase
    {
        readonly SortedDictionary<string, SortedDictionary<string, JsonValue>> _byType =
            new SortedDictionary<string, SortedDictionary<string, JsonValue>>(StringComparer.Ordinal);
        readonly SortedDictionary<string, string> _owner =
            new SortedDictionary<string, string>(StringComparer.Ordinal);

        internal void Put(string type, string id, JsonValue doc, string owningMod)
        {
            SortedDictionary<string, JsonValue> docs;
            if (!_byType.TryGetValue(type, out docs))
            {
                docs = new SortedDictionary<string, JsonValue>(StringComparer.Ordinal);
                _byType.Add(type, docs);
            }
            docs[id] = doc;
            _owner[type + "/" + id] = owningMod;
        }

        internal bool TryGet(string type, string id, out JsonValue doc)
        {
            doc = JsonValue.Null;
            SortedDictionary<string, JsonValue> docs;
            return _byType.TryGetValue(type, out docs) && docs.TryGetValue(id, out doc);
        }

        public JsonValue Get(string type, string id)
        {
            JsonValue doc;
            return TryGet(type, id, out doc) ? doc : JsonValue.Null;
        }

        public bool Contains(string type, string id) { JsonValue d; return TryGet(type, id, out d); }

        public IReadOnlyList<string> Types() { return new List<string>(_byType.Keys); }

        public IReadOnlyList<string> Ids(string type)
        {
            SortedDictionary<string, JsonValue> docs;
            return _byType.TryGetValue(type, out docs)
                ? new List<string>(docs.Keys) : (IReadOnlyList<string>)new List<string>();
        }

        /// <summary>Which mod last wrote this document. Used by diagnostics and the chronicle of a broken load.</summary>
        public string OwnerOf(string type, string id)
        {
            string mod;
            return _owner.TryGetValue(type + "/" + id, out mod) ? mod : null;
        }

        public int DocumentCount
        {
            get
            {
                int n = 0;
                foreach (var pair in _byType) n += pair.Value.Count;
                return n;
            }
        }

        /// <summary>
        /// A digest of the entire database. Two loads that produce the same
        /// number loaded the same world; the harness records it per run so a
        /// content change that was supposed to be inert can be proved inert.
        /// </summary>
        public ulong Digest()
        {
            var digest = new Godless.Sim.Core.Digest();
            foreach (var typePair in _byType)
            {
                digest.Add(Godless.Sim.Core.StableHash.OfString(typePair.Key));
                foreach (var docPair in typePair.Value)
                {
                    digest.Add(Godless.Sim.Core.StableHash.OfString(docPair.Key));
                    digest.Add(Godless.Sim.Core.StableHash.OfString(docPair.Value.ToString()));
                }
            }
            return digest.Value;
        }
    }
}
