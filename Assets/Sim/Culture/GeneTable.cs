using System.Collections.Generic;
using Godless.Sim.Content;
using Godless.Sim.Core;

namespace Godless.Sim.Culture
{
    /// <summary>One style disposition, as content declares it.</summary>
    public sealed class Gene
    {
        public Symbol Id { get; internal set; }
        public string Name { get; internal set; }
        public double Min { get; internal set; }
        public double Max { get; internal set; }
        public double Default { get; internal set; }

        /// <summary>What a stranger sees when this number moves. Required.</summary>
        public string Tell { get; internal set; }

        public double Clamp(double v) { return v < Min ? Min : (v > Max ? Max : v); }

        /// <summary>The value mapped onto 0..1, which is how genomes are compared.</summary>
        public double Normalize(double v) { return Max > Min ? (Clamp(v) - Min) / (Max - Min) : 0.0; }
    }

    /// <summary>
    /// Every gene the loaded content declares. S17.
    ///
    /// Part 04: the genome is "a float array driving a grammar — no learning,
    /// no inference, fully deterministic and fully debuggable". The genes are
    /// content (L5), so a mod can add a disposition, and they are ordered by
    /// the stable hash of their id rather than by load order (L2), so adding
    /// one never shifts another's slot.
    ///
    /// The gene rule is enforced here rather than hoped for: a gene that does
    /// not declare what a stranger would see does not load. Part 04 says to
    /// cut such a gene; the table does it for you, and says which and why.
    /// </summary>
    public sealed class GeneTable
    {
        readonly Gene[] _genes;
        readonly List<string> _problems;

        GeneTable(Gene[] genes, List<string> problems)
        {
            _genes = genes;
            _problems = problems;
        }

        public int Count { get { return _genes.Length; } }
        public Gene this[int index] { get { return _genes[index]; } }
        public IReadOnlyList<Gene> All { get { return _genes; } }

        /// <summary>Genes that were refused, and why. Empty means everything loaded.</summary>
        public IReadOnlyList<string> Problems { get { return _problems; } }

        public static GeneTable FromContent(ContentDatabase content)
        {
            var problems = new List<string>();
            var loaded = new List<Gene>();

            foreach (string id in content.Ids("gene"))
            {
                JsonValue doc = content.Get("gene", id);
                string tell = doc["tell"].AsString(null);
                double min = doc["min"].AsDouble(0.0);
                double max = doc["max"].AsDouble(1.0);
                double def = doc["default"].AsDouble(min);

                if (string.IsNullOrEmpty(tell) || tell.Trim().Length == 0)
                {
                    problems.Add("gene '" + id + "' declares no tell. Every gene must name what a stranger "
                                 + "would see change in the silhouette; one that cannot is not loaded.");
                    continue;
                }
                if (!(max > min))
                {
                    problems.Add("gene '" + id + "' has max " + Num(max) + " not above min " + Num(min) + ".");
                    continue;
                }
                if (def < min || def > max)
                {
                    problems.Add("gene '" + id + "' has default " + Num(def) + " outside [" + Num(min) + ", " + Num(max) + "].");
                    continue;
                }

                loaded.Add(new Gene
                {
                    Id = Symbol.For("gene." + id),
                    Name = id,
                    Min = min,
                    Max = max,
                    Default = def,
                    Tell = tell.Trim(),
                });
            }

            // Stable-hash order: a mod that adds a gene cannot move another.
            loaded.Sort((a, b) => a.Id.CompareTo(b.Id));
            return new GeneTable(loaded.ToArray(), problems);
        }

        static string Num(double v) { return v.ToString("R", System.Globalization.CultureInfo.InvariantCulture); }

        public int IndexOf(Symbol gene)
        {
            int lo = 0, hi = _genes.Length - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                int c = _genes[mid].Id.CompareTo(gene);
                if (c == 0) return mid;
                if (c < 0) lo = mid + 1; else hi = mid - 1;
            }
            return -1;
        }

        public Gene Find(Symbol gene)
        {
            int i = IndexOf(gene);
            return i < 0 ? null : _genes[i];
        }
    }
}
