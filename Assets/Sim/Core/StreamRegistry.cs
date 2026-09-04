using System.Collections.Generic;

namespace Godless.Sim.Core
{
    /// <summary>
    /// Resolves an RNG stream by a stable string id — "culture.mutation",
    /// "build.siting", "world.weather".
    ///
    /// L2, in one sentence: keying a stream by "the third gene in the list"
    /// makes installing any mod reshuffle every world. A one-line decision
    /// now and an unshippable bug later. So a stream's seed is derived from
    /// the world seed and the hash of its id, and from nothing else — not
    /// from registration order, not from a counter, not from how many streams
    /// exist.
    ///
    /// Ids are conventionally "system.purpose", lowercase, dotted. Treat one
    /// as permanent once it ships: renaming a stream id changes every world
    /// that used it, exactly as if the generator had changed.
    /// </summary>
    public sealed class StreamRegistry
    {
        readonly ulong _worldSeed;
        readonly Dictionary<string, RngStream> _streams;

        public StreamRegistry(ulong worldSeed)
        {
            _worldSeed = worldSeed;
            _streams = new Dictionary<string, RngStream>();
        }

        public ulong WorldSeed { get { return _worldSeed; } }

        /// <summary>
        /// The stream for this id, created on first request. Repeated calls
        /// return the same instance, so a system draws one continuous
        /// sequence rather than restarting it.
        /// </summary>
        public RngStream Get(string id)
        {
            if (string.IsNullOrEmpty(id))
                throw new System.ArgumentException("stream id must not be empty", nameof(id));

            RngStream stream;
            if (_streams.TryGetValue(id, out stream)) return stream;

            stream = new RngStream(SeedFor(id));
            _streams.Add(id, stream);
            return stream;
        }

        /// <summary>
        /// A stream that is not retained — for a one-off draw that must be
        /// reproducible from its own coordinates rather than from how many
        /// times anything else has drawn. Use it for per-chunk or per-agent
        /// derivation, where "the same input gives the same answer" matters
        /// more than sequence.
        /// </summary>
        public RngStream Derive(string id, ulong discriminator)
        {
            return new RngStream(StableHash.Combine(SeedFor(id), discriminator));
        }

        ulong SeedFor(string id)
        {
            return StableHash.Combine(_worldSeed, StableHash.OfString(id));
        }

        /// <summary>
        /// Ids that have been requested, sorted. Sorted because L2 forbids
        /// unordered dictionary iteration in the sim — and a debug dump that
        /// changes order between runs is worse than useless when the thing
        /// you are chasing is a determinism bug.
        /// </summary>
        public IReadOnlyList<string> ActiveIds()
        {
            var ids = new List<string>(_streams.Keys);
            ids.Sort(System.StringComparer.Ordinal);
            return ids;
        }
    }
}
