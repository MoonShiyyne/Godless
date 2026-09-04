using System.Collections.Generic;
using Godless.Sim.Core;

namespace Godless.Sim.Annals
{
    /// <summary>
    /// The deterministic record of everything that happened. S05.
    ///
    /// Always on, in-simulation, and never wrong: it does not interpret, it
    /// only records. Every later system writes into this one and none of them
    /// may invent a parallel log — a second log is how the chronicle and the
    /// scoring end up disagreeing about the same century.
    ///
    /// Indexed three ways, because Part 24 is explicit that chronological
    /// order is the least useful of them: the question players actually ask
    /// is not what happened in year 200, it is what happened *here*.
    /// </summary>
    public sealed class Annalist
    {
        /// <summary>
        /// Horizontal size of a place bucket, in voxels. Coarse enough that
        /// a settlement's records share a handful of buckets, fine enough
        /// that a query does not walk the island.
        /// </summary>
        public const int PlaceBucket = 16;

        readonly List<AnnalRecord> _records = new List<AnnalRecord>();
        readonly SortedDictionary<long, List<int>> _byPlace = new SortedDictionary<long, List<int>>();
        readonly SortedDictionary<ulong, List<int>> _bySubject = new SortedDictionary<ulong, List<int>>();

        static readonly IReadOnlyList<Symbol> NoParticipants = new Symbol[0];
        static readonly IReadOnlyList<AnnalRecord> NoRecords = new AnnalRecord[0];

        public int Count { get { return _records.Count; } }

        public AnnalRecord Get(RecordId id)
        {
            return (id.Exists && id.Index < _records.Count) ? _records[id.Index] : null;
        }

        /// <summary>Every record, in write order. Deterministic by construction.</summary>
        public IReadOnlyList<AnnalRecord> All() { return _records; }

        public RecordId Write(long tick, Symbol kind, Symbol subject, Int3 place,
                              RecordId cause, long valueA = 0, long valueB = 0,
                              IReadOnlyList<Symbol> participants = null)
        {
            if (kind.IsNone)
                throw new System.ArgumentException("a record must have a kind", nameof(kind));
            if (cause.Exists && cause.Index >= _records.Count)
                throw new System.ArgumentException("a record cannot be caused by one that does not exist yet", nameof(cause));
            if (_records.Count > 0 && tick < _records[_records.Count - 1].Tick)
                throw new System.ArgumentException("records are written in tick order", nameof(tick));

            var record = new AnnalRecord
            {
                Id = new RecordId(_records.Count),
                Tick = tick,
                Kind = kind,
                Subject = subject,
                Place = place,
                Cause = cause,
                Participants = participants ?? NoParticipants,
                ValueA = valueA,
                ValueB = valueB,
            };
            _records.Add(record);

            int index = record.Id.Index;
            if (record.HasPlace) Bucket(_byPlace, BucketKey(place), index);
            if (!subject.IsNone) Bucket(_bySubject, subject.Hash, index);

            return record.Id;
        }

        /// <summary>A record with no place and no subject — a world-level event.</summary>
        public RecordId Write(long tick, Symbol kind, RecordId cause)
        {
            return Write(tick, kind, Symbol.None, Int3.Nowhere, cause);
        }

        // ── queries ─────────────────────────────────────────────────────────

        public IReadOnlyList<AnnalRecord> InTickRange(long fromInclusive, long toInclusive)
        {
            var hits = new List<AnnalRecord>();
            foreach (AnnalRecord r in _records)
                if (r.Tick >= fromInclusive && r.Tick <= toInclusive) hits.Add(r);
            return hits;
        }

