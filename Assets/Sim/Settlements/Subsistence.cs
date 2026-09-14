using System.Collections.Generic;
using Godless.Sim.Annals;
using Godless.Sim.Core;
using Godless.Sim.Drives;
using Godless.Sim.Harness;

namespace Godless.Sim.Settlements
{
    /// <summary>
    /// Eating, and what follows from it. S1E.
    ///
    /// Nothing in the registry produced a population: material stock is
    /// material, and hunger was a need with no producer behind it. This is the
    /// rest — a store of food, foraging that fills it, a meal a day that
    /// empties it, and the two things that follow: a settlement that eats well
    /// and has roofs takes in children, and one that cannot feed itself loses
    /// people.
    ///
    /// The tell is the second half, and once the task board weighed a meal
    /// against a house properly (S29's tuning) it came out exactly as the
    /// stage plan said: on thin ground every hand stays on the food, the
    /// houses never go up at all, and the settlement dwindles in the open.
    /// On good ground the same twenty people roof themselves and grow.
    /// </summary>
    public sealed class Subsistence : ISimSystem
    {
        public static readonly Symbol SystemId = Symbol.For("system.subsistence");
        public static readonly Symbol BornKind = Symbol.For("person.born");
        public static readonly Symbol DiedKind = Symbol.For("person.died");
        public static readonly Symbol HungerKind = Symbol.For("settlement.hungry");
        public const string StreamId = "settlement.subsistence";

        /// <summary>Meals one person eats a day.</summary>
        public const double MealsADay = 1.0;

        /// <summary>Days of food in hand, per person, before a settlement will take in a child.</summary>
        public const int SurplusDays = 20;

        /// <summary>Days a person can go hungry before they are lost.</summary>
        public const int StarvesAfter = 40;

        /// <summary>Chance in a thousand, per day, of a birth in a settlement that is fed and roofed.</summary>
        public const int BirthChance = 25;

        readonly int _hungerNeed;

        public Subsistence(DriveRules rules) { _hungerNeed = rules.Needs.IndexOf("hunger"); }

        public Symbol Id { get { return SystemId; } }

        public void Tick(SimWorld world)
        {
            if (!world.Clock.IsFirstTickOfDay) return;
            RngStream rng = world.Streams.Get(StreamId);

            foreach (Settlement s in world.Settlements)
            {
                if (s.HouseholdRules != null) Starve(s, world);
                else
                {
                    Eat(s, world, rng);
                    Lose(s, world);
                }
                if (s.HouseholdRules != null)
                {
                    Age(s, world, rng);
                    Grow(s, world, rng);
                }
                else Gain(s, world, rng);
            }
        }

        /// <summary>A meal each, while there is food. Whoever misses one is hungry, and the settlement knows it.</summary>
        static void Eat(Settlement s, SimWorld world, RngStream rng)
        {
            double wanted = s.People.Count * MealsADay;
            double eaten = s.Food < wanted ? s.Food : wanted;
            s.Food -= eaten;

            bool fedAll = eaten >= wanted - 1e-9;
            s.Fed = fedAll;
            if (fedAll) { s.HungryDays = 0; return; }

            if (s.HungryDays == 0)
                annalHunger(s, world);
            s.HungryDays++;
        }

        static void annalHunger(Settlement s, SimWorld world)
        {
            world.Annals.Write(world.Clock.Tick, HungerKind, s.Id, s.Hearth, s.Founded, s.People.Count, (long)s.Food);
        }

        /// <summary>Long enough without food and a settlement loses people.</summary>
        void Lose(Settlement s, SimWorld world)
        {
            if (s.HungryDays < StarvesAfter || s.People.Count == 0) return;

            // The hungriest goes first; ties by founding order.
            int worst = 0;
            for (int i = 1; i < s.People.Count; i++)
                if (_hungerNeed >= 0 && s.People[i].Level(_hungerNeed) > s.People[worst].Level(_hungerNeed)) worst = i;

            Agent lost = s.People[worst];
            world.Annals.Write(world.Clock.Tick, DiedKind, lost.Id, s.Hearth, s.Founded, s.HungryDays);
            s.Remove(worst, world.Streams);
            s.HungryDays = 0;   // one at a time, and the rest eat a little longer
        }

        /// <summary>Fed, roofed and with food put by, a settlement takes in children.</summary>
        static void Gain(Settlement s, SimWorld world, RngStream rng)
        {
            if (!s.Fed || s.People.Count == 0) return;

            // Everyone under a roof and food put by. The child then makes the
            // settlement one bed short, which is what asks for the next house:
            // growth and building pull each other along.
            if (s.ShelterCapacity < s.People.Count) return;
            if (s.Food < s.People.Count * MealsADay * SurplusDays) return;
            if (rng.NextInt(1000) >= BirthChance) return;

            Agent child = s.Add(world.Streams);
            world.Annals.Write(world.Clock.Tick, BornKind, child.Id, s.Hearth, s.Founded, s.People.Count);
        }

