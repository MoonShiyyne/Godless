using System.Collections.Generic;
using Godless.Sim.Annals;
using Godless.Sim.Content;
using Godless.Sim.Core;
using Godless.Sim.Culture;
using Godless.Sim.Settlements;
using Godless.Sim.World;

namespace Godless.Sim.Build
{
    /// <summary>Where a building went, and how well the ground suited it.</summary>
    public sealed class Site
    {
        public int ParcelX { get; internal set; }
        public int ParcelZ { get; internal set; }

        /// <summary>Parcels across and deep that the building takes.</summary>
        public int ParcelsWide { get; internal set; }
        public int ParcelsDeep { get; internal set; }

        public double Score { get; internal set; }

        /// <summary>The site.chosen record. Caused by the intent, so the chain runs back to the nights in the open.</summary>
        public RecordId Record { get; internal set; }

        /// <summary>Ground level where the building sits: the highest ground under its footprint.</summary>
        public int Ground { get; internal set; }
    }

    /// <summary>One kind of building's idea of good ground, as content declares it.</summary>
    public sealed class SitingRule
    {
        public Symbol Id { get; internal set; }
        public string Name { get; internal set; }
        public string Tell { get; internal set; }

        /// <summary>The intent kind it sites, by name.</summary>
        public string For { get; internal set; }

        /// <summary>Above zero, the parcel is allowed at all. Land, dry enough, not somebody's already.</summary>
        internal Expr Allow;

        /// <summary>How good it is. Higher wins; the genome is in the expression.</summary>
        internal Expr Score;

        /// <summary>How far from the hearth the settlement will look, in parcels.</summary>
        public int SearchRadius { get; internal set; }
    }

    /// <summary>
    /// Site scoring. S15.
    ///
    /// Part 05, stage one: influence maps over the coarse grid — slope,
    /// drainage and flood history, sun, wind, distance to water and to what
    /// the building serves — "weighted by the genome, a defensive culture
    /// weights the ridge, a mercantile one weights the crossing".
    ///
    /// The weighing is content, in the same expression language the grammar
    /// uses, so which ground a culture calls good is data and not a branch in
    /// C#. The scorer reads the fields S1F computed, the parcel grid, the
    /// settlement's own claims and the genome, and picks the best parcel that
    /// the building fits on.
    /// </summary>
    public sealed class SitingTable
    {
        readonly SitingRule[] _rules;
        readonly List<string> _problems;

        SitingTable(SitingRule[] rules, List<string> problems) { _rules = rules; _problems = problems; }

        public int Count { get { return _rules.Length; } }
        public SitingRule this[int index] { get { return _rules[index]; } }
        public IReadOnlyList<SitingRule> All { get { return _rules; } }
        public IReadOnlyList<string> Problems { get { return _problems; } }

        public SitingRule For(string intentKind)
        {
            foreach (SitingRule r in _rules) if (r.For == intentKind) return r;
            return null;
        }

        static readonly string[] Known =
        {
            "field.sun", "field.snow", "field.damp", "field.exposure", "field.flood",
            "parcel.slope", "parcel.height", "parcel.water", "parcel.hearth", "parcel.land", "parcel.wet",
            "intent.budget",
        };

        public static SitingTable FromContent(ContentDatabase content, GeneTable genes, IntentKindTable intents = null)
        {
            var problems = new List<string>();
            var loaded = new List<SitingRule>();

            foreach (string id in content.Ids("siting"))
            {
                JsonValue doc = content.Get("siting", id);
                string tell = doc["tell"].AsString("").Trim();
                string builds = doc["for"].AsString("");
                string fault = tell.Length == 0 ? "declares no tell" : null;
                if (fault == null && intents != null)
                {
                    bool known = false;
                    for (int i = 0; i < intents.Count; i++) if (intents[i].Name == builds) known = true;
                    if (!known) fault = "sites '" + builds + "', which no intent kind raises";
                }

                Expr allow = null, score = null;
                if (fault == null)
                {
                    try { allow = Expr.Parse(doc["allow"].AsString("1")); }
                    catch (ExprException e) { fault = "has a broken 'allow': " + e.Message; }
                }
                if (fault == null)
                {
                    try { score = Expr.Parse(doc["score"].AsString("0")); }
                    catch (ExprException e) { fault = "has a broken 'score': " + e.Message; }
                }
                if (fault == null) fault = CheckNames(allow, genes) ?? CheckNames(score, genes);

                if (fault != null) { problems.Add("siting '" + id + "' " + fault + "."); continue; }
                loaded.Add(new SitingRule
                {
                    Id = Symbol.For("siting." + id),
                    Name = id,
                    Tell = tell,
                    For = builds,
                    Allow = allow,
                    Score = score,
                    SearchRadius = doc["searchRadiusParcels"].AsInt32(12),
                });
            }

            loaded.Sort((a, b) => a.Id.CompareTo(b.Id));
            return new SitingTable(loaded.ToArray(), problems);
        }