        /// <summary>
        /// What happened here. Walks only the buckets the radius touches, and
        /// returns records in write order so the result is deterministic.
        /// </summary>
        public IReadOnlyList<AnnalRecord> AtPlace(Int3 place, int radius)
        {
            if (place.IsNowhere) return NoRecords;

            long r2 = (long)radius * radius;
            int minX = FloorDiv(place.X - radius, PlaceBucket);
            int maxX = FloorDiv(place.X + radius, PlaceBucket);
            int minZ = FloorDiv(place.Z - radius, PlaceBucket);
            int maxZ = FloorDiv(place.Z + radius, PlaceBucket);

            var indices = new List<int>();
            for (int bx = minX; bx <= maxX; bx++)
                for (int bz = minZ; bz <= maxZ; bz++)
                {
                    List<int> bucket;
                    if (!_byPlace.TryGetValue(PackBucket(bx, bz), out bucket)) continue;
                    foreach (int i in bucket)
                        if (_records[i].Place.HorizontalDistanceSquared(place) <= r2) indices.Add(i);
                }

            indices.Sort();
            var hits = new List<AnnalRecord>(indices.Count);
            foreach (int i in indices) hits.Add(_records[i]);
            return hits;
        }

        /// <summary>What happened to this thing — a structure, a settlement, an agent.</summary>
        public IReadOnlyList<AnnalRecord> About(Symbol subject)
        {
            List<int> bucket;
            if (subject.IsNone || !_bySubject.TryGetValue(subject.Hash, out bucket)) return NoRecords;

            var hits = new List<AnnalRecord>(bucket.Count);
            foreach (int i in bucket) hits.Add(_records[i]);
            return hits;
        }

        public IReadOnlyList<AnnalRecord> OfKind(Symbol kind)
        {
            var hits = new List<AnnalRecord>();
            foreach (AnnalRecord r in _records) if (r.Kind == kind) hits.Add(r);
            return hits;
        }

        /// <summary>
        /// L3, made readable: the chain of causes behind a record, nearest
        /// first, ending at the root. This is what turns "the walls are
        /// thick" into "because of the Ash Raid, because of the drought".
        /// </summary>
        public IReadOnlyList<AnnalRecord> CausalChain(RecordId id)
        {
            var chain = new List<AnnalRecord>();
            var seen = new HashSet<int>();
            RecordId at = id;
            while (at.Exists && at.Index < _records.Count && seen.Add(at.Index))
            {
                AnnalRecord r = _records[at.Index];
                chain.Add(r);
                at = r.Cause;
            }
            return chain;
        }

        /// <summary>Records directly caused by this one, in write order.</summary>
        public IReadOnlyList<AnnalRecord> Consequences(RecordId id)
        {
            var hits = new List<AnnalRecord>();
            if (!id.Exists) return hits;
            foreach (AnnalRecord r in _records) if (r.Cause == id) hits.Add(r);
            return hits;
        }

        /// <summary>
        /// A digest of the whole record. Two runs of the same seed must agree
        /// on this — it is the concrete form of "byte-identical annals".
        /// </summary>
        public ulong Digest()
        {
            var digest = new Digest();
            foreach (AnnalRecord r in _records)
            {
                digest.Add(r.Tick);
                digest.Add(r.Kind.Hash);
                digest.Add(r.Subject.Hash);
                digest.Add(r.Place.X); digest.Add(r.Place.Y); digest.Add(r.Place.Z);
                digest.Add(r.Cause.Index);
                digest.Add(r.ValueA); digest.Add(r.ValueB);
                for (int i = 0; i < r.Participants.Count; i++) digest.Add(r.Participants[i].Hash);
            }
            return digest.Value;
        }

        // ── bucketing ───────────────────────────────────────────────────────

        static void Bucket<TKey>(SortedDictionary<TKey, List<int>> index, TKey key, int recordIndex)
        {
            List<int> bucket;
            if (!index.TryGetValue(key, out bucket)) { bucket = new List<int>(); index.Add(key, bucket); }
            bucket.Add(recordIndex);
        }

        static long BucketKey(Int3 place)
        {
            return PackBucket(FloorDiv(place.X, PlaceBucket), FloorDiv(place.Z, PlaceBucket));
        }

        static long PackBucket(int bx, int bz)
        {
            return ((long)bx << 32) ^ (uint)bz;
        }

        /// <summary>Floor division, so negative coordinates bucket correctly.</summary>
        static int FloorDiv(int value, int divisor)
        {
            int q = value / divisor;
            return (value % divisor != 0 && ((value < 0) != (divisor < 0))) ? q - 1 : q;
        }
    }
}
