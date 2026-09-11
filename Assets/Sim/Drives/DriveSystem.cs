using System.Collections.Generic;
using Godless.Sim.Annals;
using Godless.Sim.Core;
using Godless.Sim.Harness;
using Godless.Sim.Settlements;
using Godless.Sim.World;

namespace Godless.Sim.Drives
{
    /// <summary>
    /// Layer 1 of Part 03's instinct stack: per agent, every tick. S12.
    ///
    /// Each tick an agent's circumstances raise or ease its needs, it does
    /// whichever activity scores highest, and whatever stays over threshold
    /// leaves as pressure. The last tick of each day is night: people sleep,
    /// the roofs go to whoever needs one most, and the rest sleep in the open.
    ///
    /// The tell is the carry-over. Nothing resets at dawn, so a night in the
    /// rain is still in an agent's warmth the next morning and the morning
    /// goes to the fire instead of to work. And building appears nowhere in
    /// here: the only way an agent can ask for a roof is by not having one.
    /// </summary>
    public sealed class DriveSystem : ISimSystem
    {
        public static readonly Symbol SystemId = Symbol.For("system.drives");

        /// <summary>A spell of nights in the open with one shape: how many, and in what weather.</summary>
        public static readonly Symbol ExposedKind = Symbol.For("drive.exposed");

        readonly DriveRules _rules;

        public DriveSystem(DriveRules rules) { _rules = rules; }

        public Symbol Id { get { return SystemId; } }

        public void Tick(SimWorld world)
        {
            SimClock clock = world.Clock;
            bool night = clock.TickOfDay == clock.TicksPerDay - 1;
            foreach (Settlement s in world.Settlements)
            {
                Sky sky = Weather.On(world.Streams, s.Biome, clock.TotalDays, clock.DaysPerYear);
                Step(s, _rules, clock.Tick, night, sky, world.Annals);
            }
        }

        /// <summary>One tick for one settlement. Public so tests and tools can drive the weather.</summary>
        public static void Step(Settlement s, DriveRules rules, long tick, bool night, Sky sky, Annalist annals)
        {
            NeedTable needs = rules.Needs;
            ActivityTable activities = rules.Activities;
            IReadOnlyList<Agent> people = s.People;

            ulong day = Conditions.Mask(Conditions.Day) | Conditions.Mask(Conditions.Hearth) | Weathered(sky);
            ulong sheltered = Conditions.Mask(Conditions.Night) | Conditions.Mask(Conditions.Hearth)
                            | Conditions.Mask(Conditions.Sheltered);
            ulong exposed = Conditions.Mask(Conditions.Night) | Conditions.Mask(Conditions.Hearth)
                          | Conditions.Mask(Conditions.Unsheltered) | Weathered(sky)
                          | (sky.Rain ? Conditions.Mask(Conditions.Soaked) : 0UL);

            // The conditions a night in the open is to blame for. A need that
            // rises through these names the exposure; one that rises only
            // because time passed (hunger) does not.
            ulong blamed = exposed & ~(Conditions.Mask(Conditions.Night) | Conditions.Mask(Conditions.Hearth));

            RecordId exposure = RecordId.None;
            if (night)
            {
                AssignRoofs(s, needs);
                exposure = RecordExposure(s, sky, tick, annals);
            }

            int px = s.HearthParcelX, pz = s.HearthParcelZ;
            for (int i = 0; i < people.Count; i++)
            {
                Agent a = people[i];
                ulong conditions;
                RecordId cause;
                if (!night) { conditions = day; cause = RecordId.None; }
                else if (a.ShelteredLastNight) { conditions = sheltered; cause = RecordId.None; }
                else { conditions = exposed; cause = exposure; }

                Feel(a, needs, conditions, cause, blamed);
                int chosen = Choose(a, needs, activities, conditions);
                a.Activity = chosen;
                if (chosen >= 0)
                {
                    Activity act = activities[chosen];
                    for (int n = 0; n < needs.Count; n++)
                        if (act.Relieves[n] != 0.0) a.Levels[n] = SimMath.Clamp01(a.Levels[n] - act.Relieves[n]);
                    if (act.Productive) a.ProductiveTicks++;
                    s.ActivityTicks[chosen]++;
                }

                for (int n = 0; n < needs.Count; n++)
                {
                    double over = a.Levels[n] - needs[n].Threshold;
                    if (over > 0.0) s.Pressure.Add(n, px, pz, over, a.Causes[n]);
                }
            }
        }