        /// <summary>
        /// Births with families (S2N). The chance is per person, so a village
        /// of a hundred has five times the children of one of twenty: growth
        /// accelerates as the settlement does. Crowding and a thin store slow
        /// it — fertility falls with the share of people who have a bed, down
        /// to a floor, and halves on a store below a season — but neither stops
        /// it outright. What stops it is hunger, and the houses a crowded
        /// family builds are what let it go on.
        /// </summary>
        static void Grow(Settlement s, SimWorld world, RngStream rng)
        {
            int people = s.People.Count;
            if (!s.Fed || people < 2) return;
            HouseholdRules rules = s.HouseholdRules;

            double beds = s.ShelterCapacity;
            double room = rules.CrowdedFertility + (1.0 - rules.CrowdedFertility) * SimMath.Clamp01(beds / people);
            double store = s.Food >= people * MealsADay * SurplusDays ? 1.0
                         : s.Food >= people * MealsADay * 5 ? 0.5 : 0.0;
            double expected = people * rules.BirthsPerPersonYear / world.Clock.DaysPerYear * room * store;

            int births = Whole(expected, rng);
            for (int b = 0; b < births; b++)
            {
                Agent parent = s.People[rng.NextInt(s.People.Count)];
                Agent child = s.Add(world.Streams);
                RecordId born = world.Annals.Write(world.Clock.Tick, BornKind, child.Id, s.Hearth, s.Founded,
                                                   s.People.Count, 0, new[] { parent.Id });
                Settlements.Households.Born(s, child, parent, world.Clock.Tick, world.Annals, born);
            }
        }

        /// <summary>Deaths that are not hunger (S2N): a small chance a year for everyone.</summary>
        static void Age(Settlement s, SimWorld world, RngStream rng)
        {
            if (s.People.Count == 0) return;
            double expected = s.People.Count * s.HouseholdRules.DeathsPerPersonYear / world.Clock.DaysPerYear;
            int deaths = Whole(expected, rng);
            for (int d = 0; d < deaths && s.People.Count > 0; d++)
            {
                int who = rng.NextInt(s.People.Count);
                world.Annals.Write(world.Clock.Tick, DiedKind, s.People[who].Id, s.Hearth, s.Founded, 0);
                s.Remove(who, world.Streams);
            }
        }

        /// <summary>An expected count as a whole number: the whole part, and the fraction as a chance of one more.</summary>
        static int Whole(double expected, RngStream rng)
        {
            int whole = (int)expected;
            double frac = expected - whole;
            if (rng.NextInt(1000000) < (int)(frac * 1000000.0)) whole++;
            return whole;
        }

        /// <summary>Hunger level past which a person counts as starving (S2V).</summary>
        public const double Starving = 0.9;

        /// <summary>
        /// With families (S2V) nobody is fed at dawn: each person eats from the
        /// store when they are hungry enough to walk there. What is left for the
        /// day is counting who is starving. Anyone past <see cref="Starving"/>
        /// for <see cref="StarvesAfter"/> days is lost, the longest-starving
        /// first; the settlement is fed when nobody is starving.
        /// </summary>
        void Starve(Settlement s, SimWorld world)
        {
            if (_hungerNeed < 0) return;
            int starving = 0, worst = -1, worstDays = 0;
            for (int i = 0; i < s.People.Count; i++)
            {
                Agent a = s.People[i];
                if (a.Level(_hungerNeed) >= Starving) { a.HungryDays++; starving++; }
                else a.HungryDays = 0;
                if (a.HungryDays > worstDays) { worstDays = a.HungryDays; worst = i; }
            }

            bool wasFed = s.Fed;
            s.Fed = starving == 0;
            s.HungryDays = worstDays;
            if (wasFed && !s.Fed) annalHunger(s, world);

            if (worst >= 0 && worstDays >= StarvesAfter)
            {
                Agent lost = s.People[worst];
                world.Annals.Write(world.Clock.Tick, DiedKind, lost.Id, s.Hearth, s.Founded, worstDays);
                s.Remove(worst, world.Streams);
            }
        }

        /// <summary>What the settlement would like in the store: a season of meals.</summary>
        public static double Wanted(Settlement s) { return s.People.Count * MealsADay * SurplusDays * 1.5; }
    }
}
