using System.Collections.Generic;
using Godless.Sim.Annals;
using Godless.Sim.Core;
using Godless.Sim.Drives;
using Godless.Sim.Harness;
using Godless.Sim.Settlements;
using Godless.Sim.World;

namespace Godless.Sim.Build
{
    /// <summary>
    /// Layer 2 of Part 03's instinct stack: pressure in, build intents out.
    /// One per settlement. S14.
    ///
    /// Agents' unmet needs arrive as pressure, by parcel and by cause, and
    /// pile up in a demand field for the need. Once a day the pile is
    /// weighed: past the intent kind's threshold it becomes a BuildIntent,
    /// leaning toward the parcel where the pressure was heaviest and carrying
    /// the records that contributed most, and the pile is spent. Pressure that
    /// never reaches a threshold decays away, so one wet night a year never
    /// adds up to a house.
    ///
    /// The bus owns the intent's life on record: raised, claimed by a site,
    /// resolved by a finished structure, or abandoned with a reason. Every one
    /// of those records is caused by the raising, so a structure's chronicle
    /// can walk back through its intent to the nights that asked for it.
    /// </summary>
    public sealed class IntentBus : IPressureSink
    {
        public static readonly Symbol RaisedKind = Symbol.For("intent.raised");
        public static readonly Symbol ClaimedKind = Symbol.For("intent.claimed");
        public static readonly Symbol ResolvedKind = Symbol.For("intent.resolved");
        public static readonly Symbol AbandonedKind = Symbol.For("intent.abandoned");

        /// <summary>Causes an intent carries: the strongest, plus three that contributed.</summary>
        public const int MaxCauses = 4;

        // Contributions smaller than this are forgotten rather than decayed
        // forever, so a long life does not accumulate a tail of dust.
        const double Negligible = 1e-6;

        readonly IntentKindTable _kinds;
        readonly int[] _kindOfNeed;
        readonly PressureTally _tally;

        // Per kind. Sparse and sorted: pressure lands on a handful of parcels
        // and comes from a handful of records, and sorted keys keep every walk
        // over them in the same order on every machine (L2).
        readonly SortedDictionary<int, double>[] _demand;
        readonly SortedDictionary<int, double>[] _causeWeight;
        readonly double[] _total;
        readonly double[] _dailyDecay;
        readonly int[] _outstanding;

        readonly List<BuildIntent> _intents = new List<BuildIntent>();

        public IntentBus(IntentKindTable kinds, int needCount)
        {
            _kinds = kinds;
            _tally = new PressureTally(needCount);
            _kindOfNeed = new int[needCount];
            for (int n = 0; n < needCount; n++) _kindOfNeed[n] = kinds.KindFor(n);

            int k = kinds.Count;
            _demand = new SortedDictionary<int, double>[k];
            _causeWeight = new SortedDictionary<int, double>[k];
            _total = new double[k];
            _dailyDecay = new double[k];
            _outstanding = new int[k];
            for (int i = 0; i < k; i++)
            {
                _demand[i] = new SortedDictionary<int, double>();
                _causeWeight[i] = new SortedDictionary<int, double>();
                _dailyDecay[i] = SimMath.Decay(1.0, 1.0, kinds[i].HalfLifeDays);
            }
        }

        public IntentKindTable Kinds { get { return _kinds; } }

        /// <summary>Every unit of pressure that arrived, per need, whether or not an intent answers it.</summary>
        public PressureTally Tally { get { return _tally; } }

        /// <summary>Every intent ever raised here, in raising order.</summary>
        public IReadOnlyList<BuildIntent> Intents { get { return _intents; } }

        /// <summary>Unspent pressure toward the next intent of a kind.</summary>
        public double Pending(int kind) { return _total[kind]; }

        public int OutstandingCount(int kind) { return _outstanding[kind]; }

