using System.Collections.Generic;
using Godless.Sim.Core;
using Godless.Sim.Settlements;
using Godless.Sim.Voxels;

namespace Godless.Sim.Build
{
    /// <summary>
    /// What a stranger can see of a building, as numbers. S1G.
    ///
    /// Only things visible from outside at normal camera distance: how tall,
    /// how big a footprint, how long against how wide, how far the roof
    /// climbs, how far off the ground the floor sits, how much of the wall is
    /// holes, and what it is made of. Capacity and cost are deliberately not
    /// here — a stranger cannot see how many it sleeps.
    /// </summary>
    public sealed class Silhouette
    {
        public static readonly string[] Names =
        {
            "height", "footprint", "aspect", "roof rise", "raised floor", "openness",
            "stone", "timber", "thatch", "earth",
        };

        public readonly double[] Values = new double[Names.Length];

        public double Height { get { return Values[0]; } }
        public double Footprint { get { return Values[1]; } }
        public double Aspect { get { return Values[2]; } }
        public double RoofRise { get { return Values[3]; } }
        public double RaisedFloor { get { return Values[4]; } }
        public double Openness { get { return Values[5]; } }

        /// <summary>Share of the built voxels in each material class, in Names order from index 6.</summary>
        public double ShareOf(int classIndex) { return Values[6 + classIndex]; }

        static readonly string[] Classes = { "stone", "timber", "thatch", "earth" };

        public static Silhouette Measure(Blueprint plan, Structure built, MaterialTable materials, VoxelTypes types)
        {
            var s = new Silhouette();
            Symbol wall = Symbol.For("role.wall"), window = Symbol.For("role.window"), door = Symbol.For("role.door");
            Symbol roof = Symbol.For("role.roof"), floor = Symbol.For("role.floor");

            s.Values[0] = plan.OccupiedHeight;
            s.Values[1] = plan.Footprint(floor);

            int minX = int.MaxValue, maxX = int.MinValue, minZ = int.MaxValue, maxZ = int.MinValue;
            for (int z = 0; z < plan.Depth; z++)
                for (int x = 0; x < plan.Width; x++)
                    for (int y = 0; y < plan.Height; y++)
                        if (plan.At(x, y, z) == floor)
                        {
                            if (x < minX) minX = x;
                            if (x > maxX) maxX = x;
                            if (z < minZ) minZ = z;
                            if (z > maxZ) maxZ = z;
                            break;
                        }
            double across = maxX >= minX ? maxX - minX + 1 : 1, deep = maxZ >= minZ ? maxZ - minZ + 1 : 1;
            s.Values[2] = across > deep ? across / deep : deep / across;

            plan.Span(roof, out int roofLow, out int roofHigh);
            s.Values[3] = roofHigh >= roofLow ? roofHigh - roofLow : 0;
            plan.Span(floor, out int floorLow, out _);
            s.Values[4] = floorLow > 0 ? floorLow : 0;

            double holes = plan.Count(window) + plan.Count(door);
            double solid = plan.Count(wall) + holes;
            s.Values[5] = solid > 0 ? holes / solid : 0.0;

            if (built != null && materials != null)
            {
                long total = built.TotalVoxels;
                for (int m = 0; m < materials.Count; m++)
                {
                    if (built.Cost[m] <= 0 || total <= 0) continue;
                    string name = materials[m].Class.ToString().Replace("class.", "");
                    int k = System.Array.IndexOf(Classes, name);
                    if (k >= 0) s.Values[6 + k] += built.Cost[m] / (double)total;
                }
            }
            return s;
        }
    }

    /// <summary>How far apart two sets of buildings are, and which features carry it.</summary>
    public sealed class SeparationReport
    {
        /// <summary>
        /// Share of buildings a nearest-centroid rule places in the right set,
        /// each left out of its own centroid. Half is chance; one is perfect.
        /// </summary>
        public double Accuracy { get; internal set; }

        /// <summary>Per feature: the standardized difference of means, Cohen's d.</summary>
        public double[] Difference { get; internal set; }

        public int CountA { get; internal set; }
        public int CountB { get; internal set; }

