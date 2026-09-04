using System.Collections.Generic;
using Godless.Sim.Core;

namespace Godless.Sim.Annals
{
    /// <summary>Index of a record in the annals. Assigned in write order.</summary>
    public readonly struct RecordId : System.IEquatable<RecordId>, System.IComparable<RecordId>
    {
        public static readonly RecordId None = new RecordId(-1);

        public readonly int Index;

        public RecordId(int index) { Index = index; }

        public bool Exists { get { return Index >= 0; } }

        public bool Equals(RecordId o) { return Index == o.Index; }
        public override bool Equals(object obj) { return obj is RecordId && Equals((RecordId)obj); }
        public override int GetHashCode() { return Index; }
        public int CompareTo(RecordId o) { return Index.CompareTo(o.Index); }
        public static bool operator ==(RecordId a, RecordId b) { return a.Index == b.Index; }
        public static bool operator !=(RecordId a, RecordId b) { return a.Index != b.Index; }
        public override string ToString() { return Exists ? "r" + Index : "<none>"; }
    }

    /// <summary>
    /// One entry in the deterministic record. S05.
    ///
    /// Part 24 keeps the Annalist and the Chronicler apart, and this type is
    /// where that separation is structural rather than a convention: there is
    /// no message field and no string anywhere. A record states what
    /// happened, to what, where, when and because of what. Turning that into
    /// a sentence is the Chronicler's job, and swapping the Chronicler must
    /// never touch the simulation.
    ///
    /// Cause is L3. Every record names the record that produced it, from the
    /// first line that writes one — which is what the chronicle, the Silence
    /// scoring, the stratigraphic probe and the timelapse all read later.
    /// </summary>
    public sealed class AnnalRecord
    {
        public RecordId Id { get; internal set; }

        /// <summary>When. Sim ticks, never wall clock.</summary>
        public long Tick { get; internal set; }

        /// <summary>What kind of thing happened — "event.flood", "gene.mutated".</summary>
        public Symbol Kind { get; internal set; }

        /// <summary>What it happened to — a settlement, a structure, an agent.</summary>
        public Symbol Subject { get; internal set; }

        /// <summary>Where, or Int3.Nowhere for something with no place.</summary>
        public Int3 Place { get; internal set; }

        /// <summary>The record that caused this one, or RecordId.None at a root.</summary>
        public RecordId Cause { get; internal set; }

        /// <summary>Others involved. Usually empty, occasionally one or two.</summary>
        public IReadOnlyList<Symbol> Participants { get; internal set; }

        /// <summary>
        /// Two small integers whose meaning is fixed by Kind — a severity, a
        /// count, a gene index and its delta. Integers because a record is
        /// part of the save and must compare exactly.
        /// </summary>
        public long ValueA { get; internal set; }
        public long ValueB { get; internal set; }

        public bool HasPlace { get { return !Place.IsNowhere; } }

        public override string ToString()
        {
            var c = System.Globalization.CultureInfo.InvariantCulture;
            return Id + " t" + Tick.ToString(c) + " " + Kind
                 + (Subject.IsNone ? "" : " of " + Subject)
                 + (HasPlace ? " at " + Place : "")
                 + (Cause.Exists ? " <- " + Cause : "");
        }
    }
}
