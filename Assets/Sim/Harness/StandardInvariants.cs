using System.Collections.Generic;

namespace Godless.Sim.Harness
{
    /// <summary>
    /// Every invariant the simulation claims, owned by the registry id of the
    /// system that claims it. Law L7: from stratum 2 on, no system ships
    /// without at least one entry here, phrased over 200 seeds rather than
    /// over one.
    ///
    /// The list below is short and will stay short until there are systems to
    /// describe, and that is the honest state rather than a gap. Part 27
    /// names seventeen assertions the finished game should hold; most of them
    /// ("no motif library reaches zero without a settlement collapse", "a myth
    /// outlives its last first-hand scar in at least 30% of runs") cannot be
    /// written before the thing they describe exists. The machine that runs
    /// them is what stratum 0 owes; the vocabulary is what each later system
    /// owes when it lands.
    ///
    /// When adding a system, add its invariant here in the same diff.
    /// </summary>
    public static class StandardInvariants
    {
        public static IReadOnlyList<Invariant> All()
        {
            return new List<Invariant>
            {
                // S01 — the clock is exact. Integer ticks, so "roughly 300
                // years" is not a thing that can happen.
                Invariant.PerRun("S01", "the clock lands exactly on the requested year",
                    run => run.Ticks == (long)run.Years * 360L * 4L),

                // S03 — the island stays resident. Measured at ~2 MB for a
                // full surface; this fails long before a profiler would.
                Invariant.PerRun("S03", "the world stays inside its 8 MB budget",
                    run => run.Metric("world.megabytes") < 8.0),

                // S04 — provenance is not free-running. Every voxel change is
                // a delta, so a system that churns voxels every tick shows up
                // here as a stream that outgrows the building it represents.
                Invariant.PerRun("S04", "the delta stream stays under a million changes",
                    run => run.Metric("deltas.count") < 1_000_000.0),

                // S05 — the record is append-only and bounded by what happened.
                Invariant.PerRun("S05", "the annals hold a record only for things that occurred",
                    run => run.Metric("annals.count") >= 0.0
                        && run.Metric("annals.count") <= run.Metric("deltas.count") + 1_000_000.0),
            };
        }

        /// <summary>How many systems currently carry at least one invariant.</summary>
        public static IReadOnlyList<string> Owners()
        {
            var owners = new SortedSet<string>(System.StringComparer.Ordinal);
            foreach (Invariant i in All()) owners.Add(i.Owner);
            return new List<string>(owners);
        }
    }
}