        static string CheckNames(Expr e, GeneTable genes)
        {
            var names = new List<string>();
            e.Names(names);
            foreach (string n in names)
            {
                if (System.Array.IndexOf(Known, n) >= 0) continue;
                if (n.StartsWith("gene.", System.StringComparison.Ordinal))
                {
                    if (genes.IndexOf(Symbol.For(n)) < 0) return "reads '" + n + "', which no gene declares";
                    continue;
                }
                return "reads '" + n + "', which is not a field, a parcel fact or a gene";
            }
            return null;
        }
    }

    public static class SiteScorer
    {
        public static readonly Symbol ChosenKind = Symbol.For("site.chosen");

        /// <summary>
        /// The best parcel the building fits on, or null when the settlement
        /// has nowhere left it will build. Writes the site.chosen record,
        /// caused by the intent that asked for it, and claims the ground.
        /// </summary>
        public static Site Choose(Settlement settlement, BuildIntent intent, Blueprint plan, SitingRule rule,
                                  ParcelGrid grid, ConstraintFields fields, Genome genome,
                                  long tick, Annalist annals)
        {
            int wide = Parcels(plan.Width - 2 * Grammar.Margin);
            int deep = Parcels(plan.Depth - 2 * Grammar.Margin);

            var scope = new ParcelScope { Fields = fields, Grid = grid, Genome = genome, Budget = intent.BudgetVoxels };
            int hx = intent.ParcelX, hz = intent.ParcelZ;
            scope.HearthX = settlement.HearthParcelX;
            scope.HearthZ = settlement.HearthParcelZ;

            // Ground you can still walk to from the fire without crossing
            // somebody's house. Flooded once, rather than pathed per candidate.
            bool[] reachable = Reachable(settlement, grid);

            Site best = null;
            double bestScore = double.NegativeInfinity;
            for (int pz = hz - rule.SearchRadius; pz <= hz + rule.SearchRadius; pz++)
                for (int px = hx - rule.SearchRadius; px <= hx + rule.SearchRadius; px++)
                {
                    if (!Fits(settlement, grid, px, pz, wide, deep, reachable)) continue;

                    scope.X = px; scope.Z = pz;
                    if (rule.Allow.Eval(scope) <= 0.0) continue;

                    // The score is the worst parcel of the footprint, not the
                    // best: a house is only as well sited as its worst corner.
                    double score = double.MaxValue;
                    for (int dz = 0; dz < deep; dz++)
                        for (int dx = 0; dx < wide; dx++)
                        {
                            scope.X = px + dx; scope.Z = pz + dz;
                            double here = rule.Score.Eval(scope);
                            if (here < score) score = here;
                        }

                    if (score > bestScore) { bestScore = score; best = new Site { ParcelX = px, ParcelZ = pz, ParcelsWide = wide, ParcelsDeep = deep, Score = score }; }
                }

            if (best == null) return null;

            int ground = 0;
            for (int dz = 0; dz < deep; dz++)
                for (int dx = 0; dx < wide; dx++)
                {
                    int g = grid.MaxGround(best.ParcelX + dx, best.ParcelZ + dz);
                    if (g > ground) ground = g;
                }
            best.Ground = ground + 1;

            var place = new Int3(best.ParcelX * ParcelGrid.Size + wide * ParcelGrid.Size / 2, best.Ground,
                                 best.ParcelZ * ParcelGrid.Size + deep * ParcelGrid.Size / 2);
            best.Record = annals.Write(tick, ChosenKind, settlement.Id, place, intent.Record,
                                       (long)(best.Score * 1000.0), wide * deep, new[] { intent.Kind.Id });

            for (int dz = 0; dz < deep; dz++)
                for (int dx = 0; dx < wide; dx++) settlement.ClaimParcel(best.ParcelX + dx, best.ParcelZ + dz, best.Record);
            return best;
        }

