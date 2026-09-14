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
                Step(s, _rules, clock.Tick, night, sky, world.Annals, world.Island);
            }
        }

        /// <summary>One tick for one settlement. Public so tests and tools can drive the weather.</summary>
        public static void Step(Settlement s, DriveRules rules, long tick, bool night, Sky sky, Annalist annals,
                                World.IslandMap island = null)
        {
            NeedTable needs = rules.Needs;
            ActivityTable activities = rules.Activities;
            IReadOnlyList<Agent> people = s.People;

            // With families (S2V) everyone eats for themselves, so the settlement's
            // morning meal no longer moves anyone's hunger.
            ulong belly = s.Households.Count > 0 ? 0UL : Conditions.Mask(s.Fed ? Conditions.Fed : Conditions.Hungry);
            ulong day = Conditions.Mask(Conditions.Day) | Conditions.Mask(Conditions.Hearth) | Weathered(sky) | belly;
            // A roof in a settlement with more people than beds is a shared
            // roof, and that presses on everyone under it (S1E).
            // With families (S2N) crowding is each person's own: their family
            // did not fit its roof. Without, it is the whole settlement's.
            bool families = s.Households.Count > 0;
            ulong crowded = !families && s.People.Count > s.ShelterCapacity ? Conditions.Mask(Conditions.Crowded) : 0UL;
            ulong sheltered = Conditions.Mask(Conditions.Night) | Conditions.Mask(Conditions.Hearth)
                            | Conditions.Mask(Conditions.Sheltered) | belly | crowded;
            ulong crowdedBit = Conditions.Mask(Conditions.Crowded);
            ulong exposed = Conditions.Mask(Conditions.Night) | Conditions.Mask(Conditions.Hearth)
                          | Conditions.Mask(Conditions.Unsheltered) | Weathered(sky) | belly
                          | (sky.Rain ? Conditions.Mask(Conditions.Soaked) : 0UL);

            // The conditions a night in the open is to blame for. A need that
            // rises through these names the exposure; one that rises only
            // because time passed (hunger) does not.
            ulong blamed = exposed & ~(Conditions.Mask(Conditions.Night) | Conditions.Mask(Conditions.Hearth));

            RecordId exposure = RecordId.None;
            if (night)
            {
                if (families)
                {
                    Settlements.Households.Rehouse(s, tick, annals);
                    Settlements.Households.AssignRoofs(s, needs.IndexOf("shelter"));
                }
                else AssignRoofs(s, needs);
                exposure = RecordExposure(s, sky, tick, annals);
            }

            int px = s.HearthParcelX, pz = s.HearthParcelZ;
            for (int i = 0; i < people.Count; i++)
            {
                Agent a = people[i];
                ulong conditions;
                RecordId cause;
                if (!night) { conditions = day; cause = RecordId.None; }
                else if (a.ShelteredLastNight)
                {
                    conditions = families && a.Crowded ? sheltered | crowdedBit : sheltered;
                    cause = RecordId.None;
                }
                else { conditions = exposed; cause = exposure; }

                Feel(a, needs, conditions, cause, blamed);

                // Errands first (S2V): whatever presses and takes only part of the
                // tick is done on the side, and the rest of the tick is what is
                // left for everything else.
                a.LabourShare = 1.0;
                a.Errands = "";
                a.ErrandActivity = -1;
                if (families && !night) Errands(s, island, a, i, needs, activities, conditions);

                int chosen = families ? ChooseOwn(s, island, a, i, needs, activities, conditions, night)
                                      : Choose(a, needs, activities, conditions);
                a.Activity = chosen;
                if (chosen >= 0)
                {
                    Activity act = activities[chosen];

                    // Done where it is done (S2V): somewhere to walk to counts on
                    // arrival, which the movement system sees. Here and now, it counts now.
                    bool walks = families && act.At != ActionPlace.Anywhere && act.At != ActionPlace.Task && !act.Productive;
                    if (!walks)
                    {
                        for (int n = 0; n < needs.Count; n++)
                            if (act.Relieves[n] != 0.0) a.Levels[n] = SimMath.Clamp01(a.Levels[n] - act.Relieves[n]);
                        if (act.UsesFood > 0.0) s.Food = System.Math.Max(0.0, s.Food - act.UsesFood);
                    }
                    if (act.Productive) a.ProductiveTicks++;
                    s.ActivityTicks[chosen]++;
                }

                // Felt at the family's own door, so what it asks for is asked
                // for there (S2N); at the fire for anyone with no roof.
                int ax = px, az = pz;
                if (families) Settlements.Households.PressurePoint(s, a, out ax, out az);
                for (int n = 0; n < needs.Count; n++)
                {
                    double over = a.Levels[n] - needs[n].Threshold;
                    if (over > 0.0) s.Pressure.Add(n, ax, az, over, a.Causes[n]);
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

                // Somewhere to go that only a person with a place in the world
                // can go (S2V): a settlement without families eats together at
                // dawn and has no store to walk to, no water, no home of its own.
                if (act.At == ActionPlace.Store || act.At == ActionPlace.Wild || act.At == ActionPlace.Water
                    || act.At == ActionPlace.People || act.At == ActionPlace.Home) continue;

                double u = act.Base;
                for (int n = 0; n < needs.Count; n++)
                    if (act.Relieves[n] > 0.0) u += needs[n].Urgency(a.Levels[n]);

                if (u > bestUtility) { bestUtility = u; best = i; }
            }
            return best;
        }

        /// <summary>
        /// A person with a place in the world choosing for themselves (S2V): the
        /// same utility, less what only a store with food in it makes possible,
        /// and less the walk — a drink a long way off has to be wanted more.
        /// </summary>
        static int ChooseOwn(Settlement s, World.IslandMap island, Agent a, int index, NeedTable needs,
                             ActivityTable activities, ulong conditions, bool night)
        {
            int best = -1;
            double bestUtility = double.NegativeInfinity;
            for (int i = 0; i < activities.Count; i++)
            {
                Activity act = activities[i];
                if (act.IsErrand) continue;                   // done on the side, above
                if (!act.PossibleUnder(conditions)) continue;
                if (act.UsesFood > 0.0 && s.Food < act.UsesFood) continue;

                double u = act.Base;
                for (int n = 0; n < needs.Count; n++)
                    if (act.Relieves[n] > 0.0) u += needs[n].Urgency(a.Levels[n]);

                if (act.At != ActionPlace.Anywhere && act.At != ActionPlace.Task && !act.Productive && !night)
                {
                    double walk = Settlements.Places.Distance(s, island, a, index, act.At, night);
                    if (walk >= 100000.0) continue;          // nowhere to do it
                    u -= walk / WalkPerUtility;
                }

                if (u > bestUtility) { bestUtility = u; best = i; }
            }
            return best;
        }

        /// <summary>
        /// The errands a person does this tick: each one whose need is past
        /// most of its threshold, that is possible, and that fits in what is
        /// left of the tick with the walk there. Their effect is immediate —
        /// they are done within the tick — and the time they take comes off the
        /// person's share of the tick for work.
        /// </summary>
        static void Errands(Settlement s, World.IslandMap island, Agent a, int index, NeedTable needs,
                            ActivityTable activities, ulong conditions)
        {
            double left = 1.0;
            var done = new List<string>();
            for (int i = 0; i < activities.Count; i++)
            {
                Activity act = activities[i];
                if (!act.IsErrand || !act.PossibleUnder(conditions)) continue;
                if (act.UsesFood > 0.0 && s.Food < act.UsesFood) continue;

                bool presses = false;
                for (int n = 0; n < needs.Count; n++)
                    if (act.Relieves[n] > 0.0 && a.Levels[n] >= needs[n].Threshold * ErrandAt) presses = true;
                if (!presses) continue;

                double walk = act.At == ActionPlace.Anywhere ? 0.0 : Settlements.Places.Distance(s, island, a, index, act.At, false);
                if (walk >= 100000.0) continue;
                double cost = act.Takes + walk / VoxelsWalkedPerTick;
                if (cost > left) continue;

                for (int n = 0; n < needs.Count; n++)
                    if (act.Relieves[n] != 0.0) a.Levels[n] = SimMath.Clamp01(a.Levels[n] - act.Relieves[n]);
                if (act.UsesFood > 0.0) s.Food = System.Math.Max(0.0, s.Food - act.UsesFood);
                left -= cost;
                done.Add(act.Doing);
                if (act.At != ActionPlace.Anywhere) a.ErrandActivity = i;
            }
            a.LabourShare = left;
            a.Errands = string.Join(", ", done.ToArray());
        }

        /// <summary>Share of a need's threshold at which an errand for it gets done: before it is urgent, the way people eat before they starve.</summary>
        public const double ErrandAt = 0.75;

        /// <summary>
        /// Voxels a person covers in a whole tick of walking: six hours at a
        /// walking pace is many kilometres, so an errand's walk is a small share
        /// unless the water is a long way off.
        /// </summary>
        public const double VoxelsWalkedPerTick = 2400.0;

        /// <summary>Voxels of walking that cost as much as a full unit of need: a drink ninety metres off is a real errand.</summary>
        public const double WalkPerUtility = 360.0;

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