        public void Add(int need, int px, int pz, double amount, RecordId cause)
        {
            _tally.Add(need, px, pz, amount, cause);
            int k = _kindOfNeed[need];
            if (k < 0 || amount <= 0.0) return;

            if (_outstanding[k] >= _kinds[k].MaxOpen)
            {
                // As many open as the settlement allows: the pressure makes
                // the newest one more urgent instead of raising another.
                for (int i = _intents.Count - 1; i >= 0; i--)
                    if (_intents[i].Outstanding && _intents[i].Kind == _kinds[k]) { _intents[i].Weight += amount; break; }
                return;
            }

            Accumulate(_demand[k], pz * ParcelGrid.Width + px, amount);
            if (cause.Exists) Accumulate(_causeWeight[k], cause.Index, amount);
            _total[k] += amount;
        }

        static void Accumulate(SortedDictionary<int, double> into, int key, double amount)
        {
            double v;
            into.TryGetValue(key, out v);
            into[key] = v + amount;
        }

        /// <summary>
        /// The daily weighing: decay what is pending, then raise whatever has
        /// crossed its threshold. Returns the intents raised, usually none.
        /// </summary>
        public IReadOnlyList<BuildIntent> Evaluate(Settlement s, long tick, Annalist annals)
        {
            List<BuildIntent> raised = null;
            for (int k = 0; k < _kinds.Count; k++)
            {
                Decay(k);
                if (_total[k] < _kinds[k].Threshold || _outstanding[k] >= _kinds[k].MaxOpen) continue;
                if (raised == null) raised = new List<BuildIntent>();
                raised.Add(Raise(k, s, tick, annals));
            }
            return raised ?? (IReadOnlyList<BuildIntent>)new BuildIntent[0];
        }

        void Decay(int k)
        {
            double f = _dailyDecay[k];
            _total[k] *= f;
            Scale(_demand[k], f);
            Scale(_causeWeight[k], f);
        }

        static void Scale(SortedDictionary<int, double> map, double f)
        {
            if (map.Count == 0) return;
            var keys = new List<int>(map.Keys);
            foreach (int key in keys)
            {
                double v = map[key] * f;
                if (v < Negligible) map.Remove(key); else map[key] = v;
            }
        }

        BuildIntent Raise(int k, Settlement s, long tick, Annalist annals)
        {
            IntentKind kind = _kinds[k];

            // Lean toward the parcel where the pressure was heaviest; ties to
            // the lowest parcel index, which the sorted walk gives for free.
            int bestParcel = -1;
            double bestDemand = double.NegativeInfinity;
            foreach (KeyValuePair<int, double> e in _demand[k])
                if (e.Value > bestDemand) { bestDemand = e.Value; bestParcel = e.Key; }
            int px = bestParcel >= 0 ? bestParcel % ParcelGrid.Width : s.HearthParcelX;
            int pz = bestParcel >= 0 ? bestParcel / ParcelGrid.Width : s.HearthParcelZ;

            // The strongest contributors, strongest first, ties to the earlier
            // record. With none — pressure that no record explains — the
            // founding stands in, so no intent is ever uncaused (L3).
            var weighted = new List<KeyValuePair<int, double>>(_causeWeight[k]);
            weighted.Sort((a, b) =>
            {
                int c = b.Value.CompareTo(a.Value);
                return c != 0 ? c : a.Key.CompareTo(b.Key);
            });
            var causes = new List<RecordId>(MaxCauses);
            for (int i = 0; i < weighted.Count && causes.Count < MaxCauses; i++) causes.Add(new RecordId(weighted[i].Key));
            if (causes.Count == 0) causes.Add(s.Founded);

            var place = new Int3(px * ParcelGrid.Size + ParcelGrid.Size / 2, s.Hearth.Y, pz * ParcelGrid.Size + ParcelGrid.Size / 2);
            double weight = _total[k];
            RecordId record = annals.Write(tick, RaisedKind, s.Id, place, causes[0],
                                           (long)(weight * 1000.0), kind.BudgetVoxels,
                                           new[] { kind.Id }, causes.GetRange(1, causes.Count - 1));

            var intent = new BuildIntent
            {
                Record = record,
                Kind = kind,
                Settlement = s.Id,
                Weight = weight,
                ParcelX = px,
                ParcelZ = pz,
                Place = place,
                BudgetVoxels = kind.BudgetVoxels,
                Causes = causes.ToArray(),
                RaisedTick = tick,
                Status = IntentStatus.Open,
                LastRecord = record,
            };
            _intents.Add(intent);
            _outstanding[k]++;

            // Spent. The next one has to be asked for again.
            _demand[k].Clear();
            _causeWeight[k].Clear();
            _total[k] = 0.0;
            return intent;
        }

