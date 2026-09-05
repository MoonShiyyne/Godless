using System.Collections.Generic;

namespace Godless.Sim.Harness
{
    /// <summary>
    /// L7 in code: a named claim about what the simulation must always do,
    /// phrased over a batch of seeded runs rather than over one.
    ///
    /// Owner is the registry id of the system the claim belongs to, so the
    /// "one assertion per system" rule can actually be audited instead of
    /// being believed.
    /// </summary>
    public sealed class Invariant
    {
        public string Name { get; private set; }
        public string Owner { get; private set; }
        public bool IsPerRun { get; private set; }

        readonly System.Func<RunResult, bool> _perRun;
        readonly System.Func<IReadOnlyList<RunResult>, bool> _perBatch;

        Invariant(string name, string owner, bool perRun,
                  System.Func<RunResult, bool> run,
                  System.Func<IReadOnlyList<RunResult>, bool> batch)
        {
            Name = name; Owner = owner; IsPerRun = perRun;
            _perRun = run; _perBatch = batch;
        }

        /// <summary>Must hold in every run. "no build intent unresolved for > 3 years".</summary>
        public static Invariant PerRun(string owner, string name, System.Func<RunResult, bool> check)
        {
            return new Invariant(name, owner, true, check, null);
        }

        /// <summary>
        /// Must hold across the batch. "a myth outlives its last first-hand
        /// scar in at least 30% of runs" is only meaningful this way.
        /// </summary>
        public static Invariant PerBatch(string owner, string name,
                                         System.Func<IReadOnlyList<RunResult>, bool> check)
        {
            return new Invariant(name, owner, false, null, check);
        }

        internal bool CheckRun(RunResult run) { return _perRun(run); }
        internal bool CheckBatch(IReadOnlyList<RunResult> runs) { return _perBatch(runs); }
    }

    public sealed class InvariantOutcome
    {
        public Invariant Invariant { get; internal set; }
        public bool Passed { get; internal set; }

        /// <summary>Seeds that failed a per-run invariant, in ascending order.</summary>
        public IReadOnlyList<ulong> FailingSeeds { get; internal set; }
    }
}
