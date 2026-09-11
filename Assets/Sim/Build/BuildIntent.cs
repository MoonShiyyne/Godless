using System.Collections.Generic;
using Godless.Sim.Annals;
using Godless.Sim.Core;

namespace Godless.Sim.Build
{
    public enum IntentStatus
    {
        /// <summary>Raised and waiting for a site.</summary>
        Open,
        /// <summary>A site has taken it on; it is being built.</summary>
        Claimed,
        /// <summary>Built. The structure's own record carries on from here.</summary>
        Resolved,
        /// <summary>Given up on, with the reason on record.</summary>
        Abandoned,
    }

    /// <summary>
    /// A typed request to build, with a weight, a place it leans toward, a
    /// budget, and the records that produced it. S14.
    ///
    /// Part 03: "a typed request with a weight, a location bias, a material
    /// budget, and, critically, a causal tag naming the events that produced
    /// it." The causal tag is fixed at the moment of raising and never edited:
    /// a building is a record of the conditions when it was commissioned,
    /// which is why old buildings look older-thinking than new ones.
    /// </summary>
    public sealed class BuildIntent
    {
        /// <summary>The intent.raised record. Also this intent's identity.</summary>
        public RecordId Record { get; internal set; }

        public IntentKind Kind { get; internal set; }
        public Symbol Settlement { get; internal set; }

        /// <summary>
        /// Pressure behind it: what raised it, plus whatever arrived while it
        /// was outstanding and the settlement already had as many open as it
        /// allows. A long-unanswered intent grows heavier, not duplicated.
        /// </summary>
        public double Weight { get; internal set; }

        /// <summary>The parcel where the pressure was heaviest. A bias for S15, not a site.</summary>
        public int ParcelX { get; internal set; }
        public int ParcelZ { get; internal set; }
        public Int3 Place { get; internal set; }

        public int BudgetVoxels { get; internal set; }

        /// <summary>The records that produced it, strongest first. Never empty: at worst, the founding.</summary>
        public IReadOnlyList<RecordId> Causes { get; internal set; }

        public long RaisedTick { get; internal set; }
        public IntentStatus Status { get; internal set; }

        /// <summary>The most recent record in this intent's life.</summary>
        public RecordId LastRecord { get; internal set; }

        public bool Outstanding { get { return Status == IntentStatus.Open || Status == IntentStatus.Claimed; } }
    }
}
