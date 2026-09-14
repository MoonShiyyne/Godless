using System.Collections.Generic;
using Godless.Sim.Core;

namespace Godless.Sim.Drives
{
    /// <summary>
    /// The facts about an agent's circumstances that needs react to. S12.
    ///
    /// Code produces conditions; content decides what each one does to each
    /// need. So "rain makes you cold" is a line in a need file, not a line of
    /// C#, and a mod can make rain matter more without touching the sim.
    ///
    /// The set is closed on purpose. A need file that names a condition
    /// nothing produces would react to nothing, silently, so the need table
    /// refuses it by name instead. A later system that produces a new
    /// condition — a flood, a raid on the night watch — adds it here.
    /// </summary>
    public static class Conditions
    {
        public static readonly Symbol Day = Symbol.For("condition.day");
        public static readonly Symbol Night = Symbol.For("condition.night");

        /// <summary>Slept under a roof. Night only.</summary>
        public static readonly Symbol Sheltered = Symbol.For("condition.sheltered");

        /// <summary>Slept in the open. Night only.</summary>
        public static readonly Symbol Unsheltered = Symbol.For("condition.unsheltered");

        /// <summary>It is raining and this agent is out in it.</summary>
        public static readonly Symbol Rain = Symbol.For("condition.rain");

        /// <summary>It is cold and this agent is out in it.</summary>
        public static readonly Symbol Cold = Symbol.For("condition.cold");

        /// <summary>
        /// Slept in the rain with no roof. Worse than being out in a shower by
        /// day, and a separate condition so content can say by how much.
        /// </summary>
        public static readonly Symbol Soaked = Symbol.For("condition.soaked");

        /// <summary>The settlement has a fire to sit at.</summary>
        public static readonly Symbol Hearth = Symbol.For("condition.hearth");

        /// <summary>Slept under a roof in a settlement with more people than beds (S1E).</summary>
        public static readonly Symbol Crowded = Symbol.For("condition.crowded");

        /// <summary>There was food in the store this morning (S1E).</summary>
        public static readonly Symbol Fed = Symbol.For("condition.fed");

        /// <summary>There was not, and this person went without.</summary>
        public static readonly Symbol Hungry = Symbol.For("condition.hungry");

        /// <summary>Food lies in heaps in the fields, waiting to be carried in (S2X).</summary>
        public static readonly Symbol Harvest = Symbol.For("condition.harvest");

        /// <summary>A farm has plots waiting to be sown or harvested (S2I).</summary>
        public static readonly Symbol Fields = Symbol.For("condition.fields");

        // Bit positions are code order, fixed here. They never reach content,
        // a save or a digest, so they are free to be positional.
        static readonly Symbol[] _known = { Day, Night, Sheltered, Unsheltered, Rain, Cold, Soaked, Hearth, Crowded, Fed, Hungry, Fields, Harvest };

        public static IReadOnlyList<Symbol> Known { get { return _known; } }

        /// <summary>The bit for a condition, or -1 when nothing produces it.</summary>
        public static int BitOf(Symbol condition)
        {
            for (int i = 0; i < _known.Length; i++) if (_known[i] == condition) return i;
            return -1;
        }

        public static ulong Mask(Symbol condition)
        {
            int bit = BitOf(condition);
            return bit < 0 ? 0UL : 1UL << bit;
        }

        public static bool Has(ulong mask, Symbol condition) { return (mask & Mask(condition)) != 0UL; }
    }
}