        static ulong Weathered(Sky sky)
        {
            return (sky.Rain ? Conditions.Mask(Conditions.Rain) : 0UL)
                 | (sky.Cold ? Conditions.Mask(Conditions.Cold) : 0UL);
        }

        /// <summary>
        /// Conditions move needs. When the conditions the cause is to blame
        /// for push a need up, the need remembers the cause; drift and
        /// unrelated conditions never overwrite it.
        /// </summary>
        static void Feel(Agent a, NeedTable needs, ulong conditions, RecordId cause, ulong blamed)
        {
            for (int n = 0; n < needs.Count; n++)
            {
                Need need = needs[n];
                double delta = need.Drift, fromCause = 0.0;
                for (int bit = 0; bit < need.Rise.Length; bit++)
                {
                    ulong m = 1UL << bit;
                    if ((conditions & m) == 0UL) continue;
                    delta += need.Rise[bit];
                    if ((blamed & m) != 0UL) fromCause += need.Rise[bit];
                }

                a.Levels[n] = SimMath.Clamp01(a.Levels[n] + delta);
                if (fromCause > 0.0 && cause.Exists) a.Causes[n] = cause;
            }
        }

        /// <summary>
        /// Highest utility wins: base, plus the urgency of each need the
        /// activity relieves. Ties go to the earlier activity in stable-hash
        /// order, so the choice never depends on anything but the numbers.
        /// </summary>
        public static int Choose(Agent a, NeedTable needs, ActivityTable activities, ulong conditions)
        {
            int best = -1;
            double bestUtility = double.NegativeInfinity;
            for (int i = 0; i < activities.Count; i++)
            {
                Activity act = activities[i];
                if (!act.PossibleUnder(conditions)) continue;

                double u = act.Base;
                for (int n = 0; n < needs.Count; n++)
                    if (act.Relieves[n] > 0.0) u += needs[n].Urgency(a.Levels[n]);

                if (u > bestUtility) { bestUtility = u; best = i; }
            }
            return best;
        }

        /// <summary>
        /// The roofs go to whoever needs one most; ties to founding order.
        /// Nobody owns a bed yet, so an exposed night makes you first in line
        /// for the next one and the misery rotates.
        /// </summary>
        static void AssignRoofs(Settlement s, NeedTable needs)
        {
            IReadOnlyList<Agent> people = s.People;
            int shelterNeed = needs.IndexOf("shelter");

            var order = new int[people.Count];
            for (int i = 0; i < order.Length; i++) order[i] = i;
            if (shelterNeed >= 0)
                System.Array.Sort(order, (x, y) =>
                {
                    int c = people[y].Levels[shelterNeed].CompareTo(people[x].Levels[shelterNeed]);
                    return c != 0 ? c : x.CompareTo(y);
                });

            int roofs = s.ShelterCapacity;
            for (int k = 0; k < order.Length; k++) people[order[k]].ShelteredLastNight = k < roofs;
        }

        /// <summary>
        /// One record per spell of nights with the same shape — same number in
        /// the open, same weather — rather than one a night. A dry month in the
        /// open is one record; each night the rain starts or stops is another.
        /// The spell's cause is the founding: they are in the open because
        /// they came here with nothing. A flood that takes roofs will name
        /// itself instead, when floods exist.
        /// </summary>
        static RecordId RecordExposure(Settlement s, Sky sky, long tick, Annalist annals)
        {
            int inOpen = 0;
            foreach (Agent a in s.People) if (!a.ShelteredLastNight) inOpen++;

            if (inOpen == 0)
            {
                s.SpellRecord = RecordId.None;
                s.SpellSignature = -1;
                return RecordId.None;
            }

            long signature = ((long)inOpen << 2) | (sky.Rain ? 1L : 0L) | (sky.Cold ? 2L : 0L);
            if (signature != s.SpellSignature || !s.SpellRecord.Exists)
            {
                var weather = new List<Symbol>(2);
                if (sky.Rain) weather.Add(Conditions.Rain);
                if (sky.Cold) weather.Add(Conditions.Cold);
                s.SpellRecord = annals.Write(tick, ExposedKind, s.Id, s.Hearth, s.Founded,
                                             inOpen, s.ShelterCapacity, weather);
                s.SpellSignature = signature;
            }
            return s.SpellRecord;
        }
    }
}