        static int Parcels(int voxels) { return (voxels + ParcelGrid.Size - 1) / ParcelGrid.Size; }

        /// <summary>
        /// S1B: a building needs ground of its own, a parcel of daylight
        /// between it and its neighbours, and a way to the fire that does not
        /// go through somebody else's house.
        /// </summary>
        static bool Fits(Settlement settlement, ParcelGrid grid, int px, int pz, int wide, int deep, bool[] reachable)
        {
            for (int dz = 0; dz < deep; dz++)
                for (int dx = 0; dx < wide; dx++)
                {
                    int x = px + dx, z = pz + dz;
                    if (!ParcelGrid.InBounds(x, z) || !grid.IsLand(x, z) || grid.WetColumns(x, z) > 0) return false;
                }

            // The footprint and the ring round it: unclaimed, so houses do not
            // grow into each other and the gaps between them stay walkable.
            for (int dz = -1; dz <= deep; dz++)
                for (int dx = -1; dx <= wide; dx++)
                    if (settlement.IsClaimed(px + dx, pz + dz)) return false;

            for (int dz = -1; dz <= deep; dz++)
                for (int dx = -1; dx <= wide; dx++)
                {
                    int x = px + dx, z = pz + dz;
                    bool inside = dx >= 0 && dz >= 0 && dx < wide && dz < deep;
                    if (inside || !ParcelGrid.InBounds(x, z)) continue;
                    if (reachable[z * ParcelGrid.Width + x]) return true;   // a way in from the settlement
                }
            return false;
        }

        /// <summary>
        /// Every parcel a person can walk to from the hearth without crossing
        /// a claim. One flood from the fire, rather than a path per candidate.
        /// </summary>
        static bool[] Reachable(Settlement settlement, ParcelGrid grid)
        {
            var seen = new bool[ParcelGrid.Width * ParcelGrid.Depth];
            var queue = new Queue<int>();
            int start = settlement.HearthParcelZ * ParcelGrid.Width + settlement.HearthParcelX;
            seen[start] = true;
            queue.Enqueue(start);

            while (queue.Count > 0)
            {
                int at = queue.Dequeue();
                int ax = at % ParcelGrid.Width, az = at / ParcelGrid.Width;
                for (int dz = -1; dz <= 1; dz++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dz == 0) continue;
                        int nx = ax + dx, nz = az + dz;
                        if (!ParcelGrid.InBounds(nx, nz)) continue;
                        int next = nz * ParcelGrid.Width + nx;
                        if (seen[next] || !grid.IsLand(nx, nz) || settlement.IsClaimed(nx, nz)) continue;

                        double climb = grid.Height[nx, nz] - grid.Height[ax, az];
                        if (climb < 0.0) climb = -climb;
                        if (climb >= ParcelPath.Impassable || grid.Slope[nx, nz] >= ParcelPath.Impassable) continue;

                        seen[next] = true;
                        queue.Enqueue(next);
                    }
            }
            return seen;
        }

        sealed class ParcelScope : IExprScope
        {
            public ConstraintFields Fields;
            public ParcelGrid Grid;
            public Genome Genome;
            public int X, Z, HearthX, HearthZ, Budget;

            public double Resolve(string name)
            {
                switch (name)
                {
                    case "field.sun": return Fields.Sun[X, Z];
                    case "field.snow": return Fields.SnowLoad[X, Z];
                    case "field.damp": return Fields.Damp[X, Z];
                    case "field.exposure": return Fields.Exposure[X, Z];
                    case "field.flood": return Fields.FloodRisk[X, Z];
                    case "parcel.slope": return Grid.Slope[X, Z];
                    case "parcel.height": return Grid.Height[X, Z];
                    case "parcel.water": return Grid.WaterDistance[X, Z];
                    case "parcel.land": return Grid.IsLand(X, Z) ? 1.0 : 0.0;
                    case "parcel.wet": return Grid.WetColumns(X, Z);
                    case "intent.budget": return Budget;
                    case "parcel.hearth":
                    {
                        double dx = X - HearthX, dz = Z - HearthZ;
                        return SimMath.Sqrt(dx * dx + dz * dz);
                    }
                }
                if (name.StartsWith("gene.", System.StringComparison.Ordinal))
                {
                    double v = Genome[Symbol.For(name)];
                    return double.IsNaN(v) ? 0.0 : v;
                }
                return 0.0;
            }
        }
    }
}
