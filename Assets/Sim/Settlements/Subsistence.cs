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
                Eat(s, world, rng);
                Lose(s, world);
                Gain(s, world, rng);
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

        /// <summary>What the settlement would like in the store: a season of meals.</summary>
        public static double Wanted(Settlement s) { return s.People.Count * MealsADay * SurplusDays * 1.5; }
    }
}