        /// <summary>Features in order of how much they separate the two sets.</summary>
        public IReadOnlyList<int> Strongest
        {
            get
            {
                var order = new List<int>();
                for (int i = 0; i < Difference.Length; i++) order.Add(i);
                order.Sort((a, b) =>
                {
                    double da = System.Math.Abs(Difference[a]), db = System.Math.Abs(Difference[b]);
                    int c = db.CompareTo(da);
                    return c != 0 ? c : a.CompareTo(b);
                });
                return order;
            }
        }
    }

    /// <summary>
    /// The instrument G1's first test needs: two biomes, or two genomes, and a
    /// number saying whether what they build is actually different. S1G.
    ///
    /// One person looking at one seed is how a solo project convinces itself
    /// of something untrue. A nearest-centroid rule over the visible features,
    /// each building left out of its own side's centroid, is not a strong
    /// classifier — that is the point. If a rule this blunt can tell the two
    /// apart, so can a stranger; if it cannot, the difference is not there to
    /// be seen, whatever the screenshots look like.
    /// </summary>
    public static class Separation
    {
        public static SeparationReport Between(IReadOnlyList<Silhouette> a, IReadOnlyList<Silhouette> b)
        {
            int features = Silhouette.Names.Length;
            var report = new SeparationReport { CountA = a.Count, CountB = b.Count, Difference = new double[features] };
            if (a.Count < 2 || b.Count < 2) return report;

            var meanA = new double[features];
            var meanB = new double[features];
            var spread = new double[features];
            for (int f = 0; f < features; f++)
            {
                meanA[f] = Mean(a, f);
                meanB[f] = Mean(b, f);
                double varA = Variance(a, f, meanA[f]), varB = Variance(b, f, meanB[f]);
                double gap = meanB[f] - meanA[f];
                spread[f] = SimMath.Sqrt((varA + varB) * 0.5);

                // A feature can be the same in every building on each side and
                // different between them — every low-pitched roof rises two and
                // every steep one rises seven. Its within-set spread is zero,
                // and scaling by that would throw away the strongest evidence
                // there is, so the gap between the means becomes the scale.
                if (spread[f] <= 1e-9) spread[f] = gap < 0.0 ? -gap : gap;
                report.Difference[f] = spread[f] > 1e-9 ? gap / spread[f] : 0.0;
            }

            // Standardized so a footprint in columns cannot drown a roof pitch
            // in voxels, then nearest centroid, leaving each building out of
            // its own side.
            int right = 0, total = 0;
            for (int side = 0; side < 2; side++)
            {
                IReadOnlyList<Silhouette> own = side == 0 ? a : b;
                IReadOnlyList<Silhouette> other = side == 0 ? b : a;
                double[] ownMean = side == 0 ? meanA : meanB;
                double[] otherMean = side == 0 ? meanB : meanA;

                foreach (Silhouette one in own)
                {
                    double toOwn = 0.0, toOther = 0.0;
                    for (int f = 0; f < features; f++)
                    {
                        if (spread[f] <= 1e-9) continue;
                        // Leave this building out of its own centroid.
                        double withoutIt = (ownMean[f] * own.Count - one.Values[f]) / (own.Count - 1);
                        double dOwn = (one.Values[f] - withoutIt) / spread[f];
                        double dOther = (one.Values[f] - otherMean[f]) / spread[f];
                        toOwn += dOwn * dOwn;
                        toOther += dOther * dOther;
                    }
                    if (toOwn <= toOther) right++;
                    total++;
                }
            }

            report.Accuracy = total > 0 ? right / (double)total : 0.0;
            return report;
        }

        static double Mean(IReadOnlyList<Silhouette> set, int f)
        {
            double sum = 0.0;
            foreach (Silhouette s in set) sum += s.Values[f];
            return sum / set.Count;
        }

        static double Variance(IReadOnlyList<Silhouette> set, int f, double mean)
        {
            double sum = 0.0;
            foreach (Silhouette s in set)
            {
                double d = s.Values[f] - mean;
                sum += d * d;
            }
            return set.Count > 1 ? sum / (set.Count - 1) : 0.0;
        }
    }
}