        // ── the rest of an intent's life ────────────────────────────────────

        /// <summary>A site has taken it on (S15, S1A). Only an open intent can be claimed.</summary>
        public RecordId Claim(BuildIntent intent, long tick, Annalist annals, RecordId by)
        {
            Require(intent, intent.Status == IntentStatus.Open, "claimed");
            intent.Status = IntentStatus.Claimed;
            return Close(intent, ClaimedKind, tick, annals, by, false);
        }

        /// <summary>Built. <paramref name="by"/> is the finished structure's record.</summary>
        public RecordId Resolve(BuildIntent intent, long tick, Annalist annals, RecordId by)
        {
            Require(intent, intent.Outstanding, "resolved");
            intent.Status = IntentStatus.Resolved;
            return Close(intent, ResolvedKind, tick, annals, by, true);
        }

        /// <summary>Given up on. <paramref name="why"/> is the record that made it pointless or impossible.</summary>
        public RecordId Abandon(BuildIntent intent, long tick, Annalist annals, RecordId why)
        {
            Require(intent, intent.Outstanding, "abandoned");
            intent.Status = IntentStatus.Abandoned;
            return Close(intent, AbandonedKind, tick, annals, why, true);
        }

        RecordId Close(BuildIntent intent, Symbol kind, long tick, Annalist annals, RecordId by, bool ends)
        {
            if (ends)
                for (int k = 0; k < _kinds.Count; k++)
                    if (_kinds[k] == intent.Kind) { _outstanding[k]--; break; }

            RecordId record = annals.Write(tick, kind, intent.Settlement, intent.Place, intent.Record,
                                           (long)(intent.Weight * 1000.0), 0, new[] { intent.Kind.Id },
                                           by.Exists ? new[] { by } : null);
            intent.LastRecord = record;
            return record;
        }

        void Require(BuildIntent intent, bool ok, string what)
        {
            if (!_intents.Contains(intent))
                throw new System.ArgumentException("that intent was not raised on this bus", nameof(intent));
            if (!ok)
                throw new System.InvalidOperationException("an intent that is " + intent.Status + " cannot be " + what);
        }

        /// <summary>Ticks the oldest outstanding intent has waited, or 0 when none is waiting.</summary>
        public long OldestOutstandingAge(long now)
        {
            foreach (BuildIntent i in _intents) if (i.Outstanding) return now - i.RaisedTick;
            return 0;
        }

        public ulong Digest()
        {
            var d = new Digest();
            for (int k = 0; k < _kinds.Count; k++)
            {
                d.Add(System.BitConverter.DoubleToInt64Bits(_total[k]));
                d.Add(_outstanding[k]);
                foreach (KeyValuePair<int, double> e in _demand[k]) { d.Add(e.Key); d.Add(System.BitConverter.DoubleToInt64Bits(e.Value)); }
                foreach (KeyValuePair<int, double> e in _causeWeight[k]) { d.Add(e.Key); d.Add(System.BitConverter.DoubleToInt64Bits(e.Value)); }
            }
            foreach (BuildIntent i in _intents)
            {
                d.Add(i.Record.Index);
                d.Add((int)i.Status);
                d.Add(System.BitConverter.DoubleToInt64Bits(i.Weight));
            }
            return d.Value;
        }
    }

    /// <summary>Weighs every settlement's intent bus once a day, at dawn. S14.</summary>
    public sealed class IntentSystem : ISimSystem
    {
        public static readonly Symbol SystemId = Symbol.For("system.intents");

        public Symbol Id { get { return SystemId; } }

        public void Tick(SimWorld world)
        {
            if (!world.Clock.IsFirstTickOfDay) return;
            foreach (Settlement s in world.Settlements)
                if (s.Intents != null) s.Intents.Evaluate(s, world.Clock.Tick, world.Annals);
        }
    }
}
